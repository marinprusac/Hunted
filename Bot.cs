using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Hunted;

/// <summary>
/// A stand-in survivor. It looks and acts like a real one: the game's own player model with no cosmetics and one random flat colour, the player animations and footsteps, the survivors' health,
/// regeneration and boost. It starts where a survivor would, does a survivor's job and tries to stay alive; how it thinks is in BotBrain.cs. Bots count as survivors: for the number of
/// extraction points, and for the win conditions (they must be dead for the killer to win, and in a truck, once the trucks are open, for the survivors to). A killed bot leaves a death head
/// that other bots carry to a truck to revive it, as a player's is. The host runs them and everyone sees them (BotNet.cs), as many as the host's Bots setting says (0 to 5); Bots.Moves in Tuning.json turns the walking off, to leave them where they are.
/// </summary>
internal partial class Bot : MonoBehaviour
{
    internal enum StanceKind { Stand, Crouch, Crawl }
    private enum Mode { Idle, Wander, Scavenge, Deliver, Flee, Hide, Escape, Rest, FetchHead, BringHead }

    /// <summary>Every bot that is up and about (a killed one is out of the list).</summary>
    internal static readonly List<Bot> All = new();
    internal int Slot { get; private set; }
    /// <summary>Inside a truck: for the win condition, a bot there has escaped.</summary>
    internal bool InTruck { get; private set; }
    /// <summary>Bots that were killed this level (until another bot brings their head to a truck).</summary>
    internal static int DeadCount => DeadSlots.Count;
    internal string Name => $"bot {Slot + 1}";

    internal StanceKind Stance { get; private set; } = StanceKind.Stand;
    internal bool Crouching => Stance != StanceKind.Stand;
    internal bool Crawling => Stance == StanceKind.Crawl;
    internal bool Tumbling => Time.time < _tumbleUntil;
    internal bool Alive { get; private set; } = true;
    internal float Health { get; private set; }
    internal Vector3 TargetPosition => transform.position + Vector3.up * (Tumbling ? 0.2f : Stance == StanceKind.Stand ? 1.5f : Stance == StanceKind.Crouch ? 0.8f : 0.3f);

    private Rigidbody _body = null!;
    private CapsuleCollider _collider = null!;
    private NavMeshAgent? _agent;
    private Animator? _animator;
    private readonly HashSet<string> _animatorParameters = new();
    private readonly List<(Material material, Color color)> _tints = new();
    private readonly List<Material> _healthBars = new();
    private Gradient? _healthColours;
    private float _healthShown = -1f;
    private float _coverTimer;
    private float _flash;
    private static readonly HashSet<int> DeadSlots = new();
    private static List<Bounds> _trucks = new();
    private static float _trucksAt = -100f;
    private static readonly Dictionary<int, GameObject> Heads = new();
    private static readonly Dictionary<int, float> HeadInTruckSince = new();
    private static readonly Dictionary<int, Color> Colours = new();
    private Color _colour;
    private readonly Materials.MaterialTrigger _materialTrigger = new();
    private float _investigateAt;

    private Mode _mode = Mode.Idle;
    private float _modeUntil;
    private float _repathAt;
    private StanceKind _wanted = StanceKind.Stand;
    private StanceKind _forced = StanceKind.Stand; // what the ceiling allows
    private float _stamina = 5f;
    private bool _sprinting;
    private float _tumbleUntil;
    private float _boostUntil;
    private Vector3 _knock;
    private bool _wasHealthy = true;
    private bool _tumbled;
    private float _tumbleEndsBy;
    private float _tumbleFromY;
    private Vector3 _lastGround;
    private bool _moving;
    private bool _glowing;
    private (StanceKind, bool) _shapeShown;

    // What it knows about the killer, and its job.
    private Vector3 _lastKnown;
    private float _lastNoticed = -100f;
    private ValuableObject? _target;
    private ValuableObject? _carrying;
    private ExtractionPoint? _point;
    private readonly HashSet<ValuableObject> _skipped = new();

    /// <summary>Removes every bot and every death head (a new level, or the bots turned off).</summary>
    internal static void Reset()
    {
        foreach (var bot in All.ToList())
            if (bot != null)
                bot.Remove();
        All.Clear();
        foreach (var head in Heads.Values)
            DestroyHead(head);
        Heads.Clear();
        HeadInTruckSince.Clear();
        Colours.Clear();
        DeadSlots.Clear();
        BotIntel.Reset();
        BotHideSpots.Reset();
        BotCarry.Reset();
    }

