using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>What the killer room does to survivors: nowhere to hide, and they make noise.</summary>
internal static class KillerRoomRules
{
    private static readonly Dictionary<PlayerAvatar, float> AlertTimers = new();

    internal static void Reset() => AlertTimers.Clear();

    /// <summary>True if the player is in the killer room, where crouching, crawling and tumbling hide nobody.</summary>
    internal static bool CannotHide(PlayerAvatar? player)
        => KillerRole.ModeActive && player != null && KillerRoom.Contains(player.transform.position);

    internal static bool CannotHideAt(Vector3 position) => KillerRole.ModeActive && KillerRoom.Contains(position);

    // Host only: about every second a survivor in the room calls the monsters to where they stand, like an extraction point does.
    internal static void Tick(float dt)
    {
        var interval = Tuning.KillerRoom.MonsterCallIntervalSeconds;
        if (!SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive || EnemyDirector.instance == null
            || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isDisabled || KillerRole.IsKiller(player) || !KillerRoom.Contains(player.transform.position))
            {
                if (player != null)
                    AlertTimers.Remove(player);
                continue;
            }
            if (!AlertTimers.TryGetValue(player, out var timer))
                timer = interval * Random.Range(0.7f, 1.3f);
            timer -= dt;
            if (timer <= 0f)
            {
                EnemyDirector.instance.SetInvestigate(player.transform.position, 20f, false);
                timer = interval * Random.Range(0.7f, 1.3f);
            }
            AlertTimers[player] = timer;
        }
    }
}

[HarmonyPatch]
static class EnemyVisionHidePatch
{
    private static MethodBase TargetMethod() => AccessTools.EnumeratorMoveNext(AccessTools.Method(typeof(EnemyVision), "Vision"));

    // Every read of a player's crouching, crawling or tumbling state in the monsters' vision check passes through a hook that
    // says "no" for a player in the killer room.
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var fields = new[] { "isCrouching", "isCrawling", "isTumbling" };
        var hook = AccessTools.Method(typeof(EnemyVisionHidePatch), nameof(Hook));
        var replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.opcode == OpCodes.Ldfld && instruction.operand is FieldInfo field && field.DeclaringType == typeof(PlayerAvatar)
                && System.Array.IndexOf(fields, field.Name) >= 0)
            {
                var duplicate = new CodeInstruction(OpCodes.Dup);
                duplicate.labels.AddRange(instruction.labels);
                instruction.labels.Clear();
                yield return duplicate;
                yield return instruction;
                yield return new CodeInstruction(OpCodes.Call, hook);
                replaced++;
                continue;
            }
            yield return instruction;
        }
        if (replaced == 0)
            Hunted.Logger.LogWarning("Could not find the monsters' crouch checks; hiding in the killer room is still possible.");
    }

    public static bool Hook(PlayerAvatar player, bool original) => original && !KillerRoomRules.CannotHide(player);
}

/// <summary>While the local player is in the killer room the ambient light fades to red. The fog keeps the game's colour; the red comes from the room's own lights.</summary>
[HarmonyPatch(typeof(EnvironmentDirector))]
static class KillerRoomAmbience
{
    private static float _lerp;
    private static float _lastTime;

    private static Color Red(Color color, float minimum)
    {
        var strength = Mathf.Max(color.r, color.g, color.b);
        return new Color(Mathf.Max(strength * 1.5f, minimum), strength * 0.08f, strength * 0.08f, color.a);
    }

    // The game recomputes the colour every frame, so the red is laid over it every frame, and fades in and out as the player
    // crosses the room's border.
    [HarmonyPostfix, HarmonyPatch("AmbientLightLogic")]
    private static void Ambient_Postfix()
    {
        var player = PlayerAvatar.instance;
        var inRoom = KillerRole.ModeActive && player != null && !player.isDisabled && KillerRoom.Contains(player.transform.position);
        var now = Time.unscaledTime;
        var dt = Mathf.Clamp(now - _lastTime, 0f, 0.1f);
        _lastTime = now;
        _lerp = Mathf.MoveTowards(_lerp, inRoom ? 1f : 0f, dt * 1.5f);
        if (_lerp > 0f)
            RenderSettings.ambientLight = Color.Lerp(RenderSettings.ambientLight, Red(RenderSettings.ambientLight, 0.07f), _lerp);
    }
}
