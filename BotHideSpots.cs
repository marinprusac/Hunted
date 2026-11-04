using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace Hunted;

/// <summary>
/// Where on this map a survivor can really hide, found once per level and shared by the bots: the game's own hiding volumes (the "HideTriggers" layer that monsters cannot see through, and
/// its crawl spaces), the gaps under furniture low enough that one has to crawl, and failing those, alcoves with cover on most sides. Open floor is never a hiding spot. The walking mesh
/// usually stops short of a gap under a bed, so a spot has an entry: the last walkable point, from which a straight crawl leads in. The map is searched a little every frame.
/// </summary>
internal static class BotHideSpots
{
    internal enum Kind { Alcove, UnderFurniture, GameHidingVolume }

    internal sealed class Spot
    {
        internal Vector3 Position;
        internal Vector3 Entry;
        internal Kind Kind;
    }

    private const float Step = 0.7f;
    private const float MinGap = 0.5f;
    private const float MaxGap = 1.2f;
    private const int MaxSpots = 500;

    internal static readonly List<Spot> Spots = new();

    private static int _builtFor;
    private static bool _designedDone;
    private static bool _gridDone;
    private static bool _hasGrid;
    private static Bounds _area;
    private static float _x;
    private static float _z;
    private static int _mask;
    private static readonly Stopwatch Clock = new();

    internal static void Reset()
    {
        Spots.Clear();
        _builtFor = 0;
        _designedDone = false;
        _gridDone = false;
        _hasGrid = false;
    }

    /// <summary>Goes on searching for up to about a millisecond and a half.</summary>
    internal static void Tick()
    {
        var generator = LevelGenerator.Instance;
        if (generator == null || !generator.Generated)
            return;
        if (_builtFor != generator.GetInstanceID())
        {
            Reset();
            _builtFor = generator.GetInstanceID();
            _mask = LayerMask.GetMask("Default", "StaticGrabObject");
        }
        if (_gridDone)
            return;
        if (!_designedDone)
        {
            _designedDone = true;
            AddDesigned();
            return;
        }
        if (!_hasGrid && !StartGrid(generator))
        {
            _gridDone = true;
            return;
        }

        Clock.Restart();
        while (Clock.Elapsed.TotalMilliseconds < 1.5)
        {
            ScanColumn(_x, _z);
            _z += Step;
            if (_z <= _area.max.z)
                continue;
            _z = _area.min.z;
            _x += Step;
            if (_x <= _area.max.x)
                continue;
            _gridDone = true;
            Hunted.Logger.LogInfo($"Hiding spots for the bots: {Spots.Count(s => s.Kind == Kind.GameHidingVolume)} of the game's own, {Spots.Count(s => s.Kind == Kind.UnderFurniture)} under furniture, {Spots.Count(s => s.Kind == Kind.Alcove)} alcoves.");
            return;
        }
    }

    private static bool StartGrid(LevelGenerator generator)
    {
        var points = generator.LevelPathPoints;
        if (points == null || points.Count == 0)
            return false;
        _area = new Bounds(points[0].transform.position, Vector3.zero);
        foreach (var point in points)
            if (point != null)
                _area.Encapsulate(point.transform.position);
        _area.Expand(new Vector3(16f, 8f, 16f));
        _x = _area.min.x;
        _z = _area.min.z;
        _hasGrid = true;
        return true;
    }

