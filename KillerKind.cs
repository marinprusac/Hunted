using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>The killers there are to play.</summary>
internal enum KillerKind
{
    /// <summary>The skeleton: white bones with red armour, red eyes and a red glow.</summary>
    Skelespawn = 0,

    /// <summary>A druid of sorts in a mask and antlers, all purple, with a purple glow.</summary>
    Everseer = 1,
}

/// <summary>
/// Which killer this round has. The host (who is always the killer) picks it in the lobby, and nobody else sees the choice there: it only goes out to the
/// other players (in a room property) when the host starts the game, as everyone needs it then to dress the killer and play their music.
/// </summary>
internal static class KillerKinds
{
    private const string Key = "Hunted.KillerKind";

    internal static readonly string[] Names = { "Skelespawn", "Everseer" };

    /// <summary>The host's choice in the lobby. It resets when the game is restarted.</summary>
    internal static KillerKind HostPick;

    /// <summary>The killer of this round, on every computer.</summary>
    internal static KillerKind Current
    {
        get
        {
            if (SemiFunc.IsMasterClientOrSingleplayer())
                return HostPick;
            var value = RoomSync.Get<int>(Key);
            return System.Enum.IsDefined(typeof(KillerKind), value) ? (KillerKind)value : KillerKind.Skelespawn;
        }
    }

    internal static string Name(KillerKind kind) => Names[(int)kind];

    /// <summary>Host: tells everyone which killer it is. Called as the game starts, never earlier, so the survivors cannot find out in the lobby.</summary>
    internal static void HostPublish()
    {
        if (SemiFunc.IsMasterClientOrSingleplayer())
            RoomSync.Set(Key, (int)HostPick);
    }

    /// <summary>The killer's colour: of their eyes, the glow around them and the killer room.</summary>
    internal static Color Colour => Current == KillerKind.Everseer ? new Color(0.6f, 0.1f, 1f) : new Color(1f, 0f, 0f);

    /// <summary>A light's or a surface's colour, turned into the killer's colour with its brightness kept.</summary>
    internal static Color Tinted(Color color, float boost = 1f, float minimum = 0f)
    {
        var strength = Mathf.Max(Mathf.Max(color.r, color.g, color.b) * boost, minimum);
        var tint = Colour;
        return new Color(tint.r * strength, tint.g * strength, tint.b * strength, color.a);
    }
}

[HarmonyPatch(typeof(MenuPageLobby), nameof(MenuPageLobby.ButtonStart))]
static class KillerKindPublishPatch
{
    // Harmony runs this even when the start is refused (nobody to hunt); that only sets a value nothing in the lobby shows.
    [HarmonyPostfix]
    private static void ButtonStart_Postfix() => KillerKinds.HostPublish();
}
