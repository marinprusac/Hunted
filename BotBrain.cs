using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace Hunted;

/// <summary>
/// What the bots share and what the killer's movement looks like from the outside: the last sighting any bot reported (bots "talk", so the others know a moment later),
/// and how fast the killer is going (a killer standing still makes no noise).
/// </summary>
internal static class BotIntel
{
    internal static Vector3 KillerVelocity { get; private set; }
    internal static float KillerSpeed => KillerVelocity.magnitude;
    internal static Vector3 ReportedPosition { get; private set; }
    internal static Vector3 ReportedVelocity { get; private set; }
    internal static float ReportedAt { get; private set; } = -100f;

    private static Vector3 _previous;
    private static bool _hasPrevious;

    internal static void Reset()
    {
        KillerVelocity = Vector3.zero;
        ReportedAt = -100f;
        _hasPrevious = false;
    }

    /// <summary>Where the killer's eyes are and which way they look, wherever the killer is playing (a remote player's camera is synced by the game).</summary>
    internal static bool HasEyes { get; private set; }
    internal static Vector3 EyePosition { get; private set; }
    internal static Vector3 EyeForward { get; private set; } = Vector3.forward;

    internal static void Tick(float dt)
    {
        var killer = SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p));
        HasEyes = killer != null && !killer.isDisabled;
        if (!HasEyes)
            return;
        Transform? eyes = null;
        if (killer!.isLocal)
            eyes = CameraUtils.Instance != null && CameraUtils.Instance.MainCamera != null ? CameraUtils.Instance.MainCamera.transform : null;
        else if (killer.localCamera != null)
            eyes = killer.localCamera.GetOverrideTransform();
        if (eyes == null)
        {
            HasEyes = false;
            return;
        }
        EyePosition = eyes.position;
        EyeForward = eyes.forward;
        if (_hasPrevious && dt > 0f)
        {
            var velocity = (EyePosition - _previous) / dt;
            velocity.y = 0f;
            KillerVelocity = Vector3.Lerp(KillerVelocity, Vector3.ClampMagnitude(velocity, 15f), Mathf.Clamp01(dt * 6f));
        }
        _previous = EyePosition;
        _hasPrevious = true;
    }

    internal static void Report(Vector3 position, Vector3 velocity)
    {
        ReportedPosition = position;
        ReportedVelocity = velocity;
        ReportedAt = Time.time;
    }

    /// <summary>How pressed for time the survivors are: 0 is plenty, 1 is the last 90 seconds (or the trucks are open: leave).</summary>
    internal static float Urgency
    {
        get
        {
            if (ExtractionState.TrucksOpen)
                return 1f;
            var left = EscapeRule.SecondsLeft;
            if (left == null)
                return 0f;
            var total = Mathf.Max(90f, HuntedConfig.TimeLimitMinutes.Value * 60f);
            return Mathf.Clamp01(1f - ((float)left.Value - 90f) / (total - 90f + 1f));
        }
    }
}

/// <summary>
/// The bots' mind. They have what a good survivor has: they know how the killer's eyes work (how fast a crouching or crawling survivor gets picked up, from how far away and how
/// centred in the view), so they pick the stance that keeps them unseen and move or freeze accordingly. They only know what they see, hear or are told. They choose routes the
/// killer cannot cut off, and real hiding places (see BotHideSpots) out of the killer's sight; they save their stamina for when it matters, keep away from lit and unlit bomber
/// heads, stay out of the killer room, and split the work: they bring the valuables worth the walk to the extraction point closest to done, without overfilling one.
/// </summary>
internal partial class Bot
{
    private static readonly Vector3[] Corners = new Vector3[64];
    private static NavMeshPath? _scratch; // made on first use: Unity does not allow it while a behaviour is being constructed
    private readonly List<(Vector3 position, bool lit)> _bangs = new();

    /// <summary>What blocks a bot's sight. Not bodies: a survivor or the killer does not hide what is behind them, or themselves.</summary>
    private static int SightMask => _sightMask != 0 ? _sightMask : _sightMask = (int)SemiFunc.LayerMaskGetVisionObstruct() & ~LayerMask.GetMask("Player");
    private static int _sightMask;

    private readonly HashSet<PlayerDeathHead> _headsSeen = new();
    private PlayerDeathHead? _targetHead;
    private PlayerDeathHead? _carriedHead;
    private float _thinkAt;
    private float _glanceAt;
    private bool _seen;
    private bool _heard;
    private float _seenAt = -100f;
    private float _spottedUntil;
    private float _hurtAt = -100f;
    private Vector3 _killerVelocity;
    private Vector3 _killerLook = Vector3.forward;
    private Vector3 _dangerBang;
    private bool _fleeingBang;

    private BotHideSpots.Spot? _spot;
    private readonly HashSet<BotHideSpots.Spot> _badSpots = new();
    private bool _settled;
    private bool _offMesh; // crawled off the walking mesh, into a spot or on the way back out
    private bool _sliding;
    private Vector3 _offMeshEntry;
    private Vector3 _offMeshSpot;
    private bool _jogging;
    private bool _routed;
    private Vector3 _pointWalk;
    private Vector3? _restSpot;
    private ValuableObject? _reclaim;
    private ExtractionPoint? _avoidPoint;
    private float _avoidUntil;
    private Vector3 _progressFrom;
    private float _progressAt;
    private int _stuck;
    private float _scanYaw;
    private float _scanAt;

    private const float RunWithinMetres = 10f;

    private float MemorySeconds => Tuning.Bots.MemorySeconds;
    private float BoostLeft => Mathf.Max(0f, _boostUntil - Time.time);
    /// <summary>The boost is worth playing around while there is more of it left than it takes to change plans.</summary>
    private bool Boosted => BoostLeft > 1.5f;
    private bool KillerKnown => Time.time - _lastNoticed < MemorySeconds;

    private static float WalkSpeed
    {
        get
        {
            var controller = PlayerController.instance;
            return controller != null && controller.playerOriginalMoveSpeed > 0f ? controller.playerOriginalMoveSpeed : 2f;
        }
    }

    private static float RunSpeed
    {
        get
        {
            var controller = PlayerController.instance;
            return controller != null && controller.playerOriginalSprintSpeed > 0f ? controller.playerOriginalSprintSpeed : WalkSpeed * 2f;
        }
    }

    private static float SprintDrain
    {
        get
        {
            var controller = PlayerController.instance;
            return controller != null ? Mathf.Max(0.01f, controller.EnergySprintDrain + controller.SprintSpeedUpgrades) : 1f;
        }
    }

    /// <summary>Its stamina is a player's: seconds of sprinting it holds, and how many of them it gets back per second.</summary>
    private static float StaminaSeconds => (PlayerController.instance != null ? PlayerController.instance.EnergyStart : Tuning.Survivor.Stamina) / SprintDrain;
    private static float StaminaRecovery => (PlayerController.instance != null ? PlayerController.instance.sprintRechargeAmount : 2f) / SprintDrain;

