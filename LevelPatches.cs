using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>Bigger levels with N + extra extraction points.</summary>
[HarmonyPatch]
internal static class LevelPatches
{
    [HarmonyPatch(typeof(LevelGenerator), "TileGeneration")]
    [HarmonyPrefix]
    private static void TileGeneration_Prefix(LevelGenerator __instance)
    {
        if (!KillerRole.ModeActive)
            return;
        // The level's room, dead end and special counts all follow its module amount. Scale it to the number of rooms the grid will hold.
        var levels = RunManager.instance.levelsCompleted;
        var baseAmount = Mathf.Min(5 + levels, 10) + (levels >= 10 ? Mathf.Min(levels - 9, 5) : 0);
        __instance.DebugLevelSize = Mathf.Max(1f, MapGridSize.Rooms / (float)baseAmount);
    }

    // TileGeneration is a coroutine, so patch the compiler-generated MoveNext and wrap every read of ExtractionAmount.
    [HarmonyPatch]
    internal static class ExtractionAmountPatch
    {
        private static MethodBase TargetMethod()
            => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LevelGenerator), "TileGeneration"));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var field = AccessTools.Field(typeof(LevelGenerator), "ExtractionAmount");
            var hook = AccessTools.Method(typeof(ExtractionAmountPatch), nameof(Override));
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.opcode == OpCodes.Ldfld && instruction.operand as FieldInfo == field)
                    yield return new CodeInstruction(OpCodes.Call, hook);
            }
        }

        private static int _lastRequested = -1;

        /// <summary>Extraction points the survivors have to complete.</summary>
        internal static int Required => KillerRole.SurvivorCount + Tuning.Extraction.ExtraRequiredPoints;

        internal static int Desired(int original) => Override(original);

        public static int Override(int original)
        {
            if (!KillerRole.ModeActive)
                return original;
            // The start rooms' own extraction points are switched off (ExtractionPatches.Start_Prefix), so they count for nothing. The start rooms are spawned
            // after the tiles, so they can't be counted in the scene yet.
            var inStartRoom = SecondTruck.StartRoomCount * SecondTruck.ExtractionPointsPerStartRoom;
            var amount = Mathf.Max(0, Required + Tuning.Extraction.ExtraSpawnedPoints - inStartRoom);
            if (amount != _lastRequested)
            {
                _lastRequested = amount;
                Hunted.Logger.LogInfo($"Level generation: {amount} extraction points requested ({inStartRoom} already in the start rooms).");
            }
            return amount;
        }
    }
}

[HarmonyPatch(typeof(ExtractionPoint))]
static class ExtractionPatches
{
    // The extraction point that comes with each start room is switched off before it registers with the round, so the trucks
    // have no generator of their own. Runs on every client, because the room is spawned on all of them.
    [HarmonyPatch("Start")]
    [HarmonyPrefix]
    private static bool Start_Prefix(ExtractionPoint __instance)
    {
        if (!KillerRole.ModeActive || __instance.GetComponentInParent<StartRoom>() == null)
            return true;
        __instance.gameObject.SetActive(false);
        return false;
    }

    // Vanilla's extraction flow (Active, Success, Warning, Extracting, Complete) assumes one active point and reads the
    // shared haul in about 45 places. Each point's Update runs on its own, so for the duration of a generator's Update
    // the shared values are swapped for that point's own zone and put back afterwards.
    internal class Swap
    {
        internal List<GameObject> Saved = new();
        internal List<GameObject> Zone = new();
        internal int CurrentHaul;
        internal int CurrentHaulMax;
        internal ExtractionPoint? Current;
    }

    private static readonly Dictionary<ExtractionPoint, Collider?> ZoneColliders = new();

    /// <summary>True while a generator that is just opening runs its Update; vanilla sends enemies to every point it opens.</summary>
    internal static bool SuppressInvestigate;

    // Players do not open points with the button; the host opens all of them.
    [HarmonyPrefix, HarmonyPatch(nameof(ExtractionPoint.OnClick))]
    private static bool OnClick_Prefix() => !KillerRole.ModeActive;

    [HarmonyPrefix, HarmonyPatch("Update")]
    private static void Update_Prefix(ExtractionPoint __instance, out Swap? __state)
    {
        __state = null;
        var round = RoundDirector.instance;
        if (round == null || !KillerRole.ModeActive || !ExtractionPoints.Get().Contains(__instance))
            return;

        var swap = new Swap
        {
            Saved = round.dollarHaulList.ToList(),
            CurrentHaul = round.currentHaul,
            CurrentHaulMax = round.currentHaulMax,
            Current = round.extractionPointCurrent,
        };

        var zone = ZoneColliderOf(__instance);
        if (zone != null && zone.enabled && zone.gameObject.activeInHierarchy)
            swap.Zone = swap.Saved.Where(go => go != null && Inside(zone, go.transform.position)).ToList();

        round.dollarHaulList.Clear();
        round.dollarHaulList.AddRange(swap.Zone);
        round.currentHaul = 0;
        round.currentHaulMax = 0;
        foreach (var go in swap.Zone)
        {
            var valuable = go.GetComponent<ValuableObject>();
            if (valuable == null)
                continue;
            round.currentHaul += (int)valuable.dollarValueCurrent;
            round.currentHaulMax += (int)valuable.dollarValueOriginal;
        }
        round.extractionPointCurrent = __instance;
        SuppressInvestigate = __instance.stateStart && __instance.currentState == ExtractionPoint.State.Active;
        __state = swap;
    }

    // A finalizer runs even if Update throws, so the shared values are never left swapped.
    [HarmonyFinalizer, HarmonyPatch("Update")]
    private static System.Exception? Update_Finalizer(System.Exception? __exception, Swap? __state)
    {
        SuppressInvestigate = false;
        var round = RoundDirector.instance;
        if (__state == null || round == null)
            return __exception;

        // Valuables the point took out of the list (extracted and destroyed) must not come back.
        var removed = new HashSet<GameObject>(__state.Zone.Where(go => go == null || !round.dollarHaulList.Contains(go)));
        round.dollarHaulList.Clear();
        round.dollarHaulList.AddRange(__state.Saved.Where(go => go != null && !removed.Contains(go)));
        round.currentHaul = __state.CurrentHaul;
        round.currentHaulMax = __state.CurrentHaulMax;
        round.extractionPointCurrent = __state.Current;
        return __exception;
    }

    private static Collider? ZoneColliderOf(ExtractionPoint point)
    {
        if (!ZoneColliders.TryGetValue(point, out var collider) || collider == null)
        {
            collider = point.roomVolume != null ? point.roomVolume.GetComponentInChildren<Collider>(true) : null;
            ZoneColliders[point] = collider;
        }
        return collider;
    }

    private static bool Inside(Collider zone, Vector3 position)
        => Vector3.Distance(zone.ClosestPoint(position), position) < 0.05f;

    // Vanilla also sends enemies to every point when it opens; with every point opening at once that would be a stampede.
    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.EnemyInvestigate))]
    private static bool EnemyInvestigate_Prefix() => !SuppressInvestigate;
}
