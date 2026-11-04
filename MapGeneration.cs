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
/// asset, which gives thin, branching maps. Here the grid is square, a compact blob fills most of an inner square, and a free
/// ring around it gives extraction rooms and dead ends somewhere to attach. Neighbouring tiles are always connected, so a dense
/// blob means many loops.
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

    /// <summary>
    /// Extraction rooms and dead ends keep at least two tiles from a truck. A truck sits one row outside the grid, below the
    /// bottom row (first truck) or above the top row (second truck).
    /// </summary>
    public static void AddPossibleTile(List<LevelGenerator.Tile> list, LevelGenerator.Tile tile)
    {
        var generator = LevelGenerator.Instance;
        if (KillerRole.ModeActive && SecondTruck.Tile != null && generator != null)
        {
            bool Near(int truckX, int truckY) => Mathf.Max(Mathf.Abs(tile.x - truckX), Mathf.Abs(tile.y - truckY)) < 2;
            if (Near(generator.LevelWidth / 2, -1) || Near(SecondTruck.Tile.x, generator.LevelHeight))
                return;
        }
        list.Add(tile);
    }

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

        // Inner grid for the blob, columns x rows as configured. The extraction rooms, special rooms and dead ends hang off the free
        // columns on the left and right (2 x rows cells), so the rows are raised if those would not all fit.
        var columns = HuntedConfig.GridColumns.Value;
        var rows = HuntedConfig.GridRows.Value;
        var rooms = extractions + generator.SpecialAmount;
        if (rooms > rows * 2)
        {
            rows = Mathf.CeilToInt(rooms / 2f);
            Hunted.Logger.LogWarning($"{rooms} extraction and special rooms do not fit beside {HuntedConfig.GridRows.Value} rows; using {rows} rows.");
        }
        var deadEnds = Mathf.Min(generator.DeadEndAmount, Mathf.Max(0, rows * 2 - rooms));
        if (deadEnds < generator.DeadEndAmount)
            Hunted.Logger.LogInfo($"Dead ends reduced from {generator.DeadEndAmount} to {deadEnds} to fit beside {rows} rows.");
        generator.DeadEndAmount = deadEnds;
        var width = columns + 2; // one free column on each side
        var height = rows; // both truck rows are outside the grid: the bottom row faces the first truck, the top row the second
        var grid = new LevelGenerator.Tile[width, height];
        for (var x = 0; x < width; x++)
            for (var y = 0; y < height; y++)
                grid[x, y] = new LevelGenerator.Tile { x = x, y = y, active = false };

        bool Active(int x, int y) => x >= 0 && x < width && y >= 0 && y < height && grid[x, y].active;
        int Neighbours(int x, int y) => (Active(x, y + 1) ? 1 : 0) + (Active(x, y - 1) ? 1 : 0) + (Active(x + 1, y) ? 1 : 0) + (Active(x - 1, y) ? 1 : 0);

        // Activates a path from the nearest active tile to (tx, ty), one step at a time.
        void ConnectTo(int tx, int ty)
        {
            if (grid[tx, ty].active)
                return;
            LevelGenerator.Tile? from = null;
            var best = int.MaxValue;
            foreach (var tile in grid)
            {
                if (!tile.active)
                    continue;
                var distance = Mathf.Abs(tile.x - tx) + Mathf.Abs(tile.y - ty);
                if (distance < best)
                {
                    best = distance;
                    from = tile;
                }
            }
            var x = from!.x;
            var y = from.y;
            while (x != tx || y != ty)
            {
                if (x != tx && (y == ty || Random.value < 0.5f))
                    x += tx > x ? 1 : -1;
                else
                    y += ty > y ? 1 : -1;
                grid[x, y].active = true;
                // Thicken the path: a one-tile-wide straight stretch becomes a long passage room.
                var sideX = x + (width / 2 >= x ? 1 : -1);
                if (sideX >= 1 && sideX <= columns && y < rows)
                    grid[sideX, y].active = true;
                if (y - 1 >= 0 && x >= 1 && x <= columns)
                    grid[x, y - 1].active = true;
            }
        }

        var start = grid[width / 2, 0];
        start.active = true;
        start.first = true;

        // Compact growth: a cell is more likely the more active neighbours it has, which closes loops as it goes.
        // The square is sized by rounding up, so on its own it would rarely be as full as asked; add rooms until it is.
        var target = Mathf.Min(columns * rows, Mathf.CeilToInt(columns * rows * fill));
        var count = 0;
        for (var x = 1; x <= columns; x++)
            for (var y = 0; y < rows; y++)
                if (grid[x, y].active)
                    count++;
        var frontier = new List<LevelGenerator.Tile>();
        while (count < target)
        {
            frontier.Clear();
            for (var x = 1; x <= columns; x++)
                for (var y = 0; y < rows; y++)
                    if (!grid[x, y].active && Neighbours(x, y) > 0)
                        frontier.Add(grid[x, y]);
            if (frontier.Count == 0)
                break;
            var total = frontier.Sum(t => Neighbours(t.x, t.y) * Neighbours(t.x, t.y));
            var roll = Random.Range(0, total);
            foreach (var tile in frontier)
            {
                roll -= Neighbours(tile.x, tile.y) * Neighbours(tile.x, tile.y);
                if (roll < 0)
                {
                    tile.active = true;
                    count++;
                    break;
                }
            }
        }

        // Second truck: as far from the first as possible, which is a top corner. It sits above the top row (like the first truck
        // below the bottom row) and attaches to that row's tile in the left or right inner column.
        var left = 1;
        var right = columns;
        var far = Mathf.Abs(left - width / 2) > Mathf.Abs(right - width / 2) ? left
            : Mathf.Abs(left - width / 2) < Mathf.Abs(right - width / 2) ? right
            : (Random.value < 0.5f ? left : right);
        ConnectTo(far, rows - 1);
        SecondTruck.Tile = grid[far, rows - 1];

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

/// <summary>The configured inner grid: how many rooms it holds at the configured fill.</summary>
internal static class MapGridSize
{
    internal static float Fill => Mathf.Clamp(Tuning.Level.GridFillPercent, 40, 100) / 100f;

    internal static int Rooms => Mathf.Max(2, Mathf.CeilToInt(HuntedConfig.GridColumns.Value * HuntedConfig.GridRows.Value * Fill));
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
