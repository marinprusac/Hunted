using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace Hunted;

[BepInDependency("nickklmao.menulib")]
// REPOConfig (in-game settings) is optional: mod managers install it with Hunted, and Hunted works without it.
[BepInDependency("nickklmao.repoconfig", BepInDependency.DependencyFlags.SoftDependency)]
[BepInPlugin("Prussian.Hunted", "Hunted", "0.3.1")]
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
        MenuLib.MenuAPI.AddElementToLobbyMenu(LobbyOptions.Build);
        ConfigSync.Init(Config);
        Banger.Init();
        Everseer.Init();
        BotAvatar.Init();
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

    private void LateUpdate()
    {
        KillerWeapon.KeepSize();
        InventoryLook.Tick();
    }

    private void Update()
    {
        NoSeasonalEvent.Tick();
        ConfigSync.Tick();
        LobbyOptions.Tick();
        KillerRole.SessionCheck();
        RoundHud.Tick();
        LevelNumberPatches.HideLevelUi();
        TruckPatches.KeepKillerOutOfTruck();
        KillerWeapon.Tick(Time.deltaTime);
        Banger.Tick(Time.deltaTime);
        Everseer.Tick(Time.deltaTime);
        ScreenTint.Tick(Time.deltaTime);
        KillerRescue.Tick(Time.deltaTime);
        KillerBrightness.Tick();
        KillerVision.Tick(Time.deltaTime);
        KillerMap.Tick(Time.deltaTime);
        KillerHud.Tick();
        KillerEnemyContacts.Tick(Time.deltaTime);
        PlayerHealthRules.Tick(Time.deltaTime);
        SurvivorBoost.Tick();
        KillerAura.Tick();
        SurvivorSprint.Tick();
        Bot.Tick();
        BotAvatar.Tick(Time.deltaTime);
        KillerBody.Tick();
        KillerLook.Tick(Time.deltaTime);
        SurvivorReveal.Tick(Time.deltaTime);
        ChaseMusic.Tick(Time.deltaTime);
        InventorySlots.Tick();
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
            KillerRoomOutline.Tick();
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
