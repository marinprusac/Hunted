using BepInEx.Configuration;

namespace Hunted;

internal static class HuntedConfig
{
    internal static ConfigEntry<float> KillerWalkSpeedMultiplier = null!;
    internal static ConfigEntry<float> TimeLimitMinutes = null!;
    internal static ConfigEntry<float> KillerFogStart = null!;
    internal static ConfigEntry<float> KillerFogEnd = null!;
    internal static ConfigEntry<float> NightVisionRange = null!;
    internal static ConfigEntry<float> NightVisionIntensity = null!;
    internal static ConfigEntry<float> EnergyPerSurvivorHit = null!;
    internal static ConfigEntry<int> WeaponDamage = null!;
    internal static ConfigEntry<float> EnergyDrainPerSecond = null!;
    internal static ConfigEntry<float> ChargeRate = null!;
    internal static ConfigEntry<int> ExtractionGoal = null!;
    internal static ConfigEntry<int> MaxHealth = null!;
    internal static ConfigEntry<float> HealthRegenPerSecond = null!;
    internal static ConfigEntry<float> BoostSeconds = null!;
    internal static ConfigEntry<int> GridColumns = null!;
    internal static ConfigEntry<int> GridRows = null!;

    internal static void Bind(ConfigFile config)
    {
        ConfigDefaults.Load();
        KillerWalkSpeedMultiplier = config.Bind("Killer", "WalkSpeedMultiplier", ConfigDefaults.Get<float>("Killer", "WalkSpeedMultiplier"),
            new ConfigDescription("Killer walking speed relative to a normal player's walking speed. The killer cannot sprint.", new AcceptableValueRange<float>(0.5f, 5f)));
        ExtractionGoal = config.Bind("Extraction", "GoalPerPoint", ConfigDefaults.Get<int>("Extraction", "GoalPerPoint"),
            new ConfigDescription("Dollar value of valuables that must be deposited into one extraction point to complete it.", new AcceptableValueRange<int>(1000, 20000)));
        MaxHealth = config.Bind("Survivor", "MaxHealth", ConfigDefaults.Get<int>("Survivor", "MaxHealth"),
            new ConfigDescription("Health every player starts a level with, and the most they can have.", new AcceptableValueRange<int>(1, 500)));
        HealthRegenPerSecond = config.Bind("Survivor", "RegenPerSecond", ConfigDefaults.Get<float>("Survivor", "RegenPerSecond"),
            new ConfigDescription("Host only. Health a living player regenerates per second. 0 turns regeneration off.", new AcceptableValueRange<float>(0f, 50f)));
        BoostSeconds = config.Bind("Survivor", "BoostSeconds", ConfigDefaults.Get<float>("Survivor", "BoostSeconds"),
            new ConfigDescription("A survivor who is revived, or hit down to half their maximum health or less, runs without tiring and cannot be tumbled by anything but themselves for this long. 0 turns the boost off.",
                new AcceptableValueRange<float>(0f, 20f)));
        GridColumns = config.Bind("Level", "Columns", ConfigDefaults.Get<int>("Level", "Columns"),
            new ConfigDescription("Columns of rooms in the map's inner grid. The extraction rooms and dead ends sit in an extra column on each side.",
                new AcceptableValueRange<int>(3, 10)));
        GridRows = config.Bind("Level", "Rows", ConfigDefaults.Get<int>("Level", "Rows"),
            new ConfigDescription("Rows of rooms in the map's inner grid. The trucks sit outside the first and last row. More rows leave more room for extraction rooms and dead ends.",
                new AcceptableValueRange<int>(3, 10)));
        EnergyPerSurvivorHit = config.Bind("Sword", "EnergyPerSurvivorHit", ConfigDefaults.Get<float>("Sword", "EnergyPerSurvivorHit"),
            new ConfigDescription("Host only. Energy (out of 100) the sword loses when it hits a survivor.", new AcceptableValueRange<float>(0f, 100f)));
        WeaponDamage = config.Bind("Sword", "DamagePerHit", ConfigDefaults.Get<int>("Sword", "DamagePerHit"),
            new ConfigDescription("Damage the killer's weapon deals to a survivor per hit.", new AcceptableValueRange<int>(1, 200)));
        EnergyDrainPerSecond = config.Bind("Sword", "EnergyDrainPerSecond", ConfigDefaults.Get<float>("Sword", "EnergyDrainPerSecond"),
            new ConfigDescription("Energy (out of 100) the weapon loses per second, held or not, on top of the normal loss per hit.", new AcceptableValueRange<float>(0f, 10f)));
        ChargeRate = config.Bind("Sword", "ChargePerSecond", ConfigDefaults.Get<float>("Sword", "ChargePerSecond"),
            new ConfigDescription("Energy the killer's weapon regains per second while the killer is in the killer room.", new AcceptableValueRange<float>(0f, 50f)));
        KillerFogStart = config.Bind("Vision", "KillerFogStart", ConfigDefaults.Get<float>("Vision", "KillerFogStart"),
            new ConfigDescription("Killer only. Distance at which the fog starts, the same in every room.", new AcceptableValueRange<float>(0f, 200f)));
        KillerFogEnd = config.Bind("Vision", "KillerFogEnd", ConfigDefaults.Get<float>("Vision", "KillerFogEnd"),
            new ConfigDescription("Killer only. Distance at which the fog is solid and the view ends, the same in every room. 0 gives the killer the game's own room fog.", new AcceptableValueRange<float>(0f, 300f)));
        NightVisionRange = config.Bind("Vision", "NightVisionRange", ConfigDefaults.Get<float>("Vision", "NightVisionRange"),
            new ConfigDescription("Reach of the night-vision light around the killer (metres).", new AcceptableValueRange<float>(5f, 100f)));
        NightVisionIntensity = config.Bind("Vision", "NightVisionIntensity", ConfigDefaults.Get<float>("Vision", "NightVisionIntensity"),
            new ConfigDescription("Brightness of the night-vision light.", new AcceptableValueRange<float>(0f, 5f)));
        TimeLimitMinutes = config.Bind("Extraction", "TimeLimitMinutes", ConfigDefaults.Get<float>("Extraction", "TimeLimitMinutes"),
            new ConfigDescription("The survivors lose when this many minutes pass before they escape.", new AcceptableValueRange<float>(2f, 30f)));
    }
}
