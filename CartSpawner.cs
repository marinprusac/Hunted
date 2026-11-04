using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Host only. Every extraction point out on the map gets a cart on its platform. The truck rooms get none: the cart the game
/// puts in front of the truck is removed by <see cref="CartSpawnerPatches"/>.
/// </summary>
internal static class CartSpawner
{
    private const string CartItemName = "Item Cart Medium";

    private static readonly HashSet<ExtractionPoint> Spawned = new();
    private static readonly HashSet<GameObject> Own = new();
    private static bool _running;
    private static float _openFor;

    internal static void Reset()
    {
        Spawned.Clear();
        Own.Clear();
        _running = false;
        _openFor = 0f;
    }

    internal static void Tick(float dt)
    {
        if (_running || !SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive || !ExtractionState.Active)
            return;

        // The platform (the floor of the deposit zone) only exists once a point has opened.
        var ready = ExtractionPoints.Get().Where(p => p != null && !p.inStartRoom && !Spawned.Contains(p) && p.currentState != ExtractionPoint.State.Idle).ToList();
        if (ready.Count == 0)
        {
            _openFor = 0f;
            return;
        }
        _openFor += dt;
        if (_openFor < 1.5f)
            return;

        _running = true;
        Hunted.Instance.StartCoroutine(SpawnCarts(ready));
    }

    private static IEnumerator SpawnCarts(List<ExtractionPoint> points)
    {
        var item = FindCartItem();
        if (item == null)
        {
            Hunted.Logger.LogError("No cart item found.");
            foreach (var point in points)
                Spawned.Add(point);
            _running = false;
            yield break;
        }

        yield return LevelGenerator.Instance.PrefabsPrepare(new[] { item.prefab });
        foreach (var point in points)
        {
            Spawned.Add(point);
            if (point == null)
                continue;
            var position = point.platform.position + Vector3.up * 0.8f;
            var rotation = point.transform.rotation;
            var cart = GameManager.instance.gameMode != 0
                ? PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, rotation, 0)
                : Object.Instantiate(item.prefab.Prefab, position, rotation);
            if (cart == null)
                Hunted.Logger.LogError($"Could not spawn a cart at {position}.");
            else
                Own.Add(cart);
            yield return null;
        }
        Hunted.Logger.LogInfo($"Spawned {points.Count} carts at extraction points.");
        _running = false;
    }

    private static Item? FindCartItem()
    {
        var items = StatsManager.instance.itemDictionary;
        if (items.TryGetValue(CartItemName, out var exact) && exact != null && exact.prefab != null)
            return exact;
        // Any item whose prefab is a cart, in case the item was renamed.
        return items.Values.FirstOrDefault(i => i != null && i.prefab != null && i.prefab.Prefab != null && i.prefab.Prefab.GetComponent<PhysGrabCart>() != null);
    }
}

[HarmonyPatch]
static class CartSpawnerPatches
{
    // The game's cart in front of the truck is an item the players own, placed on the truck's item spots when the level starts.
    // Leaving it out of that list keeps it out of the truck rooms; the items players bought are still placed.
    [HarmonyPatch(typeof(PunManager), "TruckPopulateItemVolumes", typeof(List<PunManager.ItemSpawn>))]
    [HarmonyPrefix]
    private static void TruckPopulateItemVolumes_Prefix()
    {
        if (!KillerRole.ModeActive || ItemManager.instance == null)
            return;
        var removed = ItemManager.instance.purchasedItems.RemoveAll(i => i != null && i.itemType == SemiFunc.itemType.cart);
        if (removed > 0)
            Hunted.Logger.LogInfo($"Left {removed} cart(s) out of the truck rooms.");
    }

    // Carts spawned in the middle of a round can receive their first state RPC before their own Start has run on a client.
    // The game then throws inside the RPC (cartMesh is still null). Keep the state, skip the colours: the next change sets them.
    [HarmonyPatch(typeof(PhysGrabCart), "StateSwitchRPC")]
    [HarmonyPrefix]
    private static bool StateSwitchRPC_Prefix(PhysGrabCart __instance, PhysGrabCart.State _state)
    {
        if (__instance.cartMesh != null)
            return true;
        __instance.currentState = _state;
        return false;
    }
}
