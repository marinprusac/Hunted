using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// How the killer looks: whatever cosmetics they own are replaced by their killer's outfit, and their eyes are plain glowing circles in the killer's colour.
///  - Skelespawn: every skeleton cosmetic the game has; a white body with red armour.
///  - Everseer: a mask, branch antlers, a beard, a wizard's robe and a grabber of petals; a purple body with lavender clothes.
/// Both are decided locally on every client (cosmetics are built from a list of numbers each client receives, and the killer and their kind are known to
/// everyone), so nothing needs to be synced.
/// </summary>
internal static class KillerLook
{
    private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
    private static List<int>? _skeleton;
    private static List<int>? _everseer;
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

    private static readonly Color CosmeticRed = new(0.85f, 0f, 0f, 1f);
    private static readonly Color EverseerBody = new(0.42f, 0.1f, 0.72f, 1f);
    private static readonly Color EverseerCloth = new(0.78f, 0.62f, 1f, 1f);

    // The game's names of the Everseer's cosmetics, one for each place on the body.
    private static readonly string[] EverseerOutfit =
    {
        "Cosmetic - Face Top - Mask",
        "Cosmetic - Hat - Branch Antlers",
        "Cosmetic - Head Bottom - Beard",
        "Cosmetic - Body Top - Wizard Shirt",
        "Cosmetic - Body Bottom - Wizard Bottom",
        "Cosmetic - Arm Left - Wizard Sleeve",
        "Cosmetic - Arm Right - Wizard Sleeve",
        "Cosmetic - Grabber Mesh - Petals",
    };

    // The skeleton set, plus the skull belt (which is named "skull", not "skeleton").
    private static bool IsSkeleton(CosmeticAsset? asset) => asset != null
        && (asset.name.IndexOf("skeleton", System.StringComparison.OrdinalIgnoreCase) >= 0 || asset.name.IndexOf("skull belt", System.StringComparison.OrdinalIgnoreCase) >= 0);

    private static bool IsOverlay(CosmeticAsset? asset) => asset != null && asset.name.IndexOf("overlay", System.StringComparison.OrdinalIgnoreCase) >= 0;

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

