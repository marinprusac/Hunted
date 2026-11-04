using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Makes every enemy script behave as if the killer did not exist. Enemy code finds players through
/// GameDirector.PlayerList and a handful of SemiFunc queries, so inside Enemy* classes those are swapped
/// for versions that leave the killer out.
/// </summary>
static class EnemyIgnore
{
    private static readonly FieldInfo PlayerListField = AccessTools.Field(typeof(GameDirector), nameof(GameDirector.PlayerList));

    private static readonly Dictionary<MethodBase, MethodInfo> Replacements = new()
    {
        { AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerGetList)), AccessTools.Method(typeof(EnemyIgnore), nameof(PlayerGetList)) },
        { AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerGetAllPlayerAvatarWithinRange)), AccessTools.Method(typeof(EnemyIgnore), nameof(AllWithinRange)) },
        { AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerGetAllPlayerAvatarWithinRangeAndVision)), AccessTools.Method(typeof(EnemyIgnore), nameof(AllWithinRangeAndVision)) },
        { AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerGetNearestPlayerAvatarWithinRange)), AccessTools.Method(typeof(EnemyIgnore), nameof(NearestWithinRange)) },
        { AccessTools.Method(typeof(SemiFunc), nameof(SemiFunc.PlayerNearestDistance)), AccessTools.Method(typeof(EnemyIgnore), nameof(NearestDistance)) },
    };

    /// <summary>Patches each enemy method on its own so one bad method cannot break the rest of the mod.</summary>
    internal static void Apply(Harmony harmony)
    {
        var transpiler = new HarmonyMethod(AccessTools.Method(typeof(EnemyIgnore), nameof(Transpiler)));
        int patched = 0, failed = 0;
        foreach (var method in TargetMethods())
        {
            try
            {
                harmony.Patch(method, transpiler: transpiler);
                patched++;
            }
            catch (Exception e)
            {
                failed++;
                Hunted.Logger.LogWarning($"Could not patch {method.DeclaringType?.Name}.{method.Name}: {e.Message}");
            }
        }
        Hunted.Logger.LogInfo($"Enemy scripts: {patched} methods now ignore the killer ({failed} failed).");
    }

    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(Enemy).Assembly.GetTypes())
        {
            var outer = type;
            while (outer.DeclaringType != null)
                outer = outer.DeclaringType;
            if (!outer.Name.StartsWith("Enemy", StringComparison.Ordinal))
                continue;

            foreach (var method in type.GetMethods(AccessTools.allDeclared).Cast<MethodBase>()
                         .Concat(type.GetConstructors(AccessTools.allDeclared)))
            {
                if (method.IsAbstract || !UsesPlayers(method))
                    continue;
                yield return method;
            }
        }
    }

    private static bool UsesPlayers(MethodBase method)
    {
        try
        {
            foreach (var (opcode, operand) in PatchProcessor.ReadMethodBody(method))
            {
                if (opcode == OpCodes.Ldfld && operand as FieldInfo == PlayerListField)
                    return true;
                if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && operand is MethodBase m && Replacements.ContainsKey(m))
                    return true;
            }
        }
        catch (Exception)
        {
            // Methods we cannot read cannot be patched either.
        }
        return false;
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var filter = AccessTools.Method(typeof(EnemyIgnore), nameof(FilterList));
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand as FieldInfo == PlayerListField)
            {
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, filter);
                continue;
            }
            if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && instruction.operand is MethodBase called && Replacements.TryGetValue(called, out var replacement))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            yield return instruction;
        }
    }

    private static readonly List<PlayerAvatar> Filtered = new();

    /// <summary>The player list without the killer. One shared instance, only modified when its contents change.</summary>
    public static List<PlayerAvatar> FilterList(List<PlayerAvatar> source)
    {
        var killer = KillerRole.KillerSteamId;
        if (killer == null)
            return source;

        var same = true;
        var kept = 0;
        foreach (var player in source)
        {
            if (player != null && player.steamID == killer)
                continue;
            if (kept >= Filtered.Count || Filtered[kept] != player)
            {
                same = false;
                break;
            }
            kept++;
        }
        if (same && kept == Filtered.Count)
            return Filtered;

        Filtered.Clear();
        foreach (var player in source)
            if (player == null || player.steamID != killer)
                Filtered.Add(player!);
        return Filtered;
    }

    // Monsters that pick a random player from this list break (an index out of range, every frame) when it is empty, which happens
    // when the killer is the only player in the game (solo tests). Then the killer is on the list after all; the monsters' sight
    // still ignores them.
    public static List<PlayerAvatar> PlayerGetList()
    {
        var all = SemiFunc.PlayerGetList();
        var filtered = FilterList(all);
        return filtered.Count > 0 ? filtered : all;
    }

    public static List<PlayerAvatar> AllWithinRange(float range, Vector3 position, bool doRaycastCheck = false, LayerMask layerMask = default)
    {
        var list = SemiFunc.PlayerGetAllPlayerAvatarWithinRange(range, position, doRaycastCheck, layerMask);
        list.RemoveAll(KillerRole.IsKiller);
        return list;
    }

    public static List<PlayerAvatar> AllWithinRangeAndVision(float range, Vector3 position, PhysGrabObject? thisPhysGrabObject = null)
    {
        var list = SemiFunc.PlayerGetAllPlayerAvatarWithinRangeAndVision(range, position, thisPhysGrabObject);
        list.RemoveAll(KillerRole.IsKiller);
        return list;
    }

    public static PlayerAvatar? NearestWithinRange(float range, Vector3 position, bool doRaycastCheck = false, LayerMask layerMask = default)
    {
        var best = range;
        PlayerAvatar? result = null;
        foreach (var player in AllWithinRange(range, position, doRaycastCheck, layerMask))
        {
            var distance = Vector3.Distance(position, player.PlayerVisionTarget.VisionTransform.position);
            if (distance < best)
            {
                best = distance;
                result = player;
            }
        }
        return result;
    }

    public static float NearestDistance(Vector3 position)
    {
        var best = 999f;
        var result = 9f;
        foreach (var player in FilterList(GameDirector.instance.PlayerList))
        {
            if (player == null || player.isDisabled)
                continue;
            var distance = Vector3.Distance(position, player.PlayerVisionTarget.VisionTransform.position);
            if (distance < best)
            {
                best = distance;
                result = distance;
            }
        }
        return result;
    }
}

[HarmonyPatch]
static class EnemyPlayerTables
{
    // Enemy scripts see a player list without the killer, so the game never registers the killer in a monster's per-player tables
    // (on screen, culled, vision). A few game paths read those tables with the full list (for example while the host leaves and the
    // mode switches off), which throws KeyNotFoundException. Register every player, killer included; the filtered list keeps
    // monsters from ever acting on the killer.
    [HarmonyPatch(typeof(Enemy), nameof(Enemy.PlayerAdded))]
    [HarmonyPostfix]
    private static void PlayerAdded_Postfix(Enemy __instance)
    {
        if (!KillerRole.ModeActive || GameDirector.instance == null)
            return;
        foreach (var player in GameDirector.instance.PlayerList)
        {
            if (player == null || player.photonView == null)
                continue;
            var id = player.photonView.ViewID;
            if (__instance.HasVision && __instance.Vision != null)
                __instance.Vision.PlayerAdded(id);
            if (__instance.HasOnScreen && __instance.OnScreen != null)
                __instance.OnScreen.PlayerAdded(id);
        }
    }
}
