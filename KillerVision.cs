using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;

namespace Hunted;

/// <summary>
/// The killer is poor at spotting survivors. Every survivor has a vision score that the
/// killer charges while looking towards them and while they are close; walls do not matter. A survivor is visible while their
/// score is at or above a threshold that depends on how they hide: standing survivors have none (always visible), crouching or
/// tumbled ones need a medium score, crawling ones a high one. The score drains all the time and is capped just above the
/// threshold, so a survivor who is no longer watched or approached fades out again quickly.
///
/// Charge per second = ChargePerSecond * dot^2 * proximity. Both are multipliers: dot (squared, so it narrows the view) is how centred the survivor is in the
/// killer's view (0 to 1, 0 when to the side or behind) and proximity is (1 - distance / ProximityRange)^2 (1 next to the killer,
/// 0 at the range and beyond). So a survivor has to be both near and in front; being only one of them charges next to nothing.
/// Nobody hides in the killer room: there everyone counts as standing.
/// </summary>
internal static class KillerVision
{
    private class Spot
    {
        internal float Score;
        internal bool Shown;
        internal bool InRange;
        internal float HurtUntil;
        internal float HoldUntil;
        internal float Threshold;
        internal Renderer[] Renderers = System.Array.Empty<Renderer>();
        internal float RendererRefresh;
        internal bool Logged;
        internal bool RenderersLogged;
    }

    /// <summary>What the killer's eyes need to know about someone they might spot: a survivor.</summary>
    private readonly struct Subject
    {
        internal Subject(Component key, string name, Vector3 target, bool disabled, bool crouching, bool crawling, bool tumbling, bool exposed)
        {
            Key = key; Name = name; Target = target; Disabled = disabled; Crouching = crouching; Crawling = crawling; Tumbling = tumbling; Exposed = exposed;
        }

        internal readonly Component Key;
        internal readonly string Name;
        internal readonly Vector3 Target;
        internal readonly bool Disabled, Crouching, Crawling, Tumbling, Exposed;
    }

    // How the killer spots survivors: the values are in Tuning.json.
    private static float ProximityRange => Tuning.Vision.ProximityRange;
    private static float ChargePerSecond => Tuning.Vision.ChargePerSecond;
    private static float DecayPerSecond => Tuning.Vision.DecayPerSecond;
    private static float FastDecayPerSecond => Tuning.Vision.FastDecayPerSecond;
    private static float CrouchThreshold => Tuning.Vision.CrouchThreshold;
    private static float CrawlThreshold => Tuning.Vision.CrawlThreshold;
    private static float RevealLingerSeconds => Tuning.Vision.RevealLingerSeconds;

    private static readonly Dictionary<Component, Spot> Spots = new();
    private static float _checkTimer;

    internal static void Reset()
    {
        // Survivors hidden from the killer's view are shown again (the killer's role ended, or the level did).
        foreach (var spot in Spots.Values)
            foreach (var renderer in spot.Renderers)
                if (renderer != null)
                    renderer.forceRenderingOff = false;
        Spots.Clear();
    }

    /// <summary>
    /// Whether the killer should see this survivor's name and speech. Standing survivors are always visible, but their name tag
    /// would then show through walls from anywhere, so it only shows within the proximity range (or after a hit).
    /// </summary>
    internal static bool IsRevealed(PlayerAvatar? player)
        => player != null && Spots.TryGetValue(player, out var spot) && (Time.time < spot.HurtUntil || (spot.Shown && spot.InRange));

