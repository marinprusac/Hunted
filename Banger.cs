using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.AI;

namespace Hunted;

/// <summary>Marks one of the game's Bang monsters as one of the killer's bomber heads (set on every client, from the data it was spawned with).</summary>
internal class BangerTag : MonoBehaviour
{
    /// <summary>The head was called for by the killer and lives until it blows up (host only).</summary>
    internal bool Awake2;
    /// <summary>The killer is holding it (every client knows).</summary>
    internal bool Held;
    /// <summary>It has been let go of and is hunting (host only).</summary>
    internal bool Thrown;
    internal Vector3 SummonPosition;
    internal float ThrownAt;
}

/// <summary>
/// The killer's bomber heads: an item in inventory slot 2 with a few uses that come back slowly. Taking it out makes a real bomber head appear and puts it in the
/// killer's hands, held the way a survivor holds anything; letting go of it (left mouse button, the slot key again, or another slot) throws it out as a live head.
/// A head is the game's own Bang monster (the skeleton head with the dynamite), with its brain replaced: it knows where every survivor is, always walks towards
/// the nearest one at the killer's walking speed, lights its fuse when it is close, and blows up harder and sooner than a normal one.
///
/// Heads are made when they are called for, so there can be as many on the map as the killer can afford. A head that has blown up sleeps and is used again for the
/// next one. The inventory slot is not a real item: its icon, its charge bars and what the keys do are drawn and handled here, on the killer's own computer only.
/// </summary>
internal static class Banger
{
    private const string Marker = "Hunted.Banger";
    private const byte TakeOutEvent = 77; // killer -> host: bring me a head (position)
    private const byte HeldEvent = 78;    // host -> everyone: this head is (not) held by the killer any more (view id, held)
    private const byte ReleaseEvent = 79; // killer -> host: I let go of it

    private static float _uses;
    private static bool _usesReady;
    private static bool _held;           // the killer's side: a head is in the hands (or on its way there)
    private static EnemyBang? _heldBang; // the killer's side: which one
    private static float _cooldown;
    private static Sprite? _icon;

    private static readonly List<EnemyBang> Known = new(); // every computer: all the heads that exist
    private static EnemyBang? _hostHeld; // host: the head the killer is holding
    private static bool _acquiring;
    private static bool _releaseRequested;
    private static bool _loggedMissing;
    internal static bool SpawningHead;

    internal static void Init()
    {
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
    }

    internal static void Reset()
    {
        _usesReady = false;
        _held = false;
        _heldBang = null;
        _cooldown = 0f;
        Known.Clear();
        _hostHeld = null;
        _acquiring = false;
        _releaseRequested = false;
        _loggedMissing = false;
    }

