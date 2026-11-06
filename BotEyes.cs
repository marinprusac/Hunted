using UnityEngine;

namespace Hunted;

/// <summary>
/// The bots and the Everseer's planted eyes. A bot notices an eye only once it has had it in plain view in front of it, not too far off, for a few seconds; then
/// it knows what it is: a moment later it turns to it and destroys it, the way a player does, by holding on to it from where it stands. Bots run on the host, which tells everyone.
/// </summary>
internal partial class Bot
{
    private PlantedEye? _eyeSpotted;
    private float _eyeTouchAt;
    private PlantedEye? _eyeGlimpsed;
    private float _eyeGlimpsedSince;
    private float _eyeNoticeAfter;

    private void NoticeEyes(Vector3 eye, bool glance)
    {
        if (!Everseer.IsCurrent)
        {
            _eyeSpotted = null;
            return;
        }
        if (_eyeSpotted == null)
        {
            // A small, shut eye is easy to miss: it is only noticed after it has been in plain view in front of the bot, not too far off, for a few seconds running.
            PlantedEye? inView = null;
            foreach (var planted in PlantedEyes.All)
            {
                var offset = planted.Centre - eye;
                var distance = offset.magnitude;
                if (distance > Tuning.Everseer.BotEyeNoticeRange || distance < 0.01f || Vector3.Dot(transform.forward, offset / distance) < 0.6f)
                    continue;
                if (PlantedEyes.Aimed(eye, offset / distance, distance + 1f) != planted)
                    continue; // something is in the way
                inView = planted;
                break;
            }
            if (inView != _eyeGlimpsed)
            {
                _eyeGlimpsed = inView;
                _eyeGlimpsedSince = Time.time;
                _eyeNoticeAfter = Random.Range(Tuning.Everseer.BotEyeNoticeSeconds * 0.6f, Tuning.Everseer.BotEyeNoticeSeconds * 1.4f);
            }
            if (inView == null || Time.time - _eyeGlimpsedSince < _eyeNoticeAfter)
                return;
            _eyeSpotted = inView;
            _eyeGlimpsed = null;
            _eyeTouchAt = Time.time + Random.Range(0.4f, 1f) + HuntedConfig.EyeHoldSeconds.Value; // a moment to react, then the hold
            DebugNote("notices a planted eye");
            return;
        }
        // It holds on to it, turned towards it; if it loses sight of it, it lets go.
        var held = _eyeSpotted.Centre - eye;
        if (held.sqrMagnitude > 0.0001f && PlantedEyes.Aimed(eye, held.normalized, held.magnitude + 1f) != _eyeSpotted)
        {
            _eyeSpotted = null;
            return;
        }
        var facing = new Vector3(held.x, 0f, held.z);
        if (facing.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(facing);
        if (Time.time < _eyeTouchAt)
            return;
        var target = _eyeSpotted;
        _eyeSpotted = null;
        var toEye = target.Centre - eye;
        if (toEye.sqrMagnitude < 0.0001f)
            return;
        // Still there and still in view: it turns to it and grabs at it.
        if (PlantedEyes.Aimed(eye, toEye.normalized, toEye.magnitude + 1f) != target)
            return;
        var flat = new Vector3(toEye.x, 0f, toEye.z);
        if (flat.sqrMagnitude > 0.01f)
            transform.rotation = Quaternion.LookRotation(flat);
        PlantedEyes.Destroy(target.Id);
        DebugNote("destroys a planted eye");
    }
}
