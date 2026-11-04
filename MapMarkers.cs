using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Keeps every generator's marker on the map in step with its state, the way vanilla does for its single extraction point:
/// grey while idle, the red/orange rectangle while it is active, green once it is complete. Vanilla only switches them in the
/// moment a state starts, and skips any marker the map has not built yet, so with every point opening at the start of the
/// level the markers could stay missing.
/// </summary>
internal static class MapMarkers
{

    private static readonly Dictionary<ExtractionPoint, string> Reported = new();

    internal static void Reset()
    {
        Reported.Clear();
    }

    private static bool Shown(MapObject mapObject)
    {
        foreach (Transform child in mapObject.transform)
            if (child.gameObject.activeSelf)
                return true;
        return false;
    }

    internal static void Tick()
    {
        if (!KillerRole.ModeActive || !ExtractionState.Active)
            return;

        foreach (var point in ExtractionPoints.Get())
        {
            if (point == null)
                continue;
            var wanted = point.currentState switch
            {
                ExtractionPoint.State.None or ExtractionPoint.State.Idle => 0,
                ExtractionPoint.State.Complete => 2,
                _ => 1,
            };
            var groups = new[] { point.mapInactive, point.mapActive, point.mapUsed };
            var floors = groups.SelectMany(g => g ?? new DirtFinderMapFloor[0]).ToList();
            // Wait until the map has built every piece of this marker.
            var built = floors.Count(f => f != null && f.MapObject != null);
            var report = $"{point.currentState}: inactive {point.mapInactive?.Length ?? 0}, active {point.mapActive?.Length ?? 0}, used {point.mapUsed?.Length ?? 0} pieces, {built} of {floors.Count} built on the map";
            if (!Reported.TryGetValue(point, out var reported) || reported != report)
            {
                Reported[point] = report;
                Hunted.Logger.LogDebug($"Map marker of the extraction point at {point.transform.position}: {report}");
            }
            if (floors.Count == 0 || built != floors.Count)
                continue;

            // Vanilla hides the active and used pieces a second after generation, so look at what is really shown.
            var correct = true;
            for (var i = 0; i < groups.Length && correct; i++)
                foreach (var floor in groups[i] ?? new DirtFinderMapFloor[0])
                    if (floor.MapObject.transform.childCount > 0 && Shown(floor.MapObject) != (i == wanted))
                        correct = false;
            if (correct)
                continue;
            Hunted.Logger.LogDebug($"Map marker of the extraction point at {point.transform.position} set to {(wanted == 0 ? "inactive" : wanted == 1 ? "active" : "used")}.");

            for (var i = 0; i < groups.Length; i++)
                foreach (var floor in groups[i] ?? new DirtFinderMapFloor[0])
                {
                    if (i == wanted)
                        floor.MapObject.Show();
                    else
                        floor.MapObject.Hide();
                }
        }
    }
}
