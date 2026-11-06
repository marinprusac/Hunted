using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Dead by Daylight's terror radius and chase music. Each computer works it out for its own player, from where the killer and the survivors are
/// and where the killer looks (both are known everywhere), so nothing is sent over the network.
///
/// Terror radius: a living survivor within Chase.TerrorRadius of the killer hears a heartbeat that gets faster and louder as the killer gets closer,
/// through walls. The killer never hears it.
///
/// Chase: the killer has a survivor in the open, close and in front of them. On the killer's computer that is any survivor they can see
/// (a hidden crouching survivor does not count); on a survivor's computer it is only themselves. Once that has lasted a moment the chase music
/// comes in with a shock sound, replaces the heartbeat and holds the game's own music back, and it dies away some seconds after the killer lost sight of them.
/// </summary>
internal static class ChaseMusic
{
    // Each killer has their own chase music.
    private static string Track => KillerKinds.Current == KillerKind.Everseer ? "Hunted.EverseerChaseMusic.ogg" : "Hunted.ChaseMusic.ogg";
    private const string Sting = "Hunted.ChaseSting.ogg";

    private static GameObject? _host;
    private static AudioSource? _music;
    private static AudioSource? _beat;
    private static AudioClip? _beatClip;
    private static AudioClip? _stingClip;
    private static AudioSource? _sting;
    private static bool _loading;
    private static string? _loadedTrack;

    private static bool _sees;
    private static float _seenFor;
    private static float _lostFor = 999f;
    private static bool _chasing;
    private static float _checkTimer;
    private static float _beatTimer;
    private static float _stoppedAt = -999f;
    private static float _stoppedTime;

    private static int _sightMask;
    private static int SightMask => _sightMask != 0 ? _sightMask : _sightMask = (int)SemiFunc.LayerMaskGetVisionObstruct() & ~LayerMask.GetMask("Player");