    internal static void Tick()
    {
        var ready = KillerRole.ModeActive && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        // Other machines only show what the host's bots do.
        if (!SemiFunc.IsMasterClientOrSingleplayer())
        {
            if (!ready)
            {
                if (All.Count > 0)
                    Reset();
                return;
            }
            TickPuppets();
            return;
        }

        var count = ready && KillerRole.KillerSteamId != null ? BotRoster.Count : 0;
        if (count <= 0)
        {
            if (All.Count > 0 || Heads.Count > 0)
                Reset();
            return;
        }

        BotIntel.Tick(Time.deltaTime);
        BotHideSpots.Tick();
        BotCarry.Tick();

        All.RemoveAll(b => b == null);
        foreach (var bot in All.ToList())
        {
            if (bot.Slot >= count)
                bot.Remove();
            else if (bot.transform.position.y < -30f)
                bot.Die("a fall"); // nobody comes back from there by themselves
        }
        foreach (var slot in Heads.Keys.Where(k => k >= count).ToList())
            ClearHead(slot);

        ReviveCheck();

        // One new bot per frame, each in its own spot.
        for (var slot = 0; slot < count; slot++)
        {
            if (All.Any(b => b.Slot == slot) || DeadSlots.Contains(slot))
                continue;
            Spawn(slot, count);
            break;
        }
        SendStates();
    }

    private static void ClearHead(int slot)
    {
        if (Heads.TryGetValue(slot, out var head))
            DestroyHead(head);
        Heads.Remove(slot);
    }

    private static void DestroyHead(GameObject? head)
    {
        if (head == null)
            return;
        var view = head.GetComponent<PhotonView>();
        if (PhotonNetwork.InRoom && view != null && view.ViewID != 0)
            PhotonNetwork.Destroy(head);
        else
            Object.Destroy(head);
    }

    private void Remove()
    {
        All.Remove(this);
        DropCarried();
        Object.Destroy(gameObject);
    }

    private void OnDestroy() => DropCarried();

    /// <summary>Killed: it leaves a death head, and stays dead unless somebody brings that to a truck.</summary>
    private void Die(string cause)
    {
        Alive = false;
        Hunted.Logger.LogInfo($"{Name} was killed ({cause}).");
        DeadSlots.Add(Slot);
        LeaveHead();
        Remove();
    }

    internal void Hurt(int damage, string cause, Vector3? from = null, float tumbleSeconds = 0f, float force = 4f)
    {
        if (_puppet || !Alive || damage <= 0)
            return;
        Health -= damage;
        _hits = (_hits + 1) & 255;
        HitFeedback();
        _hurtAt = Time.time;
        KillerVision.RevealHurt(this); // somebody who was hurt cannot hide
        Hunted.Logger.LogInfo($"{Name} took {damage} damage from {cause}: {Mathf.Max(0f, Health):0}/{HuntedConfig.MaxHealth.Value} left.");
        if (Health <= 0f)
        {
            Die(cause);
            return;
        }

        // Like a real survivor: hit down to half health or less after being above it, the boost starts; and while it lasts nothing tumbles them.
        var healthy = Health > HuntedConfig.MaxHealth.Value * Tuning.Boost.HealthFraction;
        if (!healthy && _wasHealthy && HuntedConfig.BoostSeconds.Value > 0f)
        {
            _boostUntil = Time.time + HuntedConfig.BoostSeconds.Value;
            Hunted.Logger.LogDebug($"{Name}'s boost starts.");
        }
        _wasHealthy = healthy;

        var away = Vector3.zero;
        if (from.HasValue)
        {
            away = transform.position - from.Value;
            away.y = 0f;
            away = away.normalized;
            _knock = away * 4f;
            if (cause == "the sword")
            {
                _lastKnown = from.Value; // whoever hit it is who it runs from
                _lastNoticed = Time.time;
            }
        }
        if (tumbleSeconds > 0f && Time.time >= _boostUntil)
            StartTumble(away, tumbleSeconds, force);
        if (Tuning.Bots.Moves && _mode != Mode.Flee)
            StartFlee(4f);
    }

    /// <summary>Flash and sound of a hit, on every machine.</summary>
    private void HitFeedback()
    {
        _flash = 0.3f;
        PlaySound(HurtSound, transform.position);
    }

