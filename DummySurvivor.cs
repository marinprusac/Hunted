using HarmonyLib;
using UnityEngine;

namespace Hunted;

/// <summary>
/// Debug helper for testing the killer's vision alone: a fake survivor who is always crouching, that the killer can pick up
/// and carry around (stow the weapon, look at it, hold the grab button). When something is right above it (a table, a chair) it
/// curls up and counts as tumbled. It is a plain physics body and exists only on the killer's own machine, so it only ever
/// appears when nobody else is in the game.
/// </summary>
internal class DummySurvivor : MonoBehaviour
{
    internal static DummySurvivor? Current { get; private set; }

    internal bool Crouching => true;
    internal bool Tumbling { get; private set; }
    internal Vector3 TargetPosition => transform.position + Vector3.up * (Tumbling ? 0.2f : 0.5f);

    private Rigidbody _body = null!;
    private CapsuleCollider _collider = null!;
    private Transform _visual = null!;
    private Renderer _renderer = null!;
    private bool _carried;
    private float _carryDistance = 2f;
    private float _coverTimer;

    internal static void Reset()
    {
        if (Current != null)
            Object.Destroy(Current.gameObject);
        Current = null;
    }

    internal static void Tick()
    {
        var wanted = DebugToggles.DummySurvivor && KillerRole.PlayerCountForMode == 1
                     && KillerRole.ModeActive && KillerRole.LocalIsKiller && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated;
        if (!wanted)
        {
            if (Current != null)
                Reset();
            return;
        }
        if (Current == null)
            Spawn();
        else if (Current.transform.position.y < -30f)
        {
            Hunted.Logger.LogDebug("The dummy survivor fell out of the map and is brought back.");
            Reset();
            Spawn();
        }
    }

    private static void Spawn()
    {
        var killer = PlayerController.instance;
        var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        if (killer == null || camera == null)
            return;

        // Three metres ahead of the killer, on the floor.
        var forward = Vector3.ProjectOnPlane(camera.transform.forward, Vector3.up).normalized;
        var origin = killer.transform.position + forward * 3f + Vector3.up * 1.5f;
        var position = Physics.Raycast(origin, Vector3.down, out var floor, 6f, (int)SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore)
            ? floor.point + Vector3.up * 0.05f
            : killer.transform.position + forward * 2f;

        var root = new GameObject("Hunted Dummy Survivor");
        root.transform.position = position;
        var survivor = root.AddComponent<DummySurvivor>();
        survivor.Build();
        Current = survivor;
        Hunted.Logger.LogInfo($"Dummy survivor placed at {position}: stow the weapon, look at it and hold the grab button to carry it.");
    }

    private void Build()
    {
        _body = gameObject.AddComponent<Rigidbody>();
        _body.mass = 20f;
        _body.constraints = RigidbodyConstraints.FreezeRotation;
        _body.interpolation = RigidbodyInterpolation.Interpolate;
        _collider = gameObject.AddComponent<CapsuleCollider>();
        _collider.radius = 0.25f;

        var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        visual.name = "Look";
        Destroy(visual.GetComponent<Collider>());
        visual.transform.SetParent(transform, false);
        _visual = visual.transform;
        _renderer = visual.GetComponent<Renderer>();
        _renderer.material.color = new Color(0.2f, 0.9f, 0.3f);
        // Hidden from the start; the killer's vision shows it once it is spotted.
        _renderer.forceRenderingOff = true;
        _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Apply(tumbled: false);
    }

    // Crouching: about 0.9 m tall. Curled up: a ball about 0.4 m across.
    private void Apply(bool tumbled)
    {
        Tumbling = tumbled;
        var height = tumbled ? 0.4f : 0.9f;
        _collider.height = height;
        _collider.center = new Vector3(0f, height / 2f, 0f);
        _collider.radius = tumbled ? 0.2f : 0.25f;
        _visual.localScale = new Vector3(_collider.radius * 2f, height / 2f, _collider.radius * 2f);
        _visual.localPosition = new Vector3(0f, height / 2f, 0f);
        _renderer.material.color = tumbled ? new Color(1f, 0.55f, 0.1f) : new Color(0.2f, 0.9f, 0.3f);
        Hunted.Logger.LogDebug($"Dummy survivor is now {(tumbled ? "tumbled (something is above it)" : "crouching")}.");
    }

    private void Update()
    {
        var camera = CameraUtils.Instance != null ? CameraUtils.Instance.MainCamera : null;
        if (camera == null)
            return;

        CarryLogic(camera);

        // Under a table or chair it curls up.
        _coverTimer -= Time.deltaTime;
        if (_coverTimer <= 0f)
        {
            _coverTimer = 0.2f;
            var covered = Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.up, out var above, 1.1f, (int)SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore)
                          && above.collider.GetComponentInParent<DummySurvivor>() != this;
            if (covered != Tumbling)
                Apply(covered);
        }
    }

    private void CarryLogic(Camera camera)
    {
        // With the weapon in hand the grab button is the weapon's.
        var allowed = KillerRole.LocalIsKiller && !KillerWeapon.WeaponInHand();
        if (_carried && (!allowed || !SemiFunc.InputHold(InputKey.Grab)))
        {
            _carried = false;
            _body.isKinematic = false;
            Hunted.Logger.LogDebug("Dummy survivor put down.");
            return;
        }

        if (!_carried && allowed && SemiFunc.InputDown(InputKey.Grab)
            && Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)
            && hit.collider.GetComponentInParent<DummySurvivor>() == this)
        {
            _carried = true;
            _carryDistance = Mathf.Clamp(hit.distance, 1.3f, 3.5f);
            _body.isKinematic = true;
            Hunted.Logger.LogDebug("Dummy survivor picked up.");
        }
        if (!_carried)
            return;

        // The carried body hangs a little below where the killer looks, and stops at walls.
        var eye = camera.transform.position;
        var direction = camera.transform.forward;
        var distance = _carryDistance;
        if (Physics.SphereCast(eye, 0.25f, direction, out var wall, distance, (int)SemiFunc.LayerMaskGetVisionObstruct(), QueryTriggerInteraction.Ignore)
            && wall.collider.GetComponentInParent<DummySurvivor>() != this && wall.collider.GetComponentInParent<PlayerController>() == null)
            distance = Mathf.Max(0.6f, wall.distance - 0.1f);
        var target = eye + direction * distance - Vector3.up * (Tumbling ? 0.3f : 0.6f);
        transform.position = Vector3.Lerp(transform.position, target, 15f * Time.deltaTime);
    }
}

/// <summary>Debug: no monsters at all.</summary>
[HarmonyPatch]
static class NoMonstersPatch
{
    // No monsters unless Tuning.json asks for some: the game's own "no enemies" switch, set when the level starts building.
    [HarmonyPatch(typeof(LevelGenerator), "Generate")]
    [HarmonyPrefix]
    private static void Generate_Prefix(LevelGenerator __instance)
    {
        if (Tuning.Monsters.MonstersPerTier <= 0 && SemiFunc.IsMasterClientOrSingleplayer())
        {
            __instance.DebugNoEnemy = true;
            Hunted.Logger.LogInfo("No monsters: Monsters.MonstersPerTier in Tuning.json is 0.");
        }
    }
}