    /// <summary>A survivor that was hurt cannot hide: they are visible for at least this long (never shortening a longer reveal).</summary>
    internal static void RevealHurt(PlayerAvatar player)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller || player == null || player.isLocal || KillerRole.IsKiller(player))
            return;
        if (!Spots.TryGetValue(player, out var spot))
            Spots[player] = spot = new Spot();
        spot.HurtUntil = Mathf.Max(spot.HurtUntil, Time.time + Tuning.Vision.HitRevealSeconds);
    }

    internal static void RevealHurt(Component key)
    {
        if (!Spots.TryGetValue(key, out var spot))
            Spots[key] = spot = new Spot();
        spot.HurtUntil = Mathf.Max(spot.HurtUntil, Time.time + Tuning.Vision.HitRevealSeconds);
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller)
        {
            if (Spots.Count > 0)
                Reset();
            return;
        }

        var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        if (camera == null)
            return;

        SteadyFog();

        _checkTimer -= dt;
        if (_checkTimer > 0f)
        {
            ApplyVisibility();
            return;
        }
        var step = 0.1f + Mathf.Max(0f, -_checkTimer);
        _checkTimer = 0.1f;

        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isLocal || KillerRole.IsKiller(player))
                continue;
            Evaluate(camera, new Subject(player, player.playerName, player.PlayerVisionTarget.VisionTransform.position, player.isDisabled,
                player.isCrouching, player.isCrawling, player.isTumbling, KillerRoomRules.CannotHide(player)), step);
        }
        ApplyVisibility();
    }

    private static void Evaluate(Camera camera, Subject subject, float step)
    {
        if (!Spots.TryGetValue(subject.Key, out var spot))
            Spots[subject.Key] = spot = new Spot();

        // Nobody hides in the killer room: there everyone counts as standing.
        var exposed = subject.Exposed;
        var crawling = subject.Crawling && !subject.Tumbling && !exposed;
        var lowProfile = (subject.Crouching || subject.Tumbling) && !exposed;
        var threshold = crawling ? CrawlThreshold : lowProfile ? CrouchThreshold : 0f;

        var direction = subject.Target - camera.transform.position;
        var distance = direction.magnitude;
        spot.InRange = distance <= ProximityRange;

        // Charge: how centred in the view times how close. Nothing charges beyond the range or for somebody who cannot be seen at all.
        var charge = 0f;
        if (!subject.Disabled && spot.InRange && distance > 0.01f)
        {
            var dot = Mathf.Clamp01(Vector3.Dot(camera.transform.forward, direction / distance));
            var proximity = Mathf.Pow(1f - distance / ProximityRange, 2f);
            charge = ChargePerSecond * dot * dot * proximity;
        }

        // While the charge beats DecayPerSecond the score builds by the difference. Otherwise (looking away, too far, too far to the
        // side) it drains at FastDecayPerSecond, less the little charge there is. The score cannot rise more than the linger time's
        // worth of DecayPerSecond above the threshold, so a spotted survivor nobody charges any more fades out within a fraction of a second.
        var ceiling = threshold + RevealLingerSeconds * DecayPerSecond;
        var change = charge > DecayPerSecond ? charge - DecayPerSecond : charge - FastDecayPerSecond;
        spot.Score = Mathf.Clamp(spot.Score + change * step, 0f, ceiling);

        // A survivor who gets harder to see (standing to crouching, crouching to crawling) stays visible for the linger time, so a
        // visible survivor who changes stance does not blink out and come back.
        if (threshold > spot.Threshold && spot.Shown)
            spot.HoldUntil = Time.time + RevealLingerSeconds;
        spot.Threshold = threshold;
        spot.Shown = !subject.Disabled && (spot.Score >= threshold || Time.time < spot.HoldUntil);

        // For the log: when somebody is spotted and when they are lost again, with how they were hiding.
        if (spot.Shown != spot.Logged)
        {
            spot.Logged = spot.Shown;
            var how = exposed ? "in the killer room" : crawling ? "crawling" : lowProfile ? (subject.Tumbling ? "tumbled" : "crouching") : "standing";
            Hunted.Logger.LogDebug($"Killer {(spot.Shown ? "spotted" : "lost")} {subject.Name} at {distance:0.#} m ({how}, score {spot.Score:0.##} of {threshold:0.##}).");
        }
    }

    private static void ApplyVisibility()
    {
        foreach (var pair in Spots)
        {
            var player = pair.Key;
            var spot = pair.Value;
            if (player == null)
                continue;

            if (Time.time >= spot.RendererRefresh)
            {
                spot.RendererRefresh = Time.time + 0.25f;
                // The avatar's model (playerAvatarVisuals) is not a child of the PlayerAvatar component: it sits beside it under the
                // player's own root (the game destroys that root with the avatar). So the whole root is searched, not just the avatar.
                var renderers = new List<Renderer>(player.GetComponentsInChildren<Renderer>(true));
                if (player is PlayerAvatar avatar)
                {
                    if (avatar.transform.parent != null)
                        renderers.AddRange(avatar.transform.parent.GetComponentsInChildren<Renderer>(true));
                    if (avatar.playerAvatarVisuals != null)
                        renderers.AddRange(avatar.playerAvatarVisuals.GetComponentsInChildren<Renderer>(true));
                    if (avatar.tumble != null)
                        renderers.AddRange(avatar.tumble.GetComponentsInChildren<Renderer>(true));
                }
                // The death head sits under that same root, but it is an object in the world: the killer always sees it, like a valuable.
                var head = player is PlayerAvatar owner && owner.playerDeathHead != null ? owner.playerDeathHead.transform : null;
                if (head != null)
                {
                    foreach (var renderer in renderers)
                        if (renderer != null && renderer.transform.IsChildOf(head))
                            renderer.forceRenderingOff = false;
                    renderers.RemoveAll(r => r != null && r.transform.IsChildOf(head));
                }
                spot.Renderers = renderers.Where(r => r != null && !(r is ParticleSystemRenderer)).Distinct().ToArray();
                if (!spot.RenderersLogged && player is PlayerAvatar logged && spot.Renderers.Length > 0)
                {
                    spot.RenderersLogged = true;
                    var visuals = logged.playerAvatarVisuals;
                    Hunted.Logger.LogDebug($"Hiding {spot.Renderers.Length} renderers of {logged.playerName} (model is a child of the avatar: {visuals != null && visuals.transform.IsChildOf(logged.transform)}, model renderers: {(visuals != null ? visuals.GetComponentsInChildren<Renderer>(true).Length : 0)}).");
                }
            }

            var visible = spot.Shown || Time.time < spot.HurtUntil;
            foreach (var renderer in spot.Renderers)
                if (renderer != null)
                    renderer.forceRenderingOff = !visible;
        }
    }

    /// <summary>
    /// Every room has its own fog distance, and the game blends between them as you cross a border, and also cuts the picture off
    /// at the fog's end. With the killer's faster walk that shows as a whole room popping into
    /// view, going foggy, and the far wall returning as they get closer. The killer gets one steady fog distance everywhere
    /// instead; the camera's cut-off is moved with it, or the far end would still be cut off.
    /// </summary>
    private static void SteadyFog()
    {
        var end = HuntedConfig.KillerFogEnd.Value;
        var director = EnvironmentDirector.Instance;
        if (end <= 0f || director == null || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        var start = Mathf.Min(HuntedConfig.KillerFogStart.Value, end - 1f);
        director.OverrideFogDistance(new Vector2(start, end), 0.2f);
        director.FarClipPlaneOverride(end + 1f, 0.2f);
    }
}

