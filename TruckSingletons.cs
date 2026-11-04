using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The truck's screen, healer, charging station and safety spawn each keep a static "instance". With two start rooms the
/// second one overwrites them in whatever order the objects wake up. The first truck (the one at the origin) is kept as the
/// instance, so everything that looks them up keeps working as before.
/// </summary>
internal static class TruckSingletons
{
    private static bool _fixed;

    internal static void Reset() => _fixed = false;

    internal static void Tick()
    {
        if (_fixed || !KillerRole.ModeActive || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        _fixed = true;

        var screen = Closest<TruckScreenText>();
        if (screen != null) TruckScreenText.instance = screen;
        var healer = Closest<TruckHealer>();
        if (healer != null) TruckHealer.instance = healer;
        var station = Closest<ChargingStation>();
        if (station != null) ChargingStation.instance = station;
        var safety = Closest<TruckSafetySpawnPoint>();
        if (safety != null) TruckSafetySpawnPoint.instance = safety;

        var truckPoint = Object.FindObjectsByType<LevelPoint>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(p => p.Truck).OrderBy(p => p.transform.position.sqrMagnitude).FirstOrDefault();
        if (truckPoint != null)
            LevelGenerator.Instance.LevelPathTruck = truckPoint;
    }

    private static T? Closest<T>() where T : Component
        => Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .OrderBy(c => c.transform.position.sqrMagnitude).FirstOrDefault();
}
