using System.Linq;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The Everseer's abilities. Slot 2 is its wand, a real item (see KillerStaff). Slot 1 is its mask, which is not an item: the slot is drawn and its key handled here.
///
/// The mask: one shot of the eye laser. It only opens with a shot ready (a full bar in its slot). As it opens the laser starts building up (the cart laser's
/// build-up sound, the screen and the Everseer's eyes growing more and more purple); once it is ready (a click, a strong purple glow) the interact key (E) fires
/// it at once: two purple beams from the Everseer's eyes for a moment, knocking back, tumbling and hurting any survivor in them. With the mask on the Everseer
/// walks at 75% of a survivor's speed and glows purple for everyone to see. When the shot is over, or the charge has drained below a shot, the mask closes
/// by itself. The charge is like the sword's: every shot costs a bar, whether it hits or not, it drains slowly by itself, and it recharges in the killer room.
///
/// Everyone sees the mask and the laser: the killer's computer says when the mask opens and closes and when the laser builds up, fires and stops; every computer
/// draws the beams itself, from the killer's eyes along where the killer looks (both are known everywhere). A survivor is hurt by the beams on their own computer,
/// as by the sword.
/// </summary>
internal static class Everseer
{
    private const byte LaserEvent = 80; // killer -> everyone: the laser's state (Off, Boot, Fire, Ready)
    private const int Off = 0, Boot = 1, Fire = 2, Ready = 3;
    private const byte MaskEvent = 83;  // killer -> everyone: the mask is on (bool)
    private static readonly Color Purple = new(0.65f, 0.15f, 1f);

    internal static bool IsCurrent => KillerRole.ModeActive && KillerRole.KillerSteamId != null && KillerKinds.Current == KillerKind.Everseer;

    // The killer's side.
    private static bool _ready;
    private static float _energy = 100f;
    private static float _cooldown;
    /// <summary>The mask is on: on the killer's own computer, and as the killer said on everyone else's.</summary>
    internal static bool MaskOn { get; private set; }

    // Every computer: the laser.
    private static int _laser;
    private static float _laserTimer;
    private static float _laserSince;
    // Two beams that are seen (one from each eye), and a third that is not, which carries the hurt all the way through walls.
    private const int Reach = 2;
    private static SemiLaser?[] _beams = new SemiLaser?[3];
    private static LineRenderer?[] _cores = new LineRenderer?[2];
    private static Material? _coreMaterial;
    private static ItemGunLaser? _gun;
    private static ItemCartLaser? _cart;
    private static bool _searched;
    private static Sound? _buildupLoop;
    private static AudioSource? _buildupSource;
    private static float _pulseAt;

    internal static void Init()
    {
        PhotonNetwork.NetworkingClient.EventReceived += OnEvent;
        PlantedEyes.Init();
    }

    internal static void Reset()
    {
        _ready = false;
        MaskOn = false;
        _laser = 0;
        _laserTimer = 0f;
        foreach (var beam in _beams)
            if (beam != null)
                Object.Destroy(beam.gameObject);
        _beams = new SemiLaser?[3];
        foreach (var core in _cores)
            if (core != null)
                Object.Destroy(core.gameObject);
        _cores = new LineRenderer?[2];
        KillerStaff.Reset();
        PlantedEyes.Reset();
    }

    private static float ShotCost => 100f / Mathf.Max(1, HuntedConfig.LaserShots.Value);
    private static int ShotsReady => Mathf.FloorToInt(_energy / ShotCost + 0.0001f);

    internal static void Tick(float dt)
    {
        var current = IsCurrent;
        if (current && KillerRole.LocalIsKiller)
            LocalTick(dt);
        else if (!current)
        {
            MaskOn = false;
            _laser = 0;
        }
        if (current)
        {
            KillerStaff.Tick(dt);
            Show(dt);
        }
        PlantedEyes.Tick(dt, current);
    }

    // ---------------------------------------------------------------------------------------------- the killer's side

