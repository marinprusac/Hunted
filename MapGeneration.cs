using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>Where the second truck goes. Filled in while the level grid is built, used by the generation steps after it.</summary>
internal static class SecondTruck
{
    /// <summary>Start rooms in a Hunted level.</summary>
    internal const int StartRoomCount = 2;
    internal const int ExtractionPointsPerStartRoom = 0;

    internal static LevelGenerator.Tile? Tile;
    internal static Vector3 RoomPosition;
    internal static Quaternion RoomRotation = Quaternion.identity;

    internal static void Reset() => Tile = null;

    internal static bool IsEdgeBlock(Vector3 position)
        => Tile != null && Vector3.Distance(position, RoomPosition) < 1f;
}

/// <summary>
/// Replaces the level grid the game just built. Vanilla's grid comes from a random walk on a grid whose shape is set by the level
/// asset, which gives thin, branching maps. Here the layout is planned by MapPlan. Neighbouring tiles are always connected, so a dense map means many loops.
/// </summary>
[HarmonyPatch]
static class GridPatch
{
    private static MethodBase TargetMethod()
        => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(LevelGenerator), "TileGeneration"));

    // The first List<Tile> the method creates is possibleExtractionTiles, which is built right after the walk.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var listConstructor = AccessTools.Constructor(typeof(List<LevelGenerator.Tile>));
        var hook = AccessTools.Method(typeof(GridPatch), nameof(NewTileList));
        var add = AccessTools.Method(typeof(List<LevelGenerator.Tile>), nameof(List<LevelGenerator.Tile>.Add));
        var addHook = AccessTools.Method(typeof(GridPatch), nameof(AddPossibleTile));
        var replaced = false;
        var addReplaced = false;
        foreach (var instruction in instructions)
        {
            // The first Add after that is the scan that collects the cells where extraction rooms and dead ends may go.
            if (replaced && !addReplaced && instruction.opcode == OpCodes.Callvirt && instruction.operand as MethodInfo == add)
            {
                addReplaced = true;
                instruction.opcode = OpCodes.Call;
                instruction.operand = addHook;
            }
            if (!replaced && instruction.opcode == OpCodes.Newobj && instruction.operand as ConstructorInfo == listConstructor)
            {
                replaced = true;
                instruction.opcode = OpCodes.Call;
                instruction.operand = hook;
            }
            yield return instruction;
        }
        if (!replaced)
            Hunted.Logger.LogWarning("Could not find the place to replace the level grid.");
        if (replaced && !addReplaced)
            Hunted.Logger.LogWarning("Could not find the place to keep extraction rooms away from the trucks.");
    }

    /// <summary>The game offers every free cell that touches one room; only the side-column cells the plan set aside are used. They stand apart, so the game cannot waste any, and the inner rooms they open onto are always there.</summary>
    public static void AddPossibleTile(List<LevelGenerator.Tile> list, LevelGenerator.Tile tile)
    {
        if (!KillerRole.ModeActive || Slots.Contains(new Vector2Int(tile.x, tile.y)))
            list.Add(tile);
    }

    private static readonly HashSet<Vector2Int> Slots = new();

    /// <summary>The room the plan keeps for the killer: the middle of the map, in the centre column.</summary>
    internal static Vector2Int? KillerCell { get; private set; }

    public static List<LevelGenerator.Tile> NewTileList()
    {
        if (KillerRole.ModeActive)
            BuildGrid(LevelGenerator.Instance);
        return new List<LevelGenerator.Tile>();
    }

    private static void BuildGrid(LevelGenerator generator)
    {
        SecondTruck.Reset();
        var extractions = LevelPatches.ExtractionAmountPatch.Desired(generator.ExtractionAmount);
        var fill = MapGridSize.Fill;

        // The extraction rooms, special rooms and dead ends hang off a free column on the left and right. The game never puts two of them side by side, and they keep
        // two tiles from a truck, so N rows hold N of them at most: the map has as many rows as they need.
        var columns = Tuning.Level.Columns;
        var rooms = extractions + generator.SpecialAmount;
        var rows = MapGridSize.RowsFor(rooms);
        var width = columns + 2;
        var height = rows; // both truck rows are outside the grid: the bottom row faces the first truck, the top row the second

        // Second truck: as far from the first as possible, which is a top corner, on the left or right inner column.
        var left = 1;
        var right = columns;
        var far = Mathf.Abs(left - width / 2) > Mathf.Abs(right - width / 2) ? left
            : Mathf.Abs(left - width / 2) < Mathf.Abs(right - width / 2) ? right
            : (Random.value < 0.5f ? left : right);

        var plan = MapPlan.Make(columns, rows, far, fill);
        Slots.Clear();
        Slots.UnionWith(plan.Slots);
        KillerCell = plan.Killer;

        Hunted.Logger.LogInfo($"{rooms} extraction and special rooms: the map has {rows} rows.");
        var deadEnds = Mathf.Min(generator.DeadEndAmount, Mathf.Max(0, plan.Slots.Count - rooms));
        if (deadEnds < generator.DeadEndAmount)
            Hunted.Logger.LogInfo($"Dead ends reduced from {generator.DeadEndAmount} to {deadEnds} to fit beside {rows} rows.");
        generator.DeadEndAmount = deadEnds;

        var grid = new LevelGenerator.Tile[width, height];
        for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
                grid[x, y] = new LevelGenerator.Tile { x = x, y = y, active = plan.Rooms.Contains(new Vector2Int(x, y)) };
        bool Active(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && grid[x, y].active;

        grid[width / 2, 0].first = true;
        SecondTruck.Tile = grid[far, rows - 1];
        var target = plan.Rooms.Count;
        if (plan.Short > 0)
            Hunted.Logger.LogInfo($"The map keeps {plan.Short} more rooms than its fill asks for: taking them out would make corridors.");

        generator.LevelWidth = width;
        generator.LevelHeight = height;
        generator.LevelGrid = grid;

        var moduleWidth = LevelGenerator.ModuleWidth * LevelGenerator.TileSize;
        SecondTruck.RoomPosition = new Vector3(far * moduleWidth - (width / 2) * moduleWidth, 0f, rows * moduleWidth);
        SecondTruck.RoomRotation = Quaternion.Euler(0f, 180f, 0f);

        var active = 0;
        var connections = 0;
        foreach (var tile in grid)
        {
            if (!tile.active)
                continue;
            active++;
            if (Active(tile.x + 1, tile.y)) connections++;
            if (Active(tile.x, tile.y + 1)) connections++;
        }
        Hunted.Logger.LogInfo($"Map grid {width}x{height}: {active} tiles, {connections} connections ({connections - (active - 1)} loops), " +
                              $"{target} of {columns * rows} inner cells ({columns}x{rows}) filled, {extractions} extraction rooms wanted, second truck above column {far}.");
    }
}

