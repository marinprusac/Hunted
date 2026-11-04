using System.Collections.Generic;
using UnityEngine;

namespace Hunted;

/// <summary>
/// In vanilla the truck and its start room are on the map from the start; every other room is covered until a player visits it.
/// For the survivors the truck rooms get the same cover, so they have to find the trucks like any other room. The killer keeps
/// seeing everything.
/// </summary>
internal static class TruckMapCover
{
    private static readonly HashSet<RoomVolume> Done = new();

    internal static void Reset() => Done.Clear();

    internal static void Tick()
    {
        if (!KillerRole.ModeActive || KillerRole.LocalIsKiller || Map.Instance == null || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        foreach (var volume in Object.FindObjectsByType<RoomVolume>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (volume == null || volume.MapModule != null || volume.Extraction || volume.Module == null || Done.Contains(volume))
                continue;
            if (!volume.Truck && !volume.Module.StartRoom)
                continue;
            Done.Add(volume);
            Cover(volume);
        }
    }

    // The same calls the game makes for every other room (RoomVolume.Setup), which skips the truck and start rooms.
    private static void Cover(RoomVolume volume)
    {
        foreach (var box in volume.GetComponentsInChildren<BoxCollider>())
        {
            var scale = box.size * 0.5f;
            scale.x *= Mathf.Abs(box.transform.lossyScale.x);
            scale.y *= Mathf.Abs(box.transform.lossyScale.y);
            scale.z *= Mathf.Abs(box.transform.lossyScale.z);
            var position = box.transform.TransformPoint(box.center);
            var euler = new Vector3(0f, box.transform.rotation.eulerAngles.y, 0f);
            volume.MapModule = Map.Instance.AddRoomVolume(volume.gameObject, position, Quaternion.Euler(euler), scale, volume.Module);
        }
        foreach (var mesh in volume.GetComponentsInChildren<MeshCollider>())
            volume.MapModule = Map.Instance.AddRoomVolume(volume.gameObject, mesh.transform.position, mesh.transform.rotation,
                mesh.transform.lossyScale * 0.5f, volume.Module, mesh.sharedMesh);
    }
}