    private static float KillerTopSpeed => WalkSpeed * HuntedConfig.KillerWalkSpeed;

    /// <summary>How close the killer could be by now: where they were last noticed, less how far they may have walked since (half speed: they rarely come straight here).</summary>
    private float KillerDistance
    {
        get
        {
            if (!KillerKnown)
                return float.PositiveInfinity;
            var age = Mathf.Min(Time.time - _lastNoticed, 5f);
            return Mathf.Max(0f, Vector3.Distance(transform.position, _lastKnown) - age * KillerTopSpeed * 0.5f);
        }
    }

    // --- What it senses ---------------------------------------------------------------------------------------------------------

    private Vector3 EyeFor(StanceKind stance) => transform.position + Vector3.up * (stance == StanceKind.Stand ? 1.5f : stance == StanceKind.Crouch ? 0.8f : 0.3f);

    /// <summary>
    /// Sight (in front of it, nothing in between, and now and then it glances around), and sound: a killer on the move is heard at the full range, also through walls; one who stands still only from close by.
    /// What another bot reported reaches it a moment later.
    /// </summary>
    private void Sense()
    {
        var eye = EyeFor(Stance);
        var killer = BotIntel.HasEyes ? BotIntel.EyePosition : eye + Vector3.up * 1000f;
        var delta = killer - eye;
        var distance = delta.magnitude;
        // The killer's steps are heavy: full range when they walk at speed, less when they creep, close by only when they stand still.
        var speed = BotIntel.KillerSpeed;
        _heard = distance <= Tuning.Bots.HearRange * (speed > 2.5f ? 1f : speed > 0.4f ? 0.6f : 0.2f);
        _seen = false;
        // It keeps checking over its shoulder, more often when it knows the killer is about, and it looks round at once at a sound, or at somebody right behind it.
        var glance = _heard || distance < 3f || Time.time >= _glanceAt;
        if (glance)
            _glanceAt = Time.time + (KillerKnown ? Random.Range(0.6f, 1.2f) : Random.Range(1f, 2.5f));
        if (distance <= Tuning.Bots.SightRange && distance > 0.01f && (glance || Vector3.Dot(transform.forward, delta / distance) >= 0.25f))
            _seen = !Physics.Linecast(eye, killer, SightMask, QueryTriggerInteraction.Ignore);

        if (_seen || _heard)
        {
            _lastKnown = killer;
            _lastNoticed = Time.time;
            _killerVelocity = BotIntel.KillerVelocity;
            BotIntel.Report(killer, _killerVelocity);
        }
        else if (BotIntel.ReportedAt > _lastNoticed && Time.time - BotIntel.ReportedAt >= 1f)
        {
            _lastKnown = BotIntel.ReportedPosition;
            _killerVelocity = BotIntel.ReportedVelocity;
            _lastNoticed = BotIntel.ReportedAt;
        }
        if (_seen)
        {
            _seenAt = Time.time;
            _killerLook = BotIntel.EyeForward;
        }

        // A fallen survivor's head, if it comes into view, is remembered until it is gone.
        foreach (var head in FallenHeads())
        {
            if (_headsSeen.Contains(head))
                continue;
            var toHead = head.transform.position - eye;
            var far = toHead.magnitude;
            if (far > Tuning.Bots.SightRange || far < 0.01f || !(glance || Vector3.Dot(transform.forward, toHead / far) >= 0.25f))
                continue;
            if (!Physics.Linecast(eye, eye + toHead / far * Mathf.Max(0f, far - 0.4f), SightMask, QueryTriggerInteraction.Ignore))
            {
                _headsSeen.Add(head);
                DebugNote($"sees the head of {head.playerAvatar.playerName}");
            }
        }
        _headsSeen.RemoveWhere(head => head == null || !head.triggered);

        // The Everseer's eyes, if it notices one: it destroys it (see BotEyes).
        NoticeEyes(eye, glance);
    }

    /// <summary>
    /// The killer's eyes as a good player knows them: seconds until a survivor in this stance would be picked up with the killer looking the way they are now.
    /// Standing is at once if it is anywhere in their view; never when the charge cannot beat the decay.
    /// </summary>
    private float SpotTime(StanceKind stance)
    {
        var direction = EyeFor(stance) - _lastKnown;
        var distance = direction.magnitude;
        if (distance < 0.01f)
            return 0f;
        var dot = Mathf.Clamp01(Vector3.Dot(_killerLook, direction / distance));
        if (stance == StanceKind.Stand || KillerRoomRules.CannotHideAt(transform.position))
            return dot > 0.35f ? 0f : float.PositiveInfinity;
        var vision = Tuning.Vision;
        if (distance >= vision.ProximityRange)
            return float.PositiveInfinity;
        var proximity = Mathf.Pow(1f - distance / vision.ProximityRange, 2f);
        var net = vision.ChargePerSecond * dot * dot * proximity - vision.DecayPerSecond;
        if (net <= 0f)
            return float.PositiveInfinity;
        return (stance == StanceKind.Crawl ? vision.CrawlThreshold : vision.CrouchThreshold) / net;
    }

    private bool KillerApproaching
    {
        get
        {
            var toMe = transform.position - _lastKnown;
            toMe.y = 0f;
            return _killerVelocity.magnitude > 0.8f && toMe.sqrMagnitude > 0.01f && Vector3.Dot(_killerVelocity.normalized, toMe.normalized) > 0.6f;
        }
    }

    private bool NearestBang(out Vector3 position)
    {
        position = default;
        var best = float.MaxValue;
        foreach (var (at, lit) in _bangs)
        {
            var distance = Vector3.Distance(at, transform.position);
            if (distance < (lit ? 10f : 7f) && distance < best)
            {
                best = distance;
                position = at;
            }
        }
        return best < float.MaxValue;
    }

    // --- What it decides --------------------------------------------------------------------------------------------------------

