using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Only small valuables exist. The vanilla spawner is switched off and the host fills the small-valuable slots itself,
/// then spawns one more on a random empty slot every few seconds.
/// </summary>
internal static class ValuableSpawner
{
    private static bool _initialFillStarted;
    private static float _respawnTimer;
    private static readonly List<ValuableVolume> Slots = new();
    // True while a valuable is being spawned on a slot in the killer room. The game rolls the value inside the spawn call itself.
    private static bool _spawningInKillerRoom;

    internal static void Reset()
    {
        _initialFillStarted = false;
        _respawnTimer = 0f;
        Slots.Clear();
        _spawningInKillerRoom = false;
    }

    /// <summary>Doubles the value of a valuable that spawned in the killer room, once its value has been rolled (host only).</summary>
    internal static void ApplyRoomBonus(ValuableObject valuable)
    {
        if (!_spawningInKillerRoom)
            return;
        var multiplier = Tuning.Valuables.KillerRoomValueMultiplier;
        valuable.dollarValueOriginal *= multiplier;
        valuable.dollarValueCurrent *= multiplier;
    }

    internal static void Tick(float deltaTime)
    {
        var director = ValuableDirector.instance;
        if (!SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive || director == null || !director.setupComplete
            || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        if (!_initialFillStarted)
        {
            _initialFillStarted = true;
            _respawnTimer = Tuning.Valuables.RespawnSeconds;
            Slots.AddRange(Object.FindObjectsByType<ValuableVolume>(FindObjectsSortMode.None)
                .Where(v => v.VolumeType == ValuableVolume.Type.Small));
            Hunted.Logger.LogInfo($"{Slots.Count} small valuable slots, {director.smallValuables.Count} small valuable prefabs.");
            Hunted.Instance.StartCoroutine(InitialFill(director));
            return;
        }

        _respawnTimer -= deltaTime;
        if (_respawnTimer > 0f)
            return;
        _respawnTimer = Tuning.Valuables.RespawnSeconds;
        TrySpawnOne(director);
    }

    private static IEnumerator InitialFill(ValuableDirector director)
    {
        var target = Mathf.RoundToInt(Slots.Count * Tuning.Valuables.InitialFillPercent / 100f);
        var spawned = 0;
        // Players are not placed yet when a slot is checked, so the distance rule is skipped for the initial fill.
        foreach (var slot in Slots.OrderBy(_ => Random.value))
        {
            if (spawned >= target)
                break;
            if (Spawn(director, slot))
                spawned++;
            if (spawned % 5 == 0)
                yield return null;
        }
        Hunted.Logger.LogInfo($"Initial fill: {spawned}/{Slots.Count} small valuables.");
    }

    private static bool TrySpawnOne(ValuableDirector director)
    {
        var players = SemiFunc.PlayerGetList();
        var minDistance = Tuning.Valuables.MinPlayerDistance;
        foreach (var slot in Slots.Where(s => s != null).OrderBy(_ => Random.value))
        {
            if (players.Any(p => p != null && !p.isDisabled && Vector3.Distance(p.transform.position, slot.transform.position) < minDistance))
                continue;
            if (Spawn(director, slot))
                return true;
        }
        return false;
    }

    private static bool Spawn(ValuableDirector director, ValuableVolume slot)
    {
        if (slot == null || !slot.gameObject.activeInHierarchy || director.smallValuables.Count == 0)
            return false;
        if (slot.OverlapBoxCheck(SemiFunc.LayerMaskGetPhysGrabObject()).Length > 0)
            return false;
        var prefab = director.smallValuables[Random.Range(0, director.smallValuables.Count)];
        _spawningInKillerRoom = KillerRoom.Contains(slot.transform.position);
        try
        {
            return director.SpawnValuable(prefab, slot) != null;
        }
        finally
        {
            _spawningInKillerRoom = false;
        }
    }
}

[HarmonyPatch(typeof(ValuableDirector))]
static class ValuableDirectorPatches
{
    // Vanilla sets every valuable amount to 0 when the debug mode is None, so nothing spawns at level start.
    [HarmonyPrefix, HarmonyPatch(nameof(ValuableDirector.SetupHost))]
    private static void SetupHost_Prefix(ValuableDirector __instance)
    {
        if (!KillerRole.ModeActive)
            return;
        __instance.valuableDebug = ValuableDirector.ValuableDebug.None;
        // The cosmetic loot objects that turn up in levels are left out as well.
        __instance.cosmeticWorldObjectDebug = ValuableDirector.CosmeticWorldObjectDebug.None;
    }
}

[HarmonyPatch(typeof(ValuableObject), nameof(ValuableObject.DollarValueSetLogic))]
static class ValuableValuePatches
{
    // The value is rolled here on the host (inside the spawn call) and later sent to the other players, so a change made right after it is shared too.
    [HarmonyPrefix]
    private static void Prefix(ValuableObject __instance, out bool __state) => __state = !__instance.dollarValueSet;

    [HarmonyPostfix]
    private static void Postfix(ValuableObject __instance, bool __state)
    {
        if (__state && KillerRole.ModeActive && SemiFunc.IsMasterClientOrSingleplayer())
            ValuableSpawner.ApplyRoomBonus(__instance);
    }
}