/// <summary>
/// The layout, worked out before any room exists. It starts from a full inner grid and takes out as many rooms as the fill percentage asks for, one at a time and in random order, leaving
/// any whose loss would break a rule. Never taken out: the killer's room, a room that leads to an extraction room's place or a truck. And what remains must have no corridors: no room
/// the others depend on to stay connected.
/// </summary>
internal static class MapPlan
{
    internal sealed class Result
    {
        internal readonly HashSet<Vector2Int> Rooms = new();
        internal readonly List<Vector2Int> Slots = new();
        internal Vector2Int Killer;
        internal int Short;
    }

    private static readonly Vector2Int[] Sides = { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right };

    internal static Result Make(int columns, int rows, int secondTruckColumn, float fill)
    {
        var result = new Result();
        var width = columns + 2;
        var firstTruck = new Vector2Int(width / 2, 0);
        var secondTruck = new Vector2Int(secondTruckColumn, rows - 1);

        for (var x = 1; x <= columns; x++)
            for (var y = 0; y < rows; y++)
                result.Rooms.Add(new Vector2Int(x, y));

        // Where extraction rooms may go: every other row of the side columns, two tiles from a truck. The room each would open onto stays.
        var kept = new HashSet<Vector2Int> { firstTruck, secondTruck };
        foreach (var x in new[] { 0, width - 1 })
            for (var y = 0; y < rows; y += 2)
            {
                bool Near(int truckX, int truckY) => Mathf.Max(Mathf.Abs(x - truckX), Mathf.Abs(y - truckY)) < 2;
                if (Near(firstTruck.x, -1) || Near(secondTruckColumn, rows))
                    continue;
                result.Slots.Add(new Vector2Int(x, y));
                kept.Add(new Vector2Int(x == 0 ? 1 : columns, y));
            }

        // The killer's room is the middle of the map.
        var centreX = (width - 1) / 2f;
        var centreY = (rows - 1) / 2f;
        float ToCentre(Vector2Int cell) => Mathf.Abs(cell.x - centreX) + Mathf.Abs(cell.y - centreY);
        var closest = result.Rooms.Min(ToCentre);
        var middle = result.Rooms.Where(c => ToCentre(c) <= closest + 0.01f).ToList();
        result.Killer = middle[Random.Range(0, middle.Count)];
        kept.Add(result.Killer);

        // How many to take out. Some orders get stuck early, so a few are tried, and the first that manages all of them (or else the one that came closest) is the map.
        var full = new HashSet<Vector2Int>(result.Rooms);
        var candidates = full.Where(c => !kept.Contains(c)).ToList();
        var asked = full.Count - Mathf.Clamp(Mathf.CeilToInt(full.Count * fill), 2, full.Count);
        var wanted = asked;
        for (var attempt = 0; attempt < 20 && wanted > 0; attempt++)
        {
            var layout = new HashSet<Vector2Int>(full);
            var left = asked;
            foreach (var cell in candidates.OrderBy(_ => Random.value).ToList())
            {
                if (left <= 0)
                    break;
                layout.Remove(cell);
                if (NoCorridors(layout) && !Straight(layout, result.Killer))
                    left--;
                else
                    layout.Add(cell);
            }
            if (left < wanted)
            {
                wanted = left;
                result.Rooms.Clear();
                result.Rooms.UnionWith(layout);
            }
        }
        result.Short = wanted;
        return result;
    }

