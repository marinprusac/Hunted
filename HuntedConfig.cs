using BepInEx.Configuration;

namespace Hunted;

internal static class HuntedConfig
{
    internal static ConfigEntry<float> SkelespawnWalkSpeed = null!;
    internal static ConfigEntry<int> BomberSecondsPerUse = null!;
    internal static ConfigEntry<float> EverseerWalkSpeed = null!;
    internal static ConfigEntry<int> LaserDamage = null!;
    internal static ConfigEntry<float> LaserRange = null!;
    internal static ConfigEntry<float> LaserChargeSeconds = null!;
    internal static ConfigEntry<int> LaserShots = null!;
    internal static ConfigEntry<int> StaffSecondsPerUse = null!;
    internal static ConfigEntry<int> EyeSightRange = null!;
    internal static ConfigEntry<float> EyeHoldSeconds = null!;

    /// <summary>The walking speed of the killer being played, relative to a normal player's.</summary>
    internal static float KillerWalkSpeed => (KillerKinds.Current == KillerKind.Everseer ? EverseerWalkSpeed : SkelespawnWalkSpeed).Value;
    internal static ConfigEntry<int> TimeLimitMinutes = null!;
    internal static ConfigEntry<int> KillerFogStart = null!;
    internal static ConfigEntry<int> KillerFogEnd = null!;
    internal static ConfigEntry<int> EnergyPerSurvivorHit = null!;
    internal static ConfigEntry<int> WeaponDamage = null!;
    internal static ConfigEntry<float> EnergyDrainPerSecond = null!;
    internal static ConfigEntry<int> ChargeRate = null!;
    internal static ConfigEntry<int> ExtractionGoalHundreds = null!;
    internal static ConfigEntry<int> MaxHealth = null!;
    internal static ConfigEntry<float> HealthRegenPerSecond = null!;
    internal static ConfigEntry<int> BoostSeconds = null!;
    internal static ConfigEntry<bool> Heartbeat = null!;

