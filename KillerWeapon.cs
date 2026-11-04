using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Photon.Pun;
using TMPro;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The killer's weapon: a vanilla melee item that is spawned for them, can only be stowed and drawn (never dropped)
/// and hurts survivors. It runs on the game's own item energy (ItemBattery): hits drain it as usual, it also slowly
/// drains over time, and it recharges while the killer is in the killer room.
/// </summary>
internal static class KillerWeapon
{
    private const string WeaponKey = "Hunted.Weapon";

    internal static ItemMelee? Weapon { get; private set; }

    private static bool _spawning;
    private static bool _spawnedOnce;
    private static float _missingTimer;
    private static bool _batteryConfigured;
    private static float _batteryLogTimer;
    private static float _lastBatteryLife = 100f;
    private static float _weaponSeenAt = -1f;
    private static bool _stowedOnce;
    private static GameObject? _offlineWeapon;

    internal static void Reset()
    {
        Weapon = null;
        _spawning = false;
        _spawnedOnce = false;
        _missingTimer = 0f;
        _batteryConfigured = false;
        _weaponSeenAt = -1f;
        _stowedOnce = false;
        _offlineWeapon = null;
        IgnoredSolids.Clear();
    }

    internal static bool IsWeapon(ItemMelee? melee) => melee != null && Weapon != null && melee == Weapon;

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive)
            return;

        if (KillerRole.KillerSteamId == null)
            return;

        ResolveWeapon();
        // The host decides what counts as a swing (it reads this from the sword): a lower value needs less speed.
        if (Weapon != null)
            Weapon.swingDetectSpeedMultiplier = Tuning.Sword.SwingDetectMultiplier;
        if (SemiFunc.IsMasterClientOrSingleplayer())
            HostTick(dt);

        IgnoreOwnBody(dt);
        if (KillerRole.LocalIsKiller)
            KeepWeaponInHand();
        else
            KeepSurvivorsOff();
    }

    private static float _ignoreTimer;

    /// <summary>
    /// The weapon's solid body never collides with any player's body: not the killer's (with the larger body the weapon comes out
    /// of the inventory inside it and would knock the killer about) and not the survivors'. A survivor that stopped the sword at
    /// its surface also stopped the swing (the swing needs speed), so the hit box never got to hurt them. Now the sword passes
    /// through, and the hit box (which looks for players by overlap, not by collision) still hits. Walls and everything else
    /// collide as before (valuables and carts are handled below). New colliders show up now and then (a tumble body, a player's pusher), so this is repeated every second.
    /// </summary>
    private static void IgnoreOwnBody(float dt)
    {
        _ignoreTimer -= dt;
        if (_ignoreTimer > 0f || Weapon == null)
            return;
        _ignoreTimer = 1f;

        var weapon = Weapon.GetComponentsInChildren<Collider>(true);
        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null)
                continue;
            foreach (var b in BodyColliders(player))
                foreach (var a in weapon)
                    if (a != null && b != null && a != b)
                        Physics.IgnoreCollision(a, b, true);
        }

        // The same for valuables and carts: the sword goes through them, so a swing can neither knock them about nor break them.
        // (The hit box is already set to leave physics objects alone.) Valuables appear over time, so only new colliders are handled.
        if (_ignoredFor != Weapon)
        {
            _ignoredFor = Weapon; // a new sword has not ignored anything yet
            IgnoredSolids.Clear();
        }
        var solid = new List<Collider>();
        foreach (var valuable in Object.FindObjectsOfType<ValuableObject>())
            solid.AddRange(valuable.GetComponentsInChildren<Collider>(true));
        foreach (var cart in Object.FindObjectsOfType<PhysGrabCart>())
            solid.AddRange(cart.GetComponentsInChildren<Collider>(true));
        foreach (var b in solid)
        {
            if (b == null || !IgnoredSolids.Add(b))
                continue;
            foreach (var a in weapon)
                if (a != null && a != b)
                    Physics.IgnoreCollision(a, b, true);
        }
    }

    private static readonly HashSet<Collider> IgnoredSolids = new();
    private static ItemMelee? _ignoredFor;

    private static List<Collider> BodyColliders(PlayerAvatar player)
    {
        var body = new List<Collider>(player.GetComponentsInChildren<Collider>(true));
        if (player.tumble != null)
            body.AddRange(player.tumble.GetComponentsInChildren<Collider>(true));
        // The host's stand-in for a remote player, which pushes physics objects around.
        if (player.playerPhysPusher != null)
            body.AddRange(player.playerPhysPusher.GetComponentsInChildren<Collider>(true));
        if (player.isLocal && PlayerController.instance != null)
            body.AddRange(PlayerController.instance.GetComponentsInChildren<Collider>(true));
        return body;
    }

    /// <summary>
    /// The weapon is the killer's. Everyone else's grab ignores it (and it is not highlighted when they look at it); the game
    /// checks this timer on the weapon every time a player tries to grab, so it is kept topped up on each survivor's client.
    /// </summary>
    private static void KeepSurvivorsOff()
    {
        if (Weapon == null)
            return;
        var grabObject = Weapon.GetComponent<PhysGrabObject>();
        if (grabObject != null && grabObject.grabDisableTimer < 0.3f)
            grabObject.grabDisableTimer = 0.3f;
    }

    private static void ResolveWeapon()
    {
        if (Weapon != null)
            return;
        if (!SemiFunc.IsMultiplayer())
        {
            Weapon = _offlineWeapon != null ? _offlineWeapon.GetComponent<ItemMelee>() : null;
            return;
        }
        var id = RoomSync.Get<int>(WeaponKey);
        if (id == 0)
            return;
        var view = PhotonView.Find(id);
        if (view != null)
            Weapon = view.GetComponent<ItemMelee>();
    }

    private static PlayerAvatar? KillerAvatar() => SemiFunc.PlayerGetList().FirstOrDefault(p => KillerRole.IsKiller(p));

    // ---------------------------------------------------------------- host

    private static void HostTick(float dt)
    {
        var killer = KillerAvatar();
        if (killer == null || !killer.spawned || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        if (Weapon == null)
        {
            if (!_spawning && (!_spawnedOnce || (_missingTimer += dt) > 2f))
                Hunted.Instance.StartCoroutine(SpawnWeapon(killer));
            return;
        }
        _missingTimer = 0f;

        var battery = Weapon.GetComponent<ItemBattery>();
        if (battery == null)
            return;

        // Energy slowly drains over time, whether the weapon is in the hand, stowed or lying around, on top of the usual loss per hit.
        // The game's own drain is switched off: it only counts what is not in the inventory, so a weapon that is mid-swap or has
        // not been picked up yet would drain by accident and a stowed one would not drain at all.
        if (!_batteryConfigured)
        {
            _batteryConfigured = true;
            battery.autoDrain = false;
            battery.BatteryToggle(true);
        }
        // What a hit costs: the game reads this from the weapon, so it is set on it (the host decides what is lost). Only a hit on a
        // survivor costs anything: the sword never hurts anything else (see the patch below), and the game's general per-hit cost is 0.
        Weapon.durabilityDrain = 0f;
        Weapon.durabilityDrainOnEnemiesAndPVP = HuntedConfig.EnergyPerSurvivorHit.Value;
        var grabObject = Weapon.GetComponent<PhysGrabObject>();
        battery.DrainContinuous(HuntedConfig.EnergyDrainPerSecond.Value / 2f, dt); // the game drains rate * 2 per second

        // Diagnostics for the log: every few seconds, the energy with the state of the weapon, to show when energy is lost.
        _batteryLogTimer -= dt;
        if (_batteryLogTimer <= 0f)
        {
            _batteryLogTimer = 5f;
            var equippable = Weapon.GetComponent<ItemEquippable>();
            Hunted.Logger.LogDebug($"Weapon energy {battery.batteryLife:0.0}% (held={grabObject != null && grabObject.grabbed}, stowed={equippable != null && equippable.isEquipped}, " +
                                  $"drained {_lastBatteryLife - battery.batteryLife:+0.0;-0.0;0.0} since last line)");
            _lastBatteryLife = battery.batteryLife;
        }

        // The weapon recharges by itself while the killer is in the killer room.
        if (KillerRoom.Contains(killer.transform.position))
            battery.ReceiveCharge(HuntedConfig.ChargeRate.Value * dt);
    }

    private static IEnumerator SpawnWeapon(PlayerAvatar killer)
    {
        _spawning = true;
        var item = PickItem();
        if (item == null)
        {
            Hunted.Logger.LogError("No melee item found for the killer's weapon.");
            _spawning = false;
            _spawnedOnce = true;
            yield break;
        }

        yield return LevelGenerator.Instance.PrefabsPrepare(new[] { item.prefab });
        var position = killer.transform.position + killer.transform.forward * 1f + Vector3.up * 1.2f;
        var rotation = killer.transform.rotation;
        GameObject? spawned;
        if (SemiFunc.IsMultiplayer())
            spawned = PhotonNetwork.InstantiateRoomObject(item.prefab.ResourcePath, position, rotation, 0);
        else
            spawned = Object.Instantiate(item.prefab.Prefab, position, rotation);

        if (spawned == null)
        {
            Hunted.Logger.LogError($"Could not spawn {item.itemName}.");
        }
        else
        {
            Weapon = spawned.GetComponent<ItemMelee>();
            _offlineWeapon = spawned;
            _batteryConfigured = false;
            RoomSync.Set(WeaponKey, spawned.GetComponent<PhotonView>()?.ViewID ?? 0);
            Hunted.Logger.LogInfo($"Killer weapon spawned: {item.itemName}");
            var hurt = Weapon != null ? Weapon.hurtCollider : null;
            if (hurt != null)
                Hunted.Logger.LogDebug($"Weapon hit effect on players: tumble {hurt.playerTumbleTime:0.##} s, stun {hurt.playerStunTime:0.##} s, tumble force {hurt.playerTumbleForce:0.##}, " +
                                      $"torque {hurt.playerTumbleTorque:0.##}, push {hurt.playerHitForce:0.##}, cooldown {hurt.playerDamageCooldown:0.##} s");
            if (hurt != null)
            {
                var box = hurt.GetComponent<BoxCollider>();
                var sphere = hurt.GetComponent<SphereCollider>();
                var shape = box != null ? $"box size {box.size} centre {box.center}" : sphere != null ? $"sphere radius {sphere.radius:0.##}" : "no collider";
                Hunted.Logger.LogDebug($"Weapon hit box: {shape}, scale {hurt.transform.lossyScale}, wall ray check {hurt.playerRayCast}, spread {hurt.hitSpread:0}, " +
                                      $"swing detect x{Weapon!.swingDetectSpeedMultiplier:0.##}");
            }

            // The weapon starts in the inventory (slot 1), not in the killer's hand. Give it a moment to finish starting up.
            var equippable = spawned.GetComponent<ItemEquippable>();
            for (var attempt = 0; attempt < 3 && equippable != null && killer != null; attempt++)
            {
                yield return new WaitForSeconds(1.5f);
                if (equippable == null || equippable.isEquipped || equippable.isEquipping)
                    break;
                equippable.RequestEquip(0, killer.photonView.ViewID);
            }
        }
        _spawning = false;
        _spawnedOnce = true;
    }

    private static Item? PickItem()
    {
        var melee = new List<Item>();
        foreach (var item in StatsManager.instance.itemDictionary.Values)
        {
            if (item == null || item.prefab == null)
                continue;
            var prefab = item.prefab.Prefab;
            var component = prefab != null ? prefab.GetComponent<ItemMelee>() : null;
            if (component != null && component.usesBatteryLogic && prefab!.GetComponent<ItemBattery>() != null)
                melee.Add(item);
        }
        Hunted.Logger.LogDebug("Melee items: " + string.Join(", ", melee.Select(i => i.itemName)));

        // The killer's weapon is always the sword (the others are only a fallback in case the sword was renamed).
        foreach (var preferred in new[] { "sword", "baton", "hammer", "pan" })
        {
            var match = melee.FirstOrDefault(i => i.itemName.IndexOf(preferred, System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (match != null)
                return match;
        }
        return melee.FirstOrDefault();
    }

    /// <summary>The killer's client keeps the weapon in hand whenever it is not stowed.</summary>
    private static void KeepWeaponInHand()
    {
        if (Weapon == null || PhysGrabber.instance == null)
            return;
        var equippable = Weapon.GetComponent<ItemEquippable>();
        if (equippable == null)
            return;
        if (equippable.isEquipped || equippable.isEquipping)
            _stowedOnce = true;
        // Right after spawning, the host is putting the weapon into the inventory; do not grab it in the meantime.
        if (_weaponSeenAt < 0f)
            _weaponSeenAt = Time.time;
        if (!_stowedOnce && Time.time - _weaponSeenAt < 8f)
            return;
        if (equippable.isEquipped || equippable.isEquipping || equippable.isUnequipping)
            return;
        var grabObject = Weapon.GetComponent<PhysGrabObject>();
        if (grabObject != null && PhysGrabber.instance.grabbedPhysGrabObject != grabObject)
            PhysGrabber.instance.OverrideGrab(grabObject, 0.2f);
    }

    /// <summary>True while the weapon is in the local killer's hand (not stowed).</summary>
    internal static bool WeaponInHand()
    {
        if (Weapon == null)
            return false;
        var equippable = Weapon.GetComponent<ItemEquippable>();
        return equippable != null && !equippable.isEquipped && !equippable.isEquipping && !equippable.isUnequipping;
    }
}

[HarmonyPatch]
static class KillerWeaponPatches
{
    private static (float time, float force, float torque)? _baseTumble;

    // Outside the arena and shop, vanilla melee hurt colliders do 0 damage to players. The killer's weapon hurts
    // survivors, and ignores enemies and physics objects (so valuables are never damaged).
    [HarmonyPostfix, HarmonyPatch(typeof(ItemMelee), "StateSwinging")]
    private static void StateSwinging_Postfix(ItemMelee __instance)
    {
        if (!KillerWeapon.IsWeapon(__instance) || __instance.hurtCollider == null)
            return;
        var damage = HuntedConfig.WeaponDamage.Value * (KillerRoom.Contains(__instance.transform.position) ? Tuning.Killer.KillerRoomEmpowerment : 1f);
        __instance.hurtCollider.playerDamage = Mathf.RoundToInt(damage);
        __instance.hurtCollider.playerKill = false;
        // The sword's tumble (time, push and spin) is scaled from the values it came with.
        _baseTumble ??= (__instance.hurtCollider.playerTumbleTime, __instance.hurtCollider.playerTumbleForce, __instance.hurtCollider.playerTumbleTorque);
        var tumble = Tuning.Sword.TumbleMultiplier;
        __instance.hurtCollider.playerTumbleTime = _baseTumble.Value.time * tumble;
        __instance.hurtCollider.playerTumbleForce = _baseTumble.Value.force * tumble;
        __instance.hurtCollider.playerTumbleTorque = _baseTumble.Value.torque * tumble;
        __instance.hurtCollider.enemyLogic = false;
        __instance.hurtCollider.physLogic = false;
    }

    // The killer's own sword never hits the killer. The game only skips its own holder for a moment (a flag it resets after every skip),
    // and the killer's larger body is often inside the sword's hit box. A hit would still push the killer, make a hit sound and cost
    // the sword energy (the damage and the tumble are already refused for the killer).
    [HarmonyPrefix, HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    private static bool PlayerHurt_Prefix(HurtCollider __instance, PlayerAvatar _player)
    {
        var weapon = KillerWeapon.Weapon;
        if (weapon == null || _player == null || !KillerRole.IsKiller(_player))
            return true;
        return weapon.hurtCollider != __instance;
    }

    // In vanilla, hitting a player only costs durability in the arena and shop. Here a hit on a survivor costs energy
    // like a hit on an enemy does.
    [HarmonyPostfix, HarmonyPatch(typeof(ItemMelee), nameof(ItemMelee.EnemyOrPVPSwingHitRPC))]
    private static void EnemyOrPVPSwingHit_Postfix(ItemMelee __instance, bool _playerHit)
    {
        if (!_playerHit || !KillerWeapon.IsWeapon(__instance) || !__instance.usesBatteryLogic)
            return;
        if (SemiFunc.RunIsArena() || SemiFunc.RunIsShop())
            return; // vanilla already drained
        var battery = __instance.GetComponent<ItemBattery>();
        if (battery == null)
            return;
        battery.batteryLife -= __instance.durabilityDrainOnEnemiesAndPVP;
        battery.TriggerSmallBatteryDeduction();
    }

    // The killer cannot let go of the weapon; they can only stow it.
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), nameof(PhysGrabber.ReleaseObject))]
    private static bool ReleaseObject_Prefix(PhysGrabber __instance)
    {
        if (!KillerRole.IsKiller(__instance.playerAvatar) || !__instance.isLocal)
            return true;
        var weapon = KillerWeapon.Weapon;
        if (weapon == null || !KillerWeapon.WeaponInHand())
            return true;
        return __instance.grabbedPhysGrabObject != weapon.GetComponent<PhysGrabObject>();
    }

    // While armed the killer cannot pick anything else up (that would drop the weapon). Stow it to use doors and the like.
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), "StartGrabbingPhysObject")]
    private static bool StartGrabbing_Prefix(PhysGrabber __instance, PhysGrabObject _physGrabObject)
    {
        if (!KillerRole.IsKiller(__instance.playerAvatar) || !__instance.isLocal)
            return true;
        var weapon = KillerWeapon.Weapon;
        if (weapon == null || !KillerWeapon.WeaponInHand())
            return true;
        return _physGrabObject != null && _physGrabObject == weapon.GetComponent<PhysGrabObject>();
    }
}
