using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace Hunted;

/// <summary>
/// A bot that is a real player avatar: the game's own prefab, spawned by the host as a room object. On every machine, the host's included, it is a remote player: isLocal and
/// IsMine are false, so all of the game's "somebody else's avatar" code runs for it (animation, footsteps, health bar, tumbling, head, revive, enemy targeting, spectating, rules).
/// What a real remote player's own machine would do for them, the host does here: it decides their damage and health, and sends where they are. Everything in this file
/// starts by asking whether the avatar is a bot, and leaves real players to the original code. Where it walks and what it does is up to its mind (Bot.cs, BotBrain.cs), on the host.
/// </summary>
internal static class BotAvatar
{
    internal const string Marker = "Hunted.BotAvatar";
    private const byte StateEvent = 82; // host -> others: where every bot is and how it stands
    private const int FieldsPerBot = 5;
    private const float SendInterval = 1f / 20f;

    private sealed class Entry
    {
        internal PlayerAvatar Avatar = null!;
        internal int Slot;
        internal Color Colour;
        internal bool BodyTinted;
        internal bool HeadTinted;
        internal bool HealthSet;
        internal bool Checked;
        internal bool WasTumbling;
        internal float GetUpAt;
        internal float Regen;
        internal Bot? Mind;
        internal bool Armed = true; // a hit only starts the boost if it has been above the threshold since the last one
        internal float BoostUntil;
        internal bool Boosted; // as heard from the host
    }

    internal static readonly Action<PhotonView, bool> SetIsMine =
        AccessTools.MethodDelegate<Action<PhotonView, bool>>(AccessTools.PropertySetter(typeof(PhotonView), nameof(PhotonView.IsMine)));
    private static readonly HashSet<PhotonView> Views = new();
    private static readonly Dictionary<int, Entry> Entries = new();
    private static readonly List<PlayerAvatar> RealPlayers = new();
    private static readonly RaiseEventOptions ToOthers = new() { Receivers = ReceiverGroup.Others };
    private static int _spawnedFor;
    private static float _sendAt;

    /// <summary>True while the host is running the game's hit code for a bot: the parts that move the local player must not touch the host's own.</summary>
    internal static bool InHostHurt;
    internal static PlayerAvatar? HurtTarget;

    internal static bool Is(PlayerAvatar? avatar) => Views.Count > 0 && avatar != null && Views.Contains(avatar.photonView);

    private static bool IsHost => SemiFunc.IsMasterClient();

    internal static void Init()
    {
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
    }

    internal static void Reset()
    {
        _spawnedFor = 0;
        Entries.Clear();
        Views.RemoveWhere(v => v == null);
    }

    // ---- Spawning ----

    /// <summary>Host: spawns the bots just before the game places everyone, so they get a spot, a tumble body and a death head like any player.</summary>
    internal static void HostSpawnAll(LevelGenerator generator)
    {
        if (!PhotonNetwork.IsMasterClient || !KillerRole.InPlayableLevel || _spawnedFor == generator.GetInstanceID())
            return;
        _spawnedFor = generator.GetInstanceID();
        var count = BotRoster.Count;
        var prefab = NetworkManager.instance != null ? NetworkManager.instance.playerAvatarPrefab : null;
        if (count <= 0 || prefab == null)
            return;
        for (var slot = 0; slot < count; slot++)
        {
            var colour = Color.HSVToRGB((slot * 0.2f + Random.Range(0f, 0.12f)) % 1f, Random.Range(0.55f, 0.9f), Random.Range(0.6f, 0.95f));
            var rgb = (Mathf.RoundToInt(colour.r * 255f) << 16) | (Mathf.RoundToInt(colour.g * 255f) << 8) | Mathf.RoundToInt(colour.b * 255f);
            PhotonNetwork.InstantiateRoomObject(prefab.name, Vector3.zero, Quaternion.identity, 0, new object[] { Marker, slot, rgb });
        }
        Hunted.Logger.LogInfo($"Spawned {count} bot avatar(s).");
    }

