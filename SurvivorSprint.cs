using System.Collections.Generic;
using UnityEngine;

namespace Hunted;

/// <summary>
/// How long and how fast survivors run: set on each player's own game once the game has filled in its own values (they arrive shortly after the level is generated). The killer gets it too,
/// but cannot sprint, and the bots read their running from the same values. Stamina upgrades still add to it.
/// </summary>
internal static class SurvivorSprint
{
    private static PlayerController? _applied;

    internal static void Tick()
    {
        var controller = PlayerController.instance;
        if (controller == null || controller == _applied || !KillerRole.ModeActive || controller.playerOriginalSprintSpeed <= 0f || controller.playerAvatarScript == null)
            return;
        _applied = controller;

        var upgrades = StatsManager.instance != null ? StatsManager.instance.playerUpgradeStamina.GetValueOrDefault(SemiFunc.PlayerGetSteamID(controller.playerAvatarScript), 0) : 0;
        var before = controller.EnergyStart;
        controller.EnergyStart = Tuning.Survivor.Stamina + upgrades * 10f;
        controller.EnergyCurrent = controller.EnergyStart;
        var speed = controller.playerOriginalSprintSpeed;
        controller.playerOriginalSprintSpeed = speed * Tuning.Survivor.SprintSpeedMultiplier;
        controller.SprintSpeed = controller.playerOriginalSprintSpeed;
        Hunted.Logger.LogInfo($"Stamina {before:0.#} -> {controller.EnergyStart:0.#}, sprint speed {speed:0.##} -> {controller.playerOriginalSprintSpeed:0.##} (drain {controller.EnergySprintDrain:0.##} per second, {controller.EnergyStart / Mathf.Max(0.01f, controller.EnergySprintDrain + controller.SprintSpeedUpgrades):0.#} s of sprinting).");
    }
}
