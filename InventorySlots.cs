using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Only the inventory slots that are in use: every item in this mode is fixed to its slot, so the others are hidden and their keys do nothing.
/// They are made invisible rather than switched off: a slot that is switched off before it starts never joins the inventory, and then nothing can go in it
/// (the killer is not known yet when the slots start, so that would take the sword's slot too).
/// The killer has two (1: the sword, 2: the bomber heads) and everyone else has none, everywhere (with the mod installed there is only this mode).
/// The slots left are centred, as the game centres its three.
/// </summary>
internal static class InventorySlots
{
    private static InventoryUI? _ui;
    private static InventorySpot[]? _spots;
    private static TMPro.TMP_Text[]? _labels;
    private static float[]? _positions;
    private static int _applied = -1;

    /// <summary>How many slots this player has: the killer's two, a survivor's none.</summary>
    internal static int Allowed => KillerRole.LocalIsKiller ? 2 : 0;

    /// <summary>Keeps the game's own list of slots as it built it, so any number of them can be laid out again later.</summary>
    internal static void Remember(InventoryUI ui)
    {
        _ui = ui;
        _spots = ui.inventorySpots?.ToArray();
        _labels = ui.bindingLabels?.ToArray();
        _positions = ui.slotPositionsX?.ToArray();
        _applied = -1;
    }

    internal static void Tick()
    {
        var ui = InventoryUI.instance;
        if (ui == null || ui != _ui || _spots == null || _positions == null || _labels == null)
            return;
        var allowed = Mathf.Clamp(Allowed, 0, _spots.Length);
        foreach (var spot in _spots)
        {
            if (spot == null)
                continue;
            if (!spot.gameObject.activeSelf)
                spot.gameObject.SetActive(true); // switched off by an earlier version of this
            var group = spot.GetComponent<CanvasGroup>();
            if (group == null)
                group = spot.gameObject.AddComponent<CanvasGroup>();
            var alpha = spot.inventorySpotIndex < allowed ? 1f : 0f;
            if (group.alpha != alpha)
            {
                group.alpha = alpha;
                group.blocksRaycasts = alpha > 0f;
            }
        }
        if (allowed == _applied)
            return;
        _applied = allowed;
        // The game lays its slots out around the middle of the three; with fewer in its list, the ones left are spread around that same middle.
        ui.inventorySpots = _spots.Take(allowed).ToArray();
        ui.bindingLabels = _labels.Take(allowed).ToArray();
        ui.slotPositionsX = _positions.Take(allowed).ToArray();
        ui.previousBindings = new string[allowed]; // makes the game lay them out again
    }
}

/// <summary>
/// The killer's slots keep showing what is in them while it is in use: the game empties a slot when its item is taken out, but here the sword and the staff
/// stay in their slots' pictures, as the mask and the bomber heads do. Whatever is in use (in the hands, the mask on, a head held) is brighter and bigger than
/// the rest. Laid over the game's own drawing at the end of every frame.
/// </summary>
internal static class InventoryLook
{
    private static readonly Color Idle = new(0.6f, 0.6f, 0.6f, 1f);
    private const float ActiveScale = 1.3f;

    internal static void Tick()
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller || Inventory.instance == null)
            return;
        var everseer = KillerKinds.Current == KillerKind.Everseer;
        for (var index = 0; index < 2; index++)
        {
            var spot = Inventory.instance.GetSpotByIndex(index);
            if (spot == null || spot.inventoryIcon == null)
                continue;
            // The real items: the sword (Skelespawn, slot 1) and the staff (Everseer, slot 2). The others are drawn by their abilities.
            Component? item = everseer ? (index == 1 ? KillerStaff.Staff : null) : (index == 0 ? KillerWeapon.Weapon : null);
            bool active;
            if (item != null)
            {
                active = everseer ? KillerStaff.InHand : KillerWeapon.WeaponInHand();
                var icon = item.GetComponent<ItemAttributes>()?.icon;
                if (active && icon != null && spot.CurrentItem == null)
                {
                    // In the hands: the game has emptied the slot; its picture is put back.
                    if (spot.inventoryIcon.sprite != icon)
                        spot.inventoryIcon.sprite = icon;
                    spot.inventoryIcon.enabled = true;
                    if (spot.noItem != null)
                        spot.noItem.enabled = false;
                }
            }
            else
                active = everseer ? index == 0 && Everseer.MaskOn : index == 1 && Banger.Holding;
            spot.inventoryIcon.color = active ? Color.white : Idle;
            var scale = Vector3.one * (active ? ActiveScale : 1f);
            var rect = spot.inventoryIcon.rectTransform;
            rect.localScale = Vector3.Lerp(rect.localScale, scale, 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime));
        }
    }
}

[HarmonyPatch]
static class InventorySlotsPatch
{
    [HarmonyPostfix, HarmonyPatch(typeof(InventoryUI), "LayoutSetup")]
    private static void LayoutSetup_Postfix(InventoryUI __instance) => InventorySlots.Remember(__instance);

    // A hidden slot's key does nothing.
    [HarmonyPrefix, HarmonyPatch(typeof(InventorySpot), "HandleInput")]
    private static bool HandleInput_Prefix(InventorySpot __instance) => __instance.inventorySpotIndex < InventorySlots.Allowed;
}
