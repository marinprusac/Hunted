using System.Collections.Generic;
using System.Globalization;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The survivors' second wind, like the boost after a hit in Dead by Daylight. A survivor who is revived, or is hit down to the
/// health threshold, runs without tiring for a few seconds and cannot be tumbled by anything but themselves. When it ends their
/// stamina is topped up. The owner's client runs it and tells everyone through a player property, because the tumbles it stops are
/// started on other clients (the host runs the monsters).
/// </summary>
[HarmonyPatch]
internal static class SurvivorBoost
{
    private const string RoomKey = "Hunted.Boost";
    private const string PlayerKey = "Hunted.BoostUntil";
    internal static readonly Color Gold = new(1f, 0.85f, 0.15f);

    private static float _until; // local player, Time.time
    private static readonly Dictionary<PlayerAvatar, float> Pulse = new();
    private static readonly HashSet<PlayerAvatar> Seen = new();

    // A hit only starts the boost when the player has been above half their maximum health since the last one.
    private static bool _armed = true;

    /// <summary>A revive puts the player back at half their maximum health, which counts as being back at the threshold.</summary>
    internal static void Rearm() => _armed = true;

    internal static void Reset()
    {
        _armed = true;
        _until = 0f;
        Pulse.Clear();
        Seen.Clear();
        Publish(0.0);
    }

    /// <summary>Host publishes its settings; everyone else reads them.</summary>
    internal static void Sync()
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            RoomSync.Set(RoomKey, HuntedConfig.BoostSeconds.Value.ToString(CultureInfo.InvariantCulture));
    }

    internal static float Seconds()
    {
        var text = RoomSync.Get<string>(RoomKey);
        if (text != null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return seconds;
        return HuntedConfig.BoostSeconds.Value;
    }

    /// <summary>The boost triggers at a fraction of the player's maximum health or less (half; see Tuning.json).</summary>
    private static bool AtOrBelowThreshold(PlayerHealth health) => health.health <= health.maxHealth * Tuning.Boost.HealthFraction;

    private static void Publish(double until)
    {
        if (!GameManager.Multiplayer() || !PhotonNetwork.InRoom)
            return;
        // Reset runs every half second outside of levels; only a change is worth a message to everyone.
        var current = PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PlayerKey, out var value) && value is double known ? known : 0.0;
        if (current == until)
            return;
        PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { PlayerKey, until } });
    }

    internal static bool IsBoosted(PlayerAvatar? avatar)
    {
        if (avatar == null || !KillerRole.ModeActive)
            return false;
        if (BotAvatar.Is(avatar))
            return BotAvatar.IsBoosted(avatar); // its owner is the host, whose own boost is not the bot's
        if (avatar.isLocal)
            return _until > Time.time;
        var owner = avatar.photonView != null ? avatar.photonView.Owner : null;
        return owner != null && owner.CustomProperties.TryGetValue(PlayerKey, out var value) && value is double until && until > PhotonNetwork.Time;
    }

    /// <summary>Starts the boost for the local player (does nothing for the killer or if it is already running).</summary>
    internal static bool Start(PlayerAvatar avatar)
    {
        var seconds = Seconds();
        if (!KillerRole.ModeActive || !avatar.isLocal || seconds <= 0f || KillerRole.IsKiller(avatar) || _until > Time.time)
            return false;
        _until = Time.time + seconds;
        Publish(PhotonNetwork.Time + seconds);
        Hunted.Logger.LogDebug($"Boost for {seconds:0.#} s.");
        StandUp(avatar);
        return true;
    }

    private static float _standUpTimer;
    private static bool _selfTumble; // the local player's current tumble was started by their own key

    // The game only keeps whether a tumble came from the player's key on the host, so each client notes it for its own player.
    [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleSetRPC))]
    [HarmonyPostfix]
    private static void TumbleSetRPC_Postfix(PlayerTumble __instance, bool _isTumbling, bool _playerInput)
    {
        if (__instance.playerAvatar != null && __instance.playerAvatar.isLocal)
            _selfTumble = _isTumbling && _playerInput;
    }

    /// <summary>Ends a tumble the player did not start themselves (a tumble they asked for with the key stays).</summary>
    private static void StandUp(PlayerAvatar avatar)
    {
        if (avatar.tumble != null && avatar.isTumbling && !_selfTumble)
            avatar.tumble.TumbleRequest(_isTumbling: false, _playerInput: false);
    }

    internal static void Tick()
    {
        if (!KillerRole.ModeActive)
            return;

        var local = PlayerAvatar.instance;
        if (!_armed && local != null && local.playerHealth != null && !AtOrBelowThreshold(local.playerHealth))
            _armed = true;
        if (_until > 0f)
        {
            var controller = PlayerController.instance;
            if (local != null && _until > Time.time && !local.isDisabled)
            {
                // A tumble that was already started when the boost began (or got in before the others' client heard about the
                // boost) ends at once.
                _standUpTimer -= Time.deltaTime;
                if (_standUpTimer <= 0f)
                {
                    _standUpTimer = 0.2f;
                    StandUp(local);
                }
                if (controller != null)
                    controller.EnergyCurrent = controller.EnergyStart;
                // The screen turns gold for as long as it lasts (gold edges and a gold wash, as the Everseer's screen turns purple under its mask).
                ScreenTint.Request(Gold, 0.12f, 0.45f);
            }
            else
            {
                _until = 0f;
                Publish(0.0);
                if (controller != null)
                    controller.EnergyCurrent = controller.EnergyStart;
            }
        }

        // Every boosted player glows gold, pulsing, on everybody's screen.
        foreach (var avatar in SemiFunc.PlayerGetList())
        {
            var boosted = avatar != null && !avatar.isDisabled && IsBoosted(avatar);
            if (avatar != null && boosted != Seen.Contains(avatar))
            {
                if (boosted)
                    Seen.Add(avatar);
                else
                    Seen.Remove(avatar);
                Hunted.Logger.LogDebug($"Boost {(boosted ? "seen on" : "ended on")} {avatar.playerName} (local: {avatar.isLocal}).");
            }
            if (!boosted)
                continue;
            Pulse.TryGetValue(avatar, out var next);
            if (Time.time < next)
                continue;
            Pulse[avatar] = Time.time + 0.6f;
            avatar.playerHealth.SetMaterialSpecial(Gold);
        }
    }

    // A hit that leaves the survivor alive and at or below the threshold.
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    [HarmonyPrefix]
    private static void Hurt_Prefix(PlayerHealth __instance, out int __state) => __state = __instance.health;

    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    [HarmonyPostfix]
    private static void Hurt_Postfix(PlayerHealth __instance, int __state)
    {
        if (!KillerRole.ModeActive || __instance.playerAvatar == null || !__instance.playerAvatar.isLocal)
            return;
        var health = __instance.health;
        if (_armed && health > 0 && health < __state && AtOrBelowThreshold(__instance) && Start(__instance.playerAvatar))
            _armed = false; // not again until the player is back above the threshold
    }

    // Tumbles that come from outside (monsters, falls, items...) are not requested by the player; the player's own are.
    [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleRequest))]
    [HarmonyPrefix]
    private static bool TumbleRequest_Prefix(PlayerTumble __instance, bool _isTumbling, bool _playerInput)
        => !(_isTumbling && !_playerInput && IsBoosted(__instance.playerAvatar));

    [HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleSet))]
    [HarmonyPrefix]
    private static bool TumbleSet_Prefix(PlayerTumble __instance, bool _isTumbling, bool _playerInput, bool _reset)
        => !(_isTumbling && !_playerInput && !_reset && IsBoosted(__instance.playerAvatar));
}
