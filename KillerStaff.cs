using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The Everseer's wand: the game's own Void staff, spawned for the Everseer as the sword is for Skelespawn, in inventory slot 2. It is cast as the game's staff is,
/// renamed the Staff of Seeing, quieter and calmer than the game's (no shaking screen, dimmer flashes), but it is always charged (the game's needs raising first, which the tall Everseer cannot do under a ceiling), coloured purple, and its shot does nothing of its own: where it meets a surface (a ceiling, a wall or the floor) an eye is
/// planted (see PlantedEyes). Its charges come back by themselves, like the bomber heads, faster in the killer room. It can only be stowed and drawn, never dropped,
/// and nobody else can grab it.
/// </summary>
internal static class KillerStaff
{
    private const string StaffKey = "Hunted.Staff";
    internal static readonly Color Purple = new(0.65f, 0.15f, 1f);

    internal static ItemStaffVoid? Staff { get; private set; }

    private static bool _spawning;
    private static bool _spawnedOnce;
    private static float _missingTimer;
    private static GameObject? _offline;
    private static ItemStaffVoid? _coloured;
    private static float _seenAt = -1f;
    private static bool _stowedOnce;

    internal static void Reset()
    {
        Staff = null;
        _spawning = false;
        _spawnedOnce = false;
        _missingTimer = 0f;
        _offline = null;
        _coloured = null;
        _seenAt = -1f;
        _stowedOnce = false;
    }

    internal static bool IsStaff(GameObject? obj) => obj != null && Staff != null && obj == Staff.gameObject;

    /// <summary>The wand is in the local killer's hand (not stowed).</summary>
    internal static bool InHand
    {
        get
        {
            var equippable = Staff != null ? Staff.GetComponent<ItemEquippable>() : null;
            return equippable != null && !equippable.isEquipped && !equippable.isEquipping && !equippable.isUnequipping;
        }
    }

    internal static void Tick(float dt)
    {
        if (!Everseer.IsCurrent)
            return;
        Resolve();
        if (Staff != null && _coloured != Staff)
        {
            _coloured = Staff;
            Colour(Staff);
        }
        if (Staff != null)
            Rename(Staff); // the game may set its name again after it starts
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostTick(dt);
        if (KillerRole.LocalIsKiller)
            KeepInHand();
        else if (Staff != null && Staff.GetComponent<PhysGrabObject>() is { } grab && grab.grabDisableTimer < 0.3f)
            grab.grabDisableTimer = 0.3f; // nobody else can take it
    }

    /// <summary>Puts the wand away into its slot, if it is in the hand.</summary>
    internal static void Stow()
    {
        var equippable = Staff != null ? Staff.GetComponent<ItemEquippable>() : null;
        if (equippable != null && InHand && PhysGrabber.instance != null)
            equippable.RequestEquip(1, PhysGrabber.instance.photonView.ViewID);
    }

    private static void Resolve()
    {
        if (Staff != null)
            return;
        if (!SemiFunc.IsMultiplayer())
        {
            Staff = _offline != null ? _offline.GetComponent<ItemStaffVoid>() : null;
            return;
        }
        var id = RoomSync.Get<int>(StaffKey);
        var view = id != 0 ? PhotonView.Find(id) : null;
        if (view != null)
            Staff = view.GetComponent<ItemStaffVoid>();
    }

