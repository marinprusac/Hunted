using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The killer's body: bigger, and what they do sounds heavier (louder and deeper). None of their noise makes monsters investigate.
/// </summary>
internal static class KillerBody
{
    private static PlayerAvatar? _scaled;
    private static Vector3 _baseScale = Vector3.one;
    private static PlayerAvatar? _soundOwner;
    private static readonly HashSet<Sound> Sounds = new();
    private static readonly FieldInfo[] SoundFields = typeof(PlayerAvatar).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(f => f.FieldType == typeof(Sound)).ToArray();

    // The killer's items and abilities (the sword, the staff, the laser): their sounds are the killer's too.
    private static readonly HashSet<Sound> ItemSounds = new();
    private static readonly HashSet<int> ItemsDone = new();

    /// <summary>The killer's own sound effects (jump, land, crouch, tumble...) and those of their items and abilities, by identity.</summary>
    internal static bool IsKillerSound(Sound sound) => Sounds.Contains(sound) || ItemSounds.Contains(sound);

    /// <summary>Makes every sound of an item of the killer's (all the sounds its scripts have) as deep and loud as the killer's own. Once per item.</summary>
    internal static void AddItemSounds(Component? item)
    {
        if (item == null || !ItemsDone.Add(item.gameObject.GetInstanceID()))
            return;
        foreach (var behaviour in item.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null)
                continue;
            for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (field.FieldType == typeof(Sound) && field.GetValue(behaviour) is Sound sound)
                        ItemSounds.Add(sound);
        }
    }

    /// <summary>Makes one sound the killer plays (one of the game's, borrowed) as deep and loud as the killer's own.</summary>
    internal static void AddSound(Sound? sound)
    {
        if (sound != null)
            ItemSounds.Add(sound);
    }

    // While a footstep, jump, landing, voice or weapon impact of the killer runs, monsters are not told about it.
    internal static int Muted;
    // While a footstep, jump or landing of the killer runs, the sounds it plays are heavier.
    internal static int Heavy;

    internal static void Reset()
    {
        if (_scaled != null)
            _scaled.playerAvatarVisuals.transform.localScale = _baseScale;
        RestoreEyes();
        _scaled = null;
        _soundOwner = null;
        Sounds.Clear();
        ItemSounds.Clear();
        ItemsDone.Clear();
        Muted = Heavy = 0;
    }

    private static float _shadowTimer;
    private static float? _baseEyeOffset;
    private static float? _baseCrouchDrop;
    private static float? _baseCrawlDrop;

    // The killer's own screen cannot show their body (a first-person view has none), so the size shows in how high they see from:
    // the camera is raised by the extra height (about 1.7 m of eye height times the scale's excess). Crouching and crawling lower it
    // by as much more as the body is bigger, or the eyes would stay above a crouching body.
    private static void RaiseEyes(PlayerAvatar killer)
    {
        var camera = CameraPosition.instance;
        if (camera == null || !killer.isLocal)
            return;
        _baseEyeOffset ??= camera.playerOffset.y;
        var offset = camera.playerOffset;
        offset.y = _baseEyeOffset.Value + (Tuning.Killer.Scale - 1f) * 1.7f;
        camera.playerOffset = offset;

        var crouch = CameraCrouchPosition.instance;
        var crawl = CameraCrawlPosition.instance;
        if (crouch != null && _baseCrouchDrop == null)
        {
            _baseCrouchDrop = crouch.Position;
            crouch.Position *= Tuning.Killer.Scale;
            Hunted.Logger.LogDebug($"Killer's eyes: raised {offset.y - _baseEyeOffset.Value:0.##} m to an offset of {offset.y:0.##}, crouch drop {_baseCrouchDrop:0.##} -> {crouch.Position:0.##}.");
        }
        if (crawl != null && _baseCrawlDrop == null)
        {
            _baseCrawlDrop = crawl.Position;
            crawl.Position *= Tuning.Killer.Scale;
            Hunted.Logger.LogDebug($"Killer's eyes: crawl drop {_baseCrawlDrop:0.##} -> {crawl.Position:0.##}.");
        }
    }

    private static void RestoreEyes()
    {
        var camera = CameraPosition.instance;
        if (camera != null && _baseEyeOffset != null)
        {
            var offset = camera.playerOffset;
            offset.y = _baseEyeOffset.Value;
            camera.playerOffset = offset;
        }
        _baseEyeOffset = null;
        if (CameraCrouchPosition.instance != null && _baseCrouchDrop != null)
            CameraCrouchPosition.instance.Position = _baseCrouchDrop.Value;
        _baseCrouchDrop = null;
        if (CameraCrawlPosition.instance != null && _baseCrawlDrop != null)
            CameraCrawlPosition.instance.Position = _baseCrawlDrop.Value;
        _baseCrawlDrop = null;
    }

    // The killer's body must not cast shadows: the red light sits at their head, and their own (now larger) model would shut it in.
    private static void NoShadows(PlayerAvatar killer)
    {
        var renderers = new List<Renderer>(killer.GetComponentsInChildren<Renderer>(true));
        if (killer.tumble != null)
            renderers.AddRange(killer.tumble.GetComponentsInChildren<Renderer>(true));
        if (KillerWeapon.Weapon != null)
            renderers.AddRange(KillerWeapon.Weapon.GetComponentsInChildren<Renderer>(true));
        foreach (var renderer in renderers)
            if (renderer != null && renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off && !(renderer is ParticleSystemRenderer))
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    internal static void Tick()
    {
        PlayerAvatar? killer = null;
        if (KillerRole.ModeActive && KillerRole.KillerSteamId != null)
            killer = SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p));
        if (killer == null || killer.playerAvatarVisuals == null)
        {
            if (_scaled != null || _soundOwner != null)
                Reset();
            return;
        }

        if (_scaled != killer)
        {
            if (_scaled != null)
                _scaled.playerAvatarVisuals.transform.localScale = _baseScale;
            _scaled = killer;
            _baseScale = killer.playerAvatarVisuals.transform.localScale;
            Hunted.Logger.LogInfo($"Killer {killer.playerName} scaled x{Tuning.Killer.Scale:0.##} (model {_baseScale} -> {_baseScale * Tuning.Killer.Scale}, this game is {(killer.isLocal ? "the killer" : "a survivor")}).");
        }
        killer.playerAvatarVisuals.transform.localScale = _baseScale * Tuning.Killer.Scale;
        RaiseEyes(killer);

        _shadowTimer -= Time.unscaledDeltaTime;
        if (_shadowTimer <= 0f)
        {
            _shadowTimer = 1f;
            NoShadows(killer);
        }

        if (_soundOwner != killer)
        {
            _soundOwner = killer;
            Sounds.Clear();
            foreach (var field in SoundFields)
                if (field.GetValue(killer) is Sound sound && sound != null)
                    Sounds.Add(sound);
        }
    }
}