    // A corridor is a room the others depend on: without it, they would no longer all be connected. That also rules out cutting the map in two, and rooms with a single way in.
    private static bool NoCorridors(HashSet<Vector2Int> rooms)
    {
        foreach (var room in rooms)
            if (!AllConnectedWithout(rooms, room))
                return false;
        return true;
    }

    // Two ways out, straight across: the game builds such a room as a passage, which cannot be the killer's room.
    private static bool Straight(HashSet<Vector2Int> rooms, Vector2Int room)
    {
        var up = rooms.Contains(room + Vector2Int.up);
        var down = rooms.Contains(room + Vector2Int.down);
        var left = rooms.Contains(room + Vector2Int.left);
        var right = rooms.Contains(room + Vector2Int.right);
        return (up && down && !left && !right) || (left && right && !up && !down);
    }

    private static bool AllConnectedWithout(HashSet<Vector2Int> rooms, Vector2Int missing)
    {
        var start = rooms.FirstOrDefault(r => r != missing);
        var seen = new HashSet<Vector2Int> { start };
        var open = new Stack<Vector2Int>();
        open.Push(start);
        while (open.Count > 0)
        {
            var at = open.Pop();
            foreach (var side in Sides)
            {
                var next = at + side;
                if (next != missing && rooms.Contains(next) && seen.Add(next))
                    open.Push(next);
            }
        }
        return seen.Count == rooms.Count - 1;
    }
}

/// <summary>The inner grid: as many rows as the extraction points need, and how many rooms it holds at the configured fill.</summary>
internal static class MapGridSize
{
    internal static float Fill => Mathf.Clamp(Tuning.Level.GridFillPercent, 40, 100) / 100f;

    /// <summary>Rows that hold this many extraction and special rooms.</summary>
    internal static int RowsFor(int rooms) => Mathf.Max(Tuning.Level.MinRows, rooms);

    /// <summary>About how many rooms the map's inner grid will have, sized to the extraction points it needs.</summary>
    internal static int Rooms => Mathf.Max(2, Mathf.CeilToInt(Tuning.Level.Columns * RowsFor(LevelPatches.ExtractionAmountPatch.Desired(0)) * Fill));
}

[HarmonyPatch(typeof(LevelGenerator))]
static class SecondTruckPatches
{
    // After the tiles exist, the host adds the second start room (the truck) at the far end of the bottom row.
    [HarmonyPostfix, HarmonyPatch("TileGeneration")]
    private static void TileGeneration_Postfix(LevelGenerator __instance, ref IEnumerator __result)
    {
        if (KillerRole.ModeActive && SemiFunc.IsMasterClientOrSingleplayer())
            __result = Then(__result, SpawnSecondStartRoom(__instance));
    }

    // Vanilla puts a door piece between the first tile and the truck; the second truck needs one too.
    [HarmonyPostfix, HarmonyPatch("GenerateConnectObjects")]
    private static void GenerateConnectObjects_Postfix(LevelGenerator __instance, ref IEnumerator __result)
    {
        if (KillerRole.ModeActive && SemiFunc.IsMasterClientOrSingleplayer())
            __result = Then(__result, SpawnDoorway(__instance));
    }

