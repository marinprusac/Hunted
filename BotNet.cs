using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The host runs the bots; every other machine shows a copy of each, which only follows what the host sends a few times a second: where it is, its stance, what it is doing, its health.
/// The copies look and sound the same (footsteps come from their animations), and the killer's eyes and the reveal work on them like on the host's own.
/// </summary>
internal partial class Bot
{
    private const byte StateEvent = 81; // host -> others: the state of every living bot
    private const int FieldsPerBot = 7;

    private static float _sendAt;
    private static readonly RaiseEventOptions ToOthers = new() { Receivers = ReceiverGroup.Others };

    private bool _puppet;
    private Vector3 _netPosition;
    private Quaternion _netRotation = Quaternion.identity;
    private float _netAt;
    private bool _netMoving;
    private int _hits;
    private int _netHits;

    internal static void Init()
    {
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
    }

    private static void SendStates()
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom.PlayerCount < 2 || Time.time < _sendAt)
            return;
        _sendAt = Time.time + 0.1f;
        var data = new object[All.Count * FieldsPerBot];
        var i = 0;
        foreach (var bot in All)
        {
            var flags = (bot._moving ? 1 : 0) | (bot._sprinting ? 2 : 0) | (bot.Tumbling ? 4 : 0) | (bot._carrying != null || bot._carriedHead >= 0 ? 8 : 0) | (Time.time < bot._boostUntil ? 16 : 0);
            data[i++] = bot.Slot;
            data[i++] = bot.transform.position;
            data[i++] = bot.transform.rotation;
            data[i++] = ((int)bot.Stance) | (flags << 4) | (bot._hits << 16);
            data[i++] = Mathf.CeilToInt(bot.Health);
            data[i++] = ColourToInt(bot._colour);
            data[i++] = bot.InTruck;
        }
        PhotonNetwork.RaiseEvent(StateEvent, data, ToOthers, SendOptions.SendUnreliable);
    }

    private static int ColourToInt(Color colour)
        => (Mathf.RoundToInt(colour.r * 255f) << 16) | (Mathf.RoundToInt(colour.g * 255f) << 8) | Mathf.RoundToInt(colour.b * 255f);

    private static Color IntToColour(int rgb) => new(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);

    private static void OnEvent(EventData message)
    {
        if (message.Code != StateEvent || SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive
            || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated || message.CustomData is not object[] data)
            return;
        for (var i = 0; i + FieldsPerBot <= data.Length; i += FieldsPerBot)
        {
            if (data[i] is not int slot || data[i + 1] is not Vector3 position || data[i + 2] is not Quaternion rotation || data[i + 3] is not int packed
                || data[i + 4] is not int health || data[i + 5] is not int rgb || data[i + 6] is not bool inTruck)
                continue;
            var bot = All.FirstOrDefault(b => b != null && b.Slot == slot);
            if (bot == null)
            {
                Colours[slot] = IntToColour(rgb);
                var root = new GameObject($"Hunted Bot {slot + 1}");
                root.transform.SetPositionAndRotation(position, rotation);
                bot = root.AddComponent<Bot>();
                bot.Slot = slot;
                bot._puppet = true;
                bot.Build();
                All.Add(bot);
            }
            bot._netPosition = position;
            bot._netRotation = rotation;
            bot._netAt = Time.time;
            bot.Stance = (StanceKind)(packed & 15);
            var flags = (packed >> 4) & 255;
            bot._netMoving = (flags & 1) != 0;
            bot._sprinting = (flags & 2) != 0;
            bot._tumbleUntil = (flags & 4) != 0 ? Time.time + 1f : 0f;
            bot._netGrabbing = (flags & 8) != 0;
            bot._boostUntil = (flags & 16) != 0 ? Time.time + 1f : 0f;
            bot._netHits = packed >> 16;
            bot.Health = health;
            bot.InTruck = inTruck;
        }
    }

    private bool _netGrabbing;

    // A copy: it glides to where the host says it is, and shows what the host says happened.
    private void PuppetUpdate()
    {
        if ((transform.position - _netPosition).sqrMagnitude > 25f)
            transform.position = _netPosition;
        else
            transform.position = Vector3.Lerp(transform.position, _netPosition, 12f * Time.deltaTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, _netRotation, 12f * Time.deltaTime);
        if (_netHits != _hits)
        {
            _hits = _netHits;
            HitFeedback();
            KillerVision.RevealHurt(this);
        }
        _moving = _netMoving;
    }

    /// <summary>The copies whose bot has not been heard of for a while are gone (and one that has just died goes the moment its head appears).</summary>
    private static void TickPuppets()
    {
        All.RemoveAll(b => b == null);
        foreach (var bot in All.ToList())
            if (Time.time - bot._netAt > 2f)
                bot.Remove();
    }

    internal static void RemovePuppet(int slot)
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            return;
        All.FirstOrDefault(b => b != null && b.Slot == slot)?.Remove();
    }
}