    /// <summary>Called by Photon's own instantiation for every view, before any script on it wakes up.</summary>
    internal static void ViewAssigned(PhotonView view, int id)
    {
        if (id == 0)
        {
            Views.Remove(view);
            return;
        }
        if (view.InstantiationData is { Length: >= 3 } data && data[0] as string == Marker)
        {
            Views.Add(view);
            KeepRemote(view);
        }
    }

    /// <summary>A room object belongs to the host, and Photon works out IsMine whenever a view's controller is set. A bot stays remote on the host as well.</summary>
    internal static void KeepRemote(PhotonView view)
    {
        if (Views.Count > 0 && view.IsMine && Views.Contains(view))
            SetIsMine(view, false);
    }

    /// <summary>The bot's version of PlayerAvatar.Awake: it shares its owner (the host) with the host's own avatar, which the original treats as a duplicate.</summary>
    internal static bool Wake(PlayerAvatar avatar)
    {
        var view = avatar.GetComponent<PhotonView>();
        if (view == null || !Views.Contains(view) || view.InstantiationData is not { Length: >= 3 } data || data[1] is not int slot || data[2] is not int rgb)
            return true;

        avatar.rb = avatar.GetComponent<Rigidbody>();
        avatar.photonView = view;
        avatar.playerCollider = avatar.GetComponentInChildren<Collider>();
        avatar.isDisabled = false;
        avatar.transform.position = Vector3.forward * 2f;
        avatar.playerAvatarCollision = avatar.GetComponent<PlayerAvatarCollision>();
        GameDirector.instance.PlayerList.Add(avatar);

        // Known from the spawn on every machine, so the tumble body and the head, which look their player up by this, always find it.
        avatar.steamID = $"Hunted.Bot.{slot + 1}";
        avatar.playerName = $"Bot {slot + 1}";
        // The game waits for every avatar to report these: a bot has no loading screen or outro to wait for.
        avatar.levelAnimationCompleted = true;
        avatar.outroDone = true;

        // What the owner would be sending from the first moment.
        if (avatar.playerAvatarCollision != null)
            avatar.playerAvatarCollision.Scale = Vector3.one;
        if (avatar.PlayerVisionTarget != null)
        {
            avatar.PlayerVisionTarget.TargetPosition = 1.5f;
            avatar.PlayerVisionTarget.TargetRotation = Quaternion.identity;
        }

        Entries[slot] = new Entry
        {
            Avatar = avatar,
            Slot = slot,
            Colour = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f),
        };
        return false;
    }

    // ---- Every frame ----

    internal static void Tick(float dt)
    {
        if (Entries.Count == 0 || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        foreach (var entry in Entries.Values)
        {
            if (entry.Avatar == null)
                continue;
            BlockGrabbing(entry.Avatar);
            Paint(entry);
            if (!IsHost)
                continue;
            CheckOnce(entry);
            HostHealth(entry, dt);
            if (entry.Avatar.isDisabled || !entry.Avatar.spawned)
                continue;
            entry.Mind ??= Bot.Attach(entry.Avatar, entry.Slot);
            HostBoost(entry);
            HostTumble(entry);
            Apply(entry, entry.Avatar.clientPosition, entry.Avatar.clientRotation, Flags(entry), entry.Avatar.rbVelocity);
        }
        if (IsHost)
            Send();
    }

    // Players cannot take hold of a bot: neither the body while it tumbles, nor the part that gives health when grabbed.
    private static void BlockGrabbing(PlayerAvatar avatar)
    {
        if (avatar.tumble != null && avatar.tumble.physGrabObject != null)
            avatar.tumble.physGrabObject.grabDisableTimer = 1f;
        var grab = avatar.healthGrab;
        if (grab == null)
            return;
        if (grab.staticGrabObject != null && grab.staticGrabObject.enabled)
            grab.staticGrabObject.enabled = false;
        if (grab.physCollider != null && grab.physCollider.enabled)
            grab.physCollider.enabled = false;
    }

    // No cosmetics and one flat colour. Each machine does this itself, from the colour in the spawn data.
    private static void Paint(Entry entry)
    {
        var avatar = entry.Avatar;
        if (!entry.BodyTinted && avatar.playerCosmetics != null)
        {
            entry.BodyTinted = true;
            Tint(avatar.playerCosmetics, entry.Colour);
        }
        var head = avatar.playerDeathHead;
        if (!entry.HeadTinted && head != null && head.setup)
        {
            entry.HeadTinted = true;
            var cosmetics = head.GetComponentInChildren<PlayerCosmetics>(true);
            if (cosmetics != null)
                Tint(cosmetics, entry.Colour);
        }
    }

    private static void Tint(PlayerCosmetics cosmetics, Color colour)
    {
        cosmetics.SetupCosmeticsLogic(Array.Empty<int>(), false); // also prepares the materials that the hit and heal flashes use
        foreach (var part in cosmetics.playerMaterials)
        {
            if (part == null || part.material == null || !part.tintable || part.material.name.Contains("Health"))
                continue;
            if (part.material.HasProperty("_AlbedoColor"))
                part.material.SetColor("_AlbedoColor", colour);
            if (part.material.HasProperty("_AlbedoTexture"))
                part.material.SetTexture("_AlbedoTexture", Texture2D.whiteTexture);
            if (part.material.HasProperty("_FresnelColor"))
                part.material.SetColor("_FresnelColor", colour);
        }
        // Counts as having colours, or the game paints the default ones over this after ten seconds.
        for (var i = 0; i < cosmetics.colorsEquipped.Length; i++)
            cosmetics.colorsEquipped[i] = 0;
    }

    private static void CheckOnce(Entry entry)
    {
        if (entry.Checked)
            return;
        entry.Checked = true;
        var avatar = entry.Avatar;
        var controller = PlayerController.instance;
        var fine = !avatar.photonView.IsMine && !avatar.isLocal && (controller == null || controller.playerAvatarScript != avatar) && PlayerAvatar.instance != avatar;
        Hunted.Logger.Log(fine ? BepInEx.Logging.LogLevel.Info : BepInEx.Logging.LogLevel.Error,
            $"{avatar.playerName}: remote={!avatar.photonView.IsMine && !avatar.isLocal}, host's controller untouched={controller == null || controller.playerAvatarScript != avatar}, " +
            $"tumble body={avatar.tumble != null}, head={avatar.playerDeathHead != null}, at {avatar.clientPosition}.");
    }

    // ---- What the owner's machine does for a real player; the host does it for a bot ----

    private static void HostHealth(Entry entry, float dt)
    {
        var health = entry.Avatar.playerHealth;
        var (max, regen) = PlayerHealthRules.Values();
        if (!entry.HealthSet)
        {
            entry.HealthSet = true;
            SetHealth(health, max, max, effect: false);
            return;
        }
        if (regen <= 0f || entry.Avatar.isDisabled || entry.Avatar.deadSet || health.health <= 0 || health.health >= health.maxHealth)
        {
            entry.Regen = 0f;
            return;
        }
        entry.Regen += regen * dt;
        var whole = Mathf.FloorToInt(entry.Regen);
        if (whole < 1)
            return;
        entry.Regen -= whole;
        SetHealth(health, health.health + whole, health.maxHealth, effect: false);
    }

    // Goes through the game's own message, to everyone including the host: that is what plays the hurt sound and flash, moves the health bar and reveals the bot to the killer.
    private static void SetHealth(PlayerHealth health, int value, int max, bool effect, bool hurtByHeal = false)
        => health.photonView.RPC("UpdateHealthRPC", RpcTarget.All, Mathf.Clamp(value, 0, max), max, effect, hurtByHeal);

    internal static void Hurt(PlayerHealth health, int damage, bool savingGrace, int enemyIndex, bool hurtByHeal)
    {
        var avatar = health.playerAvatar;
        if (!IsHost || health.invincibleTimer > 0f || damage <= 0 || avatar.deadSet || health.godMode || GameDirector.instance.currentState != GameDirector.gameState.Main
            || (enemyIndex >= 0 && avatar.tumble != null && avatar.tumble.CocoonHeld() && enemyIndex == SemiFunc.EnemyGetIndex(avatar.tumble.cocoonOwner.enemy)))
            return;
        var left = health.health - damage;
        if (savingGrace && damage <= 25 && health.health > 5 && health.health <= 20 && left <= 0)
            left = Random.Range(1, 5);
        Hunted.Logger.LogInfo($"{avatar.playerName} took {damage} damage: {Mathf.Max(0, left)}/{health.maxHealth} left.");
        if (left <= 0)
        {
            avatar.PlayerDeath(enemyIndex);
            return;
        }
        var before = health.health;
        SetHealth(health, left, health.maxHealth, effect: true, hurtByHeal);
        var entry = Of(avatar);
        if (entry == null)
            return;
        entry.Mind?.OnHurt();
        // Like a player: hit down to the threshold after being above it, the boost starts.
        if (entry.Armed && left < before && left <= health.maxHealth * Tuning.Boost.HealthFraction)
        {
            entry.Armed = false;
            StartBoost(entry);
        }
    }

    internal static void Heal(PlayerHealth health, int amount, bool effect)
    {
        if (IsHost && amount > 0 && health.health != health.maxHealth && !health.playerAvatar.isDisabled)
            SetHealth(health, health.health + amount, health.maxHealth, effect);
    }

    internal static void Died(PlayerHealth health)
    {
        if (!IsHost)
            return;
        Hunted.Logger.LogInfo($"{health.playerAvatar.playerName} died.");
        SetHealth(health, 0, health.maxHealth, effect: true);
    }

    internal static void Revived(PlayerAvatar avatar)
    {
        if (!IsHost)
            return;
        var (max, _) = PlayerHealthRules.Values();
        SetHealth(avatar.playerHealth, Mathf.Max(1, max / 2), max, effect: false);
        Hunted.Logger.LogInfo($"{avatar.playerName} was revived.");
        var entry = Of(avatar);
        if (entry == null)
            return;
        entry.Armed = true;
        StartBoost(entry);
    }

    private static Entry? Of(PlayerAvatar avatar) => Entries.Values.FirstOrDefault(e => e.Avatar == avatar);

    private static void StartBoost(Entry entry)
    {
        var seconds = SurvivorBoost.Seconds();
        if (seconds <= 0f || entry.BoostUntil > Time.time)
            return;
        entry.BoostUntil = Time.time + seconds;
        Hunted.Logger.LogDebug($"{entry.Avatar.playerName}'s boost starts.");
    }

    // A boost ends any tumble it did not start itself, and being back above the threshold allows the next one.
    private static void HostBoost(Entry entry)
    {
        var avatar = entry.Avatar;
        if (entry.BoostUntil > Time.time && avatar.isTumbling && avatar.tumble != null && Time.time >= entry.GetUpAt)
        {
            entry.GetUpAt = Time.time + 0.2f;
            avatar.tumble.TumbleRequest(_isTumbling: false, _playerInput: false);
        }
        var health = avatar.playerHealth;
        if (!entry.Armed && health.health > health.maxHealth * Tuning.Boost.HealthFraction)
            entry.Armed = true;
    }

    /// <summary>The seconds of boost a bot has until, on the host.</summary>
    internal static float BoostUntil(int slot) => Entries.TryGetValue(slot, out var entry) ? entry.BoostUntil : 0f;

    internal static bool IsBoosted(PlayerAvatar avatar)
    {
        var entry = Of(avatar);
        return entry != null && (IsHost ? entry.BoostUntil > Time.time : entry.Boosted);
    }

    internal static void Knock(PlayerAvatar avatar, Vector3 force) => Of(avatar)?.Mind?.Knock(force);

    /// <summary>The mind says where the body is and what it is doing: what a player's own machine reads off its controller.</summary>
    internal static void Drive(PlayerAvatar avatar, Vector3 position, Quaternion rotation, Bot.StanceKind stance, bool moving, bool sprinting, Vector3 localVelocity)
    {
        avatar.clientPosition = position;
        avatar.clientRotation = rotation;
        avatar.rbVelocity = localVelocity;
        avatar.SetState(stance != Bot.StanceKind.Stand, sprinting, stance == Bot.StanceKind.Crawl, false, moving);
    }

    // A player's own controller rides along with their tumbling body and stands up on a key press; these are the same two things.
    private static void HostTumble(Entry entry)
    {
        var avatar = entry.Avatar;
        var tumble = avatar.tumble;
        if (tumble == null)
            return;
        if (avatar.isTumbling)
        {
            entry.WasTumbling = true;
            avatar.clientPosition = tumble.transform.position + Vector3.down * 0.3f;
            if (tumble.notMovingTimer > 1.5f && !tumble.tumbleOverride && tumble.physGrabObject.playerGrabbing.Count == 0 && Time.time >= entry.GetUpAt
                && NavMesh.SamplePosition(avatar.clientPosition, out _, 4f, NavMesh.AllAreas)) // nowhere to stand: it lies there until a hazard or a fall settles it
            {
                entry.GetUpAt = Time.time + 0.5f;
                tumble.TumbleRequest(_isTumbling: false, _playerInput: true);
            }
        }
        else if (entry.WasTumbling)
        {
            entry.WasTumbling = false;
            // Without a controller and its gravity, it stands on the floor only if it is put there.
            if (NavMesh.SamplePosition(avatar.clientPosition, out var hit, 2f, NavMesh.AllAreas))
                avatar.clientPosition = hit.position;
        }
    }

    // ---- What the owner's machine sends for a real player ----

    private static int Flags(Entry entry)
    {
        var avatar = entry.Avatar;
        return (avatar.isCrouching ? 1 : 0) | (avatar.isSprinting ? 2 : 0) | (avatar.isCrawling ? 4 : 0) | (avatar.isSliding ? 8 : 0) | (avatar.isMoving ? 16 : 0) | 32 | (entry.BoostUntil > Time.time ? 64 : 0);
    }

    private static void Apply(Entry entry, Vector3 position, Quaternion rotation, int flags, Vector3 localVelocity)
    {
        var avatar = entry.Avatar;
        entry.Boosted = (flags & 64) != 0;
        avatar.rbVelocity = localVelocity;
        avatar.rbVelocityRaw = rotation * localVelocity;
        avatar.InputAnalogAmount = 1f;
        avatar.clientPosition = position;
        avatar.clientRotation = rotation;
        avatar.isGrounded = (flags & 32) != 0;
        avatar.SetState((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0, (flags & 16) != 0);

        var vision = avatar.PlayerVisionTarget;
        var height = vision == null ? 1.5f : avatar.isCrawling ? vision.CrawlPosition : avatar.isCrouching ? vision.CrouchPosition : vision.StandPosition;
        if (avatar.playerAvatarCollision != null)
            avatar.playerAvatarCollision.Scale = avatar.isCrouching ? new Vector3(1f, 0.25f, 1f) : Vector3.one;
        avatar.clientCameraPosition = position + Vector3.up * height;
        avatar.clientCameraRotation = rotation;
        if (avatar.flashlightLightAim != null)
            avatar.flashlightLightAim.clientAimPoint = position + Vector3.up * height + rotation * Vector3.forward * 6f;
        if (avatar.PlayerVisionTarget != null)
        {
            avatar.PlayerVisionTarget.TargetPosition = height;
            avatar.PlayerVisionTarget.TargetRotation = rotation;
        }
    }

    private static void Send()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount < 2 || Time.time < _sendAt)
            return;
        _sendAt = Time.time + SendInterval;
        var live = Entries.Values.Where(e => e.Avatar != null && !e.Avatar.isDisabled && e.Avatar.spawned).ToList();
        if (live.Count == 0)
            return;
        var data = new object[live.Count * FieldsPerBot];
        var i = 0;
        foreach (var entry in live)
        {
            data[i++] = entry.Slot;
            data[i++] = entry.Avatar.clientPosition;
            data[i++] = entry.Avatar.clientRotation;
            data[i++] = Flags(entry);
            data[i++] = entry.Avatar.rbVelocity;
        }
        PhotonNetwork.RaiseEvent(StateEvent, data, ToOthers, SendOptions.SendUnreliable);
    }

    private static void OnEvent(EventData message)
    {
        if (message.Code != StateEvent || PhotonNetwork.IsMasterClient || message.CustomData is not object[] data)
            return;
        for (var i = 0; i + FieldsPerBot <= data.Length; i += FieldsPerBot)
        {
            if (data[i] is int slot && data[i + 1] is Vector3 position && data[i + 2] is Quaternion rotation && data[i + 3] is int flags && data[i + 4] is Vector3 velocity
                && Entries.TryGetValue(slot, out var entry) && entry.Avatar != null)
                Apply(entry, position, rotation, flags, velocity);
        }
    }

    // ---- Lists that are about real people ----

    /// <summary>The player list without the bots: for voice routing and the network's view of who is looking where, which are about real machines.</summary>
    public static List<PlayerAvatar> WithoutBots(List<PlayerAvatar> source)
    {
        if (Views.Count == 0)
            return source;
        // Only changed when its contents do, because one of these lists may still be being walked through further up the call.
        var kept = 0;
        var same = true;
        foreach (var player in source)
        {
            if (Is(player))
                continue;
            if (kept >= RealPlayers.Count || RealPlayers[kept] != player)
            {
                same = false;
                break;
            }
            kept++;
        }
        if (same && kept == RealPlayers.Count)
            return RealPlayers;
        RealPlayers.Clear();
        foreach (var player in source)
            if (!Is(player))
                RealPlayers.Add(player);
        return RealPlayers;
    }

    internal static PlayerAvatar? RealPlayerOf(Player? photonPlayer)
        => GameDirector.instance.PlayerList.FirstOrDefault(p => p != null && p.photonView.Owner == photonPlayer && !Is(p));
}

