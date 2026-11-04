using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The "killer room": one of the rooms in the middle of the map. The killer starts there, the killer's weapon recharges there,
/// and so are the monsters at the start of the round.
/// </summary>
internal static class KillerRoom
{
    private const string RoomKey = "Hunted.KillerRoom";

    private static bool _built;
    private static LevelGenerator.Tile? _tile;
    private static readonly List<LevelPoint> InRoom = new();
    private static readonly List<LevelPoint> Nearby = new();
    private static int _nextEnemy;
    // The middle of the room's tile. Every client knows it (the host shares it); only the host has the grid and the level points.
    private static Vector3? _centre;
    private static Module? _module;
    private static float _moduleSearch;

    internal static void Reset()
    {
        _built = false;
        _tile = null;
        InRoom.Clear();
        Nearby.Clear();
        _nextEnemy = 0;
        _centre = null;
        _module = null;
        _moduleSearch = 0f;
        // The previous level's room must not be there for the next one.
        if (SemiFunc.IsMasterClientOrSingleplayer() && Photon.Pun.PhotonNetwork.InRoom && Photon.Pun.PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey(RoomKey))
            RoomSync.Set(RoomKey, null);
    }

    /// <summary>True once the room is known. The host chooses it (needs the finished grid and the level points); the others read it.</summary>
    internal static bool Ensure()
    {
        if (_built)
            return true;
        // The grid only exists on the host (the other players get their rooms one by one, not as a grid), so they take the room's
        // position from the host.
        if (!SemiFunc.IsMasterClientOrSingleplayer())
            return ReadFromRoom();

        var generator = LevelGenerator.Instance;
        // The module types and level points are only final once the items are being placed; picking earlier would choose from a
        // half-built level. (The state is not PlayerSetup yet when the players spawn: the game skips that state if the players are
        // already there, and the killer's spawn point depends on this room.)
        if (generator == null || generator.LevelGrid == null || generator.LevelPathPoints == null || generator.LevelPathPoints.Count == 0
            || generator.State < LevelGenerator.LevelState.Valuable)
            return false;

        var centreX = (generator.LevelWidth - 1) / 2f;
        var centreY = (generator.LevelHeight - 1) / 2f;
        float DistanceToCentre(LevelGenerator.Tile t) => Mathf.Abs(t.x - centreX) + Mathf.Abs(t.y - centreY);
        // Never a 2x2 ("mega") room: those have type Mega, only single rooms are candidates.
        var rooms = generator.LevelGrid.Cast<LevelGenerator.Tile>().Where(t => t != null && t.active && t.type == Module.Type.Normal).ToList();
        if (rooms.Count == 0)
            return false;

        var closest = rooms.Min(DistanceToCentre);
        _tile = rooms.Where(t => DistanceToCentre(t) <= closest + 0.01f).OrderBy(_ => Random.value).First();
        var centre = generator.GridGetTilePosition(_tile.x, _tile.y);
        var half = LevelGenerator.ModuleWidth * LevelGenerator.TileSize / 2f;

        var points = generator.LevelPathPoints.Where(p => SpawnPlanner.IsOutsideTruck(p) && !p.inStartRoom).ToList();
        InRoom.AddRange(points.Where(p => Mathf.Abs(p.transform.position.x - centre.x) <= half && Mathf.Abs(p.transform.position.z - centre.z) <= half)
            .OrderBy(_ => Random.value));
        // A room has only a few points; the monsters that do not fit use the points closest to the room.
        Nearby.AddRange(points.Except(InRoom).OrderBy(p => (p.transform.position - centre).sqrMagnitude));
        if (InRoom.Count == 0 && Nearby.Count > 0)
        {
            InRoom.Add(Nearby[0]);
            Nearby.RemoveAt(0);
        }
        if (InRoom.Count == 0)
        {
            Nearby.Clear();
            Hunted.Logger.LogWarning("The killer room has no level points; trying again.");
            return false;
        }
        _built = true;
        _centre = centre;
        RoomSync.Set(RoomKey, string.Join("|", centre.x.ToString("R", CultureInfo.InvariantCulture), centre.z.ToString("R", CultureInfo.InvariantCulture)));
        Hunted.Logger.LogInfo($"Killer room: tile ({_tile.x},{_tile.y}) with {InRoom.Count} level points.");
        return true;
    }

