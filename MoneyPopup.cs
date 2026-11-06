using HarmonyLib;

namespace Hunted;

/// <summary>
/// When an extraction point is completed the game shows the run's money at the top of the screen ("$40K"), the amount just added under it ("+$15K") and the
/// cosmetic counter. Money means nothing in this mode, so they are never shown in a round.
/// </summary>
[HarmonyPatch(typeof(SemiUI), nameof(SemiUI.Show))]
static class MoneyPopupPatch
{
    [HarmonyPrefix]
    private static bool Show_Prefix(SemiUI __instance)
        => !(KillerRole.ModeActive && (__instance is CurrencyUI || __instance is ShopCostUI || __instance is CosmeticWorldObjectUI));
}
