using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// A dead player's head normally only shows on the map once somebody has seen it. Here it shows for everyone as soon as it lies in
/// a room that is explored on their own map. Each client decides for itself, since everyone's map has its own explored rooms.
/// </summary>
[HarmonyPatch(typeof(PlayerDeathHead), "Update")]
static class DeathHeadMap
{
    [HarmonyPostfix]
    private static void Update_Postfix(PlayerDeathHead __instance)
    {
        if (!KillerRole.ModeActive || !__instance.triggered || __instance.mapCustom == null)
            return;
        var entity = __instance.mapCustom.mapCustomEntity;
        if (entity == null)
            return;
        var check = __instance.GetComponent<RoomVolumeCheck>();
        if (check == null || check.CurrentRooms.Count == 0)
            return;
        // A room without a map cover (the truck and extraction rooms) counts as explored.
        var room = check.CurrentRooms[0];
        if (room != null && (room.Explored || room.MapModule == null))
        {
            // Undoes the hide that PlayerDeathHead.Update just asked for.
            entity.mapCustomHideTimer = 0f;
        }
    }
}