    private static bool ReadFromRoom()
    {
        var text = RoomSync.Get<string>(RoomKey);
        if (text == null)
            return false;
        var parts = text.Split('|');
        if (parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return false;
        _centre = new Vector3(x, 0f, z);
        _built = true;
        Hunted.Logger.LogInfo($"Killer room at ({x:0.#}, {z:0.#}) (from the host).");
        return true;
    }

    /// <summary>The host's tile for the room (host only; null on the other players' clients).</summary>
    internal static LevelGenerator.Tile? Tile => Ensure() ? _tile : null;

    /// <summary>The room's module (its lights and map pieces), on any client. Null while it has not been spawned here yet.</summary>
    internal static Module? RoomModule()
    {
        if (!Ensure())
            return null;
        if (_tile?.module != null)
            return _tile.module;
        if (_module != null)
            return _module;
        if (_centre == null || Time.unscaledTime < _moduleSearch)
            return null;
        _moduleSearch = Time.unscaledTime + 1f;
        var centre = _centre.Value;
        var half = LevelGenerator.ModuleWidth * LevelGenerator.TileSize / 2f;
        _module = Object.FindObjectsByType<Module>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(m => m != null && !m.StartRoom && Mathf.Abs(m.transform.position.x - centre.x) <= half && Mathf.Abs(m.transform.position.z - centre.z) <= half)
            .OrderBy(m => (m.transform.position - centre).sqrMagnitude).FirstOrDefault();
        return _module;
    }

    /// <summary>True if the position is inside the killer room.</summary>
    internal static bool Contains(Vector3 position) => TilesFromRoom(position) == 0;

    /// <summary>How many tiles a position is from the killer room (0 = inside it, 1 = next to it), or null if there is no room yet.</summary>
    internal static int? TilesFromRoom(Vector3 position)
    {
        if (!Ensure() || _centre == null)
            return null;
        var width = LevelGenerator.ModuleWidth * LevelGenerator.TileSize;
        var x = Mathf.RoundToInt((position.x - _centre.Value.x) / width);
        var z = Mathf.RoundToInt((position.z - _centre.Value.z) / width);
        return Mathf.Max(Mathf.Abs(x), Mathf.Abs(z));
    }

    /// <summary>A random point of the room, away from the given position if the room has one that is far enough.</summary>
    internal static LevelPoint? RandomPoint(Vector3? awayFrom = null, float minDistance = 0f)
    {
        if (!Ensure() || InRoom.Count == 0)
            return null;
        var points = InRoom.ToList();
        if (awayFrom != null && minDistance > 0f)
        {
            var far = points.Where(p => Vector3.Distance(p.transform.position, awayFrom.Value) >= minDistance).ToList();
            if (far.Count > 0)
                points = far;
        }
        return points[Random.Range(0, points.Count)];
    }


    /// <summary>Where the killer starts: a point of the room.</summary>
    internal static Vector3? KillerSpawn => Ensure() && InRoom.Count > 0 ? (InRoom.Count > 1 ? InRoom[1] : InRoom[0]).transform.position : null;

    /// <summary>The next start point for a monster: the room's points first, then the nearest ones around it.</summary>
    internal static LevelPoint? NextEnemyPoint()
    {
        if (!Ensure() || InRoom.Count == 0)
            return null;
        var all = InRoom.Concat(Nearby).ToList();
        return all[_nextEnemy++ % all.Count];
    }
}

[HarmonyPatch]
static class MonsterPatches
{
    // Only the two stronger monster tiers spawn, the same number of each (Tuning.json: Monsters.MonstersPerTier).
    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.AmountSetup))]
    [HarmonyPrefix]
    private static bool AmountSetup_Prefix(EnemyDirector __instance)
    {
        if (!KillerRole.ModeActive)
            return true;

        var count = Tuning.Monsters.MonstersPerTier;
        __instance.enemyListCurrent.Clear();
        // Level 3 is picked first and is never swapped for anything else; a monster that is on both lists counts as level 3 only.
        var picked3 = PickTier(__instance, __instance.enemiesDifficulty3, count, "level 3", null);
        var picked2 = PickTier(__instance, __instance.enemiesDifficulty2, count, "level 2", new HashSet<EnemySetup>(picked3));
        __instance.amountCurve1Value = 0;
        __instance.amountCurve2Value = count;
        __instance.amountCurve3Value = count;
        __instance.despawnedTimeMultiplier = 1f;
        __instance.totalAmount = __instance.enemyListCurrent.Count;
        Hunted.Logger.LogInfo($"Monsters: {count} of level 3 and {count} of level 2.");
        return false;
    }

    // The game's own pick skips monsters by level progress and rarity, which can leave a tier empty or doubled up. Here every monster
    // of the tier is allowed and each is used once before any is used twice, so the monsters of a tier are all different (as far as
    // the tier has different monsters). Nothing from another tier ever stands in.
    private static List<EnemySetup> PickTier(EnemyDirector director, List<EnemySetup> tier, int count, string label, HashSet<EnemySetup>? exclude)
    {
        var picked = new List<EnemySetup>();
        var all = tier.Where(e => e != null && e.spawnObjects != null && e.spawnObjects.Count > 0 && (exclude == null || !exclude.Contains(e))).Distinct().ToList();
        Hunted.Logger.LogDebug($"{label} entries: {string.Join(", ", all.Select(e => $"{e.name} x{e.spawnObjects.Count}"))}");
        // A group entry ("3 Ooglies") would stand in for one monster of this tier with several weaker ones, so groups are left out
        // (unless the tier has nothing else).
        var options = all.Where(e => e.spawnObjects.Count == 1).ToList();
        if (options.Count == 0 && all.Count > 0)
        {
            Hunted.Logger.LogWarning($"The {label} tier has only group entries; using them (one monster each).");
            options = all;
        }
        if (options.Count == 0)
        {
            Hunted.Logger.LogWarning($"The {label} tier has no monsters.");
            return picked;
        }
        if (options.Count < count)
            Hunted.Logger.LogWarning($"Only {options.Count} different {label} monsters exist for {count} survivors; some repeat.");
        var used = options.ToDictionary(e => e, _ => 0);
        for (var i = 0; i < count; i++)
        {
            var least = used.Values.Min();
            var pick = options.Where(e => used[e] == least).OrderBy(_ => Random.value).First();
            used[pick]++;
            picked.Add(pick);
            director.enemyListCurrent.Add(pick);
            director.enemyList.Add(pick);
        }
        Hunted.Logger.LogDebug($"Monsters ({label}): {string.Join(", ", picked.Select(e => e.name))}");
        return picked;
    }

    // Some entries in the tiers are groups ("Enemy Group - 3 Ooglies" is one entry that spawns three). One entry is one monster here,
    // so only the first member of a group is spawned.
    [HarmonyPatch(typeof(LevelGenerator), "EnemySpawnObject")]
    [HarmonyPrefix]
    private static bool EnemySpawnObject_Prefix(PrefabRef _enemy, EnemySetup _setup)
    {
        if (!KillerRole.ModeActive || _setup == null || _setup.spawnObjects == null || _setup.spawnObjects.Count < 2)
            return true;
        return _setup.spawnObjects[0] == _enemy;
    }

    // Monsters start in the killer room.
    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.FirstSpawnPointAdd))]
    [HarmonyPrefix]
    private static bool FirstSpawnPointAdd_Prefix(EnemyParent _enemyParent)
    {
        if (!KillerRole.ModeActive)
            return true;
        var point = KillerRoom.NextEnemyPoint();
        if (point == null)
            return true;
        _enemyParent.firstSpawnPoint = point;
        return false;
    }

    // Monsters never go away: the timer that sends them off is kept from running out.
    [HarmonyPatch(typeof(EnemyParent), "Update")]
    [HarmonyPostfix]
    private static void EnemyParentUpdate_Postfix(EnemyParent __instance)
    {
        if (__instance.Spawned && __instance.SpawnedTimer < 600f && KillerRole.ModeActive)
            __instance.SpawnedTimer = 3600f;
    }
}

