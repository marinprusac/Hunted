using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace Hunted;

/// <summary>
/// The main menu of this mod: no singleplayer, public game or tutorial (the mode needs a lobby of friends), and the two ways in are
/// "Host Hunted" (the private game button) and "Join Hunted" (the join friend button). The buttons are found by what they do (the
/// method their click calls), so the game's language does not matter; the button names are the fallback.
/// </summary>
[HarmonyPatch(typeof(MenuPageMain), "Start")]
static class MainMenuPatch
{
    [HarmonyPostfix]
    private static void Start_Postfix(MenuPageMain __instance)
    {
        foreach (var button in __instance.GetComponentsInChildren<Button>(true))
        {
            var (hide, label) = Classify(button);
            if (hide)
                button.gameObject.SetActive(false); // the button list is a layout group, so the rest closes the gap
            else if (label != null)
                Rename(button, label);
        }
    }

    private static (bool hide, string? label) Classify(Button button)
    {
        var events = button.onClick;
        for (var i = 0; i < events.GetPersistentEventCount(); i++)
        {
            switch (events.GetPersistentMethodName(i))
            {
                case "ButtonEventSinglePlayer":
                case "ButtonEventPlayRandom":
                case "ButtonEventTutorial":
                    return (true, null);
                case "ButtonEventHostGame":
                    return (false, "Host Hunted");
                case "ButtonEventJoinGame":
                    return (false, "Join Hunted");
            }
        }
        var name = button.gameObject.name.ToLowerInvariant();
        if (name.Contains("singleplayer") || name.Contains("public game") || name.Contains("tutorial"))
            return (true, null);
        if (name.Contains("private game"))
            return (false, "Host Hunted");
        if (name.Contains("join friend"))
            return (false, "Join Hunted");
        return (false, null);
    }

    private static void Rename(Button button, string text)
    {
        // The label would otherwise be put back to the game's own text whenever the language is applied.
        foreach (var localized in button.GetComponentsInChildren<LocalizationChangedEvent>(true))
            localized.localizedAsset = null;
        var menuButton = button.GetComponent<MenuButton>();
        if (menuButton == null || menuButton.buttonText == null)
            return;
        menuButton.buttonTextString = text;
        menuButton.buttonText.text = text;
        menuButton.ResizeToText();
        button.gameObject.name = "Menu Button - " + text;
    }
}

/// <summary>
/// The killer is not part of the map's mechanisms: the Museum's Red Light, Green Light robot neither wakes up for them nor
/// watches or grabs them. (The killer cannot be hurt by traps either, since they cannot be damaged at all.)
/// </summary>
[HarmonyPatch(typeof(MuseumMegaSemiBot))]
static class MapFeaturesIgnoreKiller
{
    [HarmonyPostfix, HarmonyPatch("PlayerInActivationBox")]
    private static void InActivationBox_Postfix(PlayerAvatar _player, ref bool __result)
    {
        if (__result && KillerRole.IsKiller(_player))
            __result = false;
    }

    [HarmonyPostfix, HarmonyPatch("PlayerInBotRoom")]
    private static void InBotRoom_Postfix(PlayerAvatar _player, ref bool __result)
    {
        if (__result && KillerRole.IsKiller(_player))
            __result = false;
    }
}