    private void Decide()
    {
        var now = Time.time;
        Banger.CollectThreats(_bangs);
        // Other monsters are kept away from like an unlit bomber head: it runs when they get close.
        if (EnemyDirector.instance != null)
            foreach (var parent in EnemyDirector.instance.enemiesSpawned)
                if (parent != null && parent.Spawned && parent.Enemy != null && parent.Enemy.CenterTransform != null && parent.GetComponentInChildren<BangerTag>(true) == null)
                    _bangs.Add((parent.Enemy.CenterTransform.position, false));

        // A bomber head comes before anything else: nothing hides from one.
        if (NearestBang(out var bang))
        {
            _dangerBang = bang;
            _fleeingBang = true;
            if (_mode != Mode.Flee)
            {
                DebugNote("runs from a bomber head or monster");
                StartFlee(3f);
            }
            else
                _modeUntil = Mathf.Max(_modeUntil, now + 1f);
            return;
        }
        _fleeingBang = false;

        // In a truck it has done its part.
        if (_mode == Mode.Rest)
            return;

        var distance = KillerKnown ? Vector3.Distance(transform.position, _lastKnown) : float.PositiveInfinity;

        if (now - _hurtAt < Tuning.Vision.HitRevealSeconds)
            _spottedUntil = now + 3f; // somebody who was hurt cannot hide
        if (_seen)
        {
            var spotTime = SpotTime(Stance);
            // Close up there is no sneaking away, whichever way the killer is looking: it runs. Only a bot already in its hiding place stays, and trusts it.
            if (spotTime < 0.4f || (distance < RunWithinMetres && !(_mode == Mode.Hide && _settled)))
                _spottedUntil = now + 3f;
        }
        var spotted = now < _spottedUntil && (_seen || now - _seenAt < 2f || distance < 10f);

        if (spotted)
        {
            if (_mode != Mode.Flee)
            {
                DebugNote("was spotted and runs");
                StartFlee(5f);
            }
            else
                _modeUntil = Mathf.Max(_modeUntil, now + 2f);
            // The killer is on its heels: the weight goes. It comes back for it later.
            if ((_carrying != null || _carriedHead != null) && distance < 10f)
                DropCarried();
            return;
        }

        // Boosted it runs flat out without tiring and nothing tumbles it: no time to hide, this is the moment to get far away. It goes to ground when the boost is about over.
        if (Boosted && distance < 30f && _mode != Mode.Flee && _mode != Mode.Escape)
        {
            DebugNote("uses its boost to get away");
            StartFlee(BoostLeft);
            return;
        }

        switch (_mode)
        {
            case Mode.Flee:
                if (Boosted && distance < 35f)
                    return;
                // Out of sight: run on until the time is up, then go to ground if the killer is still about.
                if (now >= _modeUntil && now - _seenAt > 2f)
                {
                    // Only with the killer really close and heard of just now. Otherwise the job goes on: survivors who hide at every scare never finish anything.
                    if (distance < 14f && now - _lastNoticed < 5f && !_fleeingBang && BotIntel.Urgency < 1f)
                        StartHide();
                    else
                        SetMode(Mode.Idle, 0.2f);
                }
                return;
            case Mode.Hide:
                if (_seen && SpotTime(StanceKind.Crawl) < 3f)
                {
                    _spottedUntil = now + 3f; // this hiding place will not hold: go now, not when it is too late
                    return;
                }
                // Stays put while the killer is right there; a killer on the move is what gives it the chance to slip away.
                if (_heard && distance < 8f && BotIntel.Urgency < 1f)
                    _modeUntil = Mathf.Max(_modeUntil, now + 2f);
                if (now >= _modeUntil)
                    SetMode(Mode.Idle, 0.2f);
                return;
        }

        // The trucks are open: leave. What is in its hands is no use any more.
        if (ExtractionState.TrucksOpen && _mode != Mode.Escape && _mode != Mode.FetchHead && _mode != Mode.BringHead && !FindHead(out _))
        {
            DropCarried(keepHead: true); // a head goes to the truck anyway
            _reclaim = null;
            _target = null;
            _point = null;
            SetMode(Mode.Escape, 180f);
        }

        // The killer is in view but has not picked it up yet: the stance that keeps it that way, or freeze, or leave.
        if (_seen)
        {
            if (SpotTime(StanceKind.Crouch) > 3f && !(KillerApproaching && distance < 14f))
                return; // working on, crouched (see WorkStance)
            // Keeping still changes nothing for the killer's eyes: it makes for real cover instead.
            if (SpotTime(StanceKind.Crawl) > 3f)
            {
                DebugNote("makes for cover");
                StartHide();
                return;
            }
            _spottedUntil = now + 3f;
            return;
        }

        // Near but out of sight, and coming this way: out of the way before they arrive.
        // Nobody is left behind: what it was fetching or wandering about for can wait.
        if ((_mode == Mode.Scavenge || _mode == Mode.Wander || _mode == Mode.Idle) && _carrying == null && _carriedHead == null && FindHead(out var head))
        {
            StartFetchHead(head);
            return;
        }

        var almostDelivered = _mode == Mode.Deliver && _agent != null && _agent.isOnNavMesh && _agent.remainingDistance < 12f;
        // Too close for hiding: it runs while it still has the lead.
        if (_heard && KillerApproaching && distance < 8f)
        {
            DebugNote("hears the killer right behind it");
            StartFlee(4f);
            return;
        }
        if (_heard && KillerApproaching && distance < 12f && BotIntel.Urgency < 1f && !almostDelivered)
        {
            DebugNote("hears the killer coming");
            StartHide();
            return;
        }
    }

    private void StartFlee(float seconds)
    {
        SetMode(Mode.Flee, seconds);
        _settled = false;
        _routed = false;
        _target = null;
    }

    /// <summary>Off to a real hiding spot. With none in reach it keeps moving rather than crouching in the open.</summary>
    private void StartHide()
    {
        var spot = FindHideSpot();
        if (spot == null)
        {
            DebugNote("finds nowhere to hide and keeps moving");
            StartFlee(3f);
            return;
        }
        SetMode(Mode.Hide, 60f); // the real time starts when it is in
        DropCarried(); // a valuable floating about gives it away; it is still there afterwards
        _target = null;
        _spot = spot;
        _settled = false;
        GoTo(spot.Entry);
        DebugNote($"goes to hide ({spot.Kind}, {Vector3.Distance(transform.position, spot.Position):0} m away)");
    }

    private void SettleIn()
    {
        _settled = true;
        _sliding = false;
        _modeUntil = Time.time + Random.Range(0.7f, 1.3f) * Tuning.Bots.HideSeconds * Mathf.Lerp(1f, 0.3f, BotIntel.Urgency);
    }