    internal static void Register(EnemyBang bang)
    {
        if (!Known.Contains(bang))
            Known.Add(bang);
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || KillerRole.KillerSteamId == null)
            return;
        Known.RemoveAll(b => b == null);
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostTick();
        if (KillerRole.LocalIsKiller)
            LocalTick(dt);
        else
            KeepOthersOff();
    }

    // ---------------------------------------------------------------------------------------------- the killer's side

    private static void LocalTick(float dt)
    {
        var max = Tuning.Banger.MaxUses;
        if (!_usesReady)
        {
            _usesReady = true;
            _uses = max;
        }
        var player = PlayerController.instance;
        if (player == null)
            return;
        var rate = 1f / Mathf.Max(0.1f, Tuning.Banger.SecondsPerUse) * (KillerRoom.Contains(player.transform.position) ? Tuning.Banger.RoomRechargeMultiplier : 1f);
        _uses = Mathf.Min(max, _uses + rate * dt);
        _cooldown -= dt;

        if (!_held)
            return;
        if (SemiFunc.InputDown(InputKey.Grab))
        {
            Drop();
            return;
        }
        // Held the way a survivor holds something: the killer's own grab is kept on the head every frame.
        var body = _heldBang != null && _heldBang.enemy != null ? _heldBang.enemy.Rigidbody : null;
        if (body != null && body.physGrabObject != null && PhysGrabber.instance != null && _heldBang!.enemy.EnemyParent.Spawned)
        {
            PhysGrabber.instance.OverrideGrab(body.physGrabObject);
        }
    }

    /// <summary>A slot key was pressed. Returns true when the game should handle the press itself.</summary>
    internal static bool OnSlotPressed(int index)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller)
            return true;
        if (index != 1)
        {
            if (_held)
                Drop(); // switching to another item lets go of the head
            return true;
        }
        if (_cooldown > 0f)
            return false;
        _cooldown = 0.25f;
        if (_held)
            Drop(); // trying to put it away lets go of it
        else if (_uses >= 1f)
            TakeOut();
        else
            Inventory.instance?.GetSpotByIndex(1)?.Ping(10f, 20f, 0.2f);
        return false;
    }

    private static void TakeOut()
    {
        var camera = Camera.main;
        if (camera == null)
            return;
        _uses -= 1f;
        _held = true;
        _heldBang = null;
        // The sword goes back into its slot: one thing in the hands at a time.
        var sword = KillerWeapon.Weapon != null ? KillerWeapon.Weapon.GetComponent<ItemEquippable>() : null;
        if (KillerWeapon.Held && sword != null && !sword.isEquipped && PhysGrabber.instance != null)
            sword.RequestEquip(0, PhysGrabber.instance.photonView.ViewID);

        var position = camera.transform.position + camera.transform.forward * 2f;
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostTakeOut(position);
        else
            PhotonNetwork.RaiseEvent(TakeOutEvent, new object[] { position }, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
    }

    private static void Drop()
    {
        _held = false;
        _heldBang = null;
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostRelease();
        else
            PhotonNetwork.RaiseEvent(ReleaseEvent, null, new RaiseEventOptions { Receivers = ReceiverGroup.MasterClient }, SendOptions.SendReliable);
    }

    /// <summary>The heads a survivor has to get away from: every one on the map that is not in the killer's hands.</summary>
    internal static void CollectThreats(List<(Vector3 position, bool lit)> into)
    {
        into.Clear();
        foreach (var bang in Known)
        {
            if (bang == null || bang.enemy == null || bang.enemy.EnemyParent == null || !bang.enemy.EnemyParent.Spawned)
                continue;
            if (bang.GetComponent<BangerTag>() is { Held: true })
                continue;
            into.Add((bang.transform.position, bang.fuseActive || bang.currentState == EnemyBang.State.FuseDelay || bang.currentState == EnemyBang.State.Fuse));
        }
    }

    /// <summary>Everybody but the killer keeps their hands off a head the killer is holding.</summary>
    private static void KeepOthersOff()
    {
        foreach (var bang in Known)
        {
            var tag = bang != null ? bang.GetComponent<BangerTag>() : null;
            var grab = tag != null && tag.Held && bang!.enemy != null && bang.enemy.Rigidbody != null ? bang.enemy.Rigidbody.physGrabObject : null;
            if (grab != null && grab.grabDisableTimer < 0.3f)
                grab.grabDisableTimer = 0.3f;
        }
    }

    // ---------------------------------------------------------------------------------------------- the inventory slot

    /// <summary>Shows the head as the icon of slot 2, with a bar for every use that is ready. Called after the slot's own update, every frame.</summary>
    internal static void DrawSlot(InventorySpot spot)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller || spot.inventorySpotIndex != 1 || spot.CurrentItem != null)
            return;
        if (!spot.IsOccupied())
            spot.currentState = InventorySpot.SpotState.Occupied;
        var icon = Icon();
        if (icon != null && spot.inventoryIcon != null && (spot.inventoryIcon.sprite != icon || !spot.inventoryIcon.enabled))
            spot.SetEmoji(icon);

        var bars = spot.batteryVisualLogic;
        if (bars == null)
            return;
        var max = Tuning.Banger.MaxUses;
        if (!bars.gameObject.activeSelf)
            bars.gameObject.SetActive(true);
        if (bars.doOutro || bars.targetScale == 0f)
            bars.ResetOutro();
        if (bars.itemBattery == null && bars.bars.Count != max)
        {
            bars.batteryBars = max;
            bars.BatteryBarsSet();
            bars.BatteryBarsUpdate(Ready(), true);
        }
        else if (bars.currentBars != Ready())
            bars.BatteryBarsUpdate(Ready(), true);
        if (_uses < max)
            bars.OverrideBatteryCharge(0.2f); // the bars show it filling up again
    }

    private static int Ready() => Mathf.Clamp(Mathf.FloorToInt(_uses + 0.0001f), 0, Tuning.Banger.MaxUses);

    // The picture of the head, the same whatever the number of uses (that is what the bars are for).
    private static Sprite? Icon()
    {
        if (_icon != null)
            return _icon;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hunted.BangerIcon.png");
            if (stream == null)
                return null;
            var bytes = new byte[stream.Length];
            stream.Read(bytes, 0, bytes.Length);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            if (!texture.LoadImage(bytes))
                return null;
            _icon = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (System.Exception exception)
        {
            Hunted.Logger.LogWarning($"Could not load the bomber head icon: {exception.Message}");
        }
        return _icon;
    }

    // ---------------------------------------------------------------------------------------------- messages

    private static void OnEvent(EventData data)
    {
        switch (data.Code)
        {
            case TakeOutEvent when SemiFunc.IsMasterClientOrSingleplayer():
                if (data.CustomData is object[] { Length: > 0 } values && values[0] is Vector3 position)
                    HostTakeOut(position);
                break;
            case ReleaseEvent when SemiFunc.IsMasterClientOrSingleplayer():
                HostRelease();
                break;
            case HeldEvent:
                if (data.CustomData is object[] { Length: > 1 } held && held[0] is int viewId && held[1] is bool isHeld)
                {
                    var view = PhotonView.Find(viewId);
                    var bang = view != null ? view.GetComponent<EnemyBang>() : null;
                    if (bang != null)
                        ApplyHeld(bang, isHeld);
                }
                break;
        }
    }

    private static void Announce(EnemyBang bang, bool held)
    {
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(HeldEvent, new object[] { bang.photonView.ViewID, held }, new RaiseEventOptions { Receivers = ReceiverGroup.All }, SendOptions.SendReliable);
        else
            ApplyHeld(bang, held);
    }

    private static void ApplyHeld(EnemyBang bang, bool held)
    {
        var tag = bang.GetComponent<BangerTag>();
        if (tag != null)
            tag.Held = held;
        if (!KillerRole.LocalIsKiller)
            return;
        if (held)
            _heldBang = bang;
        else if (_heldBang == bang)
        {
            _heldBang = null;
            _held = false; // it is gone from the killer's hands (it was thrown, or it was lost)
        }
    }

    // ---------------------------------------------------------------------------------------------- the host's side

    private static void HostTick()
    {
        foreach (var banger in Known)
        {
            var tag = banger != null ? banger.GetComponent<BangerTag>() : null;
            if (banger == null || tag == null || !banger.enemy.EnemyParent.Spawned)
                continue;
            if (tag.Held)
            {
                var body = banger.enemy.Rigidbody;
                body.OverrideMountedGrab(0.2f); // the head's own "let go of me" never kicks in
                // The killer's grip is firmer than a survivor's, so the head hangs straight in front of them.
                if (body.physGrabObject != null)
                {
                    body.physGrabObject.OverrideGrabStrength(Tuning.Banger.HoldStrength, 0.2f);
                    body.physGrabObject.OverrideTorqueStrength(Tuning.Banger.HoldTorque, 0.2f);
                }
            }
            else if (tag.Thrown)
                Drive(banger, tag);
        }
    }

    /// <summary>The killer asked for a head: wake up one that sleeps, or make a new one, right at their hands.</summary>
    private static void HostTakeOut(Vector3 position)
    {
        if (_hostHeld != null || _acquiring)
            return;
        _acquiring = true;
        _releaseRequested = false;
        Hunted.Instance.StartCoroutine(Acquire(position));
    }

    private static IEnumerator Acquire(Vector3 position)
    {
        var banger = Known.FirstOrDefault(b => b != null && b.GetComponent<BangerTag>() is { Awake2: false } && !b.enemy.EnemyParent.Spawned);
        if (banger == null)
        {
            var bangRef = FindRef("Enemy - Bang");
            if (bangRef == null)
            {
                if (!_loggedMissing)
                    Hunted.Logger.LogError("Bomber heads: the game's Bang monster was not found, so the killer has none.");
                _loggedMissing = true;
                _acquiring = false;
                yield break;
            }
            var directorRef = EnemyBangDirector.instance == null ? FindRef("Enemy - Bang Director") : null;
            yield return LevelGenerator.Instance.PrefabsPrepare(directorRef != null ? new[] { directorRef, bangRef } : new[] { bangRef });
            if (directorRef != null && EnemyBangDirector.instance == null)
                Spawn(directorRef, position);
            var spawned = Spawn(bangRef, position);
            banger = spawned != null ? spawned.GetComponentInChildren<EnemyBang>(true) : null;
            var parent = spawned != null ? spawned.GetComponent<EnemyParent>() : null;
            if (banger == null || parent == null)
            {
                _acquiring = false;
                yield break;
            }
            parent.SetupDone = true;
            var enemy = spawned!.GetComponentInChildren<Enemy>();
            if (enemy != null)
                enemy.EnemyTeleported(position);
            // The monster's own start-up puts it to sleep first; it is woken up after that.
            for (var wait = 0f; parent.Spawned && wait < 5f; wait += Time.deltaTime)
                yield return null;
            while (EnemyBangDirector.instance == null || !EnemyBangDirector.instance.setup)
                yield return null;
        }

        var tag = banger.GetComponent<BangerTag>();
        if (tag == null)
        {
            _acquiring = false;
            yield break;
        }
        tag.SummonPosition = position;
        tag.Awake2 = true;
        tag.Held = true;
        tag.Thrown = false;
        banger.enemy.EnemyParent.DespawnedTimer = 0f; // the monster's own loop wakes it up, and the patches below place it
        _hostHeld = banger;
        _acquiring = false;
        Announce(banger, true);
        Hunted.Logger.LogDebug($"Bomber head in the killer's hands at {position}");
        if (_releaseRequested)
            HostRelease();
    }

    /// <summary>The killer let go: the head is on its own now.</summary>
    private static void HostRelease()
    {
        if (_hostHeld == null)
        {
            if (_acquiring)
                _releaseRequested = true;
            return;
        }
        var tag = _hostHeld.GetComponent<BangerTag>();
        if (tag != null)
        {
            tag.Held = false;
            tag.Thrown = true;
            tag.ThrownAt = Time.time;
        }
        Announce(_hostHeld, false);
        _hostHeld = null;
    }

    internal static void OnSleep(EnemyBang bang)
    {
        if (bang == _hostHeld)
        {
            _hostHeld = null;
            Announce(bang, false);
        }
    }

    private static PrefabRef? FindRef(string name)
    {
        var director = EnemyDirector.instance;
        if (director == null)
            return null;
        foreach (var list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            foreach (var setup in list)
                if (setup != null && setup.spawnObjects != null)
                    foreach (var spawn in setup.spawnObjects)
                        if (spawn != null && spawn.ResourcePath != null && spawn.ResourcePath.EndsWith("/" + name))
                            return spawn;
        return null;
    }

    private static GameObject? Spawn(PrefabRef prefab, Vector3 position)
    {
        if (SemiFunc.IsMultiplayer())
            return PhotonNetwork.InstantiateRoomObject(prefab.ResourcePath, position, Quaternion.identity, 0, new object[] { Marker });
        SpawningHead = true;
        try
        {
            return Object.Instantiate(prefab.Prefab, position, Quaternion.identity);
        }
        finally
        {
            SpawningHead = false;
        }
    }

    /// <summary>Somebody a head can go for: a survivor.</summary>
    private readonly struct Quarry
    {
        internal readonly Vector3 Position;
        internal readonly Vector3 Vision;
        internal readonly bool Crawling;
        internal Quarry(Vector3 position, Vector3 vision, bool crawling)
        {
            Position = position;
            Vision = vision;
            Crawling = crawling;
        }
    }

    private static bool Closest(Vector3 from, out Quarry quarry)
    {
        quarry = default;
        var best = float.MaxValue;
        var found = false;
        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isDisabled || KillerRole.IsKiller(player))
                continue;
            var distance = (player.transform.position - from).sqrMagnitude;
            if (distance >= best)
                continue;
            best = distance;
            found = true;
            quarry = new Quarry(player.transform.position, player.PlayerVisionTarget.VisionTransform.position + (player.isCrawling ? Vector3.zero : Vector3.up * 0.25f), player.isCrawling);
        }
        return found;
    }

    /// <summary>
    /// What a head thinks before its monster logic runs each frame. The Bang monsters are a swarm with one shared director: they all go where it says. Each head
    /// has to have its own mind, so before a head's logic runs the director is made to say what this head wants: attack the survivor nearest to <i>this</i> head
    /// (it knows where everybody is), or stay put while it is in the killer's hands.
    /// </summary>
    internal static void Think(EnemyBang bang)
    {
        var director = EnemyBangDirector.instance;
        var tag = bang.GetComponent<BangerTag>();
        if (director == null || tag == null || !director.setup || !SemiFunc.IsMasterClientOrSingleplayer() || !bang.enemy.EnemyParent.Spawned)
            return;
        var body = bang.enemy.Rigidbody;
        var from = body != null ? body.transform.position : bang.transform.position;
        if (!tag.Thrown)
        {
            director.currentState = EnemyBangDirector.State.Idle;
            if (bang.directorIndex >= 0 && bang.directorIndex < director.destinations.Count)
                director.destinations[bang.directorIndex] = from;
            return;
        }
        if (!Closest(from, out var target))
            return;
        director.currentState = EnemyBangDirector.State.AttackPlayer;
        director.attackPosition = target.Position;
        director.attackVisionPosition = target.Vision;
        // Wherever the survivor can go, the head can: where the walkable ground does not reach (under a bed), it goes under or over on its own.
        director.playerTargetCrawling = target.Crawling || !NavMesh.SamplePosition(target.Position, out _, 0.5f, -1);
    }

    /// <summary>A thrown head, every frame: it walks at the killer's speed, lights its fuse when it is close, and blows up if it has not by itself after a while.</summary>
    private static void Drive(EnemyBang banger, BangerTag tag)
    {
        var body = banger.enemy.Rigidbody;
        var from = body != null ? body.transform.position : banger.transform.position;
        if (!Closest(from, out var target))
            return;

        // It was carried through the air, so it may not stand on the walkable ground yet: put it there once it has landed.
        var agent = banger.enemy.NavMeshAgent.Agent;
        if (agent != null && !agent.isOnNavMesh && NavMesh.SamplePosition(from, out var hit, 1.5f, -1))
            agent.Warp(hit.position);

        var controller = PlayerController.instance;
        var speed = (controller != null && controller.playerOriginalMoveSpeed > 0f ? controller.playerOriginalMoveSpeed : 2f) * HuntedConfig.KillerWalkSpeedMultiplier.Value;
        banger.enemy.NavMeshAgent.OverrideAgent(speed, speed * 4f, 0.25f);

        var fusing = banger.fuseActive || banger.currentState == EnemyBang.State.FuseDelay || banger.currentState == EnemyBang.State.Fuse;
        if (!fusing && (banger.currentState == EnemyBang.State.Idle || banger.currentState == EnemyBang.State.Roam)
            && Vector3.Distance(from, target.Position) <= Tuning.Banger.IgniteDistance)
            banger.UpdateState(EnemyBang.State.FuseDelay);
        else if (!fusing && Time.time - tag.ThrownAt >= Tuning.Banger.SelfDestructSeconds)
            Explode(banger); // it never got close to anybody: it blows up where it is
    }

    private static void Explode(EnemyBang banger)
    {
        if (SemiFunc.IsMultiplayer())
            banger.photonView.RPC("ExplodeRPC", RpcTarget.All);
        else
            banger.ExplodeRPC();
        banger.UpdateState(EnemyBang.State.Despawn);
        banger.stateImpulse = false;
        banger.enemy.EnemyParent.Despawn();
    }

    internal static bool IsBanger(Component component) => component != null && component.GetComponent<BangerTag>() != null;

    /// <summary>The head the killer is holding is the one monster the killer may grab.</summary>
    internal static bool IsHeldBody(PhysGrabObject grab)
    {
        var enemy = grab.GetComponentInParent<EnemyParent>();
        var bang = enemy != null ? enemy.GetComponentInChildren<EnemyBang>(true) : null;
        var tag = bang != null ? bang.GetComponent<BangerTag>() : null;
        return tag != null && tag.Held;
    }
}