/// <summary>
/// What the monsters do once every generator is done. The game sends all of them to a start room (the truck) and, a little later,
/// to the players. It picks one start room point at random for everybody, which with two trucks makes the whole group flip
/// between the trucks. Here each monster is tied to one truck, half of them to each, and goes to that truck's start room (and
/// comes back to it if it respawns).
/// </summary>
internal static class EndgameEnemies
{
    private static readonly Dictionary<EnemyParent, int> Assigned = new();
    private static List<LevelPoint>[]? _roomPoints;

    internal static void Reset()
    {
        Assigned.Clear();
        _roomPoints = null;
    }

    /// <summary>The level points in front of each truck: index 0 is the first truck, index 1 the second.</summary>
    internal static List<LevelPoint>[]? RoomPoints()
    {
        if (_roomPoints != null)
            return _roomPoints;
        var generator = LevelGenerator.Instance;
        if (generator == null || generator.LevelPathPoints == null)
            return null;

        var groups = generator.LevelPathPoints
            .Where(p => p != null && p.inStartRoom && !p.Truck && !(p.Room != null && p.Room.Truck))
            .GroupBy(p => p.GetComponentInParent<StartRoom>())
            .Where(g => g.Key != null)
            .OrderBy(g => g.Key.transform.position.sqrMagnitude)
            .Select(g => g.ToList())
            .ToList();
        if (groups.Count == 0)
            return null;
        _roomPoints = new[] { groups[0], groups.Count > 1 ? groups[1] : groups[0] };
        Hunted.Logger.LogInfo($"Monsters will split between the trucks ({_roomPoints[0].Count} and {_roomPoints[1].Count} points in front of them).");
        return _roomPoints;
    }