    // ...and the wall plug vanilla puts on that side of the tile must not be there.
    [HarmonyPrefix, HarmonyPatch("SpawnBlockObject")]
    private static bool SpawnBlockObject_Prefix(Vector3 position)
    {
        var skip = SecondTruck.IsEdgeBlock(position);
        if (skip)
            Hunted.Logger.LogDebug($"Skipped the wall plug at the second truck's door ({position}).");
        return !skip;
    }

    // Safety net in case the plug was spawned anyway: remove whatever plug ended up in the doorway.
    [HarmonyPostfix, HarmonyPatch("GenerateBlockObjects")]
    private static void GenerateBlockObjects_Postfix(ref IEnumerator __result)
    {
        if (KillerRole.ModeActive && SemiFunc.IsMasterClientOrSingleplayer())
            __result = Then(__result, RemoveDoorwayPlug());
    }

    // A 2x2 room covering the truck tile would have no door on its south side.
    [HarmonyPrefix, HarmonyPatch("ModuleGeneration")]
    private static void ModuleGeneration_Prefix(LevelGenerator __instance)
    {
        if (SecondTruck.Tile != null)
            __instance.MegaTiles.RemoveAll(m => m.tiles.Contains(SecondTruck.Tile));
    }

    // Each room shows either "connected" props (an open passage) or "not connected" props (bookcases and a plant in front of a
    // wall) on every side, depending on flags set when it is spawned. Vanilla sets the south flag for the first tile because the
    // truck is there; the second truck's tile needs its north flag set the same way.
    [HarmonyPatch(typeof(Module), nameof(Module.ModuleConnectionSet))]
    [HarmonyPrefix]
    private static void ModuleConnectionSet_Prefix(Module __instance, ref bool _top)
    {
        var tile = SecondTruck.Tile;
        if (tile != null && __instance.GridX == tile.x && __instance.GridY == tile.y)
        {
            _top = true;
            Hunted.Logger.LogDebug($"Marked the north side of tile ({tile.x},{tile.y}) as connected (the second truck).");
        }
    }

    private static IEnumerator Then(IEnumerator first, IEnumerator second)
    {
        while (first.MoveNext())
            yield return first.Current;
        yield return second;
    }

    private static IEnumerator RemoveDoorwayPlug()
    {
        yield return null;
        if (SecondTruck.Tile == null)
            yield break;
        var plugs = Object.FindObjectsByType<ModuleBlockObject>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(b => Vector3.Distance(b.transform.position, SecondTruck.RoomPosition) < 2f).ToList();
        Hunted.Logger.LogDebug($"Wall plugs found at the second truck's door: {plugs.Count}");
        foreach (var plug in plugs)
        {
            if (GameManager.instance.gameMode != 0)
                PhotonNetwork.Destroy(plug.gameObject);
            else
                Object.Destroy(plug.gameObject);
        }
    }

    private static IEnumerator SpawnDoorway(LevelGenerator generator)
    {
        if (SecondTruck.Tile != null)
            generator.SpawnConnectObject(SecondTruck.RoomPosition, Vector3.zero);
        yield return null;
    }

    private static IEnumerator SpawnSecondStartRoom(LevelGenerator generator)
    {
        if (SecondTruck.Tile == null)
            yield break;

        // The same start room as the first one, so the extraction point count in the start rooms is known.
        var first = Object.FindObjectsByType<StartRoom>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .OrderBy(r => r.transform.position.sqrMagnitude).FirstOrDefault();
        var prefab = generator.Level.StartRooms.FirstOrDefault(p => first != null && first.name.StartsWith(p.PrefabName))
                     ?? generator.Level.StartRooms.FirstOrDefault();
        if (prefab == null)
            yield break;

        yield return generator.PrefabsPrepare(new[] { prefab });
        var spawned = GameManager.instance.gameMode != 0
            ? PhotonNetwork.InstantiateRoomObject(prefab.ResourcePath, SecondTruck.RoomPosition, SecondTruck.RoomRotation, 0)
            : Object.Instantiate(prefab.Prefab, SecondTruck.RoomPosition, SecondTruck.RoomRotation);
        if (spawned == null)
        {
            Hunted.Logger.LogError("Could not spawn the second start room.");
            yield break;
        }
        spawned.transform.parent = generator.LevelParent.transform;
        Hunted.Logger.LogInfo($"Second start room spawned: {prefab.PrefabName}");
        yield return null;
    }
}