[HarmonyPatch]
static class BangerPatches
{
    // Every client learns which Bang monsters are heads from the data they were spawned with.
    [HarmonyPostfix, HarmonyPatch(typeof(EnemyBang), "Awake")]
    private static void BangAwake_Postfix(EnemyBang __instance)
    {
        var view = __instance.GetComponent<PhotonView>();
        var marked = Banger.SpawningHead || (view != null && view.InstantiationData is { Length: > 0 } data && data[0] as string == "Hunted.Banger");
        if (!marked)
            return;
        if (__instance.GetComponent<BangerTag>() == null)
            __instance.gameObject.AddComponent<BangerTag>();
        Banger.Register(__instance);
    }

    // A head sleeps until the killer calls for it: the monster's own timer must not wake it.
    [HarmonyPrefix, HarmonyPatch(typeof(EnemyParent), "Spawn")]
    private static bool Spawn_Prefix(EnemyParent __instance)
    {
        var bang = __instance.GetComponentInChildren<EnemyBang>(true);
        var tag = bang != null ? bang.GetComponent<BangerTag>() : null;
        return tag == null || tag.Awake2;
    }

    // A head that has blown up goes back to sleep and can be used again.
    [HarmonyPostfix, HarmonyPatch(typeof(EnemyParent), nameof(EnemyParent.Despawn))]
    private static void Despawn_Postfix(EnemyParent __instance)
    {
        var bang = __instance.GetComponentInChildren<EnemyBang>(true);
        var tag = bang != null ? bang.GetComponent<BangerTag>() : null;
        if (tag == null)
            return;
        tag.Awake2 = false;
        tag.Thrown = false;
        if (tag.Held)
            Banger.OnSleep(bang!);
        tag.Held = false;
    }