    /// <summary>The game's indices of the Everseer's cosmetics (those the game has; a missing one is left out).</summary>
    internal static int[] EverseerIndices()
    {
        var assets = MetaManager.instance != null ? MetaManager.instance.cosmeticAssets : null;
        if (assets == null)
            return System.Array.Empty<int>();
        if (_everseer == null || _everseer.Count == 0)
        {
            _everseer = new List<int>();
            foreach (var name in EverseerOutfit)
            {
                var index = assets.FindIndex(a => a != null && string.Equals(a.name.Trim(), name, System.StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    _everseer.Add(index);
                else
                    Hunted.Logger.LogWarning($"The Everseer's cosmetic \"{name}\" is not in the game.");
            }
            Hunted.Logger.LogInfo($"Everseer cosmetics: {string.Join(", ", _everseer.Select(i => assets[i].name))}");
        }
        return _everseer.ToArray();
    }

    /// <summary>The cosmetics of this round's killer.</summary>
    internal static int[] OutfitIndices() => KillerKinds.Current == KillerKind.Everseer ? EverseerIndices() : SkeletonIndices();

    private static bool InOutfit(CosmeticAsset? asset)
        => asset != null && (KillerKinds.Current == KillerKind.Everseer ? EverseerOutfit.Any(n => string.Equals(asset.name.Trim(), n, System.StringComparison.OrdinalIgnoreCase)) : IsSkeleton(asset));

    internal static bool BelongsToKiller(PlayerCosmetics cosmetics)
    {
        if (!KillerRole.ModeActive || KillerRole.KillerSteamId == null)
            return false;
        if (cosmetics.playerAvatarVisuals != null && cosmetics.playerAvatarVisuals.isMenuAvatar)
            return IsKillersEscAvatar(cosmetics.playerAvatarVisuals);
        var avatar = cosmetics.playerAvatarVisuals != null && !cosmetics.playerAvatarVisuals.isMenuAvatar ? cosmetics.playerAvatarVisuals.playerAvatar
            : cosmetics.deathHead != null ? cosmetics.deathHead.playerAvatar : null;
        return avatar != null && KillerRole.IsKiller(avatar);
    }

    /// <summary>
    /// The picture of yourself in the Escape menu, when you are the killer: it shows you as you are in the round. The avatar in the cosmetics page
    /// is left alone, since that is where you pick the outfit you wear as a survivor.
    /// </summary>
    private static bool IsKillersEscAvatar(PlayerAvatarVisuals visuals)
    {
        var menu = visuals.playerAvatarMenu;
        if (menu == null || menu.worldAvatar || menu.iconMakerAvatar || menu.expressionAvatar || menu.parentPage == null || menu.parentPage.GetComponent<MenuPageEsc>() == null)
            return false;
        var local = SemiFunc.PlayerAvatarLocal();
        return local != null && KillerRole.IsKiller(local);
    }

    /// <summary>The menu avatar's eyes, as the killer's: plain glowing circles in the killer's colour (no light, it is only a picture).</summary>
    internal static void KillerEyes(PlayerAvatarVisuals visuals)
    {
        var eyes = visuals.playerEyes;
        if (eyes == null)
            return;
        foreach (var part in new[] { eyes.eyeLeft, eyes.eyeRight, eyes.pupilLeft, eyes.pupilRight })
            if (part != null)
                foreach (var renderer in part.GetComponentsInChildren<Renderer>(true))
                    Tint(renderer, KillerKinds.Colour, Tuning.Killer.EyeGlow);
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

        EnsureOutfit(killer);
        if (killer.playerCosmetics != null)
            ColourOutfit(killer.playerCosmetics);
        GlowEyes(killer);
    }

    /// <summary>
    /// The killer's colours. Skelespawn: the skin (the body itself) is white and every cosmetic item on it is red and plain. Everseer: the skin is purple and the
    /// cosmetic items are lavender, with their own pictures kept. The player colour is replaced on every part. The surface's relief and the lighting still show
    /// its shape. The eyes are left alone.
    /// </summary>
    internal static void ColourOutfit(PlayerCosmetics cosmetics)
    {
        var everseer = KillerKinds.Current == KillerKind.Everseer;
        var eyes = cosmetics.playerAvatarVisuals != null ? cosmetics.playerAvatarVisuals.playerEyes : null;
        var eyeParts = eyes == null ? new Transform?[0] : new[] { eyes.eyeLeft, eyes.eyeRight, eyes.pupilLeft, eyes.pupilRight };
        foreach (var part in cosmetics.playerMaterials)
        {
            var material = part != null ? part.material : null;
            if (material == null || eyeParts.Any(e => e != null && part!.transform.IsChildOf(e)))
                continue;
            var albedo = material.HasProperty("_AlbedoColor") ? material.GetColor("_AlbedoColor") : Color.white;
            // The skeleton overlays wrap the body parts and draw the bones on them: they are part of the white body and keep their picture.
            // The other cosmetic items (armour, teeth, belt, grabber) are red and plain.
            var overlay = part!.cosmetic != null && IsOverlay(part.cosmetic.cosmeticAsset);
            var item = part.cosmetic != null && !overlay; // cosmetic items know their cosmetic; the body does not
            var colour = everseer ? (item ? EverseerCloth : EverseerBody) : item ? CosmeticRed : Color.white;
            SetColor(material, "_AlbedoColor", new Color(colour.r, colour.g, colour.b, albedo.a));
            if (!overlay && !(everseer && item))
                SetTexture(material, "_AlbedoTexture", Texture2D.whiteTexture);
            if (part.tintType != PlayerMaterial.TintType.Albedo)
                SetColor(material, "_EmissionColor", Color.black); // a glow in the player's colour would stay coloured
            if (part.tintFresnel)
                SetColor(material, "_FresnelColor", colour);
        }
    }

    // The killer may have been picked after the cosmetics were built (or the cosmetics arrived late): build them again.
    private static void EnsureOutfit(PlayerAvatar killer)
    {
        var cosmetics = killer.playerCosmetics;
        if (cosmetics == null || cosmetics.firstSetup || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated)
            return;
        if (_cosmeticsFor != killer)
        {
            _cosmeticsFor = killer;
            _cosmeticAttempts = 0;
        }
        if (cosmetics.cosmeticEquipped.Any(c => c != null && InOutfit(c.cosmeticAsset)) || _cosmeticAttempts >= 6)
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
                Tint(renderer, KillerKinds.Colour, glow);
            // The light is only for those who look at the killer: the killer's own view already has their red light.
            if (!killer.isLocal && white.Find("Hunted Eye Light") == null)
            {
                var lightObject = new GameObject("Hunted Eye Light");
                lightObject.transform.SetParent(white, false);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = KillerKinds.Colour;
                light.range = Tuning.Killer.EyeLightRange;
                light.intensity = Tuning.Killer.EyeLightIntensity;
                light.shadows = LightShadows.None;
            }
        }
        // The pupils get the same colour, so each eye is one plain bright circle.
        foreach (var pupil in new[] { eyes.pupilLeft, eyes.pupilRight })
            if (pupil != null)
                foreach (var renderer in pupil.GetComponentsInChildren<Renderer>(true))
                    Tint(renderer, KillerKinds.Colour, glow);

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
    // The killer wears their killer's outfit instead of what they own, as every client builds them.
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerCosmetics), "SetupCosmeticsLogic")]
    private static void SetupCosmetics_Prefix(PlayerCosmetics __instance, ref int[] _cosmeticEquipped)
    {
        if (KillerLook.BelongsToKiller(__instance))
            _cosmeticEquipped = KillerLook.OutfitIndices();
    }

    // The player's colours are applied after the cosmetics and whenever they change; the killer's are white right after.
    [HarmonyPostfix, HarmonyPatch(typeof(PlayerCosmetics), "SetupColorsLogic")]
    private static void SetupColors_Postfix(PlayerCosmetics __instance)
    {
        if (KillerLook.BelongsToKiller(__instance))
        {
            KillerLook.ColourOutfit(__instance);
            if (__instance.playerAvatarVisuals != null && __instance.playerAvatarVisuals.isMenuAvatar)
                KillerLook.KillerEyes(__instance.playerAvatarVisuals);
        }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlayerCosmetics), "SetupColorsAllLogic")]
    private static void SetupColorsAll_Postfix(PlayerCosmetics __instance)
    {
        if (KillerLook.BelongsToKiller(__instance))
        {
            KillerLook.ColourOutfit(__instance);
            if (__instance.playerAvatarVisuals != null && __instance.playerAvatarVisuals.isMenuAvatar)
                KillerLook.KillerEyes(__instance.playerAvatarVisuals);
        }
    }
}
