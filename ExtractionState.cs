using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Hunted;

/// <summary>How many extraction points must be completed and what each one asks for. Host decides, everyone reads it.</summary>
internal static class ExtractionState
{
    private const string RoomKey = "Hunted.Gen";

    internal static int Goal { get; private set; }
    internal static int Required { get; private set; }

    internal static bool Active => Required > 0;
    internal static bool TrucksOpen
        => Active && RoundDirector.instance != null && RoundDirector.instance.extractionPointsCompleted >= Required;

    internal static void Reset()
    {
        Goal = 0;
        Required = 0;
    }

    /// <summary>Host only.</summary>
    internal static void Init(int required)
    {
        Goal = HuntedConfig.ExtractionGoalHundreds.Value * 100;
        Required = required;
        RoomSync.Set(RoomKey, $"{Goal}|{Required}");
    }

    /// <summary>Clients only.</summary>
    internal static void ReadFromRoom()
    {
        var text = RoomSync.Get<string>(RoomKey);
        if (text == null)
            return;
        var parts = text.Split('|');
        if (parts.Length == 2 && int.TryParse(parts[0], out var goal) && int.TryParse(parts[1], out var required))
        {
            Goal = goal;
            Required = required;
        }
    }

    /// <summary>
    /// Vanilla counts every extraction point it finds and splits the haul goal between them. Only the required ones count here, so
    /// the last required completion is the "all extractions done" moment, and each point asks for the same share.
    /// </summary>
    internal static void ApplyToRound()
    {
        if (Active && RoundDirector.instance != null && RoundDirector.instance.extractionPoints != Required)
            RoundDirector.instance.extractionPoints = Required;
    }
}

/// <summary>The extraction points that count as the objectives: all of them except any inside the truck, in a stable order.</summary>
internal static class ExtractionPoints
{
    private static readonly List<ExtractionPoint> List = new();
    private static int _builtFor;

    internal static void Reset()
    {
        List.Clear();
        _builtFor = 0;
    }

    /// <summary>True if the position is inside one of the truck's room volumes. The start room's extraction point sits outside it.</summary>
    internal static bool IsInsideTruck(Vector3 position)
    {
        foreach (var volume in Object.FindObjectsByType<RoomVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!volume.Truck)
                continue;
            foreach (var collider in volume.GetComponentsInChildren<Collider>(true))
                if (collider.bounds.Contains(position))
                    return true;
        }
        return false;
    }

    internal static IReadOnlyList<ExtractionPoint> Get()
    {
        var generator = LevelGenerator.Instance;
        if (generator == null || !generator.Generated || RoundDirector.instance == null)
            return List;
        if (_builtFor == generator.GetInstanceID())
            return List;

        _builtFor = generator.GetInstanceID();
        List.Clear();
        List.AddRange(RoundDirector.instance.extractionPointList
            .Where(go => go != null)
            .Select(go => go.GetComponent<ExtractionPoint>())
            .Where(p => p != null && !IsInsideTruck(p.transform.position))
            .OrderBy(p => Math.Round(p.transform.position.x, 1))
            .ThenBy(p => Math.Round(p.transform.position.z, 1)));
        Hunted.Logger.LogInfo($"Found {List.Count} extraction points outside the truck.");
        return List;
    }
}