[HarmonyPatch]
static class KillerBodyPatches
{
    // --- Sound effects: louder and deeper (the Sound.Play patch is in KillerSoundPatch) ---------------------------------------

    // The footsteps and impacts all come from sounds shared by every player (the floor material's), so the killer's are marked by
    // the moment they are played.
    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.Footstep))]
    [HarmonyPrefix]
    private static void Footstep_Prefix(PlayerAvatar __instance) => Enter(__instance, heavy: true);

    [HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.Footstep))]
    [HarmonyFinalizer]
    private static void Footstep_Finalizer(PlayerAvatar __instance) => Leave(__instance, heavy: true);

    [HarmonyPatch(typeof(PlayerAvatar), "JumpRPC")]
    [HarmonyPrefix]
    private static void Jump_Prefix(PlayerAvatar __instance) => Enter(__instance, heavy: true);

    [HarmonyPatch(typeof(PlayerAvatar), "JumpRPC")]
    [HarmonyFinalizer]
    private static void Jump_Finalizer(PlayerAvatar __instance) => Leave(__instance, heavy: true);

    [HarmonyPatch(typeof(PlayerAvatar), "LandRPC")]
    [HarmonyPrefix]
    private static void Land_Prefix(PlayerAvatar __instance) => Enter(__instance, heavy: true);

    [HarmonyPatch(typeof(PlayerAvatar), "LandRPC")]
    [HarmonyFinalizer]
    private static void Land_Finalizer(PlayerAvatar __instance) => Leave(__instance, heavy: true);

    // --- Monsters do not hear the killer --------------------------------------------------------------------------------------

    [HarmonyPatch(typeof(PlayerVoiceChat), "Update")]
    [HarmonyPrefix]
    private static void Voice_Prefix(PlayerVoiceChat __instance) => Enter(__instance.playerAvatar, heavy: false);

    [HarmonyPatch(typeof(PlayerVoiceChat), "Update")]
    [HarmonyFinalizer]
    private static void Voice_Finalizer(PlayerVoiceChat __instance) => Leave(__instance.playerAvatar, heavy: false);

    // Things the killer is holding (the weapon, and valuables if the debug option lets them) make no noise either.
    [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), "EnemyInvestigate", typeof(float), typeof(Vector3))]
    [HarmonyPrefix]
    private static void Impact_Prefix(PhysGrabObjectImpactDetector __instance, out bool __state)
    {
        __state = KillerRole.ModeActive && __instance.physGrabObject != null
            && __instance.physGrabObject.playerGrabbing.Any(g => g != null && KillerRole.IsKiller(g.playerAvatar));
        if (__state)
            KillerBody.Muted++;
    }

    [HarmonyPatch(typeof(PhysGrabObjectImpactDetector), "EnemyInvestigate", typeof(float), typeof(Vector3))]
    [HarmonyFinalizer]
    private static void Impact_Finalizer(bool __state)
    {
        if (__state)
            KillerBody.Muted--;
    }

    [HarmonyPatch(typeof(EnemyDirector), nameof(EnemyDirector.SetInvestigate))]
    [HarmonyPrefix]
    private static bool SetInvestigate_Prefix() => KillerBody.Muted <= 0;

    private static void Enter(PlayerAvatar? avatar, bool heavy)
    {
        if (!KillerRole.ModeActive || !KillerRole.IsKiller(avatar))
            return;
        KillerBody.Muted++;
        if (heavy)
            KillerBody.Heavy++;
    }

    private static void Leave(PlayerAvatar? avatar, bool heavy)
    {
        if (!KillerRole.ModeActive || !KillerRole.IsKiller(avatar))
            return;
        KillerBody.Muted = Math.Max(0, KillerBody.Muted - 1);
        if (heavy)
            KillerBody.Heavy = Math.Max(0, KillerBody.Heavy - 1);
    }
}

