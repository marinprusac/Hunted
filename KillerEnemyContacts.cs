using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Hunted;

/// <summary>The killer and everything they carry pass through enemies instead of colliding with them.</summary>
internal static class KillerEnemyContacts
{
    private static readonly HashSet<int> KillerIgnored = new();
    private static readonly HashSet<int> EnemyIgnored = new();
    private static float _timer;

    internal static void Reset()
    {
        KillerIgnored.Clear();
        EnemyIgnored.Clear();
        _timer = 0f;
    }

    internal static void Tick(float dt)
    {
        if (!KillerRole.ModeActive || KillerRole.KillerSteamId == null)
            return;
        _timer -= dt;
        if (_timer > 0f)
            return;
        _timer = 1f;

        var killerColliders = new List<Collider>();
        var avatar = SemiFunc.PlayerGetList().FirstOrDefault(p => KillerRole.IsKiller(p));
        if (avatar != null)
            killerColliders.AddRange(avatar.GetComponentsInChildren<Collider>(true));
        if (KillerRole.LocalIsKiller && PlayerController.instance != null)
            killerColliders.AddRange(PlayerController.instance.GetComponentsInChildren<Collider>(true));
        if (KillerWeapon.Weapon != null)
            killerColliders.AddRange(KillerWeapon.Weapon.GetComponentsInChildren<Collider>(true));
        killerColliders = killerColliders.Where(c => c != null).Distinct().ToList();

        var enemyColliders = Object.FindObjectsByType<EnemyParent>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .SelectMany(e => e.GetComponentsInChildren<Collider>(true))
            .Where(c => c != null)
            .ToList();

        var newKiller = killerColliders.Where(c => KillerIgnored.Add(c.GetInstanceID())).ToList();
        var newEnemy = enemyColliders.Where(c => EnemyIgnored.Add(c.GetInstanceID())).ToList();
        if (newKiller.Count == 0 && newEnemy.Count == 0)
            return;

        // New colliders on either side are paired with everything on the other side.
        foreach (var killer in newKiller)
            foreach (var enemy in enemyColliders)
                Ignore(killer, enemy);
        foreach (var enemy in newEnemy)
            foreach (var killer in killerColliders.Except(newKiller))
                Ignore(killer, enemy);
    }

    private static void Ignore(Collider a, Collider b)
    {
        if (a != null && b != null && a != b)
            Physics.IgnoreCollision(a, b, true);
    }
}