/// <summary>The killer sees a little better in the dark: the picture itself is brighter on their screen, through the camera's exposure. The game's Gamma setting and the lighting stay as they are.</summary>
internal static class KillerBrightness
{
    private static ColorGrading? _grading;
    private static bool _exposureWasOn;
    private static float _exposure;
    private static bool _brightnessWasOn;
    private static float _brightness;

    internal static void Tick()
    {
        var want = KillerRole.ModeActive && KillerRole.LocalIsKiller && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        var grading = PostProcessing.Instance != null ? PostProcessing.Instance.colorGrading : null;
        if (want && grading != null && _grading != grading)
        {
            _grading = grading;
            _exposureWasOn = grading.postExposure.overrideState;
            _exposure = grading.postExposure.value;
            _brightnessWasOn = grading.brightness.overrideState;
            _brightness = grading.brightness.value;
            // Which of the two counts depends on the colour grading mode of the game's profile; each mode ignores the other's.
            grading.postExposure.Override(_exposure + Tuning.Killer.ExposureBoost);
            grading.brightness.Override(_brightness + Tuning.Killer.BrightnessBoost);
            Hunted.Logger.LogDebug($"Killer's picture brightened (grading mode {grading.gradingMode.value}).");
        }
        else if (!want && _grading != null)
        {
            _grading.postExposure.value = _exposure;
            _grading.postExposure.overrideState = _exposureWasOn;
            _grading.brightness.value = _brightness;
            _grading.brightness.overrideState = _brightnessWasOn;
            _grading = null;
        }
    }
}

[HarmonyPatch]
static class KillerVisionPatches
{
    // No flashlight for the killer.
    [HarmonyPatch(typeof(FlashlightController), "Update")]
    [HarmonyPrefix]
    private static void Flashlight_Prefix(FlashlightController __instance)
    {
        if (KillerRole.IsKiller(__instance.PlayerAvatar))
            __instance.hideFlashlight = true;
    }

    // So would the subtitles of what they say.
    [HarmonyPatch(typeof(WorldSpaceUITTS), "Update")]
    [HarmonyPostfix]
    private static void Subtitle_Postfix(WorldSpaceUITTS __instance)
    {
        if (!KillerRole.LocalIsKiller || __instance.playerAvatar == null || __instance.text == null || __instance.playerAvatar.isLocal
            || KillerRole.IsKiller(__instance.playerAvatar) || KillerVision.IsRevealed(__instance.playerAvatar))
            return;
        var color = __instance.text.color;
        color.a = 0f;
        __instance.text.color = color;
    }

    // Damage is only applied on the victim's own client, which tells everyone else the new health. When the killer's client hears
    // that a survivor lost health, that survivor is shown for a while.
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.UpdateHealthRPC))]
    [HarmonyPrefix]
    private static void UpdateHealth_Prefix(PlayerHealth __instance, out int __state) => __state = __instance.health;

    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.UpdateHealthRPC))]
    [HarmonyPostfix]
    private static void UpdateHealth_Postfix(PlayerHealth __instance, int __state)
    {
        if (__instance.health < __state && __instance.playerAvatar != null)
            KillerVision.RevealHurt(__instance.playerAvatar);
    }

    // Name tags would give unseen survivors away.
    [HarmonyPatch(typeof(WorldSpaceUIPlayerName), "Update")]
    [HarmonyPostfix]
    private static void PlayerName_Postfix(WorldSpaceUIPlayerName __instance)
    {
        if (KillerRole.LocalIsKiller && !KillerVision.IsRevealed(__instance.playerAvatar))
            __instance.text.color = new Color(1f, 1f, 1f, 0f);
    }
}