    private static void LocalTick(float dt)
    {
        if (!_ready)
        {
            _ready = true;
            _energy = 100f;
        }
        var player = PlayerController.instance;
        if (player == null)
            return;
        var inRoom = KillerRoom.Contains(player.transform.position);
        _energy = Mathf.Clamp(_energy - HuntedConfig.EnergyDrainPerSecond.Value * dt + (inRoom ? HuntedConfig.ChargeRate.Value * dt : 0f), 0f, 100f);
        _cooldown -= dt;

        // The laser builds up as soon as the mask is on, waits until it is fired, fires, and stops; then the mask closes.
        if (!MaskOn)
        {
            if (_laser != Off)
                SetLaser(Off);
            return;
        }
        if (KillerStaff.InHand)
        {
            SetMask(false); // the wand was taken out
            return;
        }
        _laserTimer -= dt;
        switch (_laser)
        {
            case Boot when _laserTimer <= 0f:
                SetLaser(Ready);
                Inventory.instance?.GetSpotByIndex(0)?.Ping(10f, 20f, 0.2f);
                break;
            case Ready when SemiFunc.InputDown(InputKey.Interact):
                _energy -= ShotCost;
                SetLaser(Fire);
                break;
            case Fire when _laserTimer <= 0f:
                SetMask(false);
                return;
        }
        if (_laser != Fire && ShotsReady < 1)
            SetMask(false); // the charge drained below a shot before it was fired
    }

    /// <summary>A slot key was pressed. Returns true when the game should handle the press itself.</summary>
    internal static bool OnSlotPressed(int index)
    {
        if (!IsCurrent || !KillerRole.LocalIsKiller)
            return true;
        if (index != 0)
        {
            if (index == 1 && MaskOn && _laser != Fire)
                SetMask(false); // taking the wand out closes the mask
            return true; // the wand is a real item: the game handles its slot
        }
        if (_cooldown > 0f)
            return false;
        _cooldown = 0.25f;
        if (MaskOn)
        {
            if (_laser != Fire)
                SetMask(false);
            return false;
        }
        if (ShotsReady < 1)
        {
            Inventory.instance?.GetSpotByIndex(0)?.Ping(10f, 20f, 0.2f); // not enough charge for a shot: it does not open
            return false;
        }
        KillerStaff.Stow();
        SetMask(true);
        return false;
    }

