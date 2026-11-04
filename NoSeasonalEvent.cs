using HarmonyLib;

namespace Hunted;

/// <summary>
/// The game's Halloween event (Oct 15 - Nov 15) changes extraction points, valuables, monsters and music. Hunted builds its own extraction rules, so the event is always
/// off. It is forced on every client (the host's event is sent to the others, and ignored here), so a lobby can never end up in a mixed state.
/// </summary>
[HarmonyPatch]
internal static class NoSeasonalEvent
{
    // The game may have set the event before the patches were applied.
    internal static void Tick()
    {
        var manager = GameManager.instance;
        if (manager != null && manager.currentGameEvent != GameManager.GameEvents.None)
        {
            manager.GameEventSet(GameManager.GameEvents.None);
            Hunted.Logger.LogInfo("Seasonal event turned off.");
        }
    }

    // The date check and the host's event both end up here.
    [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.GameEventSet))]
    private static void GameEventSet_Prefix(ref GameManager.GameEvents _event) => _event = GameManager.GameEvents.None;

    [HarmonyPrefix, HarmonyPatch(typeof(GameManager), nameof(GameManager.GameEventHostSet))]
    private static void GameEventHostSet_Prefix(ref GameManager.GameEvents _event) => _event = GameManager.GameEvents.None;

    // In case the event was already set before the patches were applied.
    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.GameEventIsHalloween))]
    private static bool IsHalloween_Prefix(ref bool __result)
    {
        __result = false;
        return false;
    }
}
