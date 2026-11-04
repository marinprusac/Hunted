using MenuLib;
using MenuLib.MonoBehaviors;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// A testing option, kept out of the config file on purpose: a small switch under the lobby's player list, shown only while you are
/// alone in the lobby, and it resets when the game is restarted.
///  - DummySurvivor: a fake crouching survivor to test the killer's vision on (see DummySurvivor.cs).
/// (Playing alone as a survivor needs no option: leave yourself unticked in the player list.)
/// </summary>
internal static class DebugToggles
{
    internal static bool DummySurvivor;

    private const float Scale = 0.55f;
    private static GameObject? _container;

    /// <summary>Called by MenuLib whenever the lobby page is built.</summary>
    internal static void Build(Transform page)
    {
        var lobby = MenuPageLobby.instance;
        if (lobby == null || lobby.playerListTransform == null)
            return;

        _container = new GameObject("Hunted Debug Options", typeof(RectTransform));
        var rect = (RectTransform)_container.transform;
        rect.SetParent(page, false);
        rect.localScale = new Vector3(Scale, Scale, 1f);

        // One row of the list is 32 units tall in the lobby; with a single player the options go just under it.
        var list = lobby.playerListTransform;
        var below = list.TransformPoint(new Vector3(0f, -32f - 45f, 0f));
        var local = page.InverseTransformPoint(below);
        rect.localPosition = new Vector3(local.x, local.y, 0f);

        MenuAPI.CreateREPOToggle("Dummy survivor", on => DummySurvivor = on, rect, new Vector2(0f, 0f), "ON", "OFF", DummySurvivor);
    }

    internal static void Tick()
    {
        if (_container == null)
            return;
        var alone = PhotonNetwork.PlayerList.Length <= 1;
        if (_container.activeSelf != alone)
            _container.SetActive(alone);
    }
}
