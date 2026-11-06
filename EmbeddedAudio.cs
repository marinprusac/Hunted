using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

namespace Hunted;

/// <summary>
/// Music built into the DLL. Unity only decodes Ogg from a file, so each track is written to the game's cache folder once and loaded from there.
/// Clips are kept once loaded.
/// </summary>
internal static class EmbeddedAudio
{
    private static readonly Dictionary<string, AudioClip> Clips = new();

    /// <summary>Loads the embedded Ogg file (its resource name, like "Hunted.MenuMusic.ogg") and hands the clip over, or nothing if it fails.</summary>
    internal static IEnumerator Load(string resource, Action<AudioClip> done)
    {
        if (Clips.TryGetValue(resource, out var cached) && cached != null)
        {
            done(cached);
            yield break;
        }
        string path;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream == null)
            {
                Hunted.Logger.LogWarning($"{resource} is not in the DLL.");
                yield break;
            }
            path = Path.Combine(Application.temporaryCachePath, resource);
            using (var file = File.Create(path))
                stream.CopyTo(file);
        }
        catch (Exception exception)
        {
            Hunted.Logger.LogWarning($"Could not unpack {resource}: {exception.Message}");
            yield break;
        }
        using var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
        yield return request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            Hunted.Logger.LogWarning($"Could not load {resource}: {request.error}");
            yield break;
        }
        var clip = DownloadHandlerAudioClip.GetContent(request);
        if (clip == null)
            yield break;
        clip.name = resource;
        Clips[resource] = clip;
        done(clip);
    }
}