    // A real tumble: the body goes limp and physics takes it, so it can roll down a slope or into a pit. It gets up once it has stopped and the time is over.
    private void StartTumble(Vector3 away, float seconds, float force)
    {
        DropCarried();
        _knock = Vector3.zero;
        _tumbleUntil = Mathf.Max(_tumbleUntil, Time.time + seconds);
        if (_agent != null)
        {
            if (_agent.enabled && _agent.isOnNavMesh)
                _agent.ResetPath();
            _agent.enabled = false;
        }
        _offMesh = false;
        _sliding = false;
        if (_tumbled)
            return;
        _tumbled = true;
        _tumbleEndsBy = Time.time + seconds + 5f;
        _tumbleFromY = transform.position.y;
        transform.position += Vector3.up * 0.3f; // the walking mesh can lie a little under the visible floor: starting there it would fall through
        _body.isKinematic = false;
        _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        _body.AddForce(away * Mathf.Clamp(2f + force * 0.15f, 2f, 9f) + Vector3.up * 2f, ForceMode.VelocityChange);
        _body.AddTorque(Random.onUnitSphere * 6f, ForceMode.VelocityChange);
    }

    private void UpdateTumble()
    {
        // Through the floor where there is floor to stand on: that is no fall, it is put back. A real pit has no walkable floor at that height.
        if (transform.position.y < _tumbleFromY - 1.5f && NavMesh.SamplePosition(new Vector3(transform.position.x, _tumbleFromY, transform.position.z), out var floor, 2f, -1))
        {
            Hunted.Logger.LogDebug($"{Name} went through the floor while tumbling and is put back.");
            GetUp(floor.position);
            return;
        }
        if (Time.time < _tumbleUntil)
            return;
        var stopped = _body.angularVelocity.sqrMagnitude < 1f && Physics.Raycast(transform.position + Vector3.up * 0.3f, Vector3.down, 1f, (int)SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore);
        if (!stopped && Time.time < _tumbleEndsBy)
            return;
        if (!NavMesh.SamplePosition(transform.position, out var hit, stopped ? 4f : 10f, -1))
        {
            _tumbleEndsBy = Time.time + 1f; // nowhere to stand here: it keeps lying there until a hazard or a fall settles it
            return;
        }
        GetUp(hit.position);
    }