    // The last stretch into a gap, or back out, is a straight crawl: the walking mesh does not go there.
    private void ActOffMesh()
    {
        var inside = _mode == Mode.Hide;
        _wanted = StanceKind.Crawl;
        if (inside && _settled)
        {
            Watch();
            return;
        }
        var target = inside ? _offMeshSpot : _offMeshEntry;
        // Scrambling out is quicker than creeping in, and upright the moment there is room.
        var speed = WalkSpeed * (inside ? 0.5f : 0.8f);
        transform.position = Vector3.MoveTowards(transform.position, target, speed * Time.deltaTime);
        var direction = target - transform.position;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), 360f * Time.deltaTime);
        _sliding = true;
        if ((transform.position - target).sqrMagnitude > 0.0025f)
            return;
        _sliding = false;
        if (inside)
        {
            SettleIn();
            return;
        }
        _offMesh = false;
        _agent!.enabled = true;
        _agent.Warp(target);
    }

    private bool RouteHolds()
        => _agent != null && _agent.hasPath && TryPath(_agent.destination, out var corners, out _) && PathRisk(corners, RunSpeed) < 10f;

    /// <summary>Upright when nothing is near; crouched when the killer may be close, so that they do not see it the moment they turn a corner.</summary>
    private StanceKind WorkStance()
    {
        if (Boosted)
            return StanceKind.Stand; // speed is what the boost is for
        if (_seen)
            return StanceKind.Crouch;
        if (KillerDistance < 14f) // beyond about 13 m a crouching survivor is not picked up at all, but a standing one is at any distance
            return BotIntel.Urgency >= 1f && Vector3.Distance(transform.position, _lastKnown) > 10f ? StanceKind.Stand : StanceKind.Crouch;
        return StanceKind.Stand;
    }

    // --- What it does -----------------------------------------------------------------------------------------------------------

    private void Behave()
    {
        if (_offMesh)
        {
            _knock = Vector3.zero;
            if (!Tuning.Bots.Moves || _agent == null)
                return;
            if (Time.time >= _thinkAt)
            {
                _thinkAt = Time.time + Random.Range(0.15f, 0.25f);
                Sense();
                Decide();
            }
            ActOffMesh();
            _stamina = Mathf.Min(StaminaSeconds, _stamina + Time.deltaTime * StaminaRecovery);
            return;
        }

        // Knocked back by a hit.
        if (_knock.sqrMagnitude > 0.01f)
        {
            if (_agent != null && _agent.isOnNavMesh)
                _agent.Move(_knock * Time.deltaTime);
            else
                transform.position += _knock * Time.deltaTime;
            _knock = Vector3.Lerp(_knock, Vector3.zero, 6f * Time.deltaTime);
        }

        var moving = Tuning.Bots.Moves && _agent != null && _agent.isOnNavMesh;
        if (!moving)
            return;
        _agent!.enabled = true;
        _agent.isStopped = false;

        // Not every frame, and not all at once.
        if (Time.time >= _thinkAt)
        {
            _thinkAt = Time.time + Random.Range(0.15f, 0.25f);
            Sense();
            Decide();
        }
        Act();
        CheckProgress();

        // Stamina: sprinting uses it, everything else gives it back; a boost never runs out.
        var boosted = Time.time < _boostUntil;
        if (boosted)
            _stamina = StaminaSeconds;
        else if (_sprinting)
        {
            _stamina = Mathf.Max(0f, _stamina - Time.deltaTime);
            if (_stamina <= 0f)
            {
                _sprinting = false;
                DebugNote("is out of stamina and can only walk");
            }
        }
        else
            _stamina = Mathf.Min(StaminaSeconds, _stamina + Time.deltaTime * StaminaRecovery);

        var stance = (StanceKind)Mathf.Max((int)_wanted, (int)_forced);
        var speed = stance == StanceKind.Crawl ? WalkSpeed * 0.35f : stance == StanceKind.Crouch ? WalkSpeed * 0.6f : _sprinting ? RunSpeed : WalkSpeed;
        if (_carrying != null)
            speed *= 0.85f;
        _agent.speed = speed;
        _agent.height = stance == StanceKind.Stand ? 1.7f : 0.9f;
    }

    private void Act()
    {
        var now = Time.time;
        var boosted = now < _boostUntil;
        var distance = KillerKnown ? Vector3.Distance(transform.position, _lastKnown) : float.PositiveInfinity;
        switch (_mode)
        {
            case Mode.Flee:
                _wanted = StanceKind.Stand;
                // Only burns stamina with the killer close or watching; out of sight it keeps some for later.
                _sprinting = boosted || (_stamina > 0f && (_fleeingBang || now - _seenAt < 3f || distance < 12f));
                if (InTruck && ExtractionState.TrucksOpen && !_fleeingBang)
                {
                    StartRest();
                    break;
                }
                if (now >= _repathAt || (Arrived(1.5f) && now >= _repathAt - 1.5f))
                {
                    _repathAt = now + 2f;
                    // A route that still holds is kept: changing its mind every few seconds is how a runner gets caught.
                    if (!_routed || Arrived(1.5f) || !RouteHolds())
                    {
                        _routed = true;
                        GoTo(PickRoute(run: true));
                    }
                }
                break;
            case Mode.Hide:
                _sprinting = false;
                if (_spot == null)
                {
                    SetMode(Mode.Idle, 0.2f);
                    break;
                }
                if (_settled)
                {
                    _wanted = StanceKind.Crawl;
                    _agent!.ResetPath();
                    Watch();
                    break;
                }
                // Getting there comes first: upright and quick, unless it is being watched from afar and low is what keeps it unseen.
                _wanted = _seen && distance >= RunWithinMetres ? StanceKind.Crouch : StanceKind.Stand;
                _sprinting = _wanted == StanceKind.Stand && _stamina > 0f && (distance < 15f || boosted);
                if (Arrived(0.4f))
                {
                    _agent!.ResetPath();
                    if (Vector3.Distance(transform.position, _spot.Position) < 0.3f)
                    {
                        _wanted = StanceKind.Crawl;
                        SettleIn();
                    }
                    else
                    {
                        _offMeshEntry = transform.position;
                        _offMeshSpot = _spot.Position;
                        _agent.enabled = false;
                        _offMesh = true;
                    }
                }
                break;
            case Mode.Idle:
                _sprinting = false;
                _wanted = WorkStance();
                _agent!.ResetPath();
                Watch();
                if (now >= _modeUntil)
                    ChooseTask();
                break;
            case Mode.Wander:
                _wanted = WorkStance();
                _sprinting = false;
                if (Arrived(1f) || now >= _modeUntil)
                    SetMode(Mode.Idle, Random.Range(1f, 2.5f));
                break;
            case Mode.Scavenge:
                _wanted = WorkStance();
                Jog(distance);
                ScavengeLogic();
                break;
            case Mode.Deliver:
                _wanted = WorkStance();
                Jog(distance);
                DeliverLogic();
                break;
            case Mode.Escape:
                _wanted = WorkStance();
                Jog(distance);
                EscapeLogic();
                break;
            case Mode.FetchHead:
                _wanted = WorkStance();
                Jog(distance);
                FetchHeadLogic();
                break;
            case Mode.BringHead:
                _wanted = WorkStance();
                Jog(distance);
                BringHeadLogic();
                break;
            case Mode.Rest:
                _wanted = StanceKind.Stand;
                if (!InTruck)
                {
                    SetMode(Mode.Escape, 180f); // knocked out of it
                    break;
                }
                // On to the back, where the killer's sword does not reach from the door; then it has done its part.
                if (_restSpot != null && !Arrived(0.4f))
                {
                    _sprinting = _stamina > 0f && distance < 12f;
                    if (now >= _repathAt)
                    {
                        _repathAt = now + 1f;
                        GoTo(_restSpot.Value);
                    }
                    break;
                }
                _sprinting = false;
                _agent!.ResetPath();
                Watch();
                break;
        }
    }

    // On long trips with nothing near it runs, but never below half its stamina, so there is always some left for a chase.
    private void Jog(float distance)
    {
        // A boost costs nothing to use: with the killer far away it goes into the job.
        if (Boosted)
        {
            _sprinting = _wanted == StanceKind.Stand;
            return;
        }
        var urgent = BotIntel.Urgency >= 1f;
        if (_stamina >= StaminaSeconds * 0.9f)
            _jogging = true;
        else if (_stamina <= StaminaSeconds * (urgent ? 0.15f : 0.5f))
            _jogging = false;
        _sprinting = _jogging && _wanted == StanceKind.Stand && (urgent || distance > 25f) && _agent != null && _agent.remainingDistance > 6f;
    }

    // Standing still it keeps its eyes open: towards where the killer was last noticed, or looking around.
    private void Watch()
    {
        if (Time.time >= _scanAt)
        {
            _scanAt = Time.time + Random.Range(1f, 2.5f);
            _scanYaw = Random.Range(0f, 360f);
        }
        var direction = KillerKnown ? _lastKnown - transform.position : Quaternion.Euler(0f, _scanYaw, 0f) * Vector3.forward;
        direction.y = 0f;
        if (direction.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(direction), 180f * Time.deltaTime);
    }

    // Walking but not getting anywhere (a doorway, a bad path): try again, and then give up on what it was after.
    private void CheckProgress()
    {
        if (Time.time < _progressAt)
            return;
        _progressAt = Time.time + 2f;
        var moved = (transform.position - _progressFrom).sqrMagnitude > 0.16f;
        _progressFrom = transform.position;
        var trying = _agent != null && _agent.hasPath && !_agent.pathPending && _agent.remainingDistance > 1f;
        if (moved || !trying)
        {
            _stuck = 0;
            return;
        }
        _stuck++;
        _repathAt = 0f;
        _agent!.ResetPath();
        if (_stuck < 2)
            return;
        _stuck = 0;
        DebugNote("is stuck and gives up on this");
        switch (_mode)
        {
            case Mode.Scavenge:
                if (_target != null)
                    _skipped.Add(_target);
                _target = null;
                SetMode(Mode.Idle, 0.2f);
                break;
            case Mode.Deliver:
                _avoidPoint = _point;
                _avoidUntil = Time.time + 20f;
                _point = null;
                SetMode(Mode.Idle, 0.2f);
                break;
            case Mode.FetchHead:
                if (_targetHead != null)
                    _headsSeen.Remove(_targetHead); // cannot get to it; if it comes into view again it gets another try
                _targetHead = null;
                SetMode(Mode.Idle, 0.2f);
                break;
            case Mode.Hide:
                if (_spot != null)
                    _badSpots.Add(_spot);
                StartHide();
                break;
        }
    }

    // --- Paths and how safe they are --------------------------------------------------------------------------------------------

    private bool TryPath(Vector3 destination, out int corners, out float length, float reach = 2.5f)
    {
        corners = 0;
        length = 0f;
        if (_agent == null || !_agent.isOnNavMesh || !NavMesh.SamplePosition(destination, out var hit, reach, -1))
            return false;
        _scratch ??= new NavMeshPath();
        if (!NavMesh.CalculatePath(transform.position, hit.position, -1, _scratch) || _scratch.status != NavMeshPathStatus.PathComplete)
            return false;
        corners = _scratch.GetCornersNonAlloc(Corners);
        for (var i = 1; i < corners; i++)
            length += Vector3.Distance(Corners[i - 1], Corners[i]);
        return true;
    }

    // The walking distance to a spot, or -1 when it cannot be reached.
    private float PathLength(Vector3 destination) => TryPath(destination, out _, out var length) ? length : -1f;

    /// <summary>
    /// How bad the path just calculated is (0 is fine): it passes close to a danger, the killer could get to a spot on it before this bot, or it goes through the killer room.
    /// The later a path is cut off the less it matters: there may be another way by then.
    /// </summary>
    private float PathRisk(int corners, float speed)
    {
        var risk = 0f;
        var killerKnown = KillerKnown;
        var killerSpeed = Mathf.Max(0.1f, KillerTopSpeed);
        var walked = 0f;
        for (var i = 1; i < corners; i++)
        {
            var from = Corners[i - 1];
            var to = Corners[i];
            var segment = Vector3.Distance(from, to);
            walked += segment;
            if (KillerRoomRules.CannotHideAt(to))
                risk += 40f;
            if (killerKnown)
            {
                var clearance = DistanceToSegment(_lastKnown, from, to);
                if (clearance < 5f)
                    risk += (5f - clearance) * 12f;
                var botTime = walked / speed;
                var killerTime = Mathf.Max(0f, Vector3.Distance(_lastKnown, to) - 2f) / killerSpeed;
                if (killerTime < botTime && botTime < 8f)
                    risk += 25f * (1f - botTime / 8f);
            }
            foreach (var (at, _) in _bangs)
                if (DistanceToSegment(at, from, to) < 5f)
                    risk += 30f;
        }
        return risk;
    }

    private static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        var line = b - a;
        var t = line.sqrMagnitude > 0.001f ? Mathf.Clamp01(Vector3.Dot(point - a, line) / line.sqrMagnitude) : 0f;
        return Vector3.Distance(point, a + line * t);
    }

    private bool InKillerSight(Vector3 spot)
        => KillerKnown && !Physics.Linecast(_lastKnown, spot + Vector3.up * 0.5f, SightMask, QueryTriggerInteraction.Ignore);

    /// <summary>Of a dozen places, where to go now: away from every danger along a path that cannot be cut off, out of the killer's sight, no dead end, not where another bot already goes. An open truck beats all.</summary>
    private Vector3 PickRoute(bool run)
    {
        var points = LevelGenerator.Instance != null ? LevelGenerator.Instance.LevelPathPoints : null;
        if (points == null || points.Count == 0)
            return transform.position;
        var best = transform.position;
        var bestScore = float.MinValue;
        var speed = run ? RunSpeed : WalkSpeed;
        var trucksOpen = ExtractionState.TrucksOpen;

        void Consider(LevelPoint? point)
        {
            if (point == null || (point.Truck && !trucksOpen) || (point.Room != null && point.Room.Truck && !trucksOpen))
                return;
            var position = point.transform.position;
            if (!TryPath(position, out var corners, out var length) || length < 5f)
                return;
            var score = -PathRisk(corners, speed) - (Boosted ? 0.03f : 0.15f) * length; // boosted, a long way costs nothing
            if (KillerKnown)
            {
                score += Mathf.Min(60f, Vector3.Distance(position, _lastKnown));
                if (!InKillerSight(position))
                    score += 15f;
            }
            foreach (var (at, _) in _bangs)
                score += Mathf.Min(25f, Vector3.Distance(position, at));
            if (point.ConnectedPoints != null && point.ConnectedPoints.Count <= 1 && !point.Truck)
                score -= 12f;
            if (KillerRoomRules.CannotHideAt(position))
                score -= 60f;
            foreach (var other in All)
                if (other != this && other != null && other._agent != null && other._agent.isOnNavMesh && Vector3.Distance(other._agent.destination, position) < 6f)
                    score -= 8f;
            if (trucksOpen && point.Truck)
                score += 60f;
            if (score > bestScore)
            {
                bestScore = score;
                best = position;
            }
        }

        for (var i = 0; i < 12; i++)
            Consider(points[Random.Range(0, points.Count)]);
        if (trucksOpen)
            foreach (var point in points.Where(p => p != null && p.Truck))
                Consider(point);
        return best;
    }

    /// <summary>The best real hiding spot in reach: the better the kind, the nearer and the further from the killer the better, out of their sight, reachable before they get there, not taken by another bot.</summary>
    private BotHideSpots.Spot? FindHideSpot()
    {
        var here = transform.position;
        var known = KillerKnown;
        var near = BotHideSpots.Spots
            .Where(s => !_badSpots.Contains(s) && (s.Position - here).sqrMagnitude < 40f * 40f && !(known && Vector3.Distance(s.Position, _lastKnown) < 7f)
                        && !All.Any(b => b != this && b != null && b._spot == s))
            .OrderByDescending(s => KindValue(s.Kind) - Vector3.Distance(s.Position, here) + (known ? Mathf.Min(30f, Vector3.Distance(s.Position, _lastKnown)) * 0.5f : 0f))
            .Take(8)
            .ToList();
        BotHideSpots.Spot? best = null;
        var bestScore = float.MinValue;
        foreach (var spot in near)
        {
            if (!TryPath(spot.Entry, out var corners, out var length) || length > 45f)
                continue;
            var score = KindValue(spot.Kind) - PathRisk(corners, RunSpeed) - 0.7f * length;
            if (known)
            {
                score += Mathf.Min(30f, Vector3.Distance(spot.Position, _lastKnown)) * 0.5f;
                if (!InKillerSight(spot.Position))
                    score += 30f;
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = spot;
            }
        }
        return best;
    }

    private static float KindValue(BotHideSpots.Kind kind)
        => kind == BotHideSpots.Kind.GameHidingVolume ? 40f : kind == BotHideSpots.Kind.UnderFurniture ? 30f : 5f;

    // --- The job ----------------------------------------------------------------------------------------------------------------

    private bool Arrived(float within)
        => _agent != null && _agent.isOnNavMesh && !_agent.pathPending && (!_agent.hasPath || _agent.remainingDistance <= within);

    private void GoTo(Vector3 point)
    {
        if (_agent != null && _agent.isOnNavMesh)
        {
            if (NavMesh.SamplePosition(point, out var hit, 2.5f, -1))
                _agent.SetDestination(hit.position);
            else
                _agent.SetDestination(point);
        }
    }

    // Deliver what it holds; else fetch what is worth the walk; else keep moving somewhere safe (valuables keep coming).
    private void ChooseTask()
    {
        if (_carriedHead != null)
        {
            SetMode(Mode.BringHead, 90f);
            return;
        }
        if (_carrying == null && FindHead(out var head))
        {
            StartFetchHead(head);
            return;
        }
        if (ExtractionState.TrucksOpen)
        {
            SetMode(Mode.Escape, 180f);
            return;
        }
        if (Tuning.Bots.Scavenges)
        {
            if (_carrying != null && FindPoint(out var point, out var length))
            {
                _point = point;
                _pointWalk = WalkSpotOf(point, out var walkTo, out _, out _) ? walkTo : point.transform.position;
                SetMode(Mode.Deliver, length / WalkSpeed * 2.5f + 15f);
                DebugNote($"takes ${_carrying.dollarValueCurrent:0} to an extraction point {length:0} m away (${ExtractionPatches.HaulAt(point)} of ${ExtractionState.Goal} there)");
                return;
            }
            if (_carrying == null && FindValuable(out var valuable, out var walk))
            {
                _target = valuable;
                SetMode(Mode.Scavenge, walk / WalkSpeed * 2.5f + 15f);
                return;
            }
        }
        SetMode(Mode.Wander, 25f);
        GoTo(PickRoute(run: false));
    }

    /// <summary>Value for the walk: there and on to the extraction point, less the risk on the way. Not what another bot has claimed or is working next to, not in the killer room unless there is nothing else.</summary>
    private bool FindValuable(out ValuableObject valuable, out float walk)
    {
        valuable = null!;
        walk = 0f;
        var origin = transform.position;
        var goal = FindPoint(out var point, out _) ? point.transform.position : origin;
        var known = KillerKnown;
        var candidates = Object.FindObjectsByType<ValuableObject>(FindObjectsSortMode.None)
            .Where(v => v != null && v.isActiveAndEnabled && !_skipped.Contains(v) && !Claimed(v) && v.dollarValueCurrent > 0f && !v.committedToExtraction
                        && v.physGrabObject != null && !v.physGrabObject.grabbed && v.physGrabObject.playerGrabbing.Count == 0
                        && (v.roomVolumeCheck == null || !v.roomVolumeCheck.CurrentRooms.Any(r => r != null && (r.Truck || r.Extraction))))
            .Select(v => (v, rough: v.dollarValueCurrent / (Vector3.Distance(v.transform.position, origin) + Vector3.Distance(v.transform.position, goal) + 15f)))
            .OrderByDescending(c => c.rough)
            .Take(10)
            .ToList();
        var bestScore = float.MinValue;
        foreach (var (candidate, _) in candidates)
        {
            var at = candidate.transform.position;
            if (!TryPath(at, out var corners, out var length))
                continue;
            var score = candidate.dollarValueCurrent / (length + Vector3.Distance(at, goal) + 15f);
            score /= 1f + PathRisk(corners, WalkSpeed) * 0.05f * (1f - BotIntel.Urgency * 0.7f);
            if (candidate == _reclaim)
                score *= 3f;
            if (KillerRoomRules.CannotHideAt(at))
                score *= 0.05f;
            if (known && Vector3.Distance(at, _lastKnown) < 12f)
                score *= 0.2f;
            foreach (var other in All)
                if (other != this && other != null && Vector3.Distance(other.transform.position, at) < 8f)
                    score *= 0.6f;
            if (score > bestScore)
            {
                bestScore = score;
                valuable = candidate;
                walk = length;
            }
        }
        return valuable != null;
    }

    // A valuable another bot is going for or carrying.
    private bool Claimed(ValuableObject valuable) => All.Any(b => b != this && (b._target == valuable || b._carrying == valuable));

    /// <summary>What is on its way to this point in the bots' hands.</summary>
    private static int InTransit(ExtractionPoint point)
    {
        var sum = 0f;
        foreach (var bot in All)
            if (bot != null && bot._carrying != null && bot._point == point)
                sum += bot._carrying.dollarValueCurrent;
        return (int)sum;
    }

    /// <summary>The extraction point to work on: the one closest to done (finishing them is what counts), not one that is already covered, not one the killer stands at.</summary>
    private bool FindPoint(out ExtractionPoint point, out float length)
    {
        point = null!;
        length = 0f;
        var best = float.MinValue;
        var goal = Mathf.Max(1, ExtractionState.Goal);
        foreach (var candidate in ExtractionPoints.Get())
        {
            if (candidate == null || candidate.currentState != ExtractionPoint.State.Active || candidate.extractionArea == null)
                continue;
            if (candidate == _avoidPoint && Time.time < _avoidUntil)
                continue;
            if (!WalkSpotOf(candidate, out var at, out var corners, out var walk))
                continue;
            var haul = ExtractionPatches.HaulAt(candidate);
            var transit = candidate == _point ? 0 : InTransit(candidate);
            var score = 60f * Mathf.Clamp01((float)haul / goal) - 0.4f * walk - PathRisk(corners, WalkSpeed);
            if (haul + transit >= goal)
                score -= 80f;
            if (KillerKnown && Vector3.Distance(at, _lastKnown) < 15f)
                score -= 60f * (1f - BotIntel.Urgency * 0.7f);
            if (score > best)
            {
                best = score;
                point = candidate;
                length = walk;
            }
        }
        return point != null;
    }

    private static readonly HashSet<ExtractionPoint> LoggedPoints = new();

    /// <summary>Where to walk to reach a point: the spot the game itself uses next to it, or the point, or its area, whichever the walking mesh gets to. The point's middle often is not walkable.</summary>
    private bool WalkSpotOf(ExtractionPoint point, out Vector3 spot, out int corners, out float length)
    {
        var candidates = new List<Vector3>();
        if (point.safetySpawn != null)
            candidates.Add(point.safetySpawn.position);
        candidates.Add(point.transform.position);
        if (point.extractionArea != null)
            candidates.Add(point.extractionArea.transform.position);
        foreach (var candidate in candidates)
        {
            if (TryPath(candidate, out corners, out length, 5f))
            {
                spot = candidate;
                return true;
            }
        }
        spot = default;
        corners = 0;
        length = 0f;
        if (LoggedPoints.Add(point))
            Hunted.Logger.LogWarning($"{Name} finds no way to the extraction point at {point.transform.position}: from {transform.position}, tried {string.Join(", ", candidates)}.");
        return false;
    }

    private void ScavengeLogic()
    {
        var target = _target;
        if (target == null || !target.isActiveAndEnabled || target.physGrabObject == null || target.physGrabObject.grabbed || Time.time >= _modeUntil)
        {
            if (target != null && Time.time >= _modeUntil)
                _skipped.Add(target); // could not get there
            _target = null;
            SetMode(Mode.Idle, 0.3f);
            return;
        }
        var flat = target.transform.position - transform.position;
        flat.y = 0f;
        if (flat.magnitude < 2f && Mathf.Abs(target.transform.position.y - transform.position.y) < 2.2f)
        {
            PickUp(target);
            _target = null;
            _reclaim = null;
            SetMode(Mode.Idle, 0.2f);
            return;
        }
        if (Time.time >= _repathAt)
        {
            _repathAt = Time.time + 1f;
            GoTo(target.transform.position);
        }
    }

    private void DeliverLogic()
    {
        var point = _point;
        if (_carrying == null || point == null || point.extractionArea == null || point.currentState != ExtractionPoint.State.Active || Time.time >= _modeUntil)
        {
            if (_carrying != null && Time.time >= _modeUntil)
                Hunted.Logger.LogDebug($"{Name} ran out of time delivering: {Vector3.Distance(transform.position, point != null ? point.transform.position : transform.position):0} m from the point, path {(_agent != null && _agent.isOnNavMesh ? _agent.pathStatus.ToString() : "none")}.");
            _point = null;
            SetMode(Mode.Idle, 0.3f);
            return;
        }
        var spot = ExtractionPatches.DropSpotOf(point);
        var flat = _pointWalk - transform.position;
        flat.y = 0f;
        // Anywhere in the point's zone will do, or as close as the walkable floor goes.
        if (ExtractionPatches.InZone(point, transform.position + Vector3.up * 0.5f) || (flat.magnitude < 3f && Arrived(0.6f)))
        {
            Hunted.Logger.LogInfo($"{Name} brought a valuable (${_carrying.dollarValueCurrent:0}) to an extraction point (${ExtractionPatches.HaulAt(point)} of ${ExtractionState.Goal} there).");
            var jitter = Random.insideUnitCircle * 0.7f;
            DropCarried(spot + new Vector3(jitter.x, 0f, jitter.y), keepHead: true);
            _reclaim = null;
            _point = null;
            SetMode(Mode.Idle, 0.3f);
            return;
        }
        if (Time.time >= _repathAt)
        {
            _repathAt = Time.time + 1f;
            GoTo(_pointWalk);
        }
    }

    // Off to the truck that can be reached safely; with the killer standing at one, the other.
    private void EscapeLogic()
    {
        if (InTruck)
        {
            DebugNote("reached the truck");
            StartRest();
            return;
        }
        GoToTruck();
    }

    private void StartRest()
    {
        var spot = FindSpotInTruck();
        SetMode(Mode.Rest, 1000000f);
        _agent!.ResetPath();
        _restSpot = spot;
        if (spot != null)
            Hunted.Logger.LogDebug($"{Name} goes to the back of the truck, {Vector3.Distance(transform.position, spot.Value):0.0} m further in.");
    }

    /// <summary>The walkable spot in the truck it just entered that is the longest walk from here, so the furthest from the door it came in by, and not where another bot is going.</summary>
    private Vector3? FindSpotInTruck()
    {
        var rooms = Avatar.RoomVolumeCheck.CurrentRooms.Where(r => r != null && r.Truck).ToList();
        var colliders = rooms.SelectMany(r => r.GetComponentsInChildren<Collider>(true)).ToList();
        if (colliders.Count == 0)
            return null;
        var bounds = colliders[0].bounds;
        foreach (var collider in colliders)
            bounds.Encapsulate(collider.bounds);

        const float step = 0.75f;
        var here = transform.position;
        var spots = new List<Vector3>();
        for (var x = bounds.min.x; x <= bounds.max.x; x += step)
            for (var z = bounds.min.z; z <= bounds.max.z; z += step)
            {
                var probe = new Vector3(x, here.y, z);
                if (!NavMesh.SamplePosition(probe, out var hit, 0.5f, NavMesh.AllAreas) || Mathf.Abs(hit.position.y - here.y) > 1.5f)
                    continue;
                var inside = hit.position + Vector3.up * 0.5f;
                if (colliders.Any(c => c is MeshCollider { convex: false } ? c.bounds.Contains(inside) : (c.ClosestPoint(inside) - inside).sqrMagnitude < 0.01f)
                    && !All.Any(b => b != this && b != null && b._restSpot != null && Vector3.Distance(b._restSpot.Value, hit.position) < 1.2f))
                    spots.Add(hit.position);
            }

        Vector3? best = null;
        var longest = 1f; // a step or two further is no improvement
        foreach (var spot in spots.OrderByDescending(s => Vector3.Distance(s, here)).Take(8))
        {
            if (TryPath(spot, out _, out var length, 0.5f) && length > longest)
            {
                longest = length;
                best = spot;
            }
        }
        return best;
    }

    private void GoToTruck()
    {
        if (Time.time < _repathAt)
            return;
        _repathAt = Time.time + 2f;
        var points = LevelGenerator.Instance != null ? LevelGenerator.Instance.LevelPathPoints : null;
        if (points == null)
            return;
        var best = float.MinValue;
        Vector3? destination = null;
        foreach (var point in points)
        {
            if (point == null || !point.Truck || !TryPath(point.transform.position, out var corners, out var length))
                continue;
            var score = -length - PathRisk(corners, WalkSpeed);
            if (KillerKnown && Vector3.Distance(point.transform.position, _lastKnown) < 10f)
                score -= 40f;
            if (score > best)
            {
                best = score;
                destination = point.transform.position;
            }
        }
        if (destination != null)
            GoTo(destination.Value);
    }

    // --- A fallen survivor's head ----------------------------------------------------------------------------------------------------

    /// <summary>The heads lying about: a bot's or a player's, it makes no difference. The killer has none to rescue.</summary>
    private static IEnumerable<PlayerDeathHead> FallenHeads()
    {
        foreach (var player in SemiFunc.PlayerGetList())
            if (player != null && !KillerRole.IsKiller(player) && player.playerDeathHead != null && player.playerDeathHead.triggered)
                yield return player.playerDeathHead;
    }

    // Where a head brings its owner back: in an extraction point as it completes, or in a truck once they are open. There it can be left.
    private static bool WhereItBelongs(PlayerDeathHead head)
        => head.roomVolumeCheck != null && (head.roomVolumeCheck.inExtractionPoint || (ExtractionState.TrucksOpen && head.roomVolumeCheck.inTruck));

    /// <summary>A head it has seen and can reach: not one another bot is seeing to, and not one the killer is standing over (unless time is running out).</summary>
    private bool FindHead(out PlayerDeathHead head)
    {
        head = null!;
        var best = float.MaxValue;
        foreach (var candidate in _headsSeen)
        {
            if (candidate == null || !candidate.triggered || All.Any(b => b != this && b != null && (b._targetHead == candidate || b._carriedHead == candidate)))
                continue;
            var at = candidate.transform.position;
            if (candidate.physGrabObject != null && (candidate.physGrabObject.grabbed || candidate.physGrabObject.playerGrabbing.Count > 0))
                continue; // a player has it
            if (KillerKnown && Time.time - _lastNoticed < 8f && Vector3.Distance(at, _lastKnown) < 10f && BotIntel.Urgency < 1f)
                continue;
            if (WhereItBelongs(candidate))
                continue;
            if (!TryPath(at, out _, out var length) || length >= best)
                continue;
            best = length;
            head = candidate;
        }
        return head != null;
    }

    private void StartFetchHead(PlayerDeathHead head)
    {
        _target = null;
        _targetHead = head;
        SetMode(Mode.FetchHead, 60f);
        DebugNote($"goes for the head of {head.playerAvatar.playerName}");
    }

    private void FetchHeadLogic()
    {
        var head = _targetHead;
        if (head == null || !head.triggered || Time.time >= _modeUntil || (head.physGrabObject != null && head.physGrabObject.grabbed))
        {
            _targetHead = null;
            SetMode(Mode.Idle, 0.2f);
            return;
        }
        if (Vector3.Distance(head.transform.position, transform.position + Vector3.up * 0.3f) < 1.6f)
        {
            _carriedHead = head;
            _targetHead = null;
            SetMode(Mode.BringHead, 90f);
            Hunted.Logger.LogInfo($"{Name} picked up the head of {head.playerAvatar.playerName}.");
            return;
        }
        if (Time.time >= _repathAt)
        {
            _repathAt = Time.time + 1f;
            GoTo(head.transform.position);
        }
    }

    // A head comes back to life where the valuables go, or at a truck once they are open: it goes wherever that is, like a survivor would take it.
    private void BringHeadLogic()
    {
        var head = _carriedHead;
        if (head == null || Time.time >= _modeUntil)
        {
            DropHead();
            _point = null;
            SetMode(Mode.Idle, 0.2f);
            return;
        }
        if (ExtractionState.TrucksOpen)
        {
            _point = null;
            if (InTruck)
            {
                Hunted.Logger.LogInfo($"{Name} brought the head of {head.playerAvatar.playerName} to a truck.");
                DropHead();
                SetMode(Mode.Idle, 0.2f);
                return;
            }
            GoToTruck();
            return;
        }
        if (_point == null || _point.currentState != ExtractionPoint.State.Active)
        {
            if (!FindPoint(out var point, out _))
            {
                DebugNote($"has nowhere to take the head of {head.playerAvatar.playerName}");
                GoToTruck();
                return;
            }
            _point = point;
            _pointWalk = WalkSpotOf(point, out var walkTo, out _, out _) ? walkTo : point.transform.position;
            _repathAt = 0f;
        }
        var flat = _pointWalk - transform.position;
        flat.y = 0f;
        if (ExtractionPatches.InZone(_point, transform.position + Vector3.up * 0.5f) || (flat.magnitude < 3f && Arrived(0.6f)))
        {
            Hunted.Logger.LogInfo($"{Name} brought the head of {head.playerAvatar.playerName} to an extraction point.");
            var jitter = Random.insideUnitCircle * 0.7f;
            DropHead(ExtractionPatches.DropSpotOf(_point) + new Vector3(jitter.x, 0f, jitter.y));
            _point = null;
            SetMode(Mode.Idle, 0.2f);
            return;
        }
        if (Time.time >= _repathAt)
        {
            _repathAt = Time.time + 1f;
            GoTo(_pointWalk);
        }
    }

    private void CarryHead()
    {
        var head = _carriedHead;
        if (head == null)
            return;
        var grab = head.physGrabObject;
        if (!head.triggered || grab == null)
        {
            _carriedHead = null; // its owner is back
            return;
        }
        if (grab.grabbed)
        {
            DropHead(); // a player took it from it
            return;
        }
        BotCarry.HoldAt(grab, transform.position + transform.forward * 0.6f + Vector3.up * (Stance == StanceKind.Stand ? 1.0f : 0.5f));
    }

    private void DropHead(Vector3? at = null)
    {
        var head = _carriedHead;
        _carriedHead = null;
        if (head != null && head.physGrabObject != null)
            BotCarry.LetGo(head.physGrabObject, at);
    }
}
