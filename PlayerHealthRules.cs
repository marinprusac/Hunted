using System.Globalization;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// More health than vanilla's 100, slow regeneration, and a revive that gives back real health. Health is owned by each player's
/// own client, so every client applies the rules to its own player; the host's values reach them through the room.
/// </summary>
[HarmonyPatch]
internal static class PlayerHealthRules
{
    private const string RoomKey = "Hunted.Health";

    private static bool _applied;
    private static float _regenTimer;

    internal static void Reset()
    {
        _applied = false;
        _regenTimer = 0f;
    }

    /// <summary>Host publishes its settings; everyone else reads them.</summary>
    internal static void Sync()
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            RoomSync.Set(RoomKey, string.Join("|", HuntedConfig.MaxHealth.Value, HuntedConfig.HealthRegenPerSecond.Value.ToString(CultureInfo.InvariantCulture)));
    }

    internal static (int max, float regen) Values()
    {
        var text = RoomSync.Get<string>(RoomKey);
        if (text != null)
        {
            var parts = text.Split('|');
            if (parts.Length == 2 && int.TryParse(parts[0], out var max) && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var regen))
                return (max, regen);
        }
        return (HuntedConfig.MaxHealth.Value, HuntedConfig.HealthRegenPerSecond.Value);
    }

    private static PlayerHealth? Local()
    {
        var avatar = PlayerAvatar.instance;
        return avatar != null ? avatar.playerHealth : null;
    }

    internal static void SetHealth(PlayerHealth health, int value, int max)
    {
        health.maxHealth = max;
        health.health = Mathf.Clamp(value, 0, max);
        StatsManager.instance.SetPlayerHealth(SemiFunc.PlayerGetSteamID(health.playerAvatar), health.health, setInShop: false);
        if (GameManager.Multiplayer())
            health.photonView.RPC("UpdateHealthRPC", RpcTarget.Others, health.health, health.maxHealth, false, false);
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        var health = Local();
        if (health == null || !health.healthSet)
            return;
        var (max, regen) = Values();

        // Once per level: start with full health at the configured maximum.
        if (!_applied)
        {
            _applied = true;
            SetHealth(health, max, max);
            Hunted.Logger.LogDebug($"Health set to {max}/{max}.");
            return;
        }

        if (regen <= 0f || health.playerAvatar.isDisabled || health.health <= 0 || health.health >= health.maxHealth)
        {
            _regenTimer = 0f;
            return;
        }
        // Health is whole numbers, so the fraction carries over until it adds up to a point.
        _regenTimer += regen * dt;
        var whole = Mathf.FloorToInt(_regenTimer);
        if (whole < 1)
            return;
        _regenTimer -= whole;
        SetHealth(health, health.health + whole, health.maxHealth);
    }

    // Vanilla brings a revived player back with 1 health; here it is half of the maximum.
    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.ReviveRPC))]
    [HarmonyPostfix]
    private static void ReviveRPC_Postfix(PlayerAvatar __instance)
    {
        if (!KillerRole.ModeActive || !__instance.isLocal)
            return;
        var (max, _) = Values();
        SetHealth(__instance.playerHealth, Mathf.Max(1, max / 2), max);
        _regenTimer = 0f;
        SurvivorBoost.Rearm();
        SurvivorBoost.Start(__instance);
    }
}
