using HarmonyLib;
using UnityEngine;

namespace Hunted;

[HarmonyPatch]
static class KillerPatches
{
    // The killer cannot run or tumble on purpose, and cannot crouch when the setting is off.
    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.InputHold))]
    private static bool InputHold_Prefix(InputKey key, ref bool __result) => AllowInput(key, ref __result);

    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.InputDown))]
    private static bool InputDown_Prefix(InputKey key, ref bool __result) => AllowInput(key, ref __result);

    [HarmonyPrefix, HarmonyPatch(typeof(SemiFunc), nameof(SemiFunc.InputUp))]
    private static bool InputUp_Prefix(InputKey key, ref bool __result) => AllowInput(key, ref __result);

    private static bool AllowInput(InputKey key, ref bool result)
    {
        if (KillerRole.KillerSteamId == null || !KillerRole.LocalIsKiller)
            return true;
        // The killer cannot sprint or tumble; crouching is always allowed.
        if (key != InputKey.Sprint && key != InputKey.Tumble)
            return true;
        result = false;
        return false;
    }

    private static float _speedFactor = 1f;

    // Faster walking speed (faster still in the killer room; a survivor's walking pace while the sword is in their hands). The game only fills playerOriginalMoveSpeed once the level has generated,
    // so leave MoveSpeed alone until then, and undo our change after the tick so we never overwrite
    // the game's own speed bookkeeping.
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    private static void FixedUpdate_Prefix(PlayerController __instance, out float __state)
    {
        __state = -1f;
        if (!KillerRole.LocalIsKiller || __instance.playerOriginalMoveSpeed <= 0f)
            return;
        __state = __instance.MoveSpeed;
        // With the sword in their hands the killer walks at a survivor's pace (MoveSpeed is the survivor's walk); stowed, at their own faster walk.
        // The Everseer walks at 75% of a survivor's pace with its mask on.
        _speedFactor = (KillerWeapon.Held ? Tuning.Sword.HeldSpeedMultiplier : Everseer.MaskOn ? Tuning.Everseer.MaskSpeedMultiplier : HuntedConfig.KillerWalkSpeedMultiplier.Value)
            * (KillerRoom.Contains(__instance.transform.position) ? Tuning.Killer.KillerRoomEmpowerment : 1f);
        __instance.MoveSpeed = __state * _speedFactor;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PlayerController), "FixedUpdate")]
    private static void FixedUpdate_Postfix(PlayerController __instance, float __state)
    {
        if (__state < 0f)
            return;
        // Only restore if the game did not assign a new value during the tick.
        if (Mathf.Approximately(__instance.MoveSpeed, __state * _speedFactor))
            __instance.MoveSpeed = __state;
    }

    // Invincible.
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Hurt))]
    private static bool Hurt_Prefix(PlayerHealth __instance) => !KillerRole.IsKiller(__instance.playerAvatar);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.Death))]
    private static bool Death_Prefix(PlayerHealth __instance) => !KillerRole.IsKiller(__instance.playerAvatar);

    [HarmonyPrefix, HarmonyPatch(typeof(PlayerAvatar), nameof(PlayerAvatar.PlayerDeath))]
    private static bool PlayerDeath_Prefix(PlayerAvatar __instance) => !KillerRole.IsKiller(__instance);

    // Cannot be tumbled (host decides tumble state for everyone).
    [HarmonyPrefix, HarmonyPatch(typeof(PlayerTumble), nameof(PlayerTumble.TumbleSet))]
    private static bool TumbleSet_Prefix(PlayerTumble __instance, bool _isTumbling)
        => !(_isTumbling && KillerRole.IsKiller(__instance.playerAvatar));

    // The killer cannot interact with valuables, carts, enemies or survivors' heads (left click would otherwise grab them).
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), "StartGrabbingPhysObject")]
    private static bool StartGrabbing_Prefix(PhysGrabber __instance, PhysGrabObject _physGrabObject)
    {
        if (!KillerRole.IsKiller(__instance.playerAvatar) || _physGrabObject == null)
            return true;
        return !KillerGrab.Blocked(_physGrabObject);
    }

    // What the killer cannot grab does not look grabbable either. The game marks a valuable you look at (corner marks, a dot and the
    // value readout) and turns the crosshair into the "grabbable" one; neither happens for the killer.
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), "DiscoverLogic")]
    private static bool DiscoverLogic_Prefix(PhysGrabber __instance) => !KillerRole.IsKiller(__instance.playerAvatar);

    // The game remembers the last grabbable thing looked at and never clears it, so it is cleared first to know that what is there
    // afterwards was found by this very check.
    [HarmonyPrefix, HarmonyPatch(typeof(PhysGrabber), nameof(PhysGrabber.RayCheck))]
    private static void RayCheck_Prefix(PhysGrabber __instance)
    {
        if (__instance.isLocal && KillerRole.IsKiller(__instance.playerAvatar))
            __instance.currentlyLookingAtPhysGrabObject = null;
    }

    [HarmonyPostfix, HarmonyPatch(typeof(PhysGrabber), nameof(PhysGrabber.RayCheck))]
    private static void RayCheck_Postfix(PhysGrabber __instance)
    {
        if (!__instance.isLocal || !KillerRole.IsKiller(__instance.playerAvatar) || __instance.grabbed)
            return;
        var looked = __instance.currentlyLookingAtPhysGrabObject;
        if (looked == null || !KillerGrab.Blocked(looked) || Aim.instance == null)
            return;
        __instance.currentlyLookingAtPhysGrabObject = null;
        Aim.instance.SetState(Aim.State.Default);
    }
}

/// <summary>What the killer cannot pick up: valuables, carts, monsters and survivors' heads.</summary>
internal static class KillerGrab
{
    internal static bool Blocked(PhysGrabObject? grab)
        => grab != null
           && (grab.GetComponent<ValuableObject>() != null
               || grab.GetComponentInParent<PhysGrabCart>() != null
               || (grab.GetComponentInParent<EnemyRigidbody>() != null && !Banger.IsHeldBody(grab)) // except the bomber head in their hands
               || grab.GetComponentInParent<PlayerDeathHead>() != null);
}
