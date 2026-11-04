using ExitGames.Client.Photon;
using Photon.Pun;

namespace Hunted;

/// <summary>Tiny wrapper around Photon room properties, which is how the host shares state with everyone.</summary>
internal static class RoomSync
{
    internal static void Set(string key, object? value)
    {
        if (!PhotonNetwork.InRoom)
            return;
        if (PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out var current) && Equals(current, value))
            return;
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { key, value } });
    }

    internal static T? Get<T>(string key)
    {
        if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(key, out var value) && value is T typed)
            return typed;
        return default;
    }
}
