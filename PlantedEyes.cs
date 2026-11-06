using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The Everseer's planted eyes. Each is the look of the game's ceiling eye, and nothing else: it does not attack, makes no sound and has no effects. It sits on
/// the ceiling, a wall or the floor, wherever the wand put it, closed, and opens and watches a survivor while one is in its sight. A survivor it sees is revealed
/// to the killer (as in the killer room), for as long as it sees them and three seconds after.
///
/// The killer always sees every eye, through walls, as a purple shape. An eye gives no sign of its own that it has spotted someone: no sound, nothing on their screen; they can only see it open.
///
/// A survivor destroys an eye by holding the grab button on it for a moment, from any distance (the eye counts as within reach however far it is, as long as
/// nothing is in the way; nothing else does). Bots destroy the ones they notice the same way.
///
/// The killer's computer says where each eye goes and every computer makes its own; whoever destroys one says so too. Every computer works out on its own what
/// each eye sees (it only needs where the survivors are), so nothing else is sent.
/// </summary>
internal static class PlantedEyes
{
    private const byte PlantEvent = 81;   // killer -> everyone: an eye (id, position, surface normal)
    private const byte DestroyEvent = 82; // anyone -> everyone: an eye is gone (id)

    private static readonly Dictionary<int, PlantedEye> Eyes = new();
    private static int _nextId;
    private static GameObject? _visual;
    private static bool _visualSearched;
    private static float _sightTimer;
    private static bool _registered;

    internal static IEnumerable<PlantedEye> All => Eyes.Values.Where(e => e != null);

    internal static void Init()
    {
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
    }

    internal static void Reset()
    {
        foreach (var eye in Eyes.Values)
            if (eye != null)
            {
                KillerXray.Hide(eye);
                Object.Destroy(eye.gameObject);
            }
        Eyes.Clear();
        _nextId = 0;
    }

    internal static void Tick(float dt, bool everseer)
    {
        if (!everseer)
        {
            if (Eyes.Count > 0)
                Reset();
            return;
        }
        if (!_registered)
        {
            _registered = true;
            // A survivor an eye sees is revealed to the killer.
            SurvivorReveal.AddSource(subject => subject.Player != null && IsCurrentlyWatched(subject.Player));
        }
        _sightTimer -= dt;
        if (_sightTimer <= 0f)
        {
            _sightTimer = 0.15f;
            // Every computer works out what each eye sees: the killer's to reveal survivors, everyone's to show the eye open and watching. Nothing else tells a
            // survivor an eye has spotted them: no sound, no mark on their screen.
            Look();
            // The killer always sees their eyes, through walls, in purple (the same see-through shape as a revealed survivor's).
            if (KillerRole.LocalIsKiller)
                foreach (var eye in All)
                    KillerXray.Show(eye, eye.transform, KillerStaff.Purple);
        }
        var local = SemiFunc.PlayerAvatarLocal();
        Hold(dt, local != null && !KillerRole.IsKiller(local) && !local.isDisabled && !local.deadSet && SemiFunc.InputHold(InputKey.Grab));
    }

    private static bool IsCurrentlyWatched(PlayerAvatar player) => Everseer.IsCurrent && All.Any(e => e.Watching.Contains(player));

    // ---------------------------------------------------------------------------------------------- planting and destroying

