using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The yellow shape a revealed survivor (see SurvivorReveal) has for the killer, through walls: a flat yellow, see-through copy of each of their meshes, drawn after everything else and only where
/// something is in front of it (a wall, a table), so a survivor in plain view is not covered by it. The copies hang on the survivor (a skinned mesh copy shares its bones, so it moves with them) and exist only on the killer's own computer.
/// </summary>
internal static class KillerXray
{
    private const string GhostName = "Hunted Xray";
    private static Material? _material;
    private static bool _failed;
    private static readonly Dictionary<Component, (int sources, List<GameObject> ghosts)> Ghosts = new();

    internal static void Reset()
    {
        foreach (var entry in Ghosts.Values)
            foreach (var ghost in entry.ghosts)
                if (ghost != null)
                    Object.Destroy(ghost);
        Ghosts.Clear();
    }

    /// <summary>Hides the shape of a survivor who is not revealed any more.</summary>
    internal static void Hide(Component key)
    {
        if (!Ghosts.TryGetValue(key, out var entry))
            return;
        foreach (var ghost in entry.ghosts)
            if (ghost != null)
                Object.Destroy(ghost);
        Ghosts.Remove(key);
    }

    private static bool Eligible(Renderer renderer)
        => renderer.gameObject.activeInHierarchy && renderer.gameObject.name != GhostName
           && (renderer is SkinnedMeshRenderer { sharedMesh: not null } || (renderer is MeshRenderer && renderer.GetComponent<MeshFilter>()?.sharedMesh != null));

    /// <summary>Shows (and keeps up to date) the shape of a revealed survivor.</summary>
    internal static void Show(Component key, Transform root)
    {
        if (_failed)
            return;
        var sources = root.GetComponentsInChildren<Renderer>(false).Where(Eligible).ToList();
        if (Ghosts.TryGetValue(key, out var entry))
        {
            if (entry.sources == sources.Count)
                return;
            foreach (var ghost in entry.ghosts)
                if (ghost != null)
                    Object.Destroy(ghost); // their cosmetics changed: copy them again
        }
        var material = Material();
        if (material == null)
            return;
        var list = new List<GameObject>();
        foreach (var source in sources)
        {
            var ghost = Copy(source, material);
            if (ghost != null)
                list.Add(ghost);
        }
        Ghosts[key] = (sources.Count, list);
    }

    private static GameObject? Copy(Renderer source, Material material)
    {
        var go = new GameObject(GhostName) { layer = source.gameObject.layer };
        go.transform.SetParent(source.transform, false);
        if (source is SkinnedMeshRenderer skinned)
        {
            var copy = go.AddComponent<SkinnedMeshRenderer>();
            copy.sharedMesh = skinned.sharedMesh;
            copy.bones = skinned.bones;
            copy.rootBone = skinned.rootBone;
            copy.localBounds = skinned.localBounds;
            copy.updateWhenOffscreen = true;
            copy.sharedMaterials = Enumerable.Repeat(material, skinned.sharedMesh.subMeshCount).ToArray();
            Configure(copy);
            return go;
        }
        var filter = source.GetComponent<MeshFilter>();
        if (filter == null)
        {
            Object.Destroy(go);
            return null;
        }
        go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = Enumerable.Repeat(material, filter.sharedMesh.subMeshCount).ToArray();
        Configure(renderer);
        return go;
    }

    private static void Configure(Renderer renderer)
    {
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
    }

    private static Material? Material()
    {
        if (_material != null)
            return _material;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            _failed = true;
            Hunted.Logger.LogWarning("Killer x-ray: the game's flat-colour shader was not found, so revealed survivors get no shape.");
            return null;
        }
        _material = new Material(shader) { name = "Hunted Xray", hideFlags = HideFlags.HideAndDontSave };
        _material.SetColor("_Color", new Color(SurvivorReveal.Color.r, SurvivorReveal.Color.g, SurvivorReveal.Color.b, Tuning.KillerRoom.XrayAlpha));
        _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Back);
        _material.SetInt("_ZWrite", 0);
        // Only where something is in front of the shape (it is "greater" than what the screen already shows). The shape is nudged a little towards the
        // camera, so the survivor's own surface, at the very same depth, never counts as being in front of it.
        _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Greater);
        _material.SetFloat("_ZBias", -1f);
        _material.renderQueue = 4000; // after everything else
        Hunted.Logger.LogInfo("Killer x-ray ready: revealed survivors show as yellow shapes through walls.");
        return _material;
    }
}