    /// <summary>The truck (0 or 1) a monster is tied to. New monsters join the truck with fewer.</summary>
    internal static int TruckOf(EnemyParent enemy)
    {
        if (Assigned.TryGetValue(enemy, out var truck))
            return truck;
        foreach (var gone in Assigned.Keys.Where(e => e == null).ToList())
            Assigned.Remove(gone);
        var first = Assigned.Values.Count(v => v == 0);
        var second = Assigned.Values.Count(v => v == 1);
        truck = first < second ? 0 : second < first ? 1 : Random.Range(0, 2);
        Assigned[enemy] = truck;
        return truck;
    }

    internal static LevelPoint? RandomPoint(EnemyParent enemy, Vector3? awayFrom = null, float minDistance = 0f)
    {
        var rooms = RoomPoints();
        if (rooms == null)
            return null;
        var points = rooms[TruckOf(enemy)];
        if (awayFrom != null && minDistance > 0f)
        {
            var far = points.Where(p => Vector3.Distance(p.transform.position, awayFrom.Value) >= minDistance).ToList();
            if (far.Count > 0)
                points = far;
        }
        return points.Count == 0 ? null : points[Random.Range(0, points.Count)];
    }
}

[HarmonyPatch]
static class EndgameEnemyPatches
{
    // The game's "head to the truck" call: one random start room point for every monster. Each goes to its own truck instead.
    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.SetInvestigate))]
    [HarmonyPrefix]
    private static bool SetInvestigate_Prefix(EnemyDirector __instance, Vector3 position, float radius, bool pathfindOnly)
    {
        if (!KillerRole.ModeActive || !SemiFunc.IsMasterClientOrSingleplayer() || !SemiFunc.EnemyDirectorEndingHeadToTruck())
            return true;
        var rooms = EndgameEnemies.RoomPoints();
        if (rooms == null || !rooms.Any(r => r.Any(p => (p.transform.position - position).sqrMagnitude < 0.25f)))
            return true;

        SemiEyes.Investigate(position, radius, pathfindOnly);
        foreach (var enemy in __instance.enemiesSpawned)
        {
            if (!enemy)
                continue;
            if (!enemy.Spawned)
            {
                if (radius >= 15f)
                    enemy.DisableDecrease(5f);
                continue;
            }
            var point = EndgameEnemies.RandomPoint(enemy);
            if (point != null && enemy.Enemy.HasStateInvestigate)
                enemy.Enemy.StateInvestigate.Set(point.transform.position, pathfindOnly);
        }
        return false;
    }

    // A monster that respawns comes back in the killer room (during the ending: in front of its own truck).
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.TeleportToPoint))]
    [HarmonyPrefix]
    private static bool TeleportToPoint_Prefix(Enemy __instance, float minDistance, ref LevelPoint __result)
    {
        if (!KillerRole.ModeActive || !__instance.EnemyParent.firstSpawnPointUsed)
            return true;
        var ending = RoundDirector.instance.allExtractionPointsCompleted;

        var nearestPlayer = SemiFunc.PlayerGetList().Where(p => p != null && !p.isDisabled).Select(p => (Vector3?)p.transform.position)
            .OrderBy(p => (p!.Value - __instance.transform.position).sqrMagnitude).FirstOrDefault();
        var point = ending ? EndgameEnemies.RandomPoint(__instance.EnemyParent, nearestPlayer, minDistance)
                           : KillerRoom.RandomPoint(nearestPlayer, minDistance);
        if (point == null)
            return true;
        __instance.TeleportPosition = point.transform.position;
        __instance.EnemyTeleported(__instance.TeleportPosition);
        __result = point;
        return false;
    }
}