    /// <summary>The killer plants an eye (on the killer's computer).</summary>
    internal static void Plant(Vector3 position, Vector3 normal)
    {
        var id = ++_nextId;
        Create(id, position, normal);
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(PlantEvent, new object[] { id, position, normal }, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
    }

    /// <summary>Destroys an eye, on every computer.</summary>
    internal static void Destroy(int id)
    {
        Remove(id);
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(DestroyEvent, new object[] { id }, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
    }

    private static void OnEvent(EventData data)
    {
        if (data.Code == PlantEvent && data.CustomData is object[] { Length: > 2 } plant && plant[0] is int id && plant[1] is Vector3 position && plant[2] is Vector3 normal)
            Create(id, position, normal);
        else if (data.Code == DestroyEvent && data.CustomData is object[] { Length: > 0 } gone && gone[0] is int goneId)
            Remove(goneId);
    }

    private static void Remove(int id)
    {
        if (Eyes.TryGetValue(id, out var eye))
        {
            if (eye != null)
            {
                KillerXray.Hide(eye);
                Object.Destroy(eye.gameObject);
            }
            Eyes.Remove(id);
        }
    }

    private static void Create(int id, Vector3 position, Vector3 normal)
    {
        if (Eyes.ContainsKey(id))
            return;
        var visual = Visual();
        if (visual == null)
            return;
        var copy = VisualClone.Make(visual, true);
        copy.name = "Hunted Planted Eye " + id;
        // As the game puts a ceiling eye on the ceiling: at the point, facing out of the surface.
        copy.transform.SetPositionAndRotation(position, Quaternion.LookRotation(normal));
        copy.transform.localScale = visual.transform.lossyScale * Tuning.Everseer.EyeScale;
        var beam = copy.transform.Find("Main Animation/Eye Offset/temp_beam");
        if (beam != null)
            Object.DestroyImmediate(beam.gameObject);
        copy.AddComponent<EyeAnimationSink>();
        var eye = copy.AddComponent<PlantedEye>();
        eye.Id = id;
        eye.Normal = normal.normalized;
        copy.SetActive(true);
        Eyes[id] = eye;
    }

    /// <summary>The part of the game's ceiling eye that is its look: its model and its animations.</summary>
    private static GameObject? Visual()
    {
        if (_visual != null || _visualSearched)
            return _visual;
        var director = EnemyDirector.instance;
        if (director == null)
            return null;
        _visualSearched = true;
        foreach (var list in new[] { director.enemiesDifficulty1, director.enemiesDifficulty2, director.enemiesDifficulty3 })
            foreach (var setup in list)
                if (setup != null && setup.spawnObjects != null)
                    foreach (var spawn in setup.spawnObjects)
                        if (spawn != null && spawn.ResourcePath != null && spawn.ResourcePath.EndsWith("/Enemy - Ceiling Eye"))
                        {
                            var prefab = spawn.Prefab;
                            var anim = prefab != null ? prefab.GetComponentInChildren<EnemyCeilingEyeAnim>(true) : null;
                            if (anim != null)
                                _visual = anim.gameObject;
                        }
        if (_visual == null)
            Hunted.Logger.LogError("The game's ceiling eye was not found, so the Everseer's wand plants nothing.");
        return _visual;
    }

    // ---------------------------------------------------------------------------------------------- what the eyes see

    private static void Look()
    {
        var range = Tuning.Everseer.EyeSightRange;
        var survivors = SemiFunc.PlayerGetList().Where(p => p != null && !KillerRole.IsKiller(p) && !p.isDisabled && !p.deadSet).ToList();
        foreach (var eye in All)
        {
            eye.Watching.Clear();
            var from = eye.transform.position + eye.Normal * 0.4f;
            PlayerAvatar? closest = null;
            var best = float.MaxValue;
            foreach (var survivor in survivors)
            {
                foreach (var point in Points(survivor))
                {
                    var offset = point - from;
                    var distance = offset.magnitude;
                    if (distance > range || distance < 0.01f || Vector3.Dot(eye.Normal, offset / distance) < 0.05f)
                        continue;
                    if (Physics.Linecast(from, point, Everseer.SightMask, QueryTriggerInteraction.Ignore))
                        continue;
                    eye.Watching.Add(survivor);
                    if (distance < best)
                    {
                        best = distance;
                        closest = survivor;
                    }
                    break;
                }
            }
            eye.Target = closest;
        }
    }

    // A survivor is seen if their head or the middle of their body is.
    private static IEnumerable<Vector3> Points(PlayerAvatar player)
    {
        if (player.PlayerVisionTarget != null && player.PlayerVisionTarget.VisionTransform != null)
            yield return player.PlayerVisionTarget.VisionTransform.position;
        yield return player.transform.position + Vector3.up * 0.5f;
    }

    // ---------------------------------------------------------------------------------------------- touching one

    /// <summary>The eye a ray from <paramref name="origin"/> meets first, if nothing else is in the way.</summary>
    internal static PlantedEye? Aimed(Vector3 origin, Vector3 direction, float maxDistance = 500f)
    {
        PlantedEye? found = null;
        var best = maxDistance;
        var radius = Tuning.Everseer.EyeTouchRadius;
        foreach (var eye in All)
        {
            var centre = eye.Centre;
            var along = Vector3.Dot(centre - origin, direction);
            if (along <= 0f || along > best)
                continue;
            var miss = (origin + direction * along - centre).magnitude;
            if (miss > radius)
                continue;
            var front = along - Mathf.Sqrt(Mathf.Max(0f, radius * radius - miss * miss));
            // Something solid in front of the eye blocks it; the surface it sits on is behind it and does not.
            if (Physics.Raycast(origin, direction, out var hit, front, Everseer.SightMask, QueryTriggerInteraction.Ignore) && hit.distance < front - 0.05f)
                continue;
            best = along;
            found = eye;
        }
        return found;
    }

    private static PlantedEye? _held;
    private static float _heldFor;
    private static Sound? _holdLoop;
    private static AudioSource? _holdSource;

    /// <summary>
    /// A survivor holding the grab button on an eye (from any distance) destroys it once they have held it long enough, as the truck's screen takes a hold to type:
    /// the eye shakes harder and the truck screen's charging sound rises the longer it is held. Letting go, or looking away, starts it over.
    /// </summary>
    private static void Hold(float dt, bool holding)
    {
        var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        var eye = holding && camera != null ? Aimed(camera.transform.position, camera.transform.forward) : null;
        if (eye != _held)
        {
            if (_held != null)
                _held.HoldProgress = 0f;
            _held = eye;
            _heldFor = 0f;
        }
        var seconds = Mathf.Max(0.1f, Tuning.Everseer.EyeHoldSeconds);
        if (_held != null)
        {
            _heldFor += dt;
            _held.HoldProgress = Mathf.Clamp01(_heldFor / seconds);
            if (_heldFor >= seconds)
            {
                Destroy(_held.Id);
                _held = null;
                _heldFor = 0f;
            }
        }
        HoldSound(_held != null, _heldFor / seconds);
    }

    // The truck screen's charging loop, rising in pitch as the hold goes on (only the survivor holding hears it).
    private static void HoldSound(bool playing, float progress)
    {
        if (_holdLoop == null)
        {
            if (!playing)
                return;
            var screen = TruckScreenText.instance != null ? TruckScreenText.instance : Object.FindObjectOfType<TruckScreenText>();
            var source = screen != null ? screen.chargeChatLoop : null;
            if (source == null || source.Sounds == null || source.Sounds.Length == 0)
                return;
            var host = new GameObject("Hunted Eye Hold");
            _holdSource = host.AddComponent<AudioSource>();
            _holdSource.playOnAwake = false;
            _holdSource.loop = true;
            _holdSource.spatialBlend = 0f;
            if (AudioManager.instance != null)
                _holdSource.outputAudioMixerGroup = AudioManager.instance.SoundMasterGroup;
            _holdLoop = new Sound { Source = _holdSource, Sounds = source.Sounds, Type = source.Type, Volume = source.Volume, VolumeRandom = 0f, Pitch = 1f, PitchRandom = 0f, SpatialBlend = 0f };
        }
        if (_holdSource == null)
        {
            _holdLoop = null;
            return;
        }
        _holdLoop.PlayLoop(playing, 0.9f, 0.9f, 1f + progress);
    }
}

/// <summary>One planted eye's looks, every frame after its animation: shut while it sees nobody, open and following the survivor it watches.</summary>
internal class PlantedEye : MonoBehaviour
{
    internal int Id;
    internal Vector3 Normal;
    internal readonly HashSet<PlayerAvatar> Watching = new();
    internal PlayerAvatar? Target;
    /// <summary>How far a survivor's hold on it has got (0 to 1), on their computer: it shakes harder as it goes.</summary>
    internal float HoldProgress;
    private Transform? _shake;

    private Animator? _animator;
    private Transform? _look;
    private Transform? _centre;
    private GameObject? _lidOpen;
    private GameObject? _lidClosed;
    private bool _started;
    private float _born;

    /// <summary>The middle of the eyeball.</summary>
    internal Vector3 Centre => _centre != null ? _centre.position : transform.position + Normal * 0.3f;

    private void Start()
    {
        _animator = GetComponent<Animator>();
        _look = transform.Find("Main Animation/Eye Offset/Eye Shake/Eye LookAt");
        _shake = transform.Find("Main Animation/Eye Offset/Eye Shake");
        _centre = transform.Find("Main Animation/Eye Offset/Eye Shake/Eye LookAt/Eye") ?? transform.Find("Main Animation/Center");
        _lidOpen = transform.Find("Main Animation/Eyelid Open")?.gameObject;
        _lidClosed = transform.Find("Main Animation/Eyelid Closed")?.gameObject;
        _born = Time.time;
        if (_animator != null)
        {
            _animator.applyRootMotion = false; // it stays where it was put
            _animator.keepAnimatorStateOnDisable = true;
            try
            {
                _animator.Play("Ceiling Eye Spawn", 0, 0f); // it grows out of the surface, as the game's does
            }
            catch
            {
                // no such state: it just appears
            }
        }
    }

    private void LateUpdate()
    {
        if (_shake != null)
            _shake.localPosition = HoldProgress > 0f ? Random.insideUnitSphere * (0.04f + 0.12f * HoldProgress) : Vector3.zero;
        // It opens and watches whoever it sees (and stares back at whoever is pulling at it).
        var watching = Target != null;
        var open = watching || HoldProgress > 0f;
        if (_animator != null)
        {
            _animator.SetBool("hasTarget", open);
            _animator.SetBool("despawn", false);
        }
        // Wait for the growing out to end before the lids are set (it animates them).
        if (!_started)
        {
            _started = _animator == null || Time.time - _born > 3f || !_animator.GetCurrentAnimatorStateInfo(0).IsName("Ceiling Eye Spawn");
            if (!_started)
                return;
        }
        if (_lidClosed != null && _lidClosed.activeSelf == open)
            _lidClosed.SetActive(!open);
        if (_lidOpen != null && _lidOpen.activeSelf != open)
            _lidOpen.SetActive(open);
        if (_look == null)
            return;
        if (watching)
        {
            var direction = SemiFunc.ClampDirection(Target.transform.position + Vector3.up * 0.8f - _look.position, transform.forward, 35f);
            _look.rotation = Quaternion.RotateTowards(_look.rotation, Quaternion.LookRotation(direction), 360f * Time.deltaTime);
        }
        else
            _look.localRotation = Quaternion.Slerp(_look.localRotation, Quaternion.identity, 5f * Time.deltaTime);
    }
}

/// <summary>Takes the events in the ceiling eye's animations (its sounds and effects, which a planted eye has none of), so they go nowhere.</summary>
internal class EyeAnimationSink : MonoBehaviour
{
    public void SfxBlink() { }
    public void SfxDespawn() { }
    public void SfxSpawn() { }
    public void SfxLaserBuildup() { }
    public void SfxLaserBeam() { }
    public void SfxStaringStart() { }
    public void SfxStaringLoop() { }
    public void TeleportParticlesStart() { }
    public void TeleportParticlesStop() { }
    public void Explosion() { }
    public void DeathEffect() { }
    public void AttackFinished() { }
    public void SetDespawn() { }
    public void SetSpawn() { }
    public void SetAttack() { }
}
