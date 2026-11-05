using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// How many bots a level has. The host's Bots slider is read when the level is set up and kept for that level (moving the slider during a level changes the next one),
/// and it counts as that many more survivors for the number of extraction points. Everything else counts them through the player list, where they are avatars like anyone.
/// </summary>
internal static class BotRoster
{
    private const string CountKey = "Hunted.Bots";

    private static int _hostCount = -1;

    /// <summary>True when this lobby will have a killer: more than one player, or the host ticked someone. Without a killer there are no bots.</summary>
    internal static bool KillerExpected
        => KillerRole.KillerSteamId != null || PhotonNetwork.PlayerList.Length >= 2 || KillerChoice.Actor != 0;

    /// <summary>Bots in this level.</summary>
    internal static int Count
    {
        get
        {
            if (!KillerExpected)
                return 0;
            if (SemiFunc.IsMasterClientOrSingleplayer())
                return _hostCount >= 0 ? _hostCount : DebugToggles.BotCount;
            return RoomSync.Get<int>(CountKey);
        }
    }

    /// <summary>Host: takes the slider's value for the level that is about to start. Called while not in a level, so the value is fixed from the moment the level is set up.</summary>
    internal static void HostNewLevel()
    {
        _hostCount = DebugToggles.BotCount;
        RoomSync.Set(CountKey, _hostCount);
    }
}

/// <summary>The game cannot start with nobody to hunt: a lobby with just the killer needs at least one bot.</summary>
[HarmonyPatch(typeof(MenuPageLobby), nameof(MenuPageLobby.ButtonStart))]
static class StartNeedsSurvivorsPatch
{
    [HarmonyPrefix]
    private static bool ButtonStart_Prefix()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer() || !PhotonNetwork.InRoom)
            return true;
        var alone = PhotonNetwork.PlayerList.Length <= 1;
        if (!alone || KillerChoice.Actor == 0 || DebugToggles.BotCount > 0)
            return true;

        MenuManager.instance.MenuEffectClick(MenuManager.MenuClickEffectType.Deny);
        MenuManager.instance.PagePopUp("Nobody to hunt", Color.red,
            "You are the killer and the only player: there would be no survivors.\n\nAdd at least one <b>bot</b> with the Bots slider under the player list, or untick yourself as the killer.", "OK", richText: true);
        return false;
    }
}
