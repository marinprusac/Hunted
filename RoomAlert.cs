using UnityEngine;

namespace Hunted;

/// <summary>
/// The cue the killer hears when a survivor walks into the killer room: one deep, bell-like note, which the hum then carries on from. It comes from where the survivor
/// is, so the killer hears which way to go and how far. It is made here out of sine waves (nothing to download or license) and played through the game's own
/// sound system, so the game's volume setting applies.
/// </summary>
internal static class RoomAlert
{
    private static Sound? _sound;

    internal static void Play(Vector3 position)
    {
        if (AssetManager.instance == null)
            return;
        if (_sound == null)
        {
            _sound = new Sound
            {
                Sounds = new[] { Build() },
                Type = AudioManager.AudioType.Default,
                SpatialBlend = 1f, // 3D: it comes from the survivor
                Volume = 1f,
                VolumeRandom = 0f,
                PitchRandom = 0f,
            };
        }
        _sound.Play(position, 1f, 8f); // a wide falloff: it stays loud at a distance, the killer room is far from some places
    }

    private static AudioClip? _humClip;

    /// <summary>
    /// A quiet, steady hum that follows a revealed survivor for as long as they are revealed. It is a soft pulsing chord, softer than the
    /// chime but with a wide falloff, so it can be heard from far away and shows which way to go. It swells in as the bell dies away (after the bell, if
    /// <paramref name="afterBell"/>), and <see cref="StopHum"/> fades it out.
    /// </summary>
    internal static AudioSource? StartHum(Transform target, bool afterBell)
    {
        var manager = AudioManager.instance;
        if (manager == null || manager.AudioDefault == null || target == null)
            return null;
        var go = Object.Instantiate(manager.AudioDefault, target.position, Quaternion.identity, manager.SoundsParent);
        go.name = "Hunted reveal hum";
        var source = go.GetComponent<AudioSource>();
        if (source == null)
        {
            Object.Destroy(go);
            return null;
        }
        source.clip = _humClip ??= BuildHum();
        source.loop = true;
        source.volume = 0f;
        source.pitch = 1f;
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.minDistance *= 8f; // the same wide falloff as the chime
        source.maxDistance *= 8f;
        go.AddComponent<SoundFollowTransform>().Target = target;
        go.AddComponent<HumFade>().FadeIn(afterBell ? 0.35f : 0f);
        source.Play();
        return source;
    }

    /// <summary>The hum dies away over a second and a half, then is removed.</summary>
    internal static void StopHum(AudioSource? hum)
    {
        if (hum == null)
            return;
        var fade = hum.GetComponent<HumFade>();
        if (fade != null)
            fade.FadeOut();
        else
            Object.Destroy(hum.gameObject);
    }

    // Two seconds that start and end at the same point (every frequency is a whole number of cycles in them), so it loops without a click.
    private static AudioClip BuildHum()
    {
        const int rate = 44100;
        const float length = 2f;
        var samples = new float[(int)(rate * length)];
        var chord = new[] { (165f, 1f), (247.5f, 0.6f), (330f, 0.4f) }; // half the pitch it was (every frequency is still a whole number of cycles in the 2 seconds)
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (float)rate;
            var pulse = 0.55f + 0.45f * Mathf.Pow(Mathf.Sin(Mathf.PI * t / length * 2f), 2f); // swells twice per loop
            var value = 0f;
            foreach (var (frequency, gain) in chord)
                value += gain * Mathf.Sin(2f * Mathf.PI * frequency * t);
            samples[i] = value / 2f * pulse * 0.9f;
        }
        var clip = AudioClip.Create("Hunted reveal hum", samples.Length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static AudioClip Build()
    {
        const int rate = 44100;
        const float length = 1.5f;
        var samples = new float[(int)(rate * length)];
        // Each note is a handful of partials that fade away together; the inharmonic ones give it the sound of a struck bell.
        var partials = new[] { (1f, 1f), (2.01f, 0.7f), (2.76f, 0.5f), (4.07f, 0.35f), (5.4f, 0.2f) };
        // Not too deep: very low notes are hard to hear on most speakers and headphones, however loud they are made.
        var notes = new[] { (start: 0f, frequency: 165f) }; // the lower of the two notes it used to have; the hum (the same pitch) carries on from it
        foreach (var note in notes)
        {
            for (var i = (int)(note.start * rate); i < samples.Length; i++)
            {
                var t = i / (float)rate - note.start;
                var envelope = Mathf.Min(1f, t / 0.01f) * Mathf.Exp(-2.4f * t);
                var value = 0f;
                foreach (var (ratio, gain) in partials)
                    value += gain * Mathf.Sin(2f * Mathf.PI * note.frequency * ratio * t);
                samples[i] += envelope * value;
            }
        }
        var peak = 0.0001f;
        foreach (var sample in samples)
            peak = Mathf.Max(peak, Mathf.Abs(sample));
        for (var i = 0; i < samples.Length; i++)
        {
            var fade = Mathf.Clamp01((samples.Length - i) / (rate * 0.1f)); // no click at the end
            samples[i] = samples[i] / peak * 0.98f * fade;
        }
        var clip = AudioClip.Create("Hunted room alert", samples.Length, 1, rate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}

/// <summary>The hum's volume: it swells in after a short wait, and dies away when told to, then removes itself.</summary>
internal class HumFade : MonoBehaviour
{
    private const float InSeconds = 1f;
    private const float OutSeconds = 1.5f;
    private AudioSource? _source;
    private float _wait;
    private bool _out;

    internal void FadeIn(float wait) => _wait = wait;

    internal void FadeOut() => _out = true;

    private void Update()
    {
        _source ??= GetComponent<AudioSource>();
        if (_source == null)
            return;
        var full = Tuning.KillerRoom.RevealHumVolume;
        if (_out)
        {
            _source.volume = Mathf.MoveTowards(_source.volume, 0f, full * Time.deltaTime / OutSeconds);
            if (_source.volume <= 0f)
                Destroy(gameObject);
            return;
        }
        if (_wait > 0f)
        {
            _wait -= Time.deltaTime;
            return;
        }
        _source.volume = Mathf.MoveTowards(_source.volume, full, full * Time.deltaTime / InSeconds);
    }
}
