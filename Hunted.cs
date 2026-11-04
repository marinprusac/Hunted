using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

[BepInDependency("nickklmao.menulib")]
[BepInPlugin("Prussian.Hunted", "Hunted", "0.1.1")]
public class Hunted : BaseUnityPlugin
{
    internal static Hunted Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    private float _roleSyncTimer;
    private float _extractionTimer;

    private void Awake()
    {
        Instance = this;

        // Prevent the plugin from being deleted
        this.gameObject.transform.parent = null;
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        Tuning.Load();
        HuntedConfig.Bind(Config);
        MenuLib.MenuAPI.AddElementToLobbyMenu(DebugToggles.Build);
        MenuLib.MenuAPI.AddElementToLobbyMenu(KillerCheckboxes.BuildHeader);
        ConfigSync.Init(Config);
        Patch();

        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    internal void Patch()
    {
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();
        EnemyIgnore.Apply(Harmony);
    }

    internal void Unpatch()
    {
        Harmony?.UnpatchSelf();
    }

    private void Update()
    {
        ConfigSync.Tick();
        KillerCheckboxes.Tick();
        DebugToggles.Tick();
        KillerRole.SessionCheck();
        RoundHud.Tick();
        LevelNumberPatches.HideLevelUi();
        TruckPatches.KeepKillerOutOfTruck();
        KillerWeapon.Tick(Time.deltaTime);
        KillerRescue.Tick(Time.deltaTime);
        KillerVision.Tick(Time.deltaTime);
        KillerMap.Tick(Time.deltaTime);
        KillerHud.Tick();
        KillerEnemyContacts.Tick(Time.deltaTime);
        PlayerHealthRules.Tick(Time.deltaTime);
        SurvivorBoost.Tick();
        KillerAura.Tick();
        DummySurvivor.Tick();
        KillerBody.Tick();
        KillerLook.Tick(Time.deltaTime);
        _extractionTimer -= Time.unscaledDeltaTime;
        if (_extractionTimer <= 0f)
        {
            _extractionTimer = 0.25f;
            ExtractionHost.Tick();
            ExtractionState.ApplyToRound();
            TruckSingletons.Tick();
            EscapeRule.Tick(0.25f);
            CartSpawner.Tick(0.25f);
            MapMarkers.Tick();
            KillerRoomMap.Tick();
            KillerRoomRules.Tick(0.25f);
            TruckMapCover.Tick();
            PlayerHealthRules.Sync();
            SurvivorBoost.Sync();
            MapDump.Tick(0.25f);
            ValuableSpawner.Tick(0.25f);
        }

        _roleSyncTimer -= Time.unscaledDeltaTime;
        if (_roleSyncTimer > 0f)
            return;
        _roleSyncTimer = 0.5f;
        KillerRole.Sync();
        ModPresence.Tick();
    }
}
