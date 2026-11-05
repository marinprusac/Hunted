using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Spawns everyone on the map instead of in the truck. The killer gets a random spot; every survivor gets a random spot of
/// their own that is not too close to the killer. The killer starts in the killer room.
/// </summary>
internal static class SpawnPlanner
{
    /// <summary>Survivors spawn at least this many metres from the killer, if the map has room.</summary>

    private static readonly Dictionary<string, Vector3> Plan = new();
    private static readonly List<Vector3> Used = new();
    private static List<Vector3> _points = new();
    private static Vector3? _killerPoint;
    private static int _planFor;

    internal static void Reset()
    {
        Plan.Clear();
        Used.Clear();
        _points = new List<Vector3>();
        _killerPoint = null;
        _planFor = 0;
    }

    internal static bool IsOutsideTruck(LevelPoint point)
        => point != null && !point.Truck && !(point.Room != null && point.Room.Truck);

    private static void EnsurePlan(LevelGenerator generator)
    {
        if (_planFor == generator.GetInstanceID())
            return;
        Reset();
        _planFor = generator.GetInstanceID();
        Build(generator);
    }

    internal static bool TryGet(PlayerAvatar player, out Vector3 position)
    {
        var generator = LevelGenerator.Instance;
        EnsurePlan(generator);
        if (_points.Count == 0)
        {
            position = default;
            return false;
        }
        if (Plan.TryGetValue(player.steamID, out position))
            return true;
        ResolveKillerPoint();

        // Players are handed their spot as they spawn, so nobody is left out if they arrive late.
        position = KillerRole.IsKiller(player) && _killerPoint != null ? _killerPoint.Value : PickSurvivorPoint();
        Plan[player.steamID] = position;
        if (!Used.Contains(position))
            Used.Add(position);
        Hunted.Logger.LogInfo($"Spawn for {player.playerName}: {position} ({(KillerRole.IsKiller(player) ? "killer" : "survivor")}).");
        return true;
    }

    private static void Build(LevelGenerator generator)
    {
        var points = generator.LevelPathPoints.Where(p => IsOutsideTruck(p) && !p.inStartRoom).ToList();
        if (points.Count == 0)
            points = generator.LevelPathPoints.Where(IsOutsideTruck).ToList();
        if (points.Count == 0)
        {
            Hunted.Logger.LogWarning("No level points outside the truck, using vanilla spawns.");
            return;
        }
        _points = points.Select(p => p.transform.position).ToList();
    }

    // The killer starts in the killer room. Looked up when first needed, because the room may not be chosen yet when the plan is built.
    private static void ResolveKillerPoint()
    {
        if (_killerPoint != null || KillerRole.KillerSteamId == null || _points.Count == 0)
            return;
        var room = KillerRoom.KillerSpawn;
        if (room == null)
            Hunted.Logger.LogWarning("The killer room is not ready; the killer gets a random spawn.");
        _killerPoint = room ?? _points[Random.Range(0, _points.Count)];
        Used.Add(_killerPoint.Value);
    }

    private static Vector3 PickSurvivorPoint()
    {
        var free = _points.Where(p => !Used.Contains(p)).ToList();
        if (free.Count == 0)
            free = _points;

        // Survivors start at least two tiles from the killer room (a whole tile of space between), and not close to the killer.
        var awayFromRoom = free.Where(p => (KillerRoom.TilesFromRoom(p) ?? 99) >= 2).ToList();
        if (awayFromRoom.Count == 0)
        {
            // A map this small has no such spot: take the farthest one there is.
            awayFromRoom = free.OrderByDescending(p => KillerRoom.TilesFromRoom(p) ?? 0).Take(Mathf.Max(1, free.Count / 4)).ToList();
        }
        if (_killerPoint == null)
            return awayFromRoom[Random.Range(0, awayFromRoom.Count)];

        var killer = _killerPoint.Value;
        var far = awayFromRoom.Where(p => Vector3.Distance(p, killer) >= Tuning.Spawn.MinKillerDistance).ToList();
        if (far.Count > 0)
            return far[Random.Range(0, far.Count)];
        return awayFromRoom.OrderByDescending(p => Vector3.Distance(p, killer)).First();
    }
}

[HarmonyPatch(typeof(PlayerAvatar))]
static class SpawnPatches
{
    [HarmonyPrefix, HarmonyPatch(nameof(PlayerAvatar.Spawn))]
    private static void Spawn_Prefix(PlayerAvatar __instance, ref Vector3 position, ref Quaternion rotation, bool _debug)
    {
        if (_debug || !KillerRole.ModeActive || !SemiFunc.IsMasterClientOrSingleplayer())
            return;

        KillerRole.HostEnsurePicked();

        if (SpawnPlanner.TryGet(__instance, out var spawn))
        {
            position = spawn + Vector3.up * 0.25f;
            rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
        }
    }
}
