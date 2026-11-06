using System.Collections.Generic;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The killer always knows where the killer room is: its corners are marked for them, through walls, with short lines in a light shade of the killer's colour
/// (the eight corners of the room's box, three lines from each, a fifth of an edge long). Nothing else of the room is drawn, so the view stays clear. Only on the
/// killer's own computer.
/// </summary>
internal static class KillerRoomOutline
{
    private static GameObject? _root;
    private static Material? _material;
    private static bool _failed;

    internal static void Reset()
    {
        if (_root != null)
            Object.Destroy(_root);
        _root = null;
    }

    internal static void Tick()
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller)
        {
            if (_root != null)
                Reset();
            return;
        }
        if (_root != null || _failed)
            return;
        var module = KillerRoom.RoomModule();
        if (module == null)
            return;
        Build(module);
    }

    private static void Build(Module module)
    {
        var colour = Color.Lerp(KillerKinds.Colour, Color.white, 0.45f);
        colour.a = 0.7f;
        var material = Material();
        if (material == null)
            return;
        material.SetColor("_Color", colour); // the shader takes its colour from here (and from each line's, which is the same)
        // The room's box: its tile across, and from its floor to its ceiling (its meshes, without anything that sticks out far above).
        var half = LevelGenerator.ModuleWidth * LevelGenerator.TileSize / 2f;
        var centre = module.transform.position;
        var bottom = centre.y;
        var top = centre.y + 3f;
        var found = false;
        foreach (var renderer in module.GetComponentsInChildren<MeshRenderer>(true))
        {
            var bounds = renderer.bounds;
            if (Mathf.Abs(bounds.center.x - centre.x) > half || Mathf.Abs(bounds.center.z - centre.z) > half || bounds.max.y > centre.y + 12f)
                continue;
            bottom = found ? Mathf.Min(bottom, bounds.min.y) : bounds.min.y;
            top = found ? Mathf.Max(top, bounds.max.y) : bounds.max.y;
            found = true;
        }
        _root = new GameObject("Hunted Killer Room Corners");
        var min = new Vector3(centre.x - half, bottom + 0.05f, centre.z - half);
        var max = new Vector3(centre.x + half, top - 0.05f, centre.z + half);
        var size = max - min;
        for (var corner = 0; corner < 8; corner++)
        {
            var at = new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y, (corner & 4) == 0 ? min.z : max.z);
            // A line along each edge that meets here, pointing into the box.
            Line(at, at + new Vector3((corner & 1) == 0 ? 1f : -1f, 0f, 0f) * size.x * 0.2f, colour, material);
            Line(at, at + new Vector3(0f, (corner & 2) == 0 ? 1f : -1f, 0f) * size.y * 0.3f, colour, material);
            Line(at, at + new Vector3(0f, 0f, (corner & 4) == 0 ? 1f : -1f) * size.z * 0.2f, colour, material);
        }
    }

    private static void Line(Vector3 from, Vector3 to, Color colour, Material material)
    {
        var line = new GameObject("Corner").AddComponent<LineRenderer>();
        line.transform.SetParent(_root!.transform, false);
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.SetPositions(new[] { from, to });
        line.sharedMaterial = material;
        line.startColor = line.endColor = colour;
        line.widthMultiplier = 0.08f;
        line.numCapVertices = 2;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
    }

    // A flat colour drawn over everything (through walls), see-through, after everything else.
    private static Material? Material()
    {
        if (_material != null)
            return _material;
        var shader = Shader.Find("Hidden/Internal-Colored");
        if (shader == null)
        {
            _failed = true;
            Hunted.Logger.LogWarning("Killer room corners: the game's flat-colour shader was not found.");
            return null;
        }
        _material = new Material(shader) { name = "Hunted Killer Room Corners", hideFlags = HideFlags.HideAndDontSave };
        _material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        _material.SetInt("_ZWrite", 0);
        _material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
        _material.renderQueue = 4000;
        return _material;
    }
}
