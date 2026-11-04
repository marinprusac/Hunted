using UnityEngine;

namespace Hunted;

/// <summary>The killer cannot be hurt and has no stamina to spend, so their health and energy readouts are hidden.</summary>
internal static class KillerHud
{
    private static bool _hidden;

    internal static void Tick()
    {
        var hide = KillerRole.ModeActive && KillerRole.LocalIsKiller;
        if (hide == _hidden)
        {
            // The HUD objects are recreated with each scene, so keep applying it while hidden.
            if (!hide)
                return;
        }
        Set(HealthUI.instance, !hide);
        Set(EnergyUI.instance, !hide);
        _hidden = hide;
    }

    private static void Set(Component? ui, bool visible)
    {
        if (ui != null && ui.gameObject.activeSelf != visible)
            ui.gameObject.SetActive(visible);
    }
}
