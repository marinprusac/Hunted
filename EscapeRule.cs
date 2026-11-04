using System.Linq;
using HarmonyLib;
using Photon.Pun;
using TMPro;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Host only. How a round ends:
///  - survivors win when the trucks are open and every living survivor is inside a truck (either one);
///  - the killer wins when every survivor is dead, or when the time limit runs out before the survivors escaped.
/// The result is shown to everyone for a few seconds before the level changes.
/// Both outcomes end the run as a failed level, so there is never a shop afterwards.
/// </summary>
internal static class EscapeRule
{
    internal const string DeadlineKey = "Hunted.Deadline";
    internal const string ResultKey = "Hunted.Result";

    private static float _sinceStart;
    private static float _holdTimer;
    private static bool _ended;
    private static bool _survivorsWon;
    private static float _endedFor;
    private static bool _changed;

    internal static void Reset()
    {
        _sinceStart = 0f;
        _holdTimer = 0f;
        _ended = false;
        _changed = false;
        _endedFor = 0f;
        // The previous level's clock and result must not show up in the next one.
        if (SemiFunc.IsMasterClientOrSingleplayer() && PhotonNetwork.InRoom)
        {
            var properties = PhotonNetwork.CurrentRoom.CustomProperties;
            if (properties.ContainsKey(DeadlineKey))
                RoomSync.Set(DeadlineKey, null);
            if (properties.ContainsKey(ResultKey))
                RoomSync.Set(ResultKey, null);
        }
    }

    /// <summary>Seconds until the time limit, or null when there is none (yet).</summary>
    internal static double? SecondsLeft
    {
        get
        {
            if (!PhotonNetwork.InRoom)
                return null;
            var deadline = RoomSync.Get<double>(DeadlineKey);
            return deadline > 0 ? deadline - PhotonNetwork.Time : null;
        }
    }

    internal static string? Result => PhotonNetwork.InRoom ? RoomSync.Get<string>(ResultKey) : null;

    internal static void Tick(float dt)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive
            || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            return;

        if (_ended)
        {
            _endedFor += dt;
            if (_endedFor >= Tuning.Round.ResultSeconds && !_changed)
            {
                _changed = true;
                // Either side ends the whole run as a lost level, without the arena minigame in between: ChangeLevel's failed branch
                // sends a player who is already in the arena straight to game over (progress reset, back to the lobby menu), so
                // pretend the arena is where we are.
                var run = RunManager.instance;
                if (run.levelArena != null && run.levelArena.Count > 0 && run.levelArena[0] != null)
                    run.levelCurrent = run.levelArena[0];
                run.ChangeLevel(_completedLevel: false, _levelFailed: true);
            }
            return;
        }

        // Players are still being placed during the first moments of a level.
        _sinceStart += dt;
        if (_sinceStart < Tuning.Round.StartDelaySeconds)
            return;

        // The clock starts once everyone is on the map.
        var minutes = HuntedConfig.TimeLimitMinutes.Value;
        if (minutes > 0f && PhotonNetwork.InRoom && RoomSync.Get<double>(DeadlineKey) <= 0)
        {
            RoomSync.Set(DeadlineKey, PhotonNetwork.Time + minutes * 60.0);
            Hunted.Logger.LogInfo($"Time limit: {minutes} minutes.");
        }

        var survivors = SemiFunc.PlayerGetList().Where(p => p != null && !KillerRole.IsKiller(p)).ToList();
        if (survivors.Count == 0)
            return;
        var alive = survivors.Where(p => !p.isDisabled && !p.deadSet).ToList();

