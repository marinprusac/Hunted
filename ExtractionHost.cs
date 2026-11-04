using UnityEngine;

namespace Hunted;

/// <summary>
/// Host only. Once the level is ready: decides how many extraction points are required, sets the haul goal, and opens every
/// point at once (vanilla opens one at a time, when a player presses its button).
/// </summary>
internal static class ExtractionHost
{
    internal static void Tick()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer() || !KillerRole.ModeActive)
            return;
        var round = RoundDirector.instance;
        var points = ExtractionPoints.Get();
        // extractionPointsFetched: vanilla has finished setting its own haul goal, which would overwrite ours.
        if (round == null || points.Count == 0 || !round.extractionPointsFetched)
            return;

        if (!ExtractionState.Active)
        {
            var required = Mathf.Min(LevelPatches.ExtractionAmountPatch.Required, points.Count);
            ExtractionState.Init(required);
            Hunted.Logger.LogInfo($"{required} of {points.Count} extraction points must be completed, goal ${ExtractionState.Goal} each.");
        }

        // Each point asks for haulGoal / extractionPoints, so this gives every point the configured amount.
        ExtractionState.ApplyToRound();
        round.haulGoal = ExtractionState.Goal * ExtractionState.Required;

        foreach (var point in points)
            if (point != null && point.currentState == ExtractionPoint.State.Idle)
                point.ButtonPress();
    }
}
