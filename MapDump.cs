using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Writes the generated level to the log once it is ready: the tile layout (rows drawn top to bottom), counts, and a check of
/// whether the second truck is connected to the first one. Host only; meant for reading the log after a test.
/// </summary>
internal static class MapDump
{
    private static bool _done;
    private static float _timer;

    internal static void Reset()
    {
        _done = false;
        _timer = 0f;
    }

    internal static void Tick(float dt)
    {
        if (_done || !SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        // Level points link up a moment after generation finishes.
        _timer += dt;
        if (_timer < 8f)
            return;
        _done = true;
        Dump(LevelGenerator.Instance);
    }

    private static char Letter(LevelGenerator.Tile tile)
    {
        if (!tile.active)
            return '.';
        return tile.type switch
        {
            Module.Type.Passage => 'P',
            Module.Type.DeadEnd => 'd',
            Module.Type.Extraction => 'E',
            Module.Type.Special => 'X',
            Module.Type.Mega => 'M',
            _ => '#',
        };
    }

    private static void Dump(LevelGenerator generator)
    {
        var grid = generator.LevelGrid;
        int width = generator.LevelWidth, height = generator.LevelHeight;
        if (grid == null)
            return;

        bool Active(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && grid[x, y].active;

        var text = new StringBuilder();
        text.AppendLine($"Level layout {width}x{height}  (# room, P passage corridor, E extraction, d dead end, X special, M mega, T truck, K killer room, . empty)");
        var northTruck = SecondTruck.Tile != null && SecondTruck.RoomPosition.z > 1f ? SecondTruck.Tile.x : -1;
        var topTruckRow = new StringBuilder("  trucks N  ");
        for (var x = 0; x < width; x++)
            topTruckRow.Append(x == northTruck ? " T" : " .");
        text.AppendLine(topTruckRow.ToString());
        for (var y = height - 1; y >= 0; y--)
        {
            text.Append($"  y={y,-2}      ");
            for (var x = 0; x < width; x++)
                text.Append(' ').Append(grid[x, y] == KillerRoom.Tile ? 'K' : Letter(grid[x, y]));
            text.AppendLine();
        }
        var bottomTruckRow = new StringBuilder("  trucks S  ");
        for (var x = 0; x < width; x++)
            bottomTruckRow.Append(x == width / 2 ? " T" : " .");
        text.AppendLine(bottomTruckRow.ToString());

        var tiles = 0;
        var connections = 0;
        var corridors = 0;
        foreach (var tile in grid)
        {
            if (!tile.active)
                continue;
            tiles++;
            if (Active(tile.x + 1, tile.y)) connections++;
            if (Active(tile.x, tile.y + 1)) connections++;
            var n = Active(tile.x, tile.y + 1);
            var s = Active(tile.x, tile.y - 1);
            var e = Active(tile.x + 1, tile.y);
            var w = Active(tile.x - 1, tile.y);
            if ((n && s && !e && !w) || (e && w && !n && !s))
                corridors++;
        }
        var counts = grid.Cast<LevelGenerator.Tile>().Where(t => t.active).GroupBy(t => Letter(t)).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");
        text.AppendLine($"  {tiles} rooms, {connections} doors, {connections - (tiles - 1)} loops, {corridors} straight 1-wide stretches; {string.Join(" ", counts)}");

        text.AppendLine(TruckReport(generator));
        Hunted.Logger.LogDebug("\n" + text.ToString().TrimEnd());
    }

    private static string Path(Transform transform)
    {
        var names = new List<string>();
        for (var t = transform; t != null && names.Count < 4; t = t.parent)
            if (t.name != "Level Generator")
                names.Add(t.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static string DoorwayReport(string label, Vector3 door, Vector3 intoTruck)
    {
        var plugs = Object.FindObjectsByType<ModuleBlockObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Count(b => Vector3.Distance(b.transform.position, door) < 2f);
        var origin = door - intoTruck * 4f + Vector3.up * 1.2f;
        var hits = Physics.RaycastAll(origin, intoTruck, 10f, ~0, QueryTriggerInteraction.Ignore)
            .OrderBy(h => h.distance)
            .Select(h => $"{Path(h.collider.transform)} [{LayerMask.LayerToName(h.collider.gameObject.layer)}] {h.distance - 4f:0.0} m");
        return $"  doorway of the {label} at {door}: {plugs} wall plugs; solid things on a line through it (distance from the door, negative = on the map side):\n      "
               + string.Join("\n      ", hits);
    }

    private static string TruckReport(LevelGenerator generator)
    {
        var report = new StringBuilder();
        var rooms = Object.FindObjectsByType<StartRoom>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var room in rooms.OrderBy(r => r.transform.position.sqrMagnitude))
            report.AppendLine($"  start room '{room.name}' at {room.transform.position} rotation {room.transform.rotation.eulerAngles}");

        if (SecondTruck.Tile != null)
        {
            var door = SecondTruck.RoomPosition;
            var connectors = Object.FindObjectsByType<ModuleConnectObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(c => Vector3.Distance(c.transform.position, door) < 3f);
            var blockName = generator.Level.BlockObject != null ? generator.Level.BlockObject.PrefabName : "";
            var blocks = blockName.Length == 0 ? -1 : Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Count(t => t.name.StartsWith(blockName) && Vector3.Distance(t.position, door) < 3f);
            var tilePosition = door + new Vector3(0f, 1f, door.z > 1f ? -4f : 4f);
            var moduleName = generator.TryGetModuleAtPosition(tilePosition, out var module) ? $"{module.name} ({module.type})" : "none found";
            // What is physically in each truck's doorway: a ray from inside the tile towards the truck, listing everything it hits.
            report.AppendLine(DoorwayReport("first truck", Vector3.zero, Vector3.back));
            report.AppendLine(DoorwayReport("second truck", door, door.z > 1f ? Vector3.forward : Vector3.back));
            report.AppendLine($"  second truck door at {door}: {connectors} connect objects and {blocks} wall plugs within 3 m, tile module: {moduleName}");
        }

        // Level points are what enemies and the game's own pathing use; if the second truck is not linked in, nothing can path to it.
        var points = generator.LevelPathPoints.Where(p => p != null).ToList();
        var trucks = points.Where(p => p.Truck).ToList();
        if (trucks.Count > 0)
        {
            var first = trucks.OrderBy(p => p.transform.position.sqrMagnitude).First();
            var reached = new HashSet<LevelPoint> { first };
            var queue = new Queue<LevelPoint>();
            queue.Enqueue(first);
            var links = points.ToDictionary(p => p, p => new List<LevelPoint>(p.ConnectedPoints.Where(c => c != null)));
            foreach (var p in points)
                foreach (var c in p.ConnectedPoints.Where(c => c != null && links.ContainsKey(c)))
                    links[c].Add(p);
            while (queue.Count > 0)
            {
                foreach (var next in links[queue.Dequeue()])
                    if (reached.Add(next))
                        queue.Enqueue(next);
            }
            var unreached = trucks.Where(t => !reached.Contains(t)).ToList();
            report.AppendLine($"  level points: {points.Count} total, {reached.Count} reachable from the first truck; {trucks.Count} truck points, {unreached.Count} of them not reachable");
        }
        return report.ToString().TrimEnd();
    }
}
