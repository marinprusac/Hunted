using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

[HarmonyPatch]
static class TruckPatches
{
    internal static bool TrucksOpen => ExtractionState.TrucksOpen;

    // The truck door stays shut until the extractions are done.
    [HarmonyPrefix, HarmonyPatch(typeof(TruckDoor), "Update")]
    private static bool TruckDoor_Update_Prefix()
    {
        if (!KillerRole.ModeActive)
            return true;
        return TrucksOpen;
    }

    private static float _truckCheckTimer;

    /// <summary>Called every frame; keeps the local killer out of the truck.</summary>
    internal static void KeepKillerOutOfTruck()
    {
        if (!KillerRole.LocalIsKiller || !KillerRole.InPlayableLevel)
            return;

        _truckCheckTimer -= Time.unscaledDeltaTime;
        if (_truckCheckTimer > 0f)
            return;
        _truckCheckTimer = 0.2f;

        var avatar = SemiFunc.PlayerAvatarLocal();
        var generator = LevelGenerator.Instance;
        if (avatar == null || generator == null || !generator.Generated || !avatar.RoomVolumeCheck.inTruck)
            return;

        var here = PlayerController.instance.transform.position;
        var exit = generator.LevelPathPoints
            .Where(SpawnPlanner.IsOutsideTruck)
            .OrderBy(p => Vector3.Distance(p.transform.position, here))
            .FirstOrDefault();
        if (exit == null)
            return;

        var target = exit.transform.position + Vector3.up * 0.25f;
        PlayerController.instance.transform.position = target;
        PlayerController.instance.rb.position = target;
        Hunted.Logger.LogInfo("Killer tried to enter the truck and was moved out.");
    }
}