    internal static void Bind(ConfigFile config)
    {
        ConfigDefaults.Load();
        SkelespawnWalkSpeed = config.Bind("Skelespawn", "WalkSpeedMultiplier", ConfigDefaults.Get<float>("Skelespawn", "WalkSpeedMultiplier"),
            new ConfigDescription("Skelespawn's walking speed relative to a normal player's walking speed. The killer cannot sprint.", new AcceptableValueRange<float>(0.5f, 5f)));
        BomberSecondsPerUse = config.Bind("Skelespawn", "BomberSecondsPerUse", ConfigDefaults.Get<int>("Skelespawn", "BomberSecondsPerUse"),
            new ConfigDescription("Seconds for one bomber head to come back (twice as fast in the killer room).", new AcceptableValueRange<int>(5, 120)));
        EverseerWalkSpeed = config.Bind("Everseer", "WalkSpeedMultiplier", ConfigDefaults.Get<float>("Everseer", "WalkSpeedMultiplier"),
            new ConfigDescription("Everseer's walking speed relative to a normal player's walking speed, with the mask closed. The killer cannot sprint.", new AcceptableValueRange<float>(0.5f, 5f)));
        LaserDamage = config.Bind("Everseer", "LaserDamage", ConfigDefaults.Get<int>("Everseer", "LaserDamage"),
            new ConfigDescription("Damage of each eye laser hit on a survivor (more in the killer room).", new AcceptableValueRange<int>(1, 200)));
        LaserRange = config.Bind("Everseer", "LaserRange", ConfigDefaults.Get<float>("Everseer", "LaserRange"),
            new ConfigDescription("How far the eye laser reaches, in metres, through walls.", new AcceptableValueRange<float>(5f, 60f)));
        LaserChargeSeconds = config.Bind("Everseer", "LaserChargeSeconds", ConfigDefaults.Get<float>("Everseer", "LaserChargeSeconds"),
            new ConfigDescription("Seconds the eye laser takes to build up after the mask opens, before E fires it.", new AcceptableValueRange<float>(0.1f, 5f)));
        LaserShots = config.Bind("Everseer", "LaserShots", ConfigDefaults.Get<int>("Everseer", "LaserShots"),
            new ConfigDescription("Laser shots a full mask charge holds. The charge drains and recharges at the Sword rates.", new AcceptableValueRange<int>(1, 20)));
        StaffSecondsPerUse = config.Bind("Everseer", "StaffSecondsPerUse", ConfigDefaults.Get<int>("Everseer", "StaffSecondsPerUse"),
            new ConfigDescription("Seconds for one Staff of Seeing charge to come back (twice as fast in the killer room).", new AcceptableValueRange<int>(5, 120)));
        EyeSightRange = config.Bind("Everseer", "EyeSightRange", ConfigDefaults.Get<int>("Everseer", "EyeSightRange"),
            new ConfigDescription("How far a planted eye sees survivors, in metres.", new AcceptableValueRange<int>(5, 60)));
        EyeHoldSeconds = config.Bind("Everseer", "EyeHoldSeconds", ConfigDefaults.Get<float>("Everseer", "EyeHoldSeconds"),
            new ConfigDescription("Seconds a survivor or bot must hold grab on an eye to destroy it.", new AcceptableValueRange<float>(0.5f, 20f)));
        ExtractionGoalHundreds = config.Bind("Extraction", "GoalPerPointInHundreds", ConfigDefaults.Get<int>("Extraction", "GoalPerPointInHundreds"),
            new ConfigDescription("Dollar value, in hundreds of dollars (100 means $10,000), of valuables that must be deposited into one extraction point to complete it.", new AcceptableValueRange<int>(10, 200)));
        MaxHealth = config.Bind("Survivor", "MaxHealth", ConfigDefaults.Get<int>("Survivor", "MaxHealth"),
            new ConfigDescription("Health every player starts a level with, and the most they can have.", new AcceptableValueRange<int>(1, 500)));
        HealthRegenPerSecond = config.Bind("Survivor", "RegenPerSecond", ConfigDefaults.Get<float>("Survivor", "RegenPerSecond"),
            new ConfigDescription("Host only. Health a living player regenerates per second. 0 turns regeneration off.", new AcceptableValueRange<float>(0f, 10f)));
        BoostSeconds = config.Bind("Survivor", "BoostSeconds", ConfigDefaults.Get<int>("Survivor", "BoostSeconds"),
            new ConfigDescription("A survivor who is revived, or hit down to half their maximum health or less, runs without tiring and cannot be tumbled by anything but themselves for this long. 0 turns the boost off.",
                new AcceptableValueRange<int>(0, 20)));
        EnergyPerSurvivorHit = config.Bind("Sword", "EnergyPerSurvivorHit", ConfigDefaults.Get<int>("Sword", "EnergyPerSurvivorHit"),
            new ConfigDescription("Host only. Energy (out of 100) the sword loses when it hits a survivor.", new AcceptableValueRange<int>(0, 100)));
        WeaponDamage = config.Bind("Sword", "DamagePerHit", ConfigDefaults.Get<int>("Sword", "DamagePerHit"),
            new ConfigDescription("Damage the killer's weapon deals to a survivor per hit.", new AcceptableValueRange<int>(1, 200)));
        EnergyDrainPerSecond = config.Bind("Sword", "EnergyDrainPerSecond", ConfigDefaults.Get<float>("Sword", "EnergyDrainPerSecond"),
            new ConfigDescription("Energy (out of 100) the weapon loses per second, held or not, on top of the normal loss per hit.", new AcceptableValueRange<float>(0f, 10f)));
        ChargeRate = config.Bind("Sword", "ChargePerSecond", ConfigDefaults.Get<int>("Sword", "ChargePerSecond"),
            new ConfigDescription("Energy the killer's weapon regains per second while the killer is in the killer room.", new AcceptableValueRange<int>(0, 50)));
        KillerFogStart = config.Bind("Vision", "KillerFogStart", ConfigDefaults.Get<int>("Vision", "KillerFogStart"),
            new ConfigDescription("Killer only. Distance at which the fog starts, the same in every room.", new AcceptableValueRange<int>(0, 200)));
        KillerFogEnd = config.Bind("Vision", "KillerFogEnd", ConfigDefaults.Get<int>("Vision", "KillerFogEnd"),
            new ConfigDescription("Killer only. Distance at which the fog is solid and the view ends, the same in every room. 0 gives the killer the game's own room fog.", new AcceptableValueRange<int>(0, 300)));
        Heartbeat = config.Bind("Chase", "Heartbeat", ConfigDefaults.Get<bool>("Chase", "Heartbeat"),
            "Survivors hear a heartbeat when the killer is near. Off: no heartbeat (the chase music still plays).");
        TimeLimitMinutes = config.Bind("Extraction", "TimeLimitMinutes", ConfigDefaults.Get<int>("Extraction", "TimeLimitMinutes"),
            new ConfigDescription("The survivors lose when this many minutes pass before they escape.", new AcceptableValueRange<int>(2, 30)));
    }
}
