using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MenuLib;
using MenuLib.MonoBehaviors;
using Photon.Pun;
using Photon.Realtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Hunted;

/// <summary>
/// Who the killer is: the host ticks a checkbox next to a player's name in the lobby's player list (everyone sees it, only the host
/// can change it). The choice is kept in a room property as the player's actor number. Nobody ticked, or the ticked player is gone:
/// the killer is picked at random.
/// </summary>
internal static class KillerChoice
{
    private const string Key = "Hunted.KillerPick";

    /// <summary>The ticked player's actor number, or 0 for nobody.</summary>
    internal static int Actor => RoomSync.Get<int>(Key);

    internal static void Toggle(int actor)
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            RoomSync.Set(Key, Actor == actor ? 0 : actor);
    }

    /// <summary>The avatar of the ticked player, if they are in the game.</summary>
    internal static PlayerAvatar? Chosen(IEnumerable<PlayerAvatar> players)
    {
        var actor = Actor;
        if (actor == 0)
            return null;
        return players.FirstOrDefault(p => p.photonView != null && p.photonView.Owner != null && p.photonView.Owner.ActorNumber == actor);
    }
}

/// <summary>
/// The checkboxes themselves: a small square at the right edge of each row of the lobby's player list (the name is right-aligned
/// next to the head at the right end of the row, so the box goes past the head), and a "KILLER" header above the column. The square
/// is a MenuLib button (so it hovers and clicks like the rest of the menu) with no text, sized to the square, and drawn on top.
/// </summary>
[HarmonyPatch]
internal static class KillerCheckboxes
{
    private sealed class Box
    {
        internal MenuPlayerListed Row = null!;
        internal int Actor;
        internal REPOButton Button = null!;
        internal Image Outer = null!;
        internal Image Fill = null!;
    }

    private static readonly List<Box> Boxes = new();
    private static readonly Color Red = new(0.95f, 0.2f, 0.15f);
    private static readonly Color Idle = new(0.55f, 0.55f, 0.55f);

    // Where the box goes, in the row's own coordinates (the row's origin is its lower-left corner; its panel is about 315 x 27 and
    // the head ends near x = 300).
    private static readonly Vector2 BoxPosition = new(326f, 17f);
    private const float BoxSize = 20f;

    /// <summary>Called by MenuLib whenever the lobby page is built: the header over the column of boxes.</summary>
    internal static void BuildHeader(Transform page)
    {
        var lobby = MenuPageLobby.instance;
        if (lobby == null || lobby.playerListTransform == null || lobby.chatPromptText == null)
            return;

        // A copy of the small prompt text at the bottom of the page, so it has the game's font and colour.
        var copy = Object.Instantiate(lobby.chatPromptText.gameObject, lobby.playerListTransform);
        copy.name = "Hunted Killer Header";
        foreach (var localized in copy.GetComponentsInChildren<LocalizationChangedEvent>(true))
            Object.Destroy(localized);
        var text = copy.GetComponent<TextMeshProUGUI>();
        text.text = "KILLER\n<size=70%>none = random</size>";
        text.alignment = TextAlignmentOptions.Center;
        text.enableAutoSizing = false;
        text.fontSize = 13f;
        text.color = new Color(0.8f, 0.8f, 0.8f);
        text.raycastTarget = false;
        var rect = text.rectTransform;
        rect.anchorMin = rect.anchorMax = Vector2.zero;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(110f, 32f);
        rect.localScale = Vector3.one;
        rect.localPosition = new Vector3(BoxPosition.x, BoxPosition.y + 36f, 0f);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(MenuPageLobby), "PlayerAdd")]
    private static void PlayerAdd_Postfix(MenuPageLobby __instance, Player player)
    {
        var row = __instance.menuPlayerListedList.LastOrDefault(r => r != null && r.playerPhoton == player);
        if (row == null)
            return;
        var actor = player.ActorNumber;
        var button = MenuAPI.CreateREPOButton("", () => KillerChoice.Toggle(actor), row.transform);
        button.overrideButtonSize = new Vector2(BoxSize, BoxSize);
        button.labelTMP.text = "";

        var box = new Box { Row = row, Actor = actor, Button = button };
        box.Outer = Square("Box", button.transform, BoxSize, Idle);
        Square("Hollow", button.transform, BoxSize - 4f, new Color(0.08f, 0.08f, 0.08f, 1f));
        box.Fill = Square("Mark", button.transform, BoxSize - 8f, Red);
        Boxes.Add(box);
    }

    private static Image Square(string name, Transform parent, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(size, size);
        rect.anchoredPosition = Vector2.zero;
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    internal static void Tick()
    {
        if (Boxes.Count == 0)
            return;
        var isHost = SemiFunc.IsMasterClientOrSingleplayer();
        var chosen = KillerChoice.Actor;
        for (var i = Boxes.Count - 1; i >= 0; i--)
        {
            var box = Boxes[i];
            if (box.Row == null || box.Button == null)
            {
                Boxes.RemoveAt(i); // the player left, or the lobby page closed
                continue;
            }

            var on = box.Actor == chosen;
            var menuButton = box.Button.menuButton;
            menuButton.disabled = !isHost;
            box.Fill.enabled = on;
            // Red when ticked, white under the cursor, grey otherwise (and dimmer for everyone but the host, who cannot change it).
            var colour = menuButton.hovering && isHost ? Color.white : on ? Red : Idle;
            if (!isHost)
                colour *= 0.8f;
            box.Outer.color = colour;

            var rect = box.Button.rectTransform;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.localPosition = new Vector3(BoxPosition.x, BoxPosition.y, 0f);
        }
    }
}