[HarmonyPatch]
static class KillerSoundPatch
{
    private static readonly MethodInfo PlayMethod = AccessTools.Method(typeof(Sound), nameof(Sound.Play),
        new[] { typeof(Vector3), typeof(float), typeof(float), typeof(float), typeof(float) });

    private static MethodBase TargetMethod() => PlayMethod;

    [HarmonyPrefix]
    private static void Play_Prefix(Sound __instance, ref float falloffMultiplier, out (float volume, float pitch, bool changed) __state)
    {
        __state = (__instance.Volume, __instance.Pitch, false);
        if (!KillerRole.ModeActive || (KillerBody.Heavy <= 0 && !KillerBody.IsKillerSound(__instance)))
            return;
        __instance.Pitch *= Tuning.Killer.SoundPitchMultiplier;
        // Louder and carrying further only for the survivors: the killer hears their own sounds at the game's volume (only deeper).
        if (!KillerRole.LocalIsKiller)
        {
            __instance.Volume *= Tuning.Killer.SoundVolumeMultiplier;
            // The distances at which the sound starts to fade and is gone grow too, so it carries further.
            falloffMultiplier *= Tuning.Killer.SoundRangeMultiplier;
        }
        __state.changed = true;
    }

    [HarmonyPostfix]
    private static void Play_Postfix(Sound __instance, (float volume, float pitch, bool changed) __state)
    {
        if (!__state.changed)
            return;
        __instance.Volume = __state.volume;
        __instance.Pitch = __state.pitch;
    }
}

/// <summary>The same for the killer's looping sounds (the laser's hum, the staff's charge), which the game plays another way.</summary>
[HarmonyPatch(typeof(Sound), nameof(Sound.PlayLoop))]
static class KillerLoopSoundPatch
{
    [HarmonyPrefix]
    private static void PlayLoop_Prefix(Sound __instance, ref float pitchMultiplier, ref float volumeMultiplier)
    {
        if (!KillerRole.ModeActive || !KillerBody.IsKillerSound(__instance))
            return;
        pitchMultiplier *= Tuning.Killer.SoundPitchMultiplier;
        if (!KillerRole.LocalIsKiller)
            volumeMultiplier *= Tuning.Killer.SoundVolumeMultiplier;
    }
}

[HarmonyPatch(typeof(PlayerCollision), "Update")]
static class KillerColliderPatch
{
    // The killer's collision (the capsule they walk with) is scaled with their body. Other players' copies of it follow, because the
    // collision's scale is what the game sends to them (it only has shortcuts for the standing and crouching sizes).
    [HarmonyPostfix]
    private static void Update_Postfix(PlayerCollision __instance)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller)
            return;
        __instance.transform.localScale *= Tuning.Killer.Scale;
    }
}