    // It wakes up where the killer's hands are, not at a random spot.
    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemySpawn))]
    private static bool EnemySpawn_Prefix(Enemy enemy, ref bool __result)
    {
        var bang = enemy.GetComponentInChildren<EnemyBang>(true);
        var tag = bang != null ? bang.GetComponent<BangerTag>() : null;
        if (tag == null)
            return true;
        enemy.TeleportPosition = tag.SummonPosition;
        enemy.EnemyTeleported(tag.SummonPosition);
        enemy.EnemyParent.firstSpawnPointUsed = true;
        __result = true;
        return false;
    }

    // A head in the killer's hands just sits there: nothing it sees or feels makes it walk off, light its fuse or get stunned out of them.
    [HarmonyPrefix, HarmonyPatch(typeof(EnemyBang), nameof(EnemyBang.UpdateState))]
    private static bool UpdateState_Prefix(EnemyBang __instance, EnemyBang.State _state)
    {
        var tag = __instance.GetComponent<BangerTag>();
        return tag == null || !tag.Held || _state == EnemyBang.State.Despawn || _state == EnemyBang.State.Spawn || _state == EnemyBang.State.Idle;
    }

    // A head in the killer's hands does not react to survivors it sees. Reacting would not just be wrong: the game's reaction calls itself for every head within 2 m, itself included,
    // and relies on the state change above to stop. A held head does not change state, so the call would never end, and the game would hang.
    [HarmonyPrefix, HarmonyPatch(typeof(EnemyBang), nameof(EnemyBang.OnVision))]
    private static bool OnVision_Prefix(EnemyBang __instance) => __instance.GetComponent<BangerTag>() is not { Held: true };

    // Each head thinks for itself (see Banger.Think).
    [HarmonyPrefix, HarmonyPatch(typeof(EnemyBang), "Update")]
    private static void Update_Prefix(EnemyBang __instance) => Banger.Think(__instance);

    // A head's way is blocked only by what a crawling survivor could not pass either: the game asks whether a wall is between it and the survivor
    // at the height of its eyes, which a bed is too; here it is asked at floor level.
    [HarmonyPrefix, HarmonyPatch(typeof(EnemyBang), "VisionBlocked")]
    private static bool VisionBlocked_Prefix(EnemyBang __instance, ref bool __result)
    {
        if (!Banger.IsBanger(__instance) || EnemyBangDirector.instance == null)
            return true;
        var body = __instance.enemy.Rigidbody;
        var from = (body != null ? body.transform.position : __instance.transform.position) + Vector3.up * 0.15f;
        var to = EnemyBangDirector.instance.attackPosition + Vector3.up * 0.15f;
        __result = Physics.Linecast(from, to, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore);
        return false;
    }

    // The fuse is shorter (on every client, so the sparks and the bang agree).
    [HarmonyPostfix, HarmonyPatch(typeof(EnemyBang), "FuseLogic")]
    private static void FuseLogic_Postfix(EnemyBang __instance)
    {
        if (!__instance.fuseActive || !Banger.IsBanger(__instance))
            return;
        var extra = 15f / Mathf.Max(0.5f, Tuning.Banger.FuseSeconds) - 1f; // the game's fuse takes 15 seconds
        __instance.fuseLerp = Mathf.Clamp01(__instance.fuseLerp + Time.deltaTime / 15f * extra);
    }

    // The explosion hits harder.
    [HarmonyPrefix, HarmonyPatch(typeof(ParticleScriptExplosion), nameof(ParticleScriptExplosion.Spawn))]
    private static void Explosion_Prefix(ParticleScriptExplosion __instance, Vector3 position, float size, ref int damage, ref int enemyDamage)
    {
        if (Banger.IsBanger(__instance))
        {
            damage = Mathf.RoundToInt(damage * Tuning.Banger.DamageMultiplier);
            enemyDamage = Mathf.RoundToInt(enemyDamage * Tuning.Banger.DamageMultiplier);
        }
    }

    // ----- the killer's inventory slot 2

    [HarmonyPrefix, HarmonyPatch(typeof(InventorySpot), "HandleInput")]
    private static bool HandleInput_Prefix(InventorySpot __instance) => Banger.OnSlotPressed(__instance.inventorySpotIndex);

    [HarmonyPostfix, HarmonyPatch(typeof(InventorySpot), "Update")]
    private static void SpotUpdate_Postfix(InventorySpot __instance) => Banger.DrawSlot(__instance);
}
