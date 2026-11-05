using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Hunted;

/// <summary>
/// The mind of a bot, which runs on the host. The body is a real player avatar (BotAvatar.cs): this is an invisible walking agent that tells it where to be, how it stands and how fast it goes,
/// the way a player's own machine does. How it thinks is in BotBrain.cs. Anything that happens to the body (damage, tumbling, death, revival) is the game's own business, and this only reacts to it.
/// </summary>
internal partial class Bot : MonoBehaviour
{
    internal enum StanceKind { Stand, Crouch, Crawl }
    private enum Mode { Idle, Wander, Scavenge, Deliver, Flee, Hide, Escape, Rest, FetchHead, BringHead }

    internal static readonly List<Bot> All = new();
    internal PlayerAvatar Avatar { get; private set; } = null!;
    internal int Slot { get; private set; }
    internal string Name => Avatar != null ? Avatar.playerName : $"Bot {Slot + 1}";

    private bool InTruck => Avatar.RoomVolumeCheck != null && Avatar.RoomVolumeCheck.inTruck;
    private bool Alive => Avatar != null && !Avatar.deadSet && !Avatar.isDisabled;
    private float _boostUntil => BotAvatar.BoostUntil(Slot);

    internal StanceKind Stance { get; private set; } = StanceKind.Stand;

    private NavMeshAgent? _agent;
    private float _coverTimer;

    private Mode _mode = Mode.Idle;
    private float _modeUntil;
    private float _repathAt;
    private StanceKind _wanted = StanceKind.Stand;
    private StanceKind _forced = StanceKind.Stand; // what the ceiling allows
    private float _stamina = 5f;
    private bool _sprinting;
    private Vector3 _knock;
    private bool _down; // dead, or tumbling: the body is not its to move
    private bool _moving;

    // What it knows about the killer, and its job.
    private Vector3 _lastKnown;
    private float _lastNoticed = -100f;
    private ValuableObject? _target;
    private ValuableObject? _carrying;
    private ExtractionPoint? _point;
    private readonly HashSet<ValuableObject> _skipped = new();

    internal static Bot Attach(PlayerAvatar avatar, int slot)
    {
        var root = new GameObject($"Hunted Bot {slot + 1} Mind");
        root.transform.SetPositionAndRotation(avatar.clientPosition, avatar.clientRotation);
        var bot = root.AddComponent<Bot>();
        bot.Avatar = avatar;
        bot.Slot = slot;
        bot._stamina = StaminaSeconds;
        bot.SetMode(Mode.Idle, 1.5f);
        bot.Join(avatar.clientPosition);
        All.Add(bot);
        return bot;
    }

    internal static void Reset()
    {
        foreach (var bot in All)
            if (bot != null)
                Object.Destroy(bot.gameObject);
        All.Clear();
        BotIntel.Reset();
        BotHideSpots.Reset();
        BotCarry.Reset();
    }

    internal static void Tick()
    {
        All.RemoveAll(b => b == null);
        if (All.Count == 0)
            return;
        BotIntel.Tick(Time.deltaTime);
        BotHideSpots.Tick();
        BotCarry.Tick();
    }

    /// <summary>Back on its feet at this spot: on the walking mesh and with the agent running.</summary>
    private void Join(Vector3 at)
    {
        if (!Tuning.Bots.Moves)
            return;
        if (NavMesh.SamplePosition(at, out var hit, 4f, NavMesh.AllAreas))
            at = hit.position;
        transform.position = at;
        if (_agent == null)
        {
            gameObject.SetActive(false); // the agent starts up only when it is set up and where it belongs
            _agent = gameObject.AddComponent<NavMeshAgent>();
            _agent.radius = 0.3f;
            _agent.height = 1.7f;
            _agent.acceleration = 14f;
            _agent.angularSpeed = 480f;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance; // bots step around each other
            _agent.avoidancePriority = 30 + Slot * 8;
            gameObject.SetActive(true);
        }
        _agent.enabled = true;
        _agent.Warp(at);
    }

    /// <summary>The body was hurt: whoever did it is what it runs from.</summary>
    internal void OnHurt()
    {
        _hurtAt = Time.time;
        if (BotIntel.HasEyes && Vector3.Distance(BotIntel.EyePosition, transform.position) < 6f)
        {
            _lastKnown = BotIntel.EyePosition;
            _lastNoticed = Time.time;
        }
        if (Tuning.Bots.Moves && _mode != Mode.Flee)
            StartFlee(4f);
    }

    /// <summary>A push that would move a player's controller.</summary>
    internal void Knock(Vector3 force) => _knock = Vector3.ClampMagnitude(force, 6f);

    // --- Each frame -------------------------------------------------------------------------------------------------------------