/// <summary>The killer room has its own colour on the map, so it is easy to find.</summary>
internal static class KillerRoomMap
{
    private static readonly Color Tint = new(0.85f, 0.02f, 0.02f);
    private static readonly HashSet<int> Done = new();

    internal static void Reset()
    {
        Done.Clear();
        LightsDone.Clear();
        ClearMarkers();
    }

    internal static void Tick()
    {
        if (!KillerRole.ModeActive || !KillerRoom.Ensure())
            return;
        TrackSurvivors();
        var module = KillerRoom.RoomModule();
        if (module == null)
            return;

        TintRoom(module);
        TintLights(module);
    }

    // Every light in the killer room is red: the lights themselves and the glowing bulbs, with their brightness kept.
    private static readonly HashSet<int> LightsDone = new();

    private static Color Red(Color color)
    {
        var strength = Mathf.Max(color.r, color.g, color.b);
        return new Color(strength, strength * 0.1f, strength * 0.1f, color.a);
    }

    private static void TintLights(Module module)
    {
        foreach (var light in module.GetComponentsInChildren<Light>(true))
        {
            if (light == null || !LightsDone.Add(light.GetInstanceID()))
                continue;
            light.color = Red(light.color);
            light.intensity *= Tuning.KillerRoom.LightIntensityMultiplier;
            light.range *= Tuning.KillerRoom.LightRangeMultiplier;
            // The game fades its lights to the intensity it stored when the light woke up, so that needs the same change.
            var prop = light.GetComponent<PropLight>();
            if (prop != null && prop.lightComponent != null)
                prop.originalIntensity *= Tuning.KillerRoom.LightIntensityMultiplier;
        }

        foreach (var bulb in module.GetComponentsInChildren<PropLightEmission>(true))
        {
            if (bulb == null || bulb.material == null || !LightsDone.Add(bulb.GetInstanceID()))
                continue;
            bulb.originalEmission = Red(bulb.originalEmission);
            bulb.material.SetColor("_EmissionColor", Red(bulb.material.GetColor("_EmissionColor")));
        }
    }

