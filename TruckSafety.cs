using HarmonyLib;

namespace Hunted;

/// <summary>
/// A survivor inside a truck is safe: nothing hurts them, knocks them back or throws them into a tumble there (a tumble they ask for themselves still works).
/// Each check runs where the game decides it (the hurt and the tumble on the survivor's own computer, the hit on whichever computer runs it), and every computer
/// knows who is in a truck.
/// </summary>
[HarmonyPatch]
static class TruckSafety
{
    internal static bool Safe(PlayerAvatar? player)
        => KillerRole.ModeActive && player != null && !KillerRole.IsKiller(player) && player.RoomVolumeCheck != null && player.RoomVolumeCheck.inTruck;

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    private static bool Hurt_Prefix(PlayerHealth __instance) => !Safe(__instance.playerAvatar);

    // Every hit box (a weapon, a laser, an explosion, a monster): no damage, push or tumble for a survivor in a truck.
    [HarmonyPrefix, HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    private static bool PlayerHurt_Prefix(PlayerAvatar _player) => !Safe(_player);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleRequest))]
    private static bool TumbleRequest_Prefix(PlayerTumble __instance, bool _isTumbling, bool _playerInput)
        => !(_isTumbling && !_playerInput && Safe(__instance.playerAvatar));

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleSet))]
    private static bool TumbleSet_Prefix(PlayerTumble __instance, bool _isTumbling, bool _playerInput, bool _reset)
        => !(_isTumbling && !_playerInput && !_reset && Safe(__instance.playerAvatar));
}
