using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// How the killer looks: whatever cosmetics they own are replaced by every skeleton cosmetic the game has, the whole outfit is white,
/// and their eyes are plain glowing red circles. Both are decided locally on every client (cosmetics are built from a list of numbers each client receives, and
/// the killer is known to everyone), so nothing needs to be synced.
/// </summary>
internal static class KillerLook
{
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    private static List<int>? _skeleton;
    private static float _timer;
    private static PlayerAvatar? _logged;
    private static int _cosmeticAttempts;
    private static PlayerAvatar? _cosmeticsFor;

    internal static void Reset()
    {
        _timer = 0f;
        _logged = null;
        _cosmeticAttempts = 0;
        _cosmeticsFor = null;
    }

    private static readonly Color CosmeticRed = new(0.85f, 0.03f, 0.03f, 1f);

    // The skeleton set, plus the skull belt (which is named "skull", not "skeleton").
    private static bool IsSkeleton(CosmeticAsset? asset) => asset != null
        && (asset.name.IndexOf("skeleton", System.StringComparison.OrdinalIgnoreCase) >= 0 || asset.name.IndexOf("skull belt", System.StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>The game's indices of every skeleton cosmetic (overlays, armor, teeth, grabber) and the skull belt.</summary>
    internal static int[] SkeletonIndices()
    {
        var assets = MetaManager.instance != null ? MetaManager.instance.cosmeticAssets : null;
        if (assets == null)
            return System.Array.Empty<int>();
        if (_skeleton == null || _skeleton.Count == 0)
        {
            _skeleton = new List<int>();
            for (var i = 0; i < assets.Count; i++)
                if (IsSkeleton(assets[i]))
                    _skeleton.Add(i);
            Hunted.Logger.LogInfo($"Skeleton cosmetics for the killer: {string.Join(", ", _skeleton.Select(i => assets[i].name))}");
        }
        return _skeleton.ToArray();
    }

    internal static bool BelongsToKiller(PlayerCosmetics cosmetics)
    {
        if (!KillerRole.ModeActive || KillerRole.KillerSteamId == null)
            return false;
        var avatar = cosmetics.playerAvatarVisuals != null && !cosmetics.playerAvatarVisuals.isMenuAvatar ? cosmetics.playerAvatarVisuals.playerAvatar
            : cosmetics.deathHead != null ? cosmetics.deathHead.playerAvatar : null;
        return avatar != null && KillerRole.IsKiller(avatar);
    }

    internal static void Tick(float dt)
    {
        _timer -= dt;
        if (_timer > 0f)
            return;
        _timer = 0.5f;

        PlayerAvatar? killer = null;
        if (KillerRole.ModeActive && KillerRole.KillerSteamId != null)
            killer = SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p));
        if (killer == null || killer.playerAvatarVisuals == null)
            return;

        EnsureSkeleton(killer);
        if (killer.playerCosmetics != null)
            WhitenOutfit(killer.playerCosmetics);
        GlowEyes(killer);
    }

    /// <summary>
    /// The killer's skin (the body itself) is white and every cosmetic item on it is red: the player colour is replaced on every part, and the cosmetics
    /// that are not tinted by it (the skeleton ones) lose their own colour too. The surface's relief and the lighting still show its shape. The eyes are left alone.
    /// </summary>
    internal static void WhitenOutfit(PlayerCosmetics cosmetics)
    {
        var eyes = cosmetics.playerAvatarVisuals != null ? cosmetics.playerAvatarVisuals.playerEyes : null;
        var eyeParts = eyes == null ? new Transform?[0] : new[] { eyes.eyeLeft, eyes.eyeRight, eyes.pupilLeft, eyes.pupilRight };
        foreach (var part in cosmetics.playerMaterials)
        {
            var material = part != null ? part.material : null;
            if (material == null || eyeParts.Any(e => e != null && part!.transform.IsChildOf(e)))
                continue;
            var albedo = material.HasProperty("_AlbedoColor") ? material.GetColor("_AlbedoColor") : Color.white;
            var colour = part!.cosmetic != null ? CosmeticRed : Color.white; // cosmetic items know their cosmetic; the body does not
            SetColor(material, "_AlbedoColor", new Color(colour.r, colour.g, colour.b, albedo.a));
            SetTexture(material, "_AlbedoTexture", Texture2D.whiteTexture);
            if (part.tintType != PlayerMaterial.TintType.Albedo)
                SetColor(material, "_EmissionColor", Color.black); // a glow in the player's colour would stay coloured
            if (part.tintFresnel)
                SetColor(material, "_FresnelColor", colour);
        }
    }

    // The killer may have been picked after the cosmetics were built (or the cosmetics arrived late): build them again.
    private static void EnsureSkeleton(PlayerAvatar killer)
    {
        var cosmetics = killer.playerCosmetics;
        if (cosmetics == null || cosmetics.firstSetup || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated)
            return;
        if (_cosmeticsFor != killer)
        {
            _cosmeticsFor = killer;
            _cosmeticAttempts = 0;
        }
        if (cosmetics.cosmeticEquipped.Any(c => c != null && IsSkeleton(c.cosmeticAsset)) || _cosmeticAttempts >= 6)
            return;
        _cosmeticAttempts++;
        cosmetics.SetupCosmeticsLogic(cosmetics.cosmeticEquippedRaw.ToArray(), false);
    }