    private void Update()
    {
        if (Avatar == null)
        {
            Destroy(gameObject);
            return;
        }
        if (!Alive || Avatar.isTumbling)
        {
            if (!_down)
                GoDown();
            return;
        }
        if (_down)
        {
            _down = false;
            transform.rotation = Quaternion.Euler(0f, Avatar.clientRotation.eulerAngles.y, 0f);
            Join(Avatar.clientPosition);
            SetMode(Mode.Idle, 0.5f);
        }

        // What the ceiling allows: a low table or bed forces it down, as it does a real survivor.
        _coverTimer -= Time.deltaTime;
        if (_coverTimer <= 0f)
        {
            _coverTimer = 0.2f;
            _forced = Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.up, out var above, 1.9f, SightMask, QueryTriggerInteraction.Ignore)
                ? (above.distance < 0.85f ? StanceKind.Crawl : StanceKind.Crouch)
                : StanceKind.Stand;
        }

        _sprinting = false;
        Behave();
        Stance = (StanceKind)Mathf.Max((int)_wanted, (int)_forced);
        var agentOn = _agent != null && _agent.enabled && _agent.isOnNavMesh;
        var velocity = agentOn ? _agent!.velocity : Vector3.zero;
        _moving = _sliding || velocity.sqrMagnitude > 0.04f;
        CarryValuable();
        CarryHead();
        BotAvatar.Drive(Avatar, transform.position, transform.rotation, Stance, _moving, _sprinting && _moving && Stance == StanceKind.Stand, transform.InverseTransformDirection(velocity));
    }

    private void GoDown()
    {
        _down = true;
        DropCarried();
        _knock = Vector3.zero;
        _offMesh = false;
        _sliding = false;
        _moving = false;
        BotAvatar.Drive(Avatar, Avatar.clientPosition, Avatar.clientRotation, Stance, false, false, Vector3.zero);
        _target = null;
        _point = null;
        if (_agent != null)
        {
            if (_agent.enabled && _agent.isOnNavMesh)
                _agent.ResetPath();
            _agent.enabled = false;
        }
    }

    private void OnDestroy() => DropCarried();

    private void SetMode(Mode mode, float seconds)
    {
        _mode = mode;
        _modeUntil = Time.time + seconds;
        _repathAt = 0f;
        if (mode != Mode.Hide)
            _spot = null;
        _restSpot = null;
        if (mode != Mode.FetchHead)
            _targetHead = null; // whatever called it away: it can pick that head again later
        _wanted = StanceKind.Stand;
    }

    private float _noteAt;
    private void DebugNote(string what)
    {
        if (Time.time < _noteAt)
            return;
        _noteAt = Time.time + 3f;
        Hunted.Logger.LogDebug($"{Name} {what}.");
    }

    // --- Carrying a valuable ----------------------------------------------------------------------------------------------------

    private void IgnoreBody(ValuableObject valuable, bool ignore)
    {
        if (Avatar == null)
            return;
        foreach (var collider in valuable.GetComponentsInChildren<Collider>())
        {
            if (Avatar.playerCollider != null)
                Physics.IgnoreCollision(collider, Avatar.playerCollider, ignore);
            if (Avatar.playerAvatarCollision != null && Avatar.playerAvatarCollision.Collider != null)
                Physics.IgnoreCollision(collider, Avatar.playerAvatarCollision.Collider, ignore);
        }
    }

    private void PickUp(ValuableObject valuable)
    {
        _carrying = valuable;
        IgnoreBody(valuable, true);
        Hunted.Logger.LogDebug($"{Name} picked up a valuable (${valuable.dollarValueCurrent:0}).");
    }

    /// <summary>Lets go of what it holds, where it is or at a given spot.</summary>
    private void DropCarried(Vector3? at = null, bool keepHead = false)
    {
        if (!keepHead)
            DropHead();
        var valuable = _carrying;
        _carrying = null;
        if (valuable == null)
            return;
        _reclaim = valuable; // it comes back for it, unless it was put where it belongs
        if (valuable.physGrabObject != null)
            BotCarry.LetGo(valuable.physGrabObject, at);
        IgnoreBody(valuable, false);
    }

    private void CarryValuable()
    {
        var valuable = _carrying;
        if (valuable == null)
            return;
        var grabbed = valuable.physGrabObject != null && valuable.physGrabObject.grabbed;
        if (valuable.GetComponent<Rigidbody>() == null || grabbed)
        {
            Hunted.Logger.LogDebug($"{Name} lost its valuable (somebody took it).");
            DropCarried();
            return;
        }
        if (valuable.physGrabObject != null)
            BotCarry.HoldAt(valuable.physGrabObject, transform.position + transform.forward * 0.7f + Vector3.up * (Stance == StanceKind.Stand ? 1.0f : 0.5f));
    }
}

/// <summary>Debug: no monsters at all.</summary>
[HarmonyPatch]
static class NoMonstersPatch
{
    // No monsters unless Tuning.json asks for some: the game's own "no enemies" switch, set when the level starts building.
    [HarmonyPatch(typeof(LevelGenerator), "Generate")]
    [HarmonyPrefix]
    private static void Generate_Prefix(LevelGenerator __instance)
    {
        if (Tuning.Monsters.MonstersPerTier <= 0 && SemiFunc.IsMasterClientOrSingleplayer())
        {
            __instance.DebugNoEnemy = true;
            Hunted.Logger.LogInfo("No monsters: Monsters.MonstersPerTier in Tuning.json is 0.");
        }
    }
}