    internal static void Tick(float dt)
    {
        var values = Tuning.Chase;
        var local = SemiFunc.PlayerAvatarLocal();
        var alive = KillerRole.ModeActive && local != null && !local.isDisabled && !local.deadSet;
        var killer = alive ? SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p)) : null;
        if (killer == null || killer.isDisabled)
        {
            _chasing = false;
            _seenFor = 0f;
            _lostFor = 999f;
            Fade(0f, dt);
            return;
        }
        Setup();

        _checkTimer -= dt;
        if (_checkTimer <= 0f)
        {
            _checkTimer = 0.1f;
            _sees = local == killer ? KillerSeesSomeone(killer) : KillerSees(killer, local!);
        }
        if (_sees)
        {
            _seenFor += dt;
            _lostFor = 0f;
            if (_seenFor >= values.ChaseStartSeconds && !_chasing)
            {
                _chasing = true;
                // The shock as the killer spots them, as in Dead by Daylight: once per chase, right as the music comes in.
                if (_sting != null && _stingClip != null)
                    _sting.PlayOneShot(_stingClip, values.StingVolume);
            }
        }
        else
        {
            _seenFor = 0f;
            _lostFor += dt;
            if (_lostFor >= values.ChaseLoseSeconds)
                _chasing = false;
        }
        Fade(_chasing ? values.MusicVolume : 0f, dt);
        if (_music != null && _music.isPlaying && LevelMusic.instance != null)
            LevelMusic.instance.Interrupt(5f); // the game's own music makes way, as the other layers do in Dead by Daylight

        if (local != killer && HuntedConfig.Heartbeat.Value)
            Heartbeat(killer, local!, dt);
    }

    // --- Who sees whom --------------------------------------------------------------------------------------------------------------

    /// <summary>On the killer's computer: any living survivor (a bot too) in the open, close and in front of them, that they can actually see.</summary>
    private static bool KillerSeesSomeone(PlayerAvatar killer)
    {
        var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        if (camera == null)
            return false;
        foreach (var survivor in SemiFunc.PlayerGetList())
        {
            if (survivor == null || survivor == killer || survivor.isDisabled || survivor.deadSet || !KillerVision.IsSeen(survivor))
                continue;
            if (InSight(camera.transform.position, camera.transform.forward, Eye(survivor)))
                return true;
        }
        return false;
    }

    /// <summary>On a survivor's computer: whether the killer has this survivor in the open, close and in front of them.</summary>
    private static bool KillerSees(PlayerAvatar killer, PlayerAvatar survivor)
    {
        var eye = killer.PlayerVisionTarget != null ? killer.PlayerVisionTarget.VisionTransform : null;
        return eye != null && InSight(eye.position, eye.forward, Eye(survivor));
    }

    private static Vector3 Eye(PlayerAvatar player)
        => player.PlayerVisionTarget != null ? player.PlayerVisionTarget.VisionTransform.position : player.transform.position + Vector3.up * 1.5f;

    private static bool InSight(Vector3 from, Vector3 facing, Vector3 to)
    {
        var offset = to - from;
        var distance = offset.magnitude;
        if (distance > Tuning.Chase.ChaseRange || distance < 0.01f)
            return false;
        if (Vector3.Dot(facing.normalized, offset / distance) < Tuning.Chase.ChaseViewDot)
            return false;
        return !Physics.Linecast(from, to, SightMask, QueryTriggerInteraction.Ignore);
    }

    // --- Sound --------------------------------------------------------------------------------------------------------------------

    private static void Setup()
    {
        if (_host == null)
        {
            _host = new GameObject("Hunted Chase Music");
            Object.DontDestroyOnLoad(_host);
            _music = _host.AddComponent<AudioSource>();
            _music.loop = true;
            _music.playOnAwake = false;
            _music.spatialBlend = 0f;
            _music.volume = 0f;
            _beat = _host.AddComponent<AudioSource>();
            _beat.playOnAwake = false;
            _beat.spatialBlend = 0f;
            _beatClip = MakeHeartbeat();
            _sting = _host.AddComponent<AudioSource>();
            _sting.playOnAwake = false;
            _sting.spatialBlend = 0f;
            _host.AddComponent<ChaseMusicHost>();
        }
        if (AudioManager.instance != null)
        {
            if (_music!.outputAudioMixerGroup == null)
                _music.outputAudioMixerGroup = AudioManager.instance.MusicMasterGroup;
            if (_beat!.outputAudioMixerGroup == null)
                _beat.outputAudioMixerGroup = AudioManager.instance.SoundMasterGroup;
            if (_sting!.outputAudioMixerGroup == null)
                _sting.outputAudioMixerGroup = AudioManager.instance.SoundMasterGroup;
        }
        var runner = _host.GetComponent<ChaseMusicHost>();
        if (_stingClip == null && !_loading)
        {
            _loading = true;
            runner.StartCoroutine(EmbeddedAudio.Load(Sting, clip => _stingClip = clip));
        }
        // The track of this round's killer (changed only while it is not playing: between chases, as a new round's killer may be another one).
        var track = Track;
        if (_loadedTrack != track && !_music!.isPlaying)
        {
            _loadedTrack = track;
            _music.clip = null;
            _stoppedAt = -999f; // the other track starts from the top
            runner.StartCoroutine(EmbeddedAudio.Load(track, clip => { if (_music != null && _loadedTrack == track) _music.clip = clip; }));
        }
    }

    private static void Fade(float target, float dt)
    {
        if (_music == null || _music.clip == null)
            return;
        var values = Tuning.Chase;
        var seconds = target > _music.volume ? values.FadeInSeconds : values.FadeOutSeconds;
        var step = values.MusicVolume * dt / Mathf.Max(0.01f, seconds);
        if (target > 0f && !_music.isPlaying)
        {
            // A chase soon after the last one picks the music up where it stopped; a new one starts from the top (the track is cut to begin on the beat).
            _music.volume = 0f;
            _music.time = Time.time - _stoppedAt < values.ResumeWithinSeconds ? _stoppedTime : 0f;
            _music.Play();
        }
        _music.volume = Mathf.MoveTowards(_music.volume, target, step);
        if (target <= 0f && _music.volume <= 0f && _music.isPlaying)
        {
            _stoppedAt = Time.time;
            _stoppedTime = _music.time;
            _music.Stop();
        }
    }

    /// <summary>The terror radius: the closer the killer, the faster and louder the heartbeat. None while the chase music plays.</summary>
    private static void Heartbeat(PlayerAvatar killer, PlayerAvatar survivor, float dt)
    {
        _beatTimer -= dt;
        if (_beat == null || _beatClip == null || _beatTimer > 0f)
            return;
        var values = Tuning.Chase;
        var distance = Vector3.Distance(killer.transform.position, survivor.transform.position);
        if (distance > values.TerrorRadius || (_music != null && _music.volume > 0.3f * values.MusicVolume))
        {
            _beatTimer = 0.2f;
            return;
        }
        var closeness = 1f - distance / values.TerrorRadius;
        _beat.PlayOneShot(_beatClip, values.HeartbeatVolume * Mathf.Lerp(0.33f, 1f, closeness));
        _beatTimer = Mathf.Lerp(values.HeartbeatSlowSeconds, values.HeartbeatFastSeconds, closeness);
    }

    /// <summary>A heartbeat ("lub-dub"), made here rather than shipped: two short, low thumps.</summary>
    private static AudioClip MakeHeartbeat()
    {
        const int rate = 44100;
        var data = new float[(int)(rate * 0.45f)];
        Thump(data, rate, 0f, 1f);
        Thump(data, rate, 0.16f, 0.7f);
        var clip = AudioClip.Create("Hunted Heartbeat", data.Length, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    private static void Thump(float[] data, int rate, float at, float amplitude)
    {
        var start = (int)(at * rate);
        var length = (int)(0.18f * rate);
        var phase = 0f;
        for (var i = 0; i < length && start + i < data.Length; i++)
        {
            var t = (float)i / rate;
            var envelope = Mathf.Min(1f, t * 300f) * Mathf.Exp(-t * 26f);
            var frequency = Mathf.Lerp(70f, 45f, t / 0.18f); // the pitch drops through the beat, as a real one does
            phase += 2f * Mathf.PI * frequency / rate;
            // The low note and a quieter one an octave up, so small speakers still carry it.
            data[start + i] += amplitude * envelope * (Mathf.Sin(phase) + 0.35f * Mathf.Sin(2f * phase));
        }
    }
}

/// <summary>Runs the loading of the chase track; lives on the chase music's own object, which stays across levels.</summary>
internal class ChaseMusicHost : MonoBehaviour
{
}