    // A survivor inside the killer room shows on the killer's map, as a marker the map's own custom-marker system draws and moves.
    private static readonly Dictionary<PlayerAvatar, MapCustom> Markers = new();
    private static Sprite? _dot;
    private static readonly HashSet<PlayerAvatar> Inside = new();
    private static float _lastAlert = -100f;

    private static Sprite Dot()
    {
        if (_dot != null)
            return _dot;
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(size / 2f - 0.5f, size / 2f - 0.5f)) / (size / 2f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - distance) * 8f)));
            }
        }
        texture.Apply();
        return _dot = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
    }

    private static void ClearMarkers()
    {
        Inside.Clear();
        foreach (var marker in Markers.Values)
            RemoveMarker(marker);
        Markers.Clear();
    }

    private static void RemoveMarker(MapCustom? marker)
    {
        if (marker == null)
            return;
        if (marker.mapCustomEntity != null)
            Object.Destroy(marker.mapCustomEntity.gameObject);
        Object.Destroy(marker);
    }

    private static void TrackSurvivors()
    {
        if (!KillerRole.LocalIsKiller || Map.Instance == null || Map.Instance.CustomObject == null)
        {
            if (Markers.Count > 0)
                ClearMarkers();
            return;
        }

        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isLocal || KillerRole.IsKiller(player))
                continue;
            var inRoom = !player.isDisabled && player.gameObject.activeInHierarchy && KillerRoom.TilesFromRoom(player.transform.position) == 0;
            var entered = inRoom && Inside.Add(player);
            if (!inRoom)
                Inside.Remove(player);
            // A beep for the killer when someone walks in, at most once per cooldown however often they come and go.
            if (entered && Time.time - _lastAlert >= Tuning.KillerRoom.AlertCooldownSeconds && AssetManager.instance != null && PlayerAvatar.instance != null)
            {
                _lastAlert = Time.time;
                AssetManager.instance.batteryLowWarning.Play(PlayerAvatar.instance.transform.position, 1.5f);
            }
            Markers.TryGetValue(player, out var marker);
            var shown = marker != null && marker.mapCustomEntity != null;
            if (inRoom && !shown)
            {
                RemoveMarker(marker);
                marker = player.gameObject.AddComponent<MapCustom>();
                marker.autoAdd = false;
                marker.sprite = Dot();
                marker.color = new Color(1f, 0.25f, 0.2f);
                marker.fadeAlpha = 1f;
                marker.Add();
                Markers[player] = marker;
            }
            else if (!inRoom && marker != null)
            {
                RemoveMarker(marker);
                Markers.Remove(player);
            }
        }
    }

    private static void TintRoom(Module module)
    {
        foreach (var floor in module.GetComponentsInChildren<DirtFinderMapFloor>(true))
        {
            var mapObject = floor.MapObject;
            if (mapObject == null || !Done.Add(mapObject.GetInstanceID()))
                continue;
            foreach (var sprite in mapObject.GetComponentsInChildren<SpriteRenderer>(true))
                sprite.color = Tint;
            foreach (var renderer in mapObject.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer is SpriteRenderer)
                    continue;
                var material = renderer.material;
                foreach (var property in new[] { "_BaseColor", "_Color", "_EmissionColor" })
                    if (material.HasProperty(property))
                        material.SetColor(property, Tint);
            }
        }
    }
}
