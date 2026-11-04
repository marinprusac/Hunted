using System.Collections.Generic;
using System.Linq;
using ExitGames.Client.Photon;
using Photon.Pun;
using UnityEngine;

namespace Hunted;

internal static class KillerRole
{
    private const string RoomKey = "Hunted.Killer";

    internal static string? KillerSteamId { get; private set; }

    /// <summary>Everyone except the killer (at least 1, so a solo test still has something to complete).</summary>
    internal static int SurvivorCount => Mathf.Max(1, PlayerCountForMode - 1);

    internal static int PlayerCountForMode => PhotonNetwork.InRoom ? PhotonNetwork.CurrentRoom.PlayerCount : 1;

    /// <summary>True during a level in which the Hunted rules apply. Known to every client before the level is generated.</summary>
    internal static bool ModeActive => InPlayableLevel;

    /// <summary>
    /// A real run level that is actually loaded. Two traps here: SemiFunc.RunIsLevel() is also true while no level is set
    /// yet, and when the host starts the game RunManager.levelCurrent changes to the new level at once while the old scene
    /// (lobby) keeps running for a few seconds with a finished level generator. So the scene's own generator must be
    /// working on the current level.
    /// </summary>
    internal static bool InPlayableLevel
    {
        get
        {
            var run = RunManager.instance;
            var generator = LevelGenerator.Instance;
            return run != null && run.levelCurrent != null && run.levels != null && run.levels.Contains(run.levelCurrent)
                   && generator != null && generator.Level == run.levelCurrent && SemiFunc.RunIsLevel();
        }
    }

    internal static bool IsKiller(PlayerAvatar? player)
        => KillerSteamId != null && player != null && player.steamID == KillerSteamId;

    internal static bool LocalIsKiller => KillerSteamId != null && IsKiller(SemiFunc.PlayerAvatarLocal());

    private static int _session;

    /// <summary>Resets all per-level state whenever a new level scene (a new LevelGenerator) shows up.</summary>
    internal static void SessionCheck()
    {
        var generator = LevelGenerator.Instance;
        var id = generator != null ? generator.GetInstanceID() : 0;
        if (id == _session)
            return;
        _session = id;
        ResetLevelState();
    }

    private static void ResetLevelState()
    {
        SpawnPlanner.Reset();
        KillerRoom.Reset();
        EndgameEnemies.Reset();
        ExtractionState.Reset();
        ExtractionPoints.Reset();
        ValuableSpawner.Reset();
        KillerWeapon.Reset();
        KillerVision.Reset();
        KillerLook.Reset();
        KillerMap.Reset();
        SecondTruck.Reset();
        KillerRescue.Reset();
        TruckSingletons.Reset();
        EscapeRule.Reset();
        CartSpawner.Reset();
        MapMarkers.Reset();
        KillerRoomMap.Reset();
        TruckMapCover.Reset();
        MapDump.Reset();
        KillerEnemyContacts.Reset();
        PlayerHealthRules.Reset();
        SurvivorBoost.Reset();
        KillerRoomRules.Reset();
        KillerAura.Reset();
        KillerBody.Reset();
        DummySurvivor.Reset();
    }

    internal static void Sync()
    {
        var inLevel = InPlayableLevel;
        var isHost = SemiFunc.IsMasterClientOrSingleplayer();

        if (!inLevel)
        {
            if (isHost)
                Publish(null);
            Apply(null);
            ResetLevelState();
            return;
        }

        if (isHost)
        {
            // Normally already picked when the players spawned; this covers the killer leaving mid-round.
            if (LevelGenerator.Instance != null && LevelGenerator.Instance.Generated)
                HostEnsurePicked();
            return;
        }

        string? synced = null;
        if (PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(RoomKey, out var value))
            synced = value as string;
        Apply(synced);
        ExtractionState.ReadFromRoom();
    }

    /// <summary>Host only. Picks (and publishes) a killer if there is none or the current one left.</summary>
    internal static void HostEnsurePicked()
    {
        var players = SemiFunc.PlayerGetList();
        if (KillerSteamId != null && players.Any(p => p.steamID == KillerSteamId))
            return;

        var pick = Pick(players);
        Publish(pick);
        Apply(pick);
        if (pick != null)
            Hunted.Logger.LogInfo($"Killer is {players.First(p => p.steamID == pick).playerName}");
    }

    private static string? Pick(List<PlayerAvatar> players)
    {
        // The host ticked someone in the lobby's player list; otherwise (or if they left) it is random.
        var chosen = KillerChoice.Chosen(players);
        if (chosen != null)
            return chosen.steamID;

        // Alone, unticked means a survivor with nobody hunting (to test the survivor side); with others it is a random killer.
        if (players.Count < 2)
            return null;
        return players[Random.Range(0, players.Count)].steamID;
    }

    private static void Publish(string? steamId)
    {
        if (!PhotonNetwork.InRoom)
            return;
        var current = PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(RoomKey, out var value) ? value as string : null;
        if (current == steamId)
            return;
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable { { RoomKey, steamId } });
    }

    private static void Apply(string? steamId)
    {
        if (steamId == KillerSteamId)
            return;

        // Enemies skip anyone in this list when looking for a target.
        var noVision = EnemyDirector.instance != null ? EnemyDirector.instance.debugNoVision : null;
        if (KillerSteamId != null)
            noVision?.Remove(KillerSteamId);
        KillerSteamId = steamId;
        if (steamId != null && noVision != null && !noVision.Contains(steamId))
            noVision.Add(steamId);
    }
}
