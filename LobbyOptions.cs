using MenuLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The host's options under the lobby's player list. The host is always the killer, and only the host sees them:
///  - Killer: which killer to play (see KillerKind). The survivors are not shown it.
///  - Bots (0 to 5): how many bots, the stand-in survivors the host runs (see Bot.cs).
/// Both are kept out of the config file on purpose and reset when the game is restarted.
/// </summary>
internal static class LobbyOptions
{
    internal static int BotCount;

    private const float Scale = 0.55f;
    private const float RowHeight = 60f; // between the two sliders, before the scale
    private static GameObject? _container;
    private static Transform? _page;
    private static int _placedFor = -1;

    /// <summary>Called by MenuLib whenever the lobby page is built.</summary>
    internal static void Build(Transform page)
    {
        var lobby = MenuPageLobby.instance;
        if (lobby == null || lobby.playerListTransform == null)
            return;

        _page = page;
        _placedFor = -1;
        _container = new GameObject("Hunted Lobby Options", typeof(RectTransform));
        var rect = (RectTransform)_container.transform;
        rect.SetParent(page, false);
        rect.localScale = new Vector3(Scale, Scale, 1f);
        Place();

        MenuAPI.CreateREPOSlider("Killer", "", (int index) => KillerKinds.HostPick = (KillerKind)index, rect, KillerKinds.Names,
            KillerKinds.Name(KillerKinds.HostPick), new Vector2(0f, 0f));
        MenuAPI.CreateREPOSlider("Bots", "", (int count) => BotCount = count, rect, new Vector2(0f, -RowHeight), 0, 5, BotCount);
        _container.SetActive(SemiFunc.IsMasterClientOrSingleplayer());
    }

    // One row of the list is 32 units tall in the lobby; the options go just under the last row, wherever that is for this many players.
    private static void Place()
    {
        var lobby = MenuPageLobby.instance;
        if (_container == null || _page == null || lobby == null || lobby.playerListTransform == null)
            return;
        var players = Mathf.Max(1, PhotonNetwork.PlayerList.Length);
        _placedFor = players;
        var below = lobby.playerListTransform.TransformPoint(new Vector3(0f, -32f * players - 45f, 0f));
        var local = _page.InverseTransformPoint(below);
        ((RectTransform)_container.transform).localPosition = new Vector3(local.x, local.y, 0f);
    }

    internal static void Tick()
    {
        if (_container == null)
            return;
        if (PhotonNetwork.PlayerList.Length != _placedFor)
            Place();
        var host = SemiFunc.IsMasterClientOrSingleplayer();
        if (_container.activeSelf != host)
            _container.SetActive(host);
    }
}
