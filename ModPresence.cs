using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;

namespace Hunted;

/// <summary>
/// Every game that runs Hunted marks its player with the build it runs. The host logs who has it, and which build, whenever that
/// changes: a player without the mod (or with another build) misses everything that runs on their own game (the red light, the
/// boost...).
/// </summary>
internal static class ModPresence
{
    private const string PlayerKey = "Hunted.Build";

    private static readonly string Build = Hunted.Instance.Info.Metadata.Version + "+" + typeof(Hunted).Assembly.ManifestModule.ModuleVersionId.ToString().Substring(0, 8);
    private static string? _lastReport;

    internal static void Tick()
    {
        if (!PhotonNetwork.InRoom)
        {
            _lastReport = null;
            return;
        }

        if (!PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PlayerKey, out var current) || !Equals(current, Build))
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hashtable { { PlayerKey, Build } });

        if (!PhotonNetwork.IsMasterClient || PhotonNetwork.PlayerList.Length < 2)
            return;
        var report = string.Join(", ", PhotonNetwork.PlayerList.Select(p =>
            $"{p.NickName}: {(p.CustomProperties.TryGetValue(PlayerKey, out var build) ? build : "NO HUNTED")}"));
        if (report == _lastReport)
            return;
        _lastReport = report;
        var missing = PhotonNetwork.PlayerList.Any(p => !p.CustomProperties.ContainsKey(PlayerKey));
        var builds = PhotonNetwork.PlayerList.Select(p => p.CustomProperties.TryGetValue(PlayerKey, out var b) ? b as string : null).Distinct().Count();
        var text = $"Players in the room: {report}";
        if (missing)
            Hunted.Logger.LogWarning(text + " (somebody has no Hunted: they miss the red light, the boost and every other effect that runs on their own game)");
        else if (builds > 1)
            Hunted.Logger.LogWarning(text + " (not everybody runs the same build)");
        else
            Hunted.Logger.LogInfo(text);
    }
}