    private static void GlowEyes(PlayerAvatar killer)
    {
        var eyes = killer.playerAvatarVisuals.playerEyes;
        if (eyes == null)
            return;
        var glow = Tuning.Killer.EyeGlow;
        foreach (var white in new[] { eyes.eyeLeft, eyes.eyeRight })
        {
            if (white == null)
                continue;
            foreach (var renderer in white.GetComponentsInChildren<Renderer>(true))
                Tint(renderer, new Color(1f, 0f, 0f, 1f), glow);
            // The light is only for those who look at the killer: the killer's own view already has their red light.
            if (!killer.isLocal && white.Find("Hunted Eye Light") == null)
            {
                var lightObject = new GameObject("Hunted Eye Light");
                lightObject.transform.SetParent(white, false);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.05f, 0.05f);
                light.range = Tuning.Killer.EyeLightRange;
                light.intensity = Tuning.Killer.EyeLightIntensity;
                light.shadows = LightShadows.None;
            }
        }
        // The pupils get the same red, so each eye is one plain bright red circle.
        foreach (var pupil in new[] { eyes.pupilLeft, eyes.pupilRight })
            if (pupil != null)
                foreach (var renderer in pupil.GetComponentsInChildren<Renderer>(true))
                    Tint(renderer, new Color(1f, 0f, 0f, 1f), glow);

        if (_logged != killer)
        {
            _logged = killer;
            var parts = (eyes.eyeLeft != null ? eyes.eyeLeft.GetComponentsInChildren<Renderer>(true) : new Renderer[0])
                .Select(r => $"{r.name} [{string.Join("/", r.sharedMaterials.Where(m => m != null).Select(m => m.shader.name))}]");
            Hunted.Logger.LogDebug($"Killer eyes: {string.Join(", ", parts)}");
        }
    }

    /// <summary>
    /// Makes a renderer one flat, glowing colour: no picture, no light or highlight, nothing but the colour's own glow. The game's
    /// player shader ("Hurtable") has its own property names, so they are set too, with the common ones for any other shader.
    /// </summary>
    private static void Tint(Renderer renderer, Color color, float emission)
    {
        if (renderer is ParticleSystemRenderer)
            return;
        var flat = emission > 0f;
        foreach (var material in renderer.materials)
        {
            if (material == null)
                continue;
            // The surface itself is black, so scene light and the surroundings cannot wash the colour out; only the glow shows.
            SetColor(material, "_AlbedoColor", flat ? Color.black : color);
            SetColor(material, "_BaseColor", flat ? Color.black : color);
            SetColor(material, "_Color", flat ? Color.black : color);
            SetTexture(material, "_AlbedoTexture", Texture2D.whiteTexture);
            SetTexture(material, "_MainTex", Texture2D.whiteTexture);
            SetTexture(material, "_BaseMap", Texture2D.whiteTexture);
            SetTexture(material, "_EmissionTexture", Texture2D.whiteTexture);
            SetTexture(material, "_EmissionMap", Texture2D.whiteTexture);
            // No rim light, shine, bumps or hurt flash.
            foreach (var name in new[] { "_FresnelAmount", "_FresnelEmission", "_Metallic", "_Smoothness", "_NormalStrength", "_ColorOverlayAmount" })
                if (material.HasProperty(name))
                    material.SetFloat(name, 0f);
            if (material.HasProperty(EmissionColor))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor(EmissionColor, flat ? color * emission : Color.black);
            }
        }
    }

    private static void SetColor(Material material, string name, Color color)
    {
        if (material.HasProperty(name))
            material.SetColor(name, color);
    }

    private static void SetTexture(Material material, string name, Texture texture)
    {
        if (material.HasProperty(name))
            material.SetTexture(name, texture);
    }
}

[HarmonyPatch]
static class KillerLookPatches
{
    // The killer wears every skeleton cosmetic instead of what they own, as every client builds them.
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerCosmetics), "SetupCosmeticsLogic")]
    private static void SetupCosmetics_Prefix(PlayerCosmetics __instance, ref int[] _cosmeticEquipped)
    {
        if (KillerLook.BelongsToKiller(__instance))
            _cosmeticEquipped = KillerLook.SkeletonIndices();
    }

    // The player's colours are applied after the cosmetics and whenever they change; the killer's are white right after.
    [HarmonyPostfix, HarmonyPatch(typeof(PlayerCosmetics), "SetupColorsLogic")]
    private static void SetupColors_Postfix(PlayerCosmetics __instance)
    {
        if (KillerLook.BelongsToKiller(__instance))
            KillerLook.WhitenOutfit(__instance);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlayerCosmetics), "SetupColorsAllLogic")]
    private static void SetupColorsAll_Postfix(PlayerCosmetics __instance)
    {
        if (KillerLook.BelongsToKiller(__instance))
            KillerLook.WhitenOutfit(__instance);
    }
}