    /// <summary>Purple instead of the game's blue, on every computer: its crystal, its light and its sparks. Its charges are the wand's.</summary>
    private static void Colour(ItemStaffVoid staff)
    {
        staff.loadedColor = Purple;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Purple, 0.6f), new GradientColorKey(new Color(0.9f, 0.7f, 1f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        staff.emissionGradient = gradient;
        foreach (var system in staff.GetComponentsInChildren<ParticleSystem>(true))
        {
            var main = system.main;
            main.startColor = Purple;
        }
        foreach (var light in staff.GetComponentsInChildren<Light>(true))
            light.color = Purple;
        var battery = staff.GetComponent<ItemBattery>();
        if (battery != null)
            battery.batteryBars = Tuning.Everseer.WandMaxUses;
        // Quieter than the game's: its hum, choir, thunder and cast.
        foreach (var sound in new[] { staff.chargeUpSound, staff.lightning01Sound, staff.castSound, staff.choir01Sound, staff.choir02Sound, staff.chargeUpSoundLoop,
                     staff.loadedLowSoundLoop, staff.loadedHighSoundLoop, staff.denySound, staff.denySoundUI })
            if (sound != null)
                sound.Volume *= Tuning.Everseer.WandVolume;
        Rename(staff);
        KillerBody.AddItemSounds(staff); // as deep and loud as the killer
    }

    internal const string Name = "Staff of Seeing";

    /// <summary>Its name, wherever the game shows it (when it is looked at, in the inventory).</summary>
    internal static void Rename(ItemStaffVoid staff)
    {
        var attributes = staff.GetComponent<ItemAttributes>();
        if (attributes == null)
            return;
        if (attributes.cachedItemName != Name)
            attributes.cachedItemName = Name;
        if (attributes.itemName != Name)
            attributes.itemName = Name;
    }

    private static PlayerAvatar? Killer() => SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p));

    private static void HostTick(float dt)
    {
        var killer = Killer();
        if (killer == null || !killer.spawned || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;
        if (Staff == null)
        {
            if (!_spawning && (!_spawnedOnce || (_missingTimer += dt) > 2f))
                Hunted.Instance.StartCoroutine(Spawn(killer));
            return;
        }
        _missingTimer = 0f;
        var battery = Staff.GetComponent<ItemBattery>();
        if (battery == null)
            return;
        // A charge comes back every so often (faster in the killer room), and nothing else drains it.
        battery.autoDrain = false;
        var values = Tuning.Everseer;
        var rate = 100f / Mathf.Max(0.1f, values.WandSecondsPerUse) / Mathf.Max(1, values.WandMaxUses)
                   * (KillerRoom.Contains(killer.transform.position) ? values.WandRoomRechargeMultiplier : 1f);
        if (battery.batteryLife < 100f)
            battery.ReceiveCharge(rate * dt);
    }

    private static IEnumerator Spawn(PlayerAvatar killer)
    {
        _spawning = true;
        var item = StatsManager.instance.itemDictionary.Values.FirstOrDefault(i => i != null && i.prefab != null && i.prefab.Prefab != null && i.prefab.Prefab.GetComponent<ItemStaffVoid>() != null);
        if (item == null)
        {
            Hunted.Logger.LogError("The game's Void staff was not found, so the Everseer has no wand.");
            _spawning = false;
            _spawnedOnce = true;
            yield break;
        }
        yield return LevelGenerator.Instance.PrefabsPrepare(new[] { item.prefab });
        var position = killer.transform.position + killer.transform.forward + Vector3.up * 1.2f;
        var spawned = SemiFunc.IsMultiplayer()
            ? PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, killer.transform.rotation, 0)
            : Object.Instantiate(item.prefab.Prefab, position, killer.transform.rotation);
        if (spawned != null)
        {
            Staff = spawned.GetComponent<ItemStaffVoid>();
            _offline = spawned;
            RoomSync.Set(StaffKey, spawned.GetComponent<PhotonView>()?.ViewID ?? 0);
            Hunted.Logger.LogInfo($"The Everseer's wand spawned: {item.itemName}");
            // It starts in slot 2, not in the hand.
            var equippable = spawned.GetComponent<ItemEquippable>();
            for (var attempt = 0; attempt < 3 && equippable != null && killer != null; attempt++)
            {
                yield return new WaitForSeconds(1.5f);
                if (equippable == null || equippable.isEquipped || equippable.isEquipping)
                    break;
                equippable.RequestEquip(1, killer.photonView.ViewID);
            }
        }
        _spawning = false;
        _spawnedOnce = true;
    }

    /// <summary>The killer's computer keeps the wand in hand whenever it is not stowed (it cannot be dropped).</summary>
    private static void KeepInHand()
    {
        if (Staff == null || PhysGrabber.instance == null)
            return;
        var equippable = Staff.GetComponent<ItemEquippable>();
        if (equippable == null)
            return;
        if (equippable.isEquipped || equippable.isEquipping)
            _stowedOnce = true;
        if (_seenAt < 0f)
            _seenAt = Time.time;
        if (!_stowedOnce && Time.time - _seenAt < 8f)
            return; // the host is still putting it into its slot
        if (!InHand)
            return;
        var grab = Staff.GetComponent<PhysGrabObject>();
        if (grab != null && PhysGrabber.instance.grabbedPhysGrabObject != grab)
            PhysGrabber.instance.OverrideGrab(grab, 0.2f);
    }

    // ---------------------------------------------------------------------------------------------- its shot

    private static readonly HashSet<SlowProjectile> Shots = new();

    /// <summary>A shot from the wand, the first time it is seen on this computer: purple, harmless, and nothing of the game's own happens where it lands.</summary>
    internal static bool IsShot(SlowProjectile shot)
    {
        if (shot.spawner == null || !IsStaff(shot.spawner))
            return false;
        if (Shots.Add(shot))
        {
            shot.useExplosion = false;
            shot.useAreaOfEffect = false;
            shot.prefabToInstantiateOnHit = new PrefabRef();
            if (shot.hurtCollider != null)
                shot.hurtCollider.gameObject.SetActive(false);
            foreach (var system in shot.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = system.main;
                main.startColor = Purple;
            }
            foreach (var light in shot.GetComponentsInChildren<Light>(true))
                light.color = Purple;
            Shots.RemoveWhere(s => s == null);
        }
        return true;
    }

    /// <summary>The host: the shot has met a surface (a ceiling, a wall or the floor; it flies through people): an eye is planted there and the shot is gone.</summary>
    internal static void ShotMoved(SlowProjectile shot)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer() || shot.destroyed)
            return;
        var from = shot.lastPosition;
        var to = shot.transform.position;
        var step = to - from;
        if (step.sqrMagnitude < 0.0001f)
            return;
        if (Physics.Raycast(from, step.normalized, out var hit, step.magnitude + 0.2f, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
        {
            PlantedEyes.Plant(hit.point, hit.normal);
            if (SemiFunc.IsMultiplayer())
                shot.photonView.RPC("DestroyRPC", RpcTarget.All, hit.point, true);
            else
                shot.DestroyRPC(hit.point, true);
            return;
        }
        shot.lastPosition = to;
    }
}