    private void GetUp(Vector3 at)
    {
        _tumbled = false;
        _tumbleUntil = 0f;
        _body.collisionDetectionMode = CollisionDetectionMode.Discrete;
        _body.isKinematic = true;
        transform.SetPositionAndRotation(at, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
        if (_agent != null)
        {
            _agent.enabled = true;
            _agent.Warp(at);
        }
    }

    /// <summary>What a damage collider of the game does to a bot in reach, as it would to a player.</summary>
    internal static void HurtColliderCheck(HurtCollider hurt)
    {
        var shape = hurt.Collider;
        if (shape == null || !shape.enabled || !hurt.playerLogic || hurt.detectionOnly || hurt.playerDamageCooldown <= 0f)
            return;
        if (!hurt.playerKill && hurt.playerDamage <= 0 && hurt.playerTumbleTime <= 0f)
            return;
        var sword = KillerWeapon.Weapon != null && KillerWeapon.Weapon.hurtCollider == hurt;
        foreach (var bot in All.ToArray())
        {
            if (bot == null || !bot.Alive || bot._puppet)
                continue;
            var target = bot.TargetPosition;
            var inReach = shape is BoxCollider or SphereCollider or CapsuleCollider or MeshCollider { convex: true }
                ? (shape.ClosestPoint(target) - target).sqrMagnitude < 0.36f
                : shape.bounds.Contains(target);
            if (!inReach || !hurt.CanHit(bot.gameObject, hurt.playerDamageCooldown, hurt.playerRayCast, target, HurtCollider.HitType.Player))
                continue;
            if (sword)
                KillerWeapon.Weapon!.PlayerSwingHit(); // what the game does for a hit on a player: the same energy loss
            var damage = hurt.playerKill ? Mathf.CeilToInt(bot.Health) : hurt.playerDamage;
            var cause = sword ? "the sword" : hurt.enemyHost != null ? "a monster" : hurt.deathPit ? "a pit" : hurt.name;
            if (damage > 0)
                bot.Hurt(damage, cause, shape.bounds.center, hurt.playerTumbleTime, hurt.playerTumbleForce);
            else if (hurt.playerTumbleTime > 0f && Time.time >= bot._boostUntil)
                bot.StartTumble(Vector3.zero, hurt.playerTumbleTime, hurt.playerTumbleForce);
        }
    }

    private static Bot? Spawn(int slot, int count, Vector3? at = null)
    {
        var killer = PlayerController.instance;
        if (killer == null)
            return null;

        // Where a survivor would start: spread out over the map and far from the killer, not where the killer is standing.
        var position = at ?? SpawnPlanner.BotSpawn() ?? killer.transform.position;

        var root = new GameObject($"Hunted Bot {slot + 1}");
        root.transform.position = position;
        var bot = root.AddComponent<Bot>();
        bot.Slot = slot;
        bot.Build();
        All.Add(bot);
        Hunted.Logger.LogInfo($"{bot.Name} placed at {position}: it scavenges and runs from the killer like a survivor (Bots section in Tuning.json).");
        return bot;
    }

    private void Build()
    {
        _body = gameObject.AddComponent<Rigidbody>();
        _body.mass = 20f;
        _body.isKinematic = true; // the walking agent moves it
        _collider = gameObject.AddComponent<CapsuleCollider>();
        KillerWeapon.IgnoreBody(_collider); // the sword goes through it like through any survivor

        if (!BuildModel())
            BuildCapsule();
        // For the killer it is hidden from the start, as any survivor: their vision shows it once it is spotted.
        if (KillerRole.LocalIsKiller)
            foreach (var renderer in GetComponentsInChildren<Renderer>(true))
                renderer.forceRenderingOff = true;

        if (!_puppet && Tuning.Bots.Moves && NavMesh.SamplePosition(transform.position, out var hit, 3f, -1))
        {
            transform.position = hit.position; // on the walking mesh before the agent exists, or it fails to start
            gameObject.SetActive(false); // the agent starts up only when it is set up and where it belongs
            _agent = gameObject.AddComponent<NavMeshAgent>();
            _agent.radius = 0.3f;
            _agent.height = 1.7f;
            _agent.acceleration = 14f;
            _agent.angularSpeed = 480f;
            _agent.obstacleAvoidanceType = ObstacleAvoidanceType.LowQualityObstacleAvoidance; // bots step around each other
            _agent.avoidancePriority = 30 + Slot * 8;
            gameObject.SetActive(true);
            _agent.Warp(hit.position);
        }
        Health = HuntedConfig.MaxHealth.Value;
        _stamina = StaminaSeconds;
        SetMode(Mode.Idle, 1.5f);
        ApplyShape();
    }

    // --- The body ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The survivors' own model: a copy of the game's player prefab's visuals (no cosmetics: those are added when a real player is set up), cut loose from the
    /// player scripts that would make it follow the killer, painted one random flat colour, and animated through the same animator the players use.
    /// </summary>
    private bool BuildModel()
    {
        try
        {
            var controller = PlayerController.instance;
            var source = controller != null && controller.playerAvatarPrefab != null
                ? controller.playerAvatarPrefab.GetComponentInChildren<PlayerAvatarVisuals>(true)
                : null;
            if (source == null)
            {
                Hunted.Logger.LogWarning("A bot has no player model to copy; it is a capsule.");
                return false;
            }

            // Built inside something switched off, so none of the model's scripts ever starts.
            var holder = new GameObject("Hunted Dummy Holder");
            holder.SetActive(false);
            var model = Object.Instantiate(source.gameObject, holder.transform);
            model.name = "Look";
            var visuals = model.GetComponent<PlayerAvatarVisuals>();
            var meshParent = visuals != null ? visuals.meshParent : null;

            _healthColours = controller!.playerAvatarPrefab.GetComponentInChildren<PlayerHealth>(true)?.healthMaterialColor;
            var parts = model.GetComponentsInChildren<PlayerMaterial>(true).Where(p => p.tintable).Select(p => p.GetComponent<Renderer>()).Where(r => r != null).ToList();

            foreach (var component in model.GetComponentsInChildren<Joint>(true))
                Object.DestroyImmediate(component);
            foreach (var component in model.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(component);
            foreach (var component in model.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(component);
            foreach (var component in model.GetComponentsInChildren<Light>(true))
                Object.DestroyImmediate(component);
            foreach (var component in model.GetComponentsInChildren<ParticleSystem>(true))
                Object.DestroyImmediate(component);
            StripScripts(model); // first: some scripts need the sound filters
            RemoveAudio(model);

            model.transform.SetParent(transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            Object.DestroyImmediate(holder);
            model.SetActive(true);
            if (meshParent != null)
                meshParent.SetActive(true);
            model.AddComponent<BotAnimationEvents>().Bot = this; // the animations call the player's methods for footsteps and the like

            // One random flat colour for the whole body, nothing else.
            if (!Colours.TryGetValue(Slot, out var colour))
                Colours[Slot] = colour = Color.HSVToRGB((Slot * 0.2f + Random.Range(0f, 0.12f)) % 1f, Random.Range(0.55f, 0.9f), Random.Range(0.6f, 0.95f));
            _colour = colour;
            foreach (var renderer in parts)
                foreach (var material in renderer!.materials)
                {
                    if (material.name.Contains("Health"))
                        continue;
                    if (material.HasProperty("_AlbedoColor"))
                        material.SetColor("_AlbedoColor", colour);
                    if (material.HasProperty("_AlbedoTexture"))
                        material.SetTexture("_AlbedoTexture", Texture2D.whiteTexture);
                    if (material.HasProperty("_FresnelColor"))
                        material.SetColor("_FresnelColor", colour);
                }

            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is ParticleSystemRenderer)
                    continue;
                renderer.enabled = true;
                renderer.gameObject.layer = 0;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                if (renderer is SkinnedMeshRenderer skinned)
                    skinned.updateWhenOffscreen = true;
                foreach (var material in renderer.materials)
                {
                    if (material != null && material.name.Contains("Health"))
                        _healthBars.Add(material);
                    else if (material != null && material.HasProperty("_AlbedoColor"))
                        _tints.Add((material, material.GetColor("_AlbedoColor")));
                }
            }

            _animator = model.GetComponent<Animator>();
            if (_animator != null)
            {
                _animator.applyRootMotion = false;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var parameter in _animator.parameters)
                    _animatorParameters.Add(parameter.name);
                _animator.Update(0f);
            }

            // The model's origin is not at its feet: lower or lift it so its lowest point is on the ground.
            var lowest = float.MaxValue;
            foreach (var skinned in model.GetComponentsInChildren<SkinnedMeshRenderer>(false))
                lowest = Mathf.Min(lowest, skinned.bounds.min.y);
            if (lowest < float.MaxValue && Mathf.Abs(lowest - transform.position.y) < 3f)
                model.transform.localPosition = new Vector3(0f, transform.position.y - lowest, 0f);
            Hunted.Logger.LogDebug($"Bot model built ({_tints.Count} coloured materials, {_healthBars.Count} health bars, {_animatorParameters.Count} animator parameters, lifted {model.transform.localPosition.y:0.00} m).");
            return true;
        }
        catch (Exception e)
        {
            Hunted.Logger.LogWarning($"Could not copy the player model for a bot ({e.Message}); it is a capsule.");
            return false;
        }
    }

    // Sound components, the filters first: they cannot outlive the AudioSource they sit on.
    private static void RemoveAudio(GameObject model)
    {
        foreach (var behaviour in model.GetComponentsInChildren<Behaviour>(true))
            if (behaviour is AudioLowPassFilter or AudioHighPassFilter or AudioReverbFilter or AudioEchoFilter or AudioDistortionFilter or AudioChorusFilter)
                Object.DestroyImmediate(behaviour);
        foreach (var source in model.GetComponentsInChildren<AudioSource>(true))
            Object.DestroyImmediate(source);
    }

    // Removes the model's scripts, those that others depend on last.
    private static void StripScripts(GameObject model)
    {
        for (var pass = 0; pass < 6; pass++)
        {
            var scripts = model.GetComponentsInChildren<MonoBehaviour>(true).Where(m => m != null).ToList();
            if (scripts.Count == 0)
                return;
            var needed = new HashSet<Type>();
            foreach (var script in scripts)
                foreach (var attribute in script.GetType().GetCustomAttributes<RequireComponent>(true))
                    foreach (var type in new[] { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 })
                        if (type != null)
                            needed.Add(type);
            foreach (var script in scripts)
                if (!needed.Any(t => t.IsInstanceOfType(script)))
                    Object.DestroyImmediate(script);
        }
    }

    private void BuildCapsule()
    {
        var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        visual.name = "Look";
        Object.Destroy(visual.GetComponent<Collider>());
        visual.transform.SetParent(transform, false);
        var renderer = visual.GetComponent<Renderer>();
        renderer.material.color = new Color(0.2f, 0.9f, 0.3f);
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _tints.Add((renderer.material, renderer.material.color));
        visual.transform.localScale = new Vector3(0.6f, 0.85f, 0.6f);
        visual.transform.localPosition = new Vector3(0f, 0.85f, 0f);
    }

    // --- Footsteps and the death head --------------------------------------------------------------------------------------------

    /// <summary>A step, called by the walking animations like for a player: the floor's own sound, and monsters nearby may investigate it.</summary>
    internal void Footstep(Materials.SoundType type)
    {
        if (Tumbling || Materials.Instance == null)
            return;
        var position = transform.position;
        Materials.Instance.Impulse(position, Vector3.down, type, true, true, _materialTrigger, Materials.HostType.OtherPlayer);
        if (SemiFunc.IsMasterClientOrSingleplayer() && Time.time >= _investigateAt && EnemyDirector.instance != null)
        {
            _investigateAt = Time.time + 0.5f;
            if (type == Materials.SoundType.Heavy)
                EnemyDirector.instance.SetInvestigate(position + Vector3.up * 0.2f, 5f);
            else if (type == Materials.SoundType.Medium)
                EnemyDirector.instance.SetInvestigate(position + Vector3.up * 0.2f, 1f);
        }
    }

    /// <summary>What a killed player leaves: the game's own death head, as a real object every player can see, grab and carry. In a truck, or in an extraction point when it completes, it brings the bot back.</summary>
    private void LeaveHead()
    {
        var at = (_tumbled ? _lastGround : TargetPosition) + Vector3.up * 0.5f; // not down a hole it fell into: somebody has to be able to reach it
        var prefab = LevelGenerator.Instance != null ? LevelGenerator.Instance.PlayerDeathHeadPrefab : null;
        if (prefab == null)
        {
            Hunted.Logger.LogWarning($"No death head to leave for {Name}.");
            return;
        }
        GameObject head;
        if (GameManager.Multiplayer())
        {
            head = PhotonNetwork.InstantiateRoomObject(prefab.name, at, Quaternion.identity, 0, new object[] { 1f, 1f, 1f, BotHeadPatches.Marker, Slot, ColourToInt(_colour) } /* the game reads the first three as the object's scale */);
        }
        else
        {
            BotHeadPatches.OfflineSpawn = (Slot, _colour);
            head = Object.Instantiate(prefab, at, Quaternion.identity);
        }
        Heads[Slot] = head;
        Hunted.Logger.LogDebug($"{Name} left a death head at {at}.");
    }

    private static PlayerAvatar? PrefabAvatar
        => PlayerController.instance != null && PlayerController.instance.playerAvatarPrefab != null ? PlayerController.instance.playerAvatarPrefab.GetComponent<PlayerAvatar>() : null;

    private static Sound? HurtSound
        => PlayerController.instance != null && PlayerController.instance.playerAvatarPrefab != null
            ? PlayerController.instance.playerAvatarPrefab.GetComponentInChildren<PlayerHealth>(true)?.hurtOther
            : null;

    private static void PlaySound(Sound? sound, Vector3 at)
    {
        if (sound != null && sound.Sounds != null && sound.Sounds.Length > 0)
            sound.Play(at);
    }

    internal static void PlayDeathSound(Vector3 at) => PlaySound(PrefabAvatar?.deathSound, at);

    // The gold pulse every boosted survivor has, on everybody's screen.
    private void ShowBoost()
    {
        var boosted = Time.time < _boostUntil;
        if (!boosted && !_glowing)
            return;
        _glowing = boosted;
        var amount = boosted ? Mathf.Lerp(0.25f, 0.75f, Mathf.PingPong(Time.time * 2f, 1f)) : 0f;
        foreach (var (material, _) in _tints)
        {
            if (material == null)
                continue;
            if (material.HasProperty("_ColorOverlay"))
                material.SetColor("_ColorOverlay", SurvivorBoost.Gold);
            if (material.HasProperty("_ColorOverlayAmount"))
                material.SetFloat("_ColorOverlayAmount", amount);
        }
    }

    private void SetAnimator(string name, bool value)
    {
        if (_animator != null && _animatorParameters.Contains(name))
            _animator.SetBool(name, value);
    }

    // The shape of its collision; the model follows through the animator.
    private void ApplyShape()
    {
        var tumbled = Tumbling;
        var height = tumbled ? 0.4f : Stance == StanceKind.Stand ? 1.7f : Stance == StanceKind.Crouch ? 0.9f : 0.45f;
        var radius = tumbled ? 0.2f : Stance == StanceKind.Crawl ? 0.25f : 0.3f;
        _collider.height = height;
        _collider.center = new Vector3(0f, height / 2f, 0f);
        _collider.radius = radius;
    }

    private void ShowFlash(bool on)
    {
        foreach (var (material, color) in _tints)
            if (material != null)
            {
                if (material.HasProperty("_AlbedoColor"))
                    material.SetColor("_AlbedoColor", on ? Color.red : color);
                else if (material.HasProperty("_Color"))
                    material.SetColor("_Color", on ? Color.red : color);
            }
    }

    private bool _animSprinting;

    // The bar on the back of the head, as the game draws a player's: how far the texture is shifted, and its colour.
    private void ShowHealth()
    {
        if (_healthBars.Count == 0 || Mathf.Approximately(_healthShown, Health))
            return;
        _healthShown = Health;
        var fraction = Mathf.Clamp01(Health / Mathf.Max(1f, HuntedConfig.MaxHealth.Value));
        var offset = Mathf.Lerp(0.98f, 0f, fraction);
        if (offset <= 0f)
            offset = -0.5f;
        foreach (var material in _healthBars)
        {
            if (material == null)
                continue;
            material.SetFloat("_OffsetX", offset);
            if (_healthColours == null)
                continue;
            var colour = _healthColours.Evaluate(fraction);
            material.SetColor("_AlbedoColor", colour);
            colour.a = material.GetColor("_EmissionColor").a;
            material.SetColor("_EmissionColor", colour);
        }
    }

    private void UpdateAnimator(bool moving)
    {
        if (_animator == null)
            return;
        var tumbling = Tumbling;
        SetAnimator("Crouching", Stance != StanceKind.Stand || tumbling);
        SetAnimator("Crawling", Stance == StanceKind.Crawl || tumbling);
        SetAnimator("Tumbling", tumbling);
        SetAnimator("Moving", moving && !tumbling);
        // Like a player's: the sprint starts with a trigger, the bool only keeps it going. Upright only, there is no crouched sprint.
        var sprinting = moving && _sprinting && !tumbling && Stance == StanceKind.Stand;
        if (sprinting && !_animSprinting && _animatorParameters.Contains("SprintingImpulse"))
            _animator.SetTrigger("SprintingImpulse");
        _animSprinting = sprinting;
        SetAnimator("Sprinting", sprinting);
        SetAnimator("Grabbing", _carrying != null || _carriedHead >= 0 || _netGrabbing);
    }

    // --- Each frame -------------------------------------------------------------------------------------------------------------

    private void Update()
    {
        if (_puppet)
            PuppetUpdate();
        else
            HostUpdate();

        if (_flash > 0f)
        {
            if (_flash == 0.3f)
                ShowFlash(true);
            _flash -= Time.deltaTime;
            if (_flash <= 0f)
            {
                _flash = 0f;
                ShowFlash(false);
            }
        }
        if (_shapeShown != (Stance, Tumbling))
        {
            _shapeShown = (Stance, Tumbling);
            ApplyShape();
        }
        UpdateAnimator(_moving);
        ShowHealth();
        ShowBoost();
    }

    private void HostUpdate()
    {
        Health = Mathf.Min(HuntedConfig.MaxHealth.Value, Health + HuntedConfig.HealthRegenPerSecond.Value * Time.deltaTime);
        if (Health > HuntedConfig.MaxHealth.Value * Tuning.Boost.HealthFraction)
            _wasHealthy = true;

        // What the ceiling allows: a low table or bed forces it down, as it does a real survivor.
        _coverTimer -= Time.deltaTime;
        if (_coverTimer <= 0f)
        {
            _coverTimer = 0.2f;
            var mask = (int)SemiFunc.LayerMaskGetVisionObstruct();
            _forced = Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.up, out var above, 1.9f, mask, QueryTriggerInteraction.Ignore)
                      && above.collider.GetComponentInParent<Bot>() == null
                ? (above.distance < 0.85f ? StanceKind.Crawl : StanceKind.Crouch)
                : StanceKind.Stand;
        }

        _truckAt -= Time.deltaTime;
        if (_truckAt <= 0f)
        {
            _truckAt = 0.5f;
            InTruck = Trucks().Any(b => b.Contains(transform.position + Vector3.up * 0.5f));
        }

        _sprinting = false;
        if (_tumbled)
            UpdateTumble();
        else
        {
            if (!_offMesh)
                _lastGround = transform.position;
            Behave();
        }

        Stance = (StanceKind)Mathf.Max((int)_wanted, (int)_forced);
        _moving = !_tumbled && (_sliding || (_agent != null && _agent.enabled && _agent.isOnNavMesh && _agent.velocity.sqrMagnitude > 0.04f));
        CarryValuable();
        CarryHead();
    }

