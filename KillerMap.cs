using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>The map is zoomed out for everyone (more for the killer), and the killer's shows every room from the start.</summary>
internal static class KillerMap
{
    private static Camera? _camera;
    private static float _baseSize;
    private static float _baseFov;
    private static float _revealTimer;

    internal static void Reset()
    {
        _camera = null;
        _revealTimer = 0f;
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        var killer = KillerRole.LocalIsKiller;
        _revealTimer -= dt;
        if (_revealTimer <= 0f)
        {
            _revealTimer = 1f;
            if (killer)
                RevealRooms();
            FindCamera();
        }
        ApplyZoom(killer ? Tuning.Map.KillerZoomOut : Tuning.Map.SurvivorZoomOut);
    }

    /// <summary>
    /// The dark cover over a room on the map is removed when the room counts as explored. A room's map cover only exists once
    /// the map has built it, so rooms are checked repeatedly; marking one as explored too early would leave its cover forever.
    /// </summary>
    private static void RevealRooms()
    {
        foreach (var room in Object.FindObjectsByType<RoomVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (room.MapModule != null)
                room.SetExplored();
    }

    private static void FindCamera()
    {
        if (_camera != null)
            return;
        var mask = LayerMask.GetMask("DirtFinderMap");
        var main = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        _camera = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(c => c != main && (c.cullingMask & mask) != 0)
            .OrderBy(c => c.cullingMask == mask ? 0 : 1)
            .FirstOrDefault();
        if (_camera == null)
            return;
        _baseSize = _camera.orthographicSize;
        _baseFov = _camera.fieldOfView;
        Hunted.Logger.LogDebug($"Map camera: {_camera.name}, orthographic={_camera.orthographic}, size={_baseSize}, fov={_baseFov}");
    }

    private static void ApplyZoom(float zoomOut)
    {
        if (_camera == null)
            return;
        zoomOut = Mathf.Max(0.1f, zoomOut);
        if (_camera.orthographic)
            _camera.orthographicSize = _baseSize * zoomOut;
        else
            _camera.fieldOfView = Mathf.Min(170f, Mathf.Atan(Mathf.Tan(_baseFov * 0.5f * Mathf.Deg2Rad) * zoomOut) * 2f * Mathf.Rad2Deg);
    }
}