        if (alive.Count == 0)
        {
            Hold(dt, Tuning.Round.KillerWinHoldSeconds, survivorsWin: false, "ALL SURVIVORS ARE DEAD - THE KILLER WINS");
        }
        else if (ExtractionState.TrucksOpen && alive.All(p => p.RoomVolumeCheck.inTruck))
        {
            Hold(dt, Tuning.Round.SurvivorEscapeHoldSeconds, survivorsWin: true, "THE SURVIVORS ESCAPED");
        }
        else
        {
            _holdTimer = 0f;
            var left = SecondsLeft;
            if (left != null && left <= 0)
            {
                // Out of time: nobody gets away.
                foreach (var survivor in alive)
                    survivor.PlayerDeath(-1);
                End(survivorsWin: false, "TIME IS UP - THE KILLER WINS");
            }
        }
    }

    // The condition has to hold for a moment, so a survivor stepping through a truck door does not end the round.
    private static void Hold(float dt, float seconds, bool survivorsWin, string text)
    {
        _holdTimer += dt;
        if (_holdTimer >= seconds)
            End(survivorsWin, text);
    }

    private static void End(bool survivorsWin, string text)
    {
        _ended = true;
        _survivorsWon = survivorsWin;
        Hunted.Logger.LogInfo($"Round over: {text}");
        RoomSync.Set(ResultKey, text);
    }
}

/// <summary>
/// The round clock and the result, drawn with the game's own UI. The clock is a copy of the extraction counter's text (same font
/// and colour) placed at the top centre; the result goes through the game's big message, the one that announces extractions.
/// </summary>
internal static class RoundHud
{
    private static TextMeshProUGUI? _clock;

    internal static void Tick()
    {
        var active = KillerRole.ModeActive && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        var result = active ? EscapeRule.Result : null;
        if (!string.IsNullOrEmpty(result))
        {
            SetClock(null);
            if (BigMessageUI.instance != null)
            {
                var survivorsWon = result!.Contains("ESCAPED");
                SemiFunc.UIBigMessage(result, survivorsWon ? "{check}" : "{!}", 25f, Color.white, survivorsWon ? Color.green : Color.red);
            }
            return;
        }

        var left = active ? EscapeRule.SecondsLeft : null;
        if (left == null)
        {
            SetClock(null);
            return;
        }
        var seconds = Mathf.Max(0, Mathf.CeilToInt((float)left.Value));
        var text = $"{seconds / 60}:{seconds % 60:00}";
        SetClock(seconds <= 60 ? $"<color=#FF2A20>{text}</color>" : text);
    }

    private static void SetClock(string? text)
    {
        if (text == null)
        {
            if (_clock != null && _clock.gameObject.activeSelf)
                _clock.gameObject.SetActive(false);
            return;
        }
        if (_clock == null && !Create())
            return;
        if (!_clock!.gameObject.activeSelf)
            _clock.gameObject.SetActive(true);
        if (_clock.text != text)
            _clock.text = text;
    }

    private static bool Create()
    {
        var goal = GoalUI.instance;
        if (goal == null)
            return false;

        var copy = Object.Instantiate(goal.gameObject, goal.transform.parent);
        copy.name = "Hunted Round Clock";
        // Only the text is wanted: the game UI behaviour would register itself as the goal counter.
        foreach (var behaviour in copy.GetComponents<MonoBehaviour>())
            if (behaviour is not TMP_Text)
                Object.DestroyImmediate(behaviour);

        _clock = copy.GetComponent<TextMeshProUGUI>();
        var rect = _clock.rectTransform;
        rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -20f);
        _clock.alignment = TextAlignmentOptions.Top;
        _clock.fontSize *= 0.8f;
        _clock.enableWordWrapping = false;
        _clock.raycastTarget = false;
        _clock.enabled = true;
        Hunted.Logger.LogDebug("Round clock created from the extraction counter.");
        return true;
    }
}

/// <summary>The level number means nothing in this mode, so it is left off the loading screen and the map.</summary>
[HarmonyPatch]
static class LevelNumberPatches
{
    [HarmonyPatch(typeof(LoadingUI), nameof(LoadingUI.LevelAnimationStart))]
    [HarmonyPostfix]
    private static void LevelAnimationStart_Postfix(LoadingUI __instance)
    {
        if (KillerRole.ModeActive && __instance.levelNumberText != null && SemiFunc.RunIsLevel())
            __instance.levelNumberText.text = "";
    }

    // The "level" readout that slides in next to the map.
    internal static void HideLevelUi()
    {
        var ui = LevelUI.instance;
        if (ui == null)
            return;
        var visible = !KillerRole.ModeActive;
        if (ui.gameObject.activeSelf != visible)
            ui.gameObject.SetActive(visible);
    }
}