[HarmonyPatch]
static class KillerStaffPatches
{
    // The wand needs no raising: the game charges its staff while it is held up (looking up past a point), so to the wand it always seems held up, and it
    // charges as soon as it is in the hand. Right after a cast it seems lowered for a moment, which is what lets the game charge it again.
    [HarmonyPostfix, HarmonyPatch(typeof(ItemStaffVoid), "SetCameraPitch")]
    private static void SetCameraPitch_Postfix(ItemStaffVoid __instance)
    {
        if (!KillerStaff.IsStaff(__instance.gameObject))
            return;
        __instance.cameraPitch = __instance.currentState == ItemStaffVoid.State.Lowering ? 0f : -90f;
        __instance.lookUpProgress = __instance.currentState == ItemStaffVoid.State.Lowering ? 0f : 1f;
    }

    // Calmer than the game's staff: no shaking of the screen as it charges and casts its spell, and its flashes of light are dimmer.
    [HarmonyPrefix, HarmonyPatch(typeof(ItemStaffVoid), "UpdateChargeShake")]
    private static bool UpdateChargeShake_Prefix(ItemStaffVoid __instance) => !KillerStaff.IsStaff(__instance.gameObject);

    [HarmonyPrefix, HarmonyPatch(typeof(ItemStaffVoid), "ChargeImpact")]
    private static bool ChargeImpact_Prefix(ItemStaffVoid __instance) => !KillerStaff.IsStaff(__instance.gameObject);

    [HarmonyPrefix, HarmonyPatch(typeof(ItemStaffVoid), nameof(ItemStaffVoid.OverrideLightAndCrystal), typeof(Color), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float))]
    private static void OverrideLight_Prefix(ItemStaffVoid __instance, ref Color _color, ref float _intensity)
    {
        if (!KillerStaff.IsStaff(__instance.gameObject))
            return;
        _intensity *= Tuning.Everseer.WandFlash;
        if (_color == Color.white)
            _color = new Color(0.85f, 0.7f, 1f); // its flashes are pale purple, not white
    }

    // Nor the arrow on the screen that says to raise it.
    [HarmonyPostfix, HarmonyPatch(typeof(ItemStaffVoid), "Update")]
    private static void StaffUpdate_Postfix(ItemStaffVoid __instance)
    {
        if (KillerStaff.IsStaff(__instance.gameObject) && __instance.screenArrow != null && __instance.screenArrow.gameObject.activeSelf)
            __instance.screenArrow.gameObject.SetActive(false);
    }

    // The wand's shot: harmless and purple, and the host plants an eye where it meets a surface instead of the game's own hit.
    [HarmonyPrefix, HarmonyPatch(typeof(SlowProjectile), "CollisionCheck")]
    private static bool CollisionCheck_Prefix(SlowProjectile __instance)
    {
        if (!KillerStaff.IsShot(__instance))
            return true;
        KillerStaff.ShotMoved(__instance);
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(SlowProjectile), nameof(SlowProjectile.OnHurtPlayer))]
    private static bool OnHurtPlayer_Prefix(SlowProjectile __instance) => !KillerStaff.IsShot(__instance);

    [HarmonyPrefix, HarmonyPatch(typeof(SlowProjectile), nameof(SlowProjectile.OnHurtOther))]
    private static bool OnHurtOther_Prefix(SlowProjectile __instance) => !KillerStaff.IsShot(__instance);

    // The killer cannot let go of the wand; they can only stow it.
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), nameof(PhysGrabber.ReleaseObject))]
    private static bool ReleaseObject_Prefix(PhysGrabber __instance)
    {
        if (!__instance.isLocal || !KillerRole.IsKiller(__instance.playerAvatar) || KillerStaff.Staff == null || !KillerStaff.InHand)
            return true;
        return __instance.grabbedPhysGrabObject != KillerStaff.Staff.GetComponent<PhysGrabObject>();
    }

    // With the wand in hand the killer cannot pick anything else up (that would drop it).
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), "StartGrabbingPhysObject")]
    private static bool StartGrabbing_Prefix(PhysGrabber __instance, PhysGrabObject _physGrabObject)
    {
        if (!__instance.isLocal || !KillerRole.IsKiller(__instance.playerAvatar) || KillerStaff.Staff == null || !KillerStaff.InHand)
            return true;
        return _physGrabObject != null && _physGrabObject == KillerStaff.Staff.GetComponent<PhysGrabObject>();
    }
}
