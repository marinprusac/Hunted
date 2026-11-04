using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Hunted;

/// <summary>
/// How a bot holds things (the host runs the bots and owns the physics). Only a bot gets this: what it carries travels without colliding (a large valuable would otherwise jam in the first doorway)
/// and cannot break, and is safe for a moment after it is put down. The moment a player grabs it, it is an ordinary object again.
/// </summary>
internal static class BotCarry
{
    private const float ProtectedSeconds = 2f;

    private static readonly Dictionary<PhysGrabObject, float> Settling = new(); // what a bot just put down, until when it is protected
    private static readonly PropertyInfo? LinearVelocity = typeof(Rigidbody).GetProperty("linearVelocity");

    internal static void Reset() => Settling.Clear();

    internal static void HoldAt(PhysGrabObject grab, Vector3 hands)
    {
        var body = grab.GetComponent<Rigidbody>();
        if (body == null)
            return;
        grab.OverrideKinematic(0.3f);
        grab.OverrideIndestructible(0.3f);
        body.position = Vector3.MoveTowards(body.position, hands, 20f * Time.deltaTime);
    }

    internal static void LetGo(PhysGrabObject grab, Vector3? at)
    {
        var body = grab.GetComponent<Rigidbody>();
        if (body == null)
            return;
        grab.OverrideKinematic(0f);
        grab.OverrideIndestructible(ProtectedSeconds);
        Settling[grab] = Time.time + ProtectedSeconds;
        if (at.HasValue)
        {
            body.position = at.Value;
            grab.transform.position = at.Value;
        }
        LinearVelocity?.SetValue(body, Vector3.zero);
    }

    internal static void Tick()
    {
        if (Settling.Count == 0 || !SemiFunc.IsMasterClientOrSingleplayer())
            return;
        foreach (var pair in Settling.ToList())
        {
            var grab = pair.Key;
            if (grab == null || Time.time >= pair.Value)
            {
                Settling.Remove(grab!);
                continue;
            }
            if (grab.grabbed)
            {
                grab.ResetIndestructible();
                Settling.Remove(grab);
            }
        }
    }
}
