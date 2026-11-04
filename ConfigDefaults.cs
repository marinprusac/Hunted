using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Hunted;

/// <summary>
/// The default of every player-facing setting comes from Defaults.json, which is compiled into the DLL (rebuild after editing it).
/// A setting that HuntedConfig declares but the file does not have is an error: it is logged and the mod refuses to load, so a
/// missing default is noticed at once instead of silently getting some other value. The player's own values still live in the
/// config file and override these.
/// </summary>
internal static class ConfigDefaults
{
    private static JObject? _root;

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hunted.Defaults.json");
        if (stream == null)
            throw Fail("Defaults.json is not in the DLL.");
        using var reader = new StreamReader(stream);
        try
        {
            _root = JObject.Parse(reader.ReadToEnd());
        }
        catch (Exception e)
        {
            throw Fail($"Defaults.json cannot be read: {e.Message}");
        }
    }

    internal static T Get<T>(string section, string key)
    {
        var token = _root?[section]?[key];
        if (token == null || token.Type == JTokenType.Null)
            throw Fail($"Defaults.json has no default for the setting [{section}] {key}.");
        try
        {
            return token.ToObject<T>()!;
        }
        catch (Exception e)
        {
            throw Fail($"The default for [{section}] {key} in Defaults.json is not a valid {typeof(T).Name}: {e.Message}");
        }
    }

    private static InvalidOperationException Fail(string message)
    {
        Hunted.Logger.LogError(message);
        return new InvalidOperationException(message);
    }
}
