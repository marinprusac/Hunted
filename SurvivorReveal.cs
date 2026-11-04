using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Revealing a survivor to the killer, whatever the reason. A survivor who is revealed gets, all at once and always the same: a yellow see-through shape the killer sees
/// through walls (see KillerXray), a yellow dot on the killer's map, a chime from the survivor's direction when they become revealed, and a quiet hum from them for as long as they stay revealed. Everything that can reveal
/// a survivor (the killer room today, perks later) only has to say so by adding a source with <see cref="AddSource"/>; the rest is done here, on the killer's own computer.
/// </summary>
internal static class SurvivorReveal
{
    /// <summary>The colour of every reveal: the shape and the dot.</summary>
    internal static readonly Color Color = new(1f, 1f, 0f);

    /// <summary>A survivor a source can reveal: a player, or a bot.</summary>
    internal readonly struct Subject
    {
        internal readonly Component Key;
        internal readonly Vector3 Position;
        internal readonly Transform Root;
        internal readonly PlayerAvatar? Player;

        internal Subject(Component key, Vector3 position, Transform root, PlayerAvatar? player)
        {
            Key = key;
            Position = position;
            Root = root;
            Player = player;
        }
    }

    private static readonly List<Func<Subject, bool>> Sources = new();
    private static readonly HashSet<Component> Revealed = new();
    private static readonly Dictionary<Component, float> LastChime = new();
    private static readonly Dictionary<Component, MapCustom> Markers = new();
    private static readonly Dictionary<Component, AudioSource> Hums = new();
    private static Sprite? _dot;
    private static float _timer;
    private static bool _registered;

    /// <summary>Adds a reason to reveal survivors: called every few tenths of a second for every living survivor, true means "reveal them now".</summary>
    internal static void AddSource(Func<Subject, bool> source) => Sources.Add(source);

    private static void RegisterBuiltIn()
    {
        _registered = true;
        // Nobody hides in the killer room.
        AddSource(subject => KillerRoom.Ensure() && KillerRoom.TilesFromRoom(subject.Position) == 0);
    }

    internal static void Reset()
    {
        Revealed.Clear();
        LastChime.Clear();
        foreach (var marker in Markers.Values)
            RemoveMarker(marker);
        Markers.Clear();
        foreach (var hum in Hums.Values)
            if (hum != null)
                UnityEngine.Object.Destroy(hum.gameObject);
        Hums.Clear();
        KillerXray.Reset();
        _timer = 0f;
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller)
        {
            if (Revealed.Count > 0 || Markers.Count > 0)
                Reset();
            return;
        }
        if (!_registered)
            RegisterBuiltIn();
        _timer -= dt;
        if (_timer > 0f)
            return;
        _timer = 0.2f;

        var seen = new HashSet<Component>();
        foreach (var player in SemiFunc.PlayerGetList())
        {
            if (player == null || player.isLocal || KillerRole.IsKiller(player) || player.isDisabled || !player.gameObject.activeInHierarchy || player.playerAvatarVisuals == null)
                continue;
            Update(new Subject(player, player.transform.position, player.playerAvatarVisuals.transform, player), seen);
        }
        foreach (var bot in Bot.All.ToArray())
            if (bot != null && bot.Alive)
                Update(new Subject(bot, bot.transform.position, bot.transform, null), seen);

        // Survivors that are gone (dead, left, or out of the level) are not revealed any more.
        foreach (var key in Revealed.Where(k => k == null || !seen.Contains(k)).ToList())
            Hide(key);
    }

    private static void Update(Subject subject, HashSet<Component> seen)
    {
        seen.Add(subject.Key);
        var revealed = Sources.Any(source => source(subject));
        if (!revealed)
        {
            if (Revealed.Contains(subject.Key))
                Hide(subject.Key);
            return;
        }

        if (Revealed.Add(subject.Key) && (!LastChime.TryGetValue(subject.Key, out var last) || Time.time - last >= Tuning.KillerRoom.AlertCooldownSeconds))
        {
            // The chime comes from the survivor, so the killer hears which way they are. At most once per cooldown however often they come and go.
            LastChime[subject.Key] = Time.time;
            RoomAlert.Play(subject.Position);
        }
        KillerXray.Show(subject.Key, subject.Root);
        // A hum comes from the survivor for as long as they are revealed.
        if (!Hums.TryGetValue(subject.Key, out var hum) || hum == null)
        {
            hum = RoomAlert.StartHum(subject.Root);
            if (hum != null)
                Hums[subject.Key] = hum;
        }
        if (!Markers.TryGetValue(subject.Key, out var marker) || marker == null || marker.mapCustomEntity == null)
            AddMarker(subject);
    }

    private static void Hide(Component key)
    {
        Revealed.Remove(key);
        KillerXray.Hide(key);
        if (Hums.TryGetValue(key, out var hum))
        {
            if (hum != null)
                UnityEngine.Object.Destroy(hum.gameObject);
            Hums.Remove(key);
        }
        if (Markers.TryGetValue(key, out var marker))
        {
            RemoveMarker(marker);
            Markers.Remove(key);
        }
    }

    // The map's own custom-marker system draws and moves the dot.
    private static void AddMarker(Subject subject)
    {
        if (Map.Instance == null || Map.Instance.CustomObject == null)
            return;
        if (Markers.TryGetValue(subject.Key, out var old))
            RemoveMarker(old);
        var marker = subject.Key.gameObject.AddComponent<MapCustom>();
        marker.autoAdd = false;
        marker.sprite = Dot();
        marker.color = Color;
        marker.fadeAlpha = 1f;
        marker.Add();
        if (marker.mapCustomEntity != null)
            marker.mapCustomEntity.transform.localScale *= Tuning.KillerRoom.DotScale;
        Markers[subject.Key] = marker;
    }

    private static void RemoveMarker(MapCustom? marker)
    {
        if (marker == null)
            return;
        if (marker.mapCustomEntity != null)
            UnityEngine.Object.Destroy(marker.mapCustomEntity.gameObject);
        UnityEngine.Object.Destroy(marker);
    }

    private static Sprite Dot()
    {
        if (_dot != null)
            return _dot;
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
        for (var x = 0; x < size; x++)
        {
            for (var y = 0; y < size; y++)
            {
                var distance = Vector2.Distance(new Vector2(x, y), new Vector2(size / 2f - 0.5f, size / 2f - 0.5f)) / (size / 2f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01((1f - distance) * 8f)));
            }
        }
        texture.Apply();
        return _dot = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 64f);
    }
}
