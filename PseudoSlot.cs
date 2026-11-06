using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Hunted;

/// <summary>
/// An inventory slot that holds no real item but an ability: its picture and its charge bars are drawn here, every frame after the slot's own update, and its key
/// is handled by the ability (see Banger and Everseer). Only on the killer's own computer.
/// </summary>
internal static class PseudoSlot
{
    private static readonly Dictionary<string, Sprite?> Icons = new();

    /// <summary>Draws the slot: the icon (brighter while the ability is on), <paramref name="bars"/> charge bars of which <paramref name="ready"/> are full, filling up again if <paramref name="filling"/>.</summary>
    internal static void Draw(InventorySpot spot, Sprite? icon, int bars, int ready, bool filling, bool active)
    {
        if (!spot.IsOccupied())
            spot.currentState = InventorySpot.SpotState.Occupied;
        if (icon != null && spot.inventoryIcon != null)
        {
            if (spot.inventoryIcon.sprite != icon || !spot.inventoryIcon.enabled)
                spot.SetEmoji(icon); // how bright and big it is: see InventorySlots.Look
        }

        var visual = spot.batteryVisualLogic;
        if (visual == null)
            return;
        ready = Mathf.Clamp(ready, 0, bars);
        if (!visual.gameObject.activeSelf)
            visual.gameObject.SetActive(true);
        if (visual.doOutro || visual.targetScale == 0f)
            visual.ResetOutro();
        if (visual.itemBattery == null && visual.bars.Count != bars)
        {
            visual.batteryBars = bars;
            visual.BatteryBarsSet();
            visual.BatteryBarsUpdate(ready, true);
        }
        else if (visual.currentBars != ready)
            visual.BatteryBarsUpdate(ready, true);
        if (filling)
            visual.OverrideBatteryCharge(0.2f); // the bars show it filling up again
    }

    /// <summary>A picture built into the DLL (its resource name, like "Hunted.MaskIcon.png"), loaded once.</summary>
    internal static Sprite? Icon(string resource)
    {
        if (Icons.TryGetValue(resource, out var cached) && cached != null)
            return cached;
        Sprite? sprite = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream != null)
            {
                var bytes = new byte[stream.Length];
                stream.Read(bytes, 0, bytes.Length);
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                if (texture.LoadImage(bytes))
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
            }
        }
        catch (System.Exception exception)
        {
            Hunted.Logger.LogWarning($"Could not load {resource}: {exception.Message}");
        }
        Icons[resource] = sprite;
        return sprite;
    }
}

/// <summary>Copies of the game's models with nothing but their looks: no scripts, sounds, effects, colliders or network parts.</summary>
internal static class VisualClone
{
    /// <summary>
    /// A copy of the looks of <paramref name="source"/> (a prefab or a part of one): the same tree of objects with the same names, places and meshes, and its
    /// Animator if <paramref name="keepAnimator"/> (which finds its parts by name, so it animates the copy). Nothing else is copied, so none of the source's
    /// scripts, sounds or effects come along. It comes back switched off.
    /// </summary>
    internal static GameObject Make(GameObject source, bool keepAnimator)
    {
        var copy = Copy(source.transform, null, keepAnimator);
        copy.SetActive(false);
        return copy;
    }

    private static GameObject Copy(Transform source, Transform? parent, bool keepAnimator)
    {
        var copy = new GameObject(source.name);
        copy.layer = source.gameObject.layer;
        if (parent != null)
        {
            copy.transform.SetParent(parent, false);
            copy.SetActive(source.gameObject.activeSelf);
        }
        copy.transform.localPosition = source.localPosition;
        copy.transform.localRotation = source.localRotation;
        copy.transform.localScale = source.localScale;
        var filter = source.GetComponent<MeshFilter>();
        var renderer = source.GetComponent<MeshRenderer>();
        if (filter != null && renderer != null && !(source.GetComponent<ParticleSystem>() != null))
        {
            copy.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var copied = copy.AddComponent<MeshRenderer>();
            copied.sharedMaterials = renderer.sharedMaterials;
            copied.shadowCastingMode = renderer.shadowCastingMode;
            copied.enabled = renderer.enabled;
        }
        if (keepAnimator && source.GetComponent<Animator>() is { } animator)
        {
            var copied = copy.AddComponent<Animator>();
            copied.runtimeAnimatorController = animator.runtimeAnimatorController;
            copied.avatar = animator.avatar;
            copied.cullingMode = animator.cullingMode;
            copied.applyRootMotion = false;
        }
        foreach (Transform child in source)
            Copy(child, copy.transform, keepAnimator);
        return copy;
    }

    /// <summary>Every renderer under <paramref name="root"/> takes this colour (with its own picture and brightness kept), and glows <paramref name="glow"/> times as bright in it.</summary>
    internal static void Recolour(GameObject root, Color colour, float glow)
    {
        foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer is LineRenderer line)
            {
                line.startColor = new Color(colour.r, colour.g, colour.b, line.startColor.a);
                line.endColor = new Color(colour.r, colour.g, colour.b, line.endColor.a);
            }
            if (renderer is ParticleSystemRenderer)
            {
                var system = renderer.GetComponent<ParticleSystem>();
                if (system != null)
                {
                    var main = system.main;
                    main.startColor = colour;
                }
            }
            foreach (var material in renderer.materials)
            {
                if (material == null)
                    continue;
                foreach (var name in new[] { "_Color", "_BaseColor", "_TintColor", "_AlbedoColor" })
                    if (material.HasProperty(name))
                    {
                        var old = material.GetColor(name);
                        material.SetColor(name, new Color(colour.r, colour.g, colour.b, old.a));
                    }
                if (material.HasProperty("_EmissionColor"))
                {
                    // As bright as it was (the game's effects glow far past 1), only purple.
                    var old = material.GetColor("_EmissionColor");
                    var strength = Mathf.Max(0.5f, Mathf.Max(old.r, Mathf.Max(old.g, old.b)));
                    material.SetColor("_EmissionColor", colour * strength * glow);
                }
            }
        }
        foreach (var light in root.GetComponentsInChildren<Light>(true))
            light.color = colour;
    }
}
