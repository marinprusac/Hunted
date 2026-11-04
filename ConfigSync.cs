using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Only the host's settings count. The host puts every setting of its config into the room; every other player's game swaps its
/// own values for the host's while they are in that room (in memory only: their config file is never written with them) and
/// goes back to its own when they leave.
/// </summary>
internal static class ConfigSync
{
    private const string RoomKey = "Hunted.Config";

    private static ConfigFile _file = null!;
    private static readonly Dictionary<string, ConfigEntryBase> Entries = new();
    private static readonly Dictionary<string, string> Own = new();
    private static bool _overridden;
    private static float _timer;
    private static string? _lastApplied;

    internal static void Init(ConfigFile file)
    {
        _file = file;
        foreach (var definition in file.Keys.ToList())
        {
            var entry = file[definition];
            var key = definition.Section + "." + definition.Key;
            Entries[key] = entry;
            Own[key] = entry.GetSerializedValue();
        }
        // New settings were just added to the file; from now on changes made while playing are not written back.
        file.Save();
        file.SaveOnConfigSet = false;
    }

    internal static void Tick()
    {
        _timer -= Time.unscaledDeltaTime;
        if (_timer > 0f)
            return;
        _timer = 0.5f;

        if (!PhotonNetwork.InRoom || SemiFunc.IsMasterClientOrSingleplayer())
        {
            Restore();
            if (PhotonNetwork.InRoom)
                Publish();
            return;
        }

        var text = RoomSync.Get<string>(RoomKey);
        if (text == null)
        {
            Restore();
            return;
        }
        if (text == _lastApplied && _overridden)
            return;
        Apply(text);
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>Host: every setting, as "section.key=value" records.</summary>
    private static void Publish()
    {
        var text = new StringBuilder();
        foreach (var pair in Entries)
        {
            if (text.Length > 0)
                text.Append('|');
            text.Append(Escape(pair.Key)).Append('=').Append(Escape(pair.Value.GetSerializedValue()));
        }
        RoomSync.Set(RoomKey, text.ToString());
    }

    private static void Apply(string text)
    {
        var changed = 0;
        foreach (var record in text.Split('|'))
        {
            var split = record.IndexOf('=');
            if (split <= 0)
                continue;
            var key = Uri.UnescapeDataString(record.Substring(0, split));
            var value = Uri.UnescapeDataString(record.Substring(split + 1));
            if (Entries.TryGetValue(key, out var entry) && entry.GetSerializedValue() != value)
            {
                try
                {
                    entry.SetSerializedValue(value);
                    changed++;
                }
                catch (Exception e)
                {
                    Hunted.Logger.LogWarning($"Could not take the host's value for {key}: {e.Message}");
                }
            }
        }
        _overridden = true;
        _lastApplied = text;
        Hunted.Logger.LogInfo($"Using the host's settings ({changed} differ from this game's own).");
    }

    private static void Restore()
    {
        if (!_overridden)
            return;
        foreach (var pair in Own)
            if (Entries.TryGetValue(pair.Key, out var entry) && entry.GetSerializedValue() != pair.Value)
                entry.SetSerializedValue(pair.Value);
        _overridden = false;
        _lastApplied = null;
        Hunted.Logger.LogInfo("Back to this game's own settings.");
    }
}