[HarmonyPatch]
static class BotAvatarPatches
{
    // Photon

    [HarmonyPostfix, HarmonyPatch(typeof(PhotonView), nameof(PhotonView.ViewID), MethodType.Setter)]
    private static void ViewID_Postfix(PhotonView __instance, int value) => BotAvatar.ViewAssigned(__instance, value);

    [HarmonyPostfix, HarmonyPatch(typeof(PhotonView), nameof(PhotonView.ControllerActorNr), MethodType.Setter)]
    private static void Controller_Postfix(PhotonView __instance) => BotAvatar.KeepRemote(__instance);

    // Spawning

    [HarmonyPrefix, HarmonyPatch(typeof(LevelGenerator), nameof(LevelGenerator.PlayerSpawn))]
    private static void PlayerSpawn_Prefix(LevelGenerator __instance) => BotAvatar.HostSpawnAll(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAvatar), "Awake")]
    private static bool Awake_Prefix(PlayerAvatar __instance) => BotAvatar.Wake(__instance);

    // Health: for a real player these run on their own machine; for a bot, on the host.

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    private static bool Hurt_Prefix(PlayerHealth __instance, int damage, bool savingGrace, int enemyIndex, bool hurtByHeal)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        BotAvatar.Hurt(__instance, damage, savingGrace, enemyIndex, hurtByHeal);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.HurtOtherRPC))]
    private static bool HurtOtherRPC_Prefix(PlayerHealth __instance, int damage, Vector3 hurtPosition, bool savingGrace, int enemyIndex, bool hurtByHeal, PhotonMessageInfo _info)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        if (SemiFunc.MasterAndOwnerOnlyRPC(_info, __instance.photonView) && (hurtPosition == Vector3.zero || Vector3.Distance(__instance.playerAvatar.transform.position, hurtPosition) < 2f))
            BotAvatar.Hurt(__instance, damage, savingGrace, enemyIndex, hurtByHeal);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Heal))]
    private static bool Heal_Prefix(PlayerHealth __instance, int healAmount, bool effect)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        BotAvatar.Heal(__instance, healAmount, effect);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.HealOtherRPC))]
    private static bool HealOtherRPC_Prefix(PlayerHealth __instance, int healAmount, bool effect, PhotonMessageInfo _info)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        if (SemiFunc.MasterAndOwnerOnlyRPC(_info, __instance.photonView))
            BotAvatar.Heal(__instance, healAmount, effect);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Death))]
    private static bool Death_Prefix(PlayerHealth __instance)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        BotAvatar.Died(__instance);
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ReviveRPC))]
    private static void ReviveRPC_Postfix(PlayerAvatar __instance)
    {
        if (BotAvatar.Is(__instance))
            BotAvatar.Revived(__instance);
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.BreakImpact))]
    private static bool BreakImpact_Prefix(PlayerTumble __instance)
    {
        if (!BotAvatar.Is(__instance.playerAvatar))
            return true;
        if (SemiFunc.IsMasterClient() && __instance.impactHurtTimer > 0f && !__instance.CocoonHeld())
        {
            BotAvatar.Hurt(__instance.playerAvatar.playerHealth, __instance.impactHurtDamage, true, -1, false);
            __instance.impactHurtTimer = 0f;
        }
        return false;
    }

    // Everything that hurts players checks only for its own machine's avatar. On the host, a bot counts as that for the length of this one call.

    [HarmonyPrefix, HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    private static void PlayerHurt_Prefix(PlayerAvatar _player, out bool __state)
    {
        __state = PhotonNetwork.IsMasterClient && BotAvatar.Is(_player);
        if (!__state)
            return;
        BotAvatar.InHostHurt = true;
        BotAvatar.HurtTarget = _player;
        BotAvatar.SetIsMine(_player.photonView, true);
    }

    [HarmonyFinalizer, HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    private static void PlayerHurt_Finalizer(PlayerAvatar _player, bool __state)
    {
        if (!__state)
            return;
        BotAvatar.SetIsMine(_player.photonView, false);
        BotAvatar.InHostHurt = false;
    }

    // The one line of that hit code that pushes the local player directly: it pushes the bot instead.
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerController), nameof(PlayerController.ForceImpulse))]
    private static bool ForceImpulse_Prefix(Vector3 force)
    {
        if (!BotAvatar.InHostHurt)
            return true;
        BotAvatar.Knock(BotAvatar.HurtTarget!, force);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAvatar), "ForceImpulseRPC")]
    private static bool ForceImpulseRPC_Prefix(PlayerAvatar __instance, Vector3 _force)
    {
        if (!BotAvatar.Is(__instance))
            return true;
        if (PhotonNetwork.IsMasterClient)
            BotAvatar.Knock(__instance, _force);
        return false;
    }

    // Lookups that are about real people

    [HarmonyPostfix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.PlayerAvatarGetFromPhotonPlayer))]
    private static void FromPhotonPlayer_Postfix(Player photonPlayer, ref PlayerAvatar __result)
    {
        if (BotAvatar.Is(__result))
            __result = BotAvatar.RealPlayerOf(photonPlayer)!;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(StatsManager), nameof(StatsManager.PlayersAddAll))]
    private static bool PlayersAddAll_Prefix(StatsManager __instance)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer())
            return false;
        // The game's own loop, minus the bots (since R.E.P.O. v0.5.1 it no longer hands out player colours here).
        foreach (var player in BotAvatar.WithoutBots(GameDirector.instance.PlayerList))
            if (!string.IsNullOrEmpty(player.steamID))
                __instance.PlayerAdd(player.steamID, player.playerName);
        return false;
    }
}

/// <summary>Voice routing and the network's camera bookkeeping expect each avatar to be a machine that talks and reports its camera. Bots are left out of those.</summary>
[HarmonyPatch]
static class BotAvatarRealPlayerLists
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerCameraRemoteRefresh));
        yield return AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.NetworkThrottleViewpointsTick));
        yield return AccessTools.Method(typeof(PlayerVoiceChat), nameof(PlayerVoiceChat.VoiceTargetPlayersTick));
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = AccessTools.Field(typeof(GameDirector), nameof(GameDirector.PlayerList));
        var filter = AccessTools.Method(typeof(BotAvatar), nameof(BotAvatar.WithoutBots));
        foreach (var instruction in instructions)
        {
            yield return instruction;
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand as FieldInfo == list)
                yield return new CodeInstruction(OpCodes.Call, filter);
        }
    }
}
