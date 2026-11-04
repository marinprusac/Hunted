using UnityEngine;

namespace Hunted;

/// <summary>
/// A deep red light all around the killer, like the red stain in Dead by Daylight, so survivors see the killer coming. The killer does not see it themselves: it is built on every client, and switched off only on the killer's own. It hangs on
/// the killer's view camera (every client keeps that in step with the player), so it is built on every client. The killer's body
/// does not cast shadows (see KillerBody), or it would block the light from the inside.
/// </summary>
internal static class KillerAura
{
    private static GameObject? _object;
    private static PlayerAvatar? _owner;

    internal static void Reset()
    {
        if (_object != null)
            Object.Destroy(_object);
        _object = null;
        _owner = null;
    }

    internal static void Tick()
    {
        PlayerAvatar? killer = null;
        if (KillerRole.ModeActive && KillerRole.KillerSteamId != null)
        {
            foreach (var player in SemiFunc.PlayerGetList())
            {
                if (player != null && KillerRole.IsKiller(player))
                {
                    killer = player;
                    break;
                }
            }
        }

        if (killer == null || killer.localCamera == null || killer.isDisabled)
        {
            if (_object != null)
                Reset();
            return;
        }
        if (_object != null && _owner == killer)
        {
            Apply(_object.GetComponent<Light>());
            return;
        }

        Reset();
        _owner = killer;
        _object = new GameObject("Hunted Red Light");
        _object.transform.SetParent(killer.localCamera.transform, false);
        _object.transform.localPosition = Vector3.zero;
        _object.transform.localRotation = Quaternion.identity;
        var light = _object.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = new Color(1f, 0f, 0f);
        Apply(light);
        Hunted.Logger.LogDebug($"Red light built on the camera of {killer.playerName} (this game is {(killer.isLocal ? "the killer" : "a survivor")}): enabled={light.enabled}, range={light.range}, intensity={light.intensity}, shadows={light.shadows}.");
    }

    private static void Apply(Light light)
    {
        // Every computer has its own copy of the light. On the killer's own it is off, so the killer's view is not red; on everybody
        // else's it is on, and it stops at walls, so the red does not give the killer away from the next room.
        var visible = !_owner!.isLocal;
        if (light.enabled != visible)
            light.enabled = visible;
        var shadows = visible ? LightShadows.Hard : LightShadows.None;
        if (light.shadows != shadows)
            light.shadows = shadows;
        light.range = Tuning.Killer.RedLightRange;
        light.intensity = Tuning.Killer.RedLightIntensity;
    }
}