    private void SetMode(Mode mode, float seconds)
    {
        _mode = mode;
        _modeUntil = Time.time + seconds;
        _repathAt = 0f;
        if (mode != Mode.Hide)
            _spot = null;
        _wanted = StanceKind.Stand;
    }

    private float _truckAt;
    private float _noteAt;
    private void DebugNote(string what)
    {
        if (Time.time < _noteAt)
            return;
        _noteAt = Time.time + 3f;
        Hunted.Logger.LogDebug($"{Name} {what}.");
    }

    /// <summary>The trucks' volumes, a little wider than they are (the doors and the ramp), so a bot standing at a truck counts as in it.</summary>
    private static List<Bounds> Trucks()
    {
        if (Time.time - _trucksAt < 3f && _trucks.Count > 0)
            return _trucks;
        _trucksAt = Time.time;
        _trucks = new List<Bounds>();
        foreach (var volume in Object.FindObjectsByType<RoomVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!volume.Truck)
                continue;
            var colliders = volume.GetComponentsInChildren<Collider>(true);
            if (colliders.Length == 0)
                continue;
            var bounds = colliders[0].bounds;
            foreach (var collider in colliders)
                bounds.Encapsulate(collider.bounds);
            bounds.Expand(new Vector3(2.5f, 1f, 2.5f));
            _trucks.Add(bounds);
        }
        // The truck's own walking points count too: as far as a walking bot can get.
        var points = LevelGenerator.Instance != null ? LevelGenerator.Instance.LevelPathPoints : null;
        if (points != null)
            foreach (var point in points)
                if (point != null && point.Truck)
                    _trucks.Add(new Bounds(point.transform.position, new Vector3(4f, 3f, 4f)));
        return _trucks;
    }

    // --- Carrying a valuable ----------------------------------------------------------------------------------------------------

    private void PickUp(ValuableObject valuable)
    {
        _carrying = valuable;
        foreach (var collider in valuable.GetComponentsInChildren<Collider>())
            Physics.IgnoreCollision(collider, _collider, true);
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
        foreach (var collider in valuable.GetComponentsInChildren<Collider>())
            if (_collider != null)
                Physics.IgnoreCollision(collider, _collider, false);
    }

    // The valuable floats in front of its chest, pulled there by physics so it still behaves like an object.
    private void CarryValuable()
    {
        var valuable = _carrying;
        if (valuable == null)
            return;
        var body = valuable.GetComponent<Rigidbody>();
        var grabbed = valuable.physGrabObject != null && valuable.physGrabObject.grabbed;
        var hand = transform.position + transform.forward * 0.7f + Vector3.up * (Stance == StanceKind.Stand ? 1.0f : 0.5f);
        if (body == null || grabbed)
        {
            Hunted.Logger.LogDebug($"{Name} lost its valuable (somebody took it).");
            DropCarried();
            return;
        }
        if (valuable.physGrabObject != null)
            BotCarry.HoldAt(valuable.physGrabObject, hand);
    }
}

/// <summary>
/// Receives what the player model's animations call (footsteps, crouch sounds, foot springs). A bot has none of the player's scripts, so without this the
/// animations would complain about every call; only the footsteps do anything.
/// </summary>
internal class BotAnimationEvents : MonoBehaviour
{
    internal Bot? Bot;

    public void FootstepLight() => Bot?.Footstep(Materials.SoundType.Light);
    public void FootstepMedium() => Bot?.Footstep(Materials.SoundType.Medium);
    public void FootstepHeavy() => Bot?.Footstep(Materials.SoundType.Heavy);

    public void StandToCrouch() { }
    public void CrouchToStand() { }
    public void CrouchToCrawl() { }
    public void CrawlToCrouch() { }
    public void RightFootUp() { }
    public void RightFootUpSlow() { }
    public void RightFootDown() { }
    public void RightFootDownSlow() { }
    public void LeftFootUp() { }
    public void LeftFootUpSlow() { }
    public void LeftFootDown() { }
    public void LeftFootDownSlow() { }
    public void JumpImpulse() { }
    public void PhysRidingCheck() { }
    public void Revive() { }
    public void PowerupJumpEffect() { }
    public void TumbleBreakFreeEffect() { }
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