    private static void SetMask(bool on)
    {
        if (MaskOn == on)
            return;
        MaskOn = on;
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(MaskEvent, new object[] { on }, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
        SetLaser(on ? Boot : Off); // it starts building up at once
    }

    internal static void DrawSlot(InventorySpot spot)
    {
        if (!IsCurrent || !KillerRole.LocalIsKiller || spot.CurrentItem != null || spot.inventorySpotIndex != 0)
            return;
        var player = PlayerController.instance;
        var filling = player != null && KillerRoom.Contains(player.transform.position) && _energy < 100f;
        // One bar a shot: a bar that shows is a shot that can be fired.
        PseudoSlot.Draw(spot, PseudoSlot.Icon("Hunted.MaskIcon.png"), Mathf.Max(1, HuntedConfig.LaserShots.Value), ShotsReady, filling, MaskOn);
    }

    private static void SetLaser(int state)
    {
        var values = Tuning.Everseer;
        if (_laser == state)
            return;
        _laserTimer = state == Boot ? HuntedConfig.LaserChargeSeconds.Value : state == Fire ? values.LaserShotSeconds : 0f;
        ApplyLaser(state);
        if (SemiFunc.IsMultiplayer())
            PhotonNetwork.RaiseEvent(LaserEvent, new object[] { state }, new RaiseEventOptions { Receivers = ReceiverGroup.Others }, SendOptions.SendReliable);
    }

    private static void OnEvent(EventData data)
    {
        if (data.Code == LaserEvent && data.CustomData is object[] { Length: > 0 } values && values[0] is int state)
            ApplyLaser(state);
        else if (data.Code == MaskEvent && data.CustomData is object[] { Length: > 0 } mask && mask[0] is bool on)
            MaskOn = on;
    }

    // ---------------------------------------------------------------------------------------------- every computer

    private static PlayerAvatar? Killer() => SemiFunc.PlayerGetList().FirstOrDefault(p => p != null && KillerRole.IsKiller(p));

    private static void ApplyLaser(int state)
    {
        var previous = _laser;
        _laser = state;
        _laserSince = Time.time;
        var killer = Killer();
        Find();
        // The game's sounds the laser borrows are the killer's while it does (nothing else uses these items in this mode). Again each time: every level starts
        // the list afresh.
        KillerBody.AddSound(_cart?.soundBuildupStart);
        KillerBody.AddSound(_gun?.soundReload2);
        KillerBody.AddSound(_gun?.soundInitialCrack);
        KillerBody.AddSound(_buildupLoop);
        if (killer == null)
            return;
        var at = EyePosition(killer, 0);
        // The game's own sounds: the cart laser's build-up, the laser gun's click as it is ready, and its crack as it fires.
        if (state == Boot && previous != Boot)
            _cart?.soundBuildupStart?.Play(at);
        if (state == Ready && previous != Ready)
            _gun?.soundReload2?.Play(at);
        if (state == Fire && previous != Fire)
            _gun?.soundInitialCrack?.Play(at);
    }

    /// <summary>The game's cart laser (its beam and build-up sound) and laser gun (its click and crack), which the Everseer's laser is made of.</summary>
    private static void Find()
    {
        if (_searched || StatsManager.instance == null)
            return;
        _searched = true;
        foreach (var item in StatsManager.instance.itemDictionary.Values)
        {
            var prefab = item != null && item.prefab != null ? item.prefab.Prefab : null;
            if (prefab == null)
                continue;
            if (_gun == null && prefab.GetComponent<ItemGunLaser>() is { semiLaser: not null } gun)
                _gun = gun;
            if (_cart == null && prefab.GetComponentInChildren<ItemCartLaser>(true) is { } cart)
                _cart = cart;
        }
        if (_gun == null && _cart == null)
            Hunted.Logger.LogError("Neither of the game's lasers was found, so the Everseer has no laser.");
        else if (_cart == null)
            Hunted.Logger.LogWarning("The game's cart laser was not found, so the Everseer's laser looks like the laser gun's and builds up silently.");
    }

    /// <summary>The mask and the laser as everyone sees and hears them.</summary>
    private static void Show(float dt)
    {
        var killer = Killer();
        if (killer == null || killer.isDisabled)
            return;
        Find();
        var boot = Mathf.Clamp01((Time.time - _laserSince) / Mathf.Max(0.1f, HuntedConfig.LaserChargeSeconds.Value));
        // How strong the purple is: it grows while the laser builds up and throbs once it is ready.
        var strength = !MaskOn ? 0f : _laser == Boot ? Mathf.Lerp(0.4f, 0.8f, boot) : _laser == Ready ? 0.85f + 0.15f * Mathf.Sin(Time.time * 12f) : 0.6f;
        // The killer's own screen turns purple while the mask is on.
        if (MaskOn && killer.isLocal)
        {
            ScreenTint.Request(new Color(0.55f, 0.1f, 1f), 0.35f * strength, 0.85f * strength);
            // Once it is ready, the key that fires it, where the game shows a held item's name and key (as it does for the staff).
            if (_laser == Ready && ItemInfoUI.instance != null)
                ItemInfoUI.instance.ItemInfoText(null!, "Eye Laser <color=#FFFFFF>[" + SemiFunc.InputDisplayReplaceTags("[interact]") + "]</color>");
        }
        // Everyone else sees the Everseer glow purple, pulsing (the game's own flash on a player's body, as a boosted survivor glows gold), faster as the laser builds
        // up and fastest when it is ready, and its eyes blaze brighter.
        if (MaskOn && !killer.isLocal && Time.time >= _pulseAt && killer.playerHealth != null)
        {
            _pulseAt = Time.time + (_laser == Ready ? 0.15f : _laser == Boot ? Mathf.Lerp(0.5f, 0.2f, boot) : 0.4f);
            killer.playerHealth.SetMaterialSpecial(Purple);
        }
        EyeLights(killer, strength);
        BuildupSound(killer);
        if (_laser == Fire)
            Beams(killer);
        foreach (var core in _cores)
            if (core != null && core.enabled != (_laser == Fire))
                core.enabled = _laser == Fire;
    }

    // The light of the Everseer's eyes (on everybody else's screen): brighter and wider with the mask on, the more so the closer the laser is to firing.
    private static void EyeLights(PlayerAvatar killer, float strength)
    {
        var eyes = killer.playerAvatarVisuals != null ? killer.playerAvatarVisuals.playerEyes : null;
        if (eyes == null)
            return;
        foreach (var eye in new[] { eyes.eyeLeft, eyes.eyeRight })
        {
            var light = eye != null ? eye.Find("Hunted Eye Light")?.GetComponent<Light>() : null;
            if (light == null)
                continue;
            light.intensity = Tuning.Killer.EyeLightIntensity * (1f + 5f * strength);
            light.range = Tuning.Killer.EyeLightRange * (1f + 1.5f * strength);
        }
    }

    // The cart laser's build-up hum, rising in pitch, from the Everseer's head while the laser builds up.
    private static void BuildupSound(PlayerAvatar killer)
    {
        if (_cart == null || _cart.soundBuildupLoop == null || _cart.soundBuildupLoop.Sounds.Length == 0)
            return;
        if (_buildupSource == null)
        {
            var host = new GameObject("Hunted Laser Buildup");
            _buildupSource = host.AddComponent<AudioSource>();
            _buildupSource.playOnAwake = false;
            _buildupSource.loop = true;
            _buildupSource.spatialBlend = 1f;
            _buildupSource.minDistance = 3f;
            _buildupSource.maxDistance = 30f;
            if (AudioManager.instance != null)
                _buildupSource.outputAudioMixerGroup = AudioManager.instance.SoundMasterGroup;
            var source = _cart.soundBuildupLoop;
            _buildupLoop = new Sound
            {
                Source = _buildupSource,
                Sounds = source.Sounds,
                Type = source.Type,
                Volume = source.Volume,
                VolumeRandom = 0f,
                Pitch = source.Pitch,
                PitchRandom = 0f,
                SpatialBlend = 1f,
                FalloffMultiplier = source.FalloffMultiplier,
            };
            KillerBody.AddSound(_buildupLoop);
        }
        _buildupSource.transform.position = EyePosition(killer, 0);
        var boot = Mathf.Max(0.1f, HuntedConfig.LaserChargeSeconds.Value);
        _buildupLoop!.PlayLoop(_laser == Boot, 5f, 5f, Mathf.Lerp(0.8f, 1.4f, Mathf.Clamp01((Time.time - _laserSince) / boot)));
    }

    private static SemiLaser? Beam(int index, PlayerAvatar killer)
    {
        if (_beams[index] != null)
            return _beams[index];
        // The cart laser's beam (the laser gun's if there is no cart laser).
        var source = _cart != null && _cart.semiLaser != null ? _cart.semiLaser : _gun != null ? _gun.semiLaser : null;
        if (source == null)
            return null;
        var copy = Object.Instantiate(source.gameObject);
        copy.name = "Hunted Eye Laser " + index;
        var beam = copy.GetComponent<SemiLaser>();
        // Its hit box is set up at once, before it can touch anything: the laser only finds it when it starts up, a frame later, and until then it would
        // still break the valuables in its way.
        var hit = copy.GetComponentInChildren<HurtCollider>(true);
        if (hit != null)
            Configure(hit, index, killer);
        if (index == Reach)
        {
            // Nothing of it shows or sounds: only its hit box counts.
            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
            foreach (var light in copy.GetComponentsInChildren<Light>(true))
                light.enabled = false;
            foreach (var audio in copy.GetComponentsInChildren<AudioSource>(true))
                audio.mute = true;
            foreach (var sound in new[] { beam.soundLaserStart, beam.soundLaserStartGlobal, beam.soundLaserEnd, beam.soundLaserEndGlobal, beam.soundLaserLoop,
                         beam.soundLaserHitStart, beam.soundLaserHitEnd, beam.soundLaserHitLoop, beam.soundLaserGrace })
                if (sound != null)
                    sound.Volume = 0f;
        }
        else if (killer.isLocal)
        {
            // Before the laser starts up (it reads these then). On the killer's own screen: thinner and mostly see-through, so it does not hide what it aims at.
            var values = Tuning.Everseer;
            beam.beamThickness *= values.LaserOwnThickness;
            VisualClone.Recolour(copy, Purple * values.LaserOwnAlpha, 1.5f);
            foreach (var line in copy.GetComponentsInChildren<LineRenderer>(true))
            {
                line.startColor = new Color(line.startColor.r, line.startColor.g, line.startColor.b, values.LaserOwnAlpha);
                line.endColor = new Color(line.endColor.r, line.endColor.g, line.endColor.b, values.LaserOwnAlpha);
            }
            // The sparks where the beams leave would be right in front of the killer's eyes.
            if (beam.shootTransform != null)
                foreach (var system in beam.shootTransform.GetComponentsInChildren<ParticleSystem>(true))
                    system.gameObject.SetActive(false);
        }
        else
        {
            // The cart laser's beam as it is, in purple and with more light, for everyone else.
            beam.beamThickness *= Tuning.Everseer.LaserThickness;
            VisualClone.Recolour(copy, Purple, 1.5f);
            foreach (var light in copy.GetComponentsInChildren<Light>(true))
            {
                light.intensity *= Tuning.Everseer.LaserLight;
                light.range *= 1.5f;
            }
        }
        KillerBody.AddItemSounds(beam); // as deep and loud as the killer
        _beams[index] = beam;
        return beam;
    }

    private static Vector3 EyePosition(PlayerAvatar killer, int index)
    {
        // On the killer's own screen the beams leave from just under the view, a little apart, so they do not cover what the killer looks at.
        if (killer.isLocal)
        {
            var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
            if (camera != null)
            {
                var t = camera.transform;
                return t.position + t.forward * 0.4f - t.up * 0.3f + t.right * (index == 0 ? -0.12f : 0.12f);
            }
        }
        var eyes = killer.playerAvatarVisuals != null ? killer.playerAvatarVisuals.playerEyes : null;
        var eye = eyes == null ? null : index == 0 ? eyes.eyeLeft : eyes.eyeRight;
        if (eye != null)
            return eye.position;
        return killer.localCamera != null ? killer.localCamera.transform.position : killer.transform.position + Vector3.up * 2f;
    }

    private static int _sightMask;
    internal static int SightMask => _sightMask != 0 ? _sightMask : _sightMask = (int)SemiFunc.LayerMaskGetVisionObstruct() & ~LayerMask.GetMask("Player");

    /// <summary>
    /// While the laser fires, every computer draws both beams, from the killer's eyes to where the killer looks. They look as the cart laser does: they end on the first
    /// wall they meet, with its flare, light and shaking there. Their hurt goes on through walls: a third beam that is not seen carries it all the way.
    /// </summary>
    private static void Beams(PlayerAvatar killer)
    {
        if (killer.localCamera == null)
            return;
        var aim = killer.localCamera.transform;
        var reach = aim.position + aim.forward * HuntedConfig.LaserRange.Value;
        var hitting = Physics.Raycast(aim.position + aim.forward * 0.5f, aim.forward, out var hit, HuntedConfig.LaserRange.Value, SightMask, QueryTriggerInteraction.Ignore);
        var end = hitting ? hit.point : reach;
        for (var i = 0; i < 3; i++)
        {
            var beam = Beam(i, killer);
            if (beam == null)
                continue;
            if (beam.hurtCollider != null)
                Configure(beam.hurtCollider, i, killer);
            if (i == Reach)
            {
                beam.LaserActive(EyePosition(killer, 0), reach, false, 0f, 0f, 0f);
                continue;
            }
            // No shaking on the killer's own screen; everyone else's shakes near where it hits, as with the game's laser.
            var start = EyePosition(killer, i);
            if (killer.isLocal)
                beam.LaserActive(start, end, hitting, 0f, 0f, 0f);
            else
                beam.LaserActive(start, end, hitting, 8f, 12f, 3f);
            if (Tuning.Everseer.LaserCoreWidth > 0f && Core(i, killer) is { } core)
            {
                core.SetPosition(0, start);
                core.SetPosition(1, end);
            }
        }
    }

    /// <summary>A solid purple line down the middle of each beam, so the beam reads as one bright, opaque ray (the game's beam is a see-through glow). Fainter on the
    /// killer's own screen.</summary>
    private static LineRenderer? Core(int index, PlayerAvatar killer)
    {
        if (_cores[index] != null)
            return _cores[index];
        if (_coreMaterial == null)
        {
            var shader = Shader.Find("Sprites/Default") ?? Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
                return null;
            _coreMaterial = new Material(shader) { name = "Hunted Laser Core", hideFlags = HideFlags.HideAndDontSave };
        }
        var values = Tuning.Everseer;
        var line = new GameObject("Hunted Laser Core " + index).AddComponent<LineRenderer>();
        line.sharedMaterial = _coreMaterial;
        line.positionCount = 2;
        line.useWorldSpace = true;
        line.numCapVertices = 4;
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;
        var own = killer.isLocal;
        var colour = new Color(0.8f, 0.45f, 1f, own ? values.LaserOwnAlpha : 1f);
        line.startColor = line.endColor = colour;
        line.widthMultiplier = values.LaserCoreWidth * (own ? values.LaserOwnThickness : values.LaserThickness);
        line.enabled = false;
        _cores[index] = line;
        return line;
    }

    /// <summary>What a beam's hit box does: it hurts, knocks back and tumbles survivors (only the beam that goes all the way, so a survivor is hit once), and
    /// nothing else: no monster, no valuable, no door or other physics object.</summary>
    private static void Configure(HurtCollider hurt, int index, PlayerAvatar killer)
    {
        hurt.playerLogic = index == Reach;
        hurt.playerDamage = Mathf.RoundToInt(HuntedConfig.LaserDamage.Value * (KillerRoom.Contains(killer.transform.position) ? Tuning.Killer.KillerRoomEmpowerment : 1f));
        hurt.playerDamageCooldown = Tuning.Everseer.LaserHitCooldown;
        hurt.playerKill = false;
        // It knocks a survivor back and tumbles them (a boosted one cannot be tumbled).
        hurt.playerTumbleForce = Tuning.Everseer.LaserTumbleForce;
        hurt.playerTumbleTorque = Tuning.Everseer.LaserTumbleForce;
        hurt.playerTumbleTime = Tuning.Everseer.LaserTumbleSeconds;
        hurt.playerHitForce = Tuning.Everseer.LaserKnockback;
        hurt.playerRayCast = false; // a wall between the eyes and a survivor does not shield them
        hurt.playerCausingHurtOverride = killer;
        hurt.enemyLogic = false;
        hurt.physLogic = false;
        hurt.physDestroy = false;
        hurt.physHingeDestroy = false;
        hurt.physHingeBreak = false;
        hurt.physHitForce = 0f;
        hurt.physHitTorque = 0f;
        hurt.physImpact = HurtCollider.BreakImpact.None;
    }

    internal static bool IsLaser(HurtCollider collider) => _beams.Any(b => b != null && b.hurtCollider == collider);
}

[HarmonyPatch]
static class EverseerPatches
{
    [HarmonyPrefix, HarmonyPatch(typeof(InventorySpot), "HandleInput")]
    private static bool HandleInput_Prefix(InventorySpot __instance) => Everseer.OnSlotPressed(__instance.inventorySpotIndex);

    [HarmonyPostfix, HarmonyPatch(typeof(InventorySpot), "Update")]
    private static void SpotUpdate_Postfix(InventorySpot __instance) => Everseer.DrawSlot(__instance);

    // The killer's own beams never touch the killer.
    [HarmonyPrefix, HarmonyPatch(typeof(HurtCollider), "PlayerHurt")]
    private static bool PlayerHurt_Prefix(HurtCollider __instance, PlayerAvatar _player)
        => _player == null || !KillerRole.IsKiller(_player) || !Everseer.IsLaser(__instance);
}