/// <summary>Marks a death head as a bot's. The game's own head is built around a player; this one has none, so everything that needs a player is switched off and what is left is the real thing: the model, its physics, grabbing, the sync and the map dot.</summary>
internal class BotHeadTag : MonoBehaviour
{
    internal int Slot;
    internal Color Colour;
}

[HarmonyPatch(typeof(PlayerDeathHead))]
static class BotHeadPatches
{
    internal const string Marker = "Hunted.BotHead";
    internal static (int slot, Color colour)? OfflineSpawn;

    private static BotHeadTag? TagOf(PlayerDeathHead head)
    {
        var tag = head.GetComponent<BotHeadTag>();
        if (tag != null)
            return tag;
        var view = head.GetComponent<PhotonView>();
        if (view != null && view.InstantiationData is { Length: >= 6 } data && data[3] as string == Marker && data[4] is int slot && data[5] is int rgb)
        {
            tag = head.gameObject.AddComponent<BotHeadTag>();
            tag.Slot = slot;
            tag.Colour = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
        }
        return tag;
    }

    [HarmonyPrefix, HarmonyPatch("Start")]
    private static bool Start_Prefix(PlayerDeathHead __instance)
    {
        if (OfflineSpawn is { } offline)
        {
            var added = __instance.gameObject.AddComponent<BotHeadTag>();
            added.Slot = offline.slot;
            added.Colour = offline.colour;
            OfflineSpawn = null;
        }
        var tag = TagOf(__instance);
        if (tag == null)
            return true;

        __instance.physGrabObject = __instance.GetComponent<PhysGrabObject>();
        __instance.photonView = __instance.GetComponent<PhotonView>();
        __instance.roomVolumeCheck = __instance.GetComponent<RoomVolumeCheck>();
        foreach (var behaviour in __instance.GetComponentsInChildren<MonoBehaviour>(true))
        {
            var name = behaviour.GetType().Name;
            if (!(behaviour is PlayerDeathHead) && !name.Contains("Photon") && !name.Contains("PhysGrab") && !name.Contains("RoomVolume") && !name.Contains("MapCustom") && !name.Contains("BotHead"))
                behaviour.enabled = false;
        }
        if (__instance.meshParent != null)
            __instance.meshParent.SetActive(true);
        if (__instance.meshHeadParent != null)
            __instance.meshHeadParent.SetActive(true);
        foreach (var renderer in __instance.colorMeshRenderers ?? System.Array.Empty<MeshRenderer>())
        {
            if (renderer == null)
                continue;
            foreach (var material in renderer.materials)
            {
                if (material.HasProperty("_AlbedoColor"))
                    material.SetColor("_AlbedoColor", tag.Colour);
                if (material.HasProperty("_AlbedoTexture"))
                    material.SetTexture("_AlbedoTexture", Texture2D.whiteTexture);
                if (material.HasProperty("_Color"))
                    material.SetColor("_Color", tag.Colour);
            }
        }
        Bot.RemovePuppet(tag.Slot);
        Bot.PlayDeathSound(__instance.transform.position);
        return false;
    }

    private static bool NotBot(PlayerDeathHead head) => TagOf(head) == null;

    [HarmonyPrefix, HarmonyPatch("Update")]
    private static bool Update_Prefix(PlayerDeathHead __instance) => NotBot(__instance);

    [HarmonyPrefix, HarmonyPatch("LateUpdate")]
    private static bool LateUpdate_Prefix(PlayerDeathHead __instance) => NotBot(__instance);

    [HarmonyPrefix, HarmonyPatch("FixedUpdate")]
    private static bool FixedUpdate_Prefix(PlayerDeathHead __instance) => NotBot(__instance);

    [HarmonyPrefix, HarmonyPatch("OnDestroy")]
    private static bool OnDestroy_Prefix(PlayerDeathHead __instance) => NotBot(__instance);

    [HarmonyPrefix, HarmonyPatch("OnPhotonSerializeView")]
    private static bool Serialize_Prefix(PlayerDeathHead __instance) => NotBot(__instance);
}

/// <summary>
/// Anything that hurts players hurts a bot: every damage collider of the game (monsters' attacks, traps, explosions, the extraction tube, pits, the killer's sword), with its own damage, tumble and cooldown.
/// The host decides, for the bots it runs.
/// </summary>
[HarmonyPatch(typeof(HurtCollider), "Update")]
static class BotHazardPatch
{
    [HarmonyPostfix]
    private static void Update_Postfix(HurtCollider __instance)
    {
        if (Bot.All.Count > 0 && SemiFunc.IsMasterClientOrSingleplayer())
            Bot.HurtColliderCheck(__instance);
    }
}
