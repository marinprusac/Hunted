using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Hunted;

/// <summary>
/// The killer cannot die, so a hole in the map (a death pit) would leave them stuck below it. Instead they float: the game's own
/// anti-gravity (the one zero-gravity items use) is switched on and they drift up out of the hole and back to where they last stood
/// safely, then land. Runs on the killer's own client.
/// </summary>
internal static class KillerRescue
{
    private static readonly Queue<Vector3> Safe = new();
    private static readonly PropertyInfo? LinearVelocity = typeof(Rigidbody).GetProperty("linearVelocity");

    private static float _sampleTimer;
    private static float _airTime;
    private static float _floatTime;
    private static bool _floating;
    private static Vector3 _target;

    internal static void Reset()
    {
        Safe.Clear();
        _sampleTimer = 0f;
        _airTime = 0f;
        _floatTime = 0f;
        _floating = false;
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || !KillerRole.LocalIsKiller || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
        {
            if (Safe.Count > 0 || _floating)
                Reset();
            return;
        }
        var controller = PlayerController.instance;
        if (controller == null || controller.rb == null)
            return;
        var position = controller.transform.position;

        if (_floating)
        {
            Float(controller, position, dt);
            return;
        }

        if (controller.CollisionController != null && controller.CollisionController.Grounded)
        {
            _airTime = 0f;
            _sampleTimer -= dt;
            if (_sampleTimer <= 0f)
            {
                // A spot every half second, six of them: the oldest is from about three seconds ago, before any fall began.
                _sampleTimer = 0.5f;
                Safe.Enqueue(position);
                while (Safe.Count > 6)
                    Safe.Dequeue();
            }
            return;
        }

        _airTime += dt;
        if (Safe.Count == 0 || _airTime < Tuning.Rescue.AirSeconds)
            return;
        var anchor = Safe.Peek();
        if (position.y >= anchor.y - Tuning.Rescue.FallDepth)
            return;

        _floating = true;
        _floatTime = 0f;
        _target = anchor + Vector3.up * 1.2f;
        Hunted.Logger.LogInfo($"The killer fell into a hole at {position}; floating back to {anchor}.");
    }

    private static void Float(PlayerController controller, Vector3 position, float dt)
    {
        _floatTime += dt;
        controller.AntiGravity(0.2f);
        Stop(controller.rb);

        var toTarget = _target - position;
        if (toTarget.magnitude < 0.6f)
        {
            _floating = false;
            _airTime = 0f;
            return;
        }

        // Up out of the hole first, then across to the spot, so the walls of the hole are never in the way.
        var direction = position.y < _target.y - 0.4f ? Vector3.up : toTarget.normalized;
        var next = _floatTime >= Tuning.Rescue.TeleportAfterSeconds
            ? _target
            : position + direction * Mathf.Min(Tuning.Rescue.FloatSpeed * dt, toTarget.magnitude);
        controller.rb.position = next;
        controller.transform.position = next;
    }

    // Falling speed would otherwise carry on while floating.
    private static void Stop(Rigidbody rb)
    {
        LinearVelocity?.SetValue(rb, Vector3.zero);
        rb.angularVelocity = Vector3.zero;
    }
}