    // The volumes the map's makers placed. Large ones get a spot every couple of metres.
    private static void AddDesigned()
    {
        var layer = LayerMask.NameToLayer("HideTriggers");
        var colliders = Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)
            .Where(c => c != null && c.gameObject.activeInHierarchy && (c.gameObject.layer == layer || c.GetComponent<CrawlTrigger>() != null));
        foreach (var collider in colliders)
        {
            var bounds = collider.bounds;
            for (var x = bounds.min.x + Mathf.Min(1f, bounds.extents.x); x <= bounds.max.x; x += 2f)
                for (var z = bounds.min.z + Mathf.Min(1f, bounds.extents.z); z <= bounds.max.z; z += 2f)
                    if (Physics.Raycast(new Vector3(x, bounds.max.y + 0.2f, z), Vector3.down, out var floor, bounds.size.y + 1.5f, _mask, QueryTriggerInteraction.Ignore))
                        TryAdd(floor.point, Kind.GameHidingVolume, 1.5f);
        }
    }

    private static void ScanColumn(float x, float z)
    {
        var origin = new Vector3(x, _area.max.y, z);
        var left = _area.size.y;
        for (var i = 0; i < 6 && left > 0f; i++)
        {
            if (!Physics.Raycast(origin, Vector3.down, out var hit, left, _mask, QueryTriggerInteraction.Ignore))
                return;
            left -= hit.distance + 0.1f;
            origin = hit.point + Vector3.down * 0.1f;
            if (hit.normal.y < 0.7f)
                continue;
            if (Under(hit.point))
                TryAdd(hit.point, Kind.UnderFurniture, 1.2f);
            else if (Alcove(hit.point))
                TryAdd(hit.point, Kind.Alcove, 3f);
        }
    }

    // A ceiling at crawling height, here and on most sides around, so the whole body is under it.
    private static bool Under(Vector3 floor)
    {
        if (!LowAbove(floor))
            return false;
        var covered = 0;
        foreach (var side in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
            if (LowAbove(floor + side * 0.4f))
                covered++;
        return covered >= 3;
    }

    private static bool LowAbove(Vector3 floor)
        => Physics.Raycast(floor + Vector3.up * 0.05f, Vector3.up, out var above, MaxGap, _mask, QueryTriggerInteraction.Ignore) && above.distance >= MinGap;

    // Walled in on most sides, low and high, right at the edge of the walkable floor.
    private static bool Alcove(Vector3 floor)
    {
        if (!NavMesh.SamplePosition(floor, out var mesh, 0.3f, -1) || !NavMesh.FindClosestEdge(mesh.position, out var edge, -1) || edge.distance > 0.7f)
            return false;
        var low = 0;
        var high = 0;
        for (var angle = 0f; angle < 360f; angle += 45f)
        {
            var direction = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            if (Physics.Raycast(floor + Vector3.up * 0.5f, direction, 1.4f, _mask, QueryTriggerInteraction.Ignore))
                low++;
            if (Physics.Raycast(floor + Vector3.up * 1.5f, direction, 1.4f, _mask, QueryTriggerInteraction.Ignore))
                high++;
        }
        return low >= 5 && high >= 3;
    }

    // Worth keeping only if a body fits, it can be crawled into from the walkable floor, it is not next to one already known, and not where nobody can hide.
    private static void TryAdd(Vector3 floor, Kind kind, float spacing)
    {
        if (Spots.Count >= MaxSpots || KillerRoomRules.CannotHideAt(floor))
            return;
        var position = floor + Vector3.up * 0.03f;
        if (Physics.CheckCapsule(position + Vector3.up * 0.22f, position + Vector3.up * 0.3f, 0.18f, _mask, QueryTriggerInteraction.Ignore))
            return;
        foreach (var other in Spots)
            if ((other.Position - position).sqrMagnitude < spacing * spacing)
                return;
        if (!NavMesh.SamplePosition(position, out var mesh, 2.5f, -1) || Mathf.Abs(mesh.position.y - position.y) > 0.5f)
            return;
        var from = mesh.position + Vector3.up * 0.3f;
        var to = position + Vector3.up * 0.3f;
        var way = to - from;
        if (way.magnitude > 0.2f && Physics.SphereCast(from, 0.18f, way.normalized, out _, way.magnitude, _mask, QueryTriggerInteraction.Ignore))
            return;
        Spots.Add(new Spot { Position = position, Entry = mesh.position, Kind = kind });
    }
}
