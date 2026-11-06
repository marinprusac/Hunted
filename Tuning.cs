using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;

namespace Hunted;

/// <summary>
/// Values for balancing and testing that players cannot change: Tuning.json is compiled into the DLL (rebuild after editing it).
/// Each value below has the same default as in the file, so a missing or broken file still leaves a working game.
/// </summary>
internal static class Tuning
{
    [Serializable]
    internal class VisionValues
    {
        /// <summary>Farther than this (metres) from the killer nothing charges a survivor's vision score. It is also where the proximity index reaches 0.</summary>
        public float ProximityRange = 20f;
        /// <summary>Score per second for a survivor dead centre in the killer's view and right next to them. It is multiplied by how centred they are (the dot product, squared: 0 to 1, 0 when to the side or behind) and by the proximity index, (1 - distance / range) squared.</summary>
        public float ChargePerSecond = 8f;
        /// <summary>The charge has to beat this (score per second) for the score to build; it builds by the difference.</summary>
        public float DecayPerSecond = 1f;
        /// <summary>When the charge does not beat DecayPerSecond, the score drains at this rate (less the little charge there is).</summary>
        public float FastDecayPerSecond = 3f;
        /// <summary>The score at which a crouching (or tumbled) survivor becomes visible, and a crawling one. Standing survivors need none (always visible).</summary>
        public float CrouchThreshold = 1.5f;
        public float CrawlThreshold = 9f;
        /// <summary>The score cannot rise above the threshold by more than this many seconds of decay, and also how long a visible survivor stays visible after getting harder to see (a stance change).</summary>
        public float RevealLingerSeconds = 1f;
        /// <summary>A survivor that takes damage is visible to the killer for at least this long.</summary>
        public float HitRevealSeconds = 3f;
    }

    [Serializable]
    internal class KillerRoomValues
    {
        /// <summary>How many times as strong and as far-reaching the killer room's (red) lights are.</summary>
        public float LightIntensityMultiplier = 3f;
        public float LightRangeMultiplier = 3f;
        /// <summary>After a survivor enters the room, the alert for the killer cannot go off again for this long.</summary>
        public float AlertCooldownSeconds = 3f;
        /// <summary>About how often a survivor in the room calls the monsters to where they stand.</summary>
        public float MonsterCallIntervalSeconds = 1f;
        /// <summary>How big the yellow dot of a revealed survivor is on the killer's map (1 is the picture's full size, a whole room's width).</summary>
        public float DotScale = 0.3f;
        /// <summary>How solid the yellow shape of a revealed survivor is where it shows through a wall (0 is invisible, 1 is solid).</summary>
        public float XrayAlpha = 0.4f;
        /// <summary>How loud the hum that follows a revealed survivor is (the chime is at the full 1).</summary>
        public float RevealHumVolume = 0.2f;
        /// <summary>A revealed survivor stays revealed for this long after leaving the killer room.</summary>
        public float RevealLingerSeconds = 3f;
    }

    [Serializable]
    internal class RoundValues
    {
        /// <summary>The round rules (win, loss, clock) only start this long after the level begins, while players are still being placed.</summary>
        public float StartDelaySeconds = 15f;
        /// <summary>How long every survivor must be dead, or all living ones inside a truck, before the round ends.</summary>
        public float KillerWinHoldSeconds = 3f;
        public float SurvivorEscapeHoldSeconds = 2f;
        /// <summary>How long the result is shown before the level changes.</summary>
        public float ResultSeconds = 4f;
    }

    [Serializable]
    internal class ExtractionValues
    {
        /// <summary>Extraction points the survivors must complete beyond one per survivor (survivors + this).</summary>
        public int ExtraRequiredPoints = 0;
        /// <summary>Extraction points on the map beyond the number the survivors must complete.</summary>
        public int ExtraSpawnedPoints = 2;
    }

    [Serializable]
    internal class MonsterValues
    {
        /// <summary>How many level 3 monsters and how many level 2 monsters spawn (2 means 2 of each). 0: no monsters at all.</summary>
        public int MonstersPerTier = 0;
    }

    [Serializable]
    internal class SwordValues
    {
        /// <summary>How strong the sword's tumble on a hit survivor is compared to the game's (1 = a 1 s tumble with a push and spin). Scales the time, the push and the spin.</summary>
        public float TumbleMultiplier = 0.5f;
        /// <summary>How hard a hit pushes the survivor away (the game's sword does not push at all; the Everseer's laser pushes with 30).</summary>
        public float PushForce = 8f;
        /// <summary>How fast the sword must move to count as a swing. Replaces the game's value (1.15 for the sword); lower is easier to swing.</summary>
        public float SwingDetectMultiplier = 0.5f;
        /// <summary>The killer's walking speed while the sword is in their hands, as a multiple of a survivor's walking speed (1 = the same as a survivor; the killer room's boost still adds to it).
        /// Stowing the sword gives the killer their own, faster walk back (Skelespawn / WalkSpeedMultiplier and Everseer / WalkSpeedMultiplier in the config).</summary>
        public float HeldSpeedMultiplier = 0.75f;
        /// <summary>Size of the sword compared to the game's (1 = the game's). Its hit box and reach grow with it, and everyone sees the same size.</summary>
        public float Scale = 1.7f;
    }

    [Serializable]
    internal class MapValues
    {
        /// <summary>How much further the map is zoomed out than the game's own (1 = vanilla), for the killer and for the survivors.</summary>
        public float KillerZoomOut = 2.5f;
        public float SurvivorZoomOut = 1.5f;
    }

    [Serializable]
    internal class ValuablesValues
    {
        /// <summary>Valuables that spawn in the killer room are worth this many times as much.</summary>
        public float KillerRoomValueMultiplier = 3f;
        /// <summary>A new small valuable spawns on a random empty slot every this many seconds.</summary>
        public float RespawnSeconds = 20f;
        /// <summary>Percentage of the map's small-valuable slots that are filled when the level starts.</summary>
        public int InitialFillPercent = 60;
        /// <summary>Valuables never respawn this close to a player (metres).</summary>
        public float MinPlayerDistance = 15f;
    }

    [Serializable]
    internal class KillerValues
    {
        /// <summary>Size of the killer compared to a normal player: the body (as the others see it), the collision and the eye height. Large values do not fit through the map's doors and corridors.</summary>
        public float Scale = 1.7f;
        /// <summary>How much brighter the picture is on the killer's screen: added to the camera's exposure (in stops) and brightness (-100 to 100). The colour grading mode uses one of them.</summary>
        public float ExposureBoost = 0.5f;
        public float BrightnessBoost = 15f;
        /// <summary>The killer's eyes glow red: how bright the eyes are (emission), and how far and how strongly they light up their surroundings for the others.</summary>
        public float EyeGlow = 6f;
        public float EyeLightRange = 4f;
        public float EyeLightIntensity = 1.5f;
        /// <summary>How much louder the killer's sound effects are, how much further they carry, and their pitch (below 1 is deeper).</summary>
        public float SoundVolumeMultiplier = 2f;
        public float SoundRangeMultiplier = 2f;
        public float SoundPitchMultiplier = 0.5f;
        /// <summary>The red light around the killer: how far it reaches (metres; 0 turns it off) and how bright it is.</summary>
        public float RedLightRange = 30f;
        public float RedLightIntensity = 1.2f;
        /// <summary>While the killer is in the killer room, both their sword damage to survivors and their walking speed (on top of the normal killer speed) are multiplied by this.</summary>
        public float KillerRoomEmpowerment = 1.5f;
    }

    [Serializable]
    internal class LevelValues
    {
        /// <summary>How full the map's inner grid is (40 to 100). Higher means a denser map with more connections and loops.</summary>
        public int GridFillPercent = 80;
        /// <summary>Columns of rooms in the map's inner grid. The extraction rooms and dead ends sit in an extra column on each side.</summary>
        public int Columns = 3;
        /// <summary>Rows of rooms a map has at least. It gets more when the extraction rooms need them.</summary>
        public int MinRows = 4;
    }

    [Serializable]
    internal class RescueValues
    {
        /// <summary>The killer has fallen into a hole when they have been in the air for AirSeconds and are FallDepth metres below where they last stood.</summary>
        public float FallDepth = 4f;
        public float AirSeconds = 1.2f;
        /// <summary>How fast (m/s) the killer floats back up to where they last stood safely, and when to give up floating and put them there.</summary>
        public float FloatSpeed = 6f;
        public float TeleportAfterSeconds = 6f;
    }

    [Serializable]
    internal class SpawnValues
    {
        /// <summary>Survivors spawn at least this far (metres) from the killer, when the map allows.</summary>
        public float MinKillerDistance = 40f;
    }

    [Serializable]
    internal class BoostValues
    {
        /// <summary>A hit that leaves a survivor alive at or below this fraction of their maximum health starts the boost.</summary>
        public float HealthFraction = 0.5f;
    }

    [Serializable]
    internal class BangerValues
    {
        /// <summary>How many bomber heads (the game's Bang monster) the killer can have ready at most, and how long one takes to come back (the killer room recharges faster).</summary>
        public int MaxUses = 3;
        public float RoomRechargeMultiplier = 2f;
        /// <summary>How much harder a bomber head's explosion hits compared to the game's, and how many seconds its fuse burns (the game's is 15).</summary>
        public float DamageMultiplier = 3f;
        public float FuseSeconds = 5f;
        /// <summary>A thrown head that has not lit its fuse by then blows up on its own; lighting the fuse stops this.</summary>
        public float SelfDestructSeconds = 20f;
        /// <summary>How firmly the killer's hands hold a head (1 is a normal player's grip), for the head's weight and for keeping it pointing straight.</summary>
        public float HoldStrength = 3f;
        public float HoldTorque = 3f;
        /// <summary>A bomber head lights its fuse once it is this close to the survivor it is going for; until then it just walks towards the nearest survivor.</summary>
        public float IgniteDistance = 6f;
    }

    [Serializable]
    internal class EverseerValues
    {
        /// <summary>How fast the Everseer walks while its mask is on, as a multiple of a survivor's walking speed.</summary>
        public float MaskSpeedMultiplier = 0.75f;
        /// <summary>How hard a hit knocks a survivor back, how hard it throws them into a tumble, and for how long.</summary>
        public float LaserKnockback = 30f;
        public float LaserTumbleForce = 25f;
        public float LaserTumbleSeconds = 0.5f;
        /// <summary>How much thicker the beams are than the cart laser's, for everyone but the killer.</summary>
        public float LaserThickness = 1f;
        /// <summary>How much brighter the beams' lights are than the cart laser's, for everyone but the killer.</summary>
        public float LaserLight = 2f;
        /// <summary>How wide an extra solid line down the middle of each beam is, in metres (times the thickness above); 0 for none, the cart laser's beam as it is.</summary>
        public float LaserCoreWidth = 0f;
        /// <summary>How long the laser fires, and how often a survivor standing in the beam is hit again. Its damage, range and build-up are settings (HuntedConfig).</summary>
        public float LaserShotSeconds = 2f;
        public float LaserHitCooldown = 0.3f;
        /// <summary>How the beams look on the killer's own screen, compared to what everyone else sees: thinner and mostly see-through, so they do not block the view.</summary>
        public float LaserOwnThickness = 1f;
        public float LaserOwnAlpha = 0.6f;
        /// <summary>The wand (the game's Void staff): how many charges it holds, and how long one takes to come back (faster in the killer room).</summary>
        public int WandMaxUses = 3;
        public float WandRoomRechargeMultiplier = 2f;
        /// <summary>How loud the wand is, and how bright its flashes, compared to the game's Void staff.</summary>
        public float WandVolume = 0.35f;
        public float WandFlash = 0.3f;
        /// <summary>A survivor can touch (and so destroy) an eye from any distance, by grabbing at it; this is how big an eye counts for that, in metres.</summary>
        public float EyeTouchRadius = 0.3f;
        /// <summary>A bot notices an eye after it has had it in plain view in front of it, within this many metres, for about this many seconds (give or take 40%).</summary>
        public float BotEyeNoticeRange = 12f;
        public float BotEyeNoticeSeconds = 3f;
        /// <summary>How big a planted eye is, compared to the game's ceiling eye.</summary>
        public float EyeScale = 0.5f;
    }

    [Serializable]
    internal class SurvivorValues
    {
        /// <summary>A survivor's stamina (each stamina upgrade adds 10 on top), and how much faster they sprint than the game's sprint.</summary>
        public float Stamina = 50f;
        public float SprintSpeedMultiplier = 1.2f;
    }

    [Serializable]
    internal class BotValues
    {
        /// <summary>The bots (the stand-ins for survivors, set under the lobby's player list) walk about like real survivors. Off leaves them standing where they are placed.</summary>
        public bool Moves = true;
        /// <summary>About how long it hides, crouched or crawling, after it got away.</summary>
        public float HideSeconds = 10f;
        /// <summary>Like a survivor, it goes for the valuables lying about, carries them to an extraction point and sets them down in it. Off: it only walks about.</summary>
        public bool Scavenges = true;
        /// <summary>It only notices the killer like a survivor does: by sight (in front of it, in the open, within SightRange metres) or by sound (within HearRange metres, even behind a wall). It remembers where it last noticed them for MemorySeconds.</summary>
        public float SightRange = 30f;
        public float HearRange = 16f;
        public float MemorySeconds = 20f;
    }

    [Serializable]
    internal class ChaseValues
    {
        /// <summary>The terror radius (metres): a living survivor this close to the killer hears a heartbeat, through walls. The killer never hears it.</summary>
        public float TerrorRadius = 24f;
        /// <summary>Seconds between heartbeats at the edge of the terror radius, and right next to the killer.</summary>
        public float HeartbeatSlowSeconds = 1.1f;
        public float HeartbeatFastSeconds = 0.45f;
        /// <summary>How loud the heartbeat is right next to the killer (it is a third of that at the edge of the radius).</summary>
        public float HeartbeatVolume = 0.9f;
        /// <summary>A chase: the killer has a survivor in the open, within ChaseRange metres and in front of them (the cosine of the angle off their facing is at least ChaseViewDot).
        /// It starts once that has lasted ChaseStartSeconds and ends ChaseLoseSeconds after it stopped. Both the killer and the chased survivor hear the chase music.</summary>
        public float ChaseRange = 16f;
        public float ChaseViewDot = 0.5f;
        public float ChaseStartSeconds = 0.35f;
        public float ChaseLoseSeconds = 6f;
        /// <summary>The chase music's volume, and how long it takes to come in and to die away.</summary>
        public float MusicVolume = 1f;
        public float FadeInSeconds = 0.3f;
        public float FadeOutSeconds = 3f;
        /// <summary>A chase that starts within this many seconds of the music dying away picks the music up where it stopped, instead of from the top.</summary>
        public float ResumeWithinSeconds = 20f;
        /// <summary>How loud the shock sound is that plays as a chase starts.</summary>
        public float StingVolume = 1f;
    }

    [Serializable]
    private class Data
    {
        public ChaseValues Chase = new();
        public SurvivorValues Survivor = new();
        public BotValues Bots = new();
        public BangerValues Banger = new();
        public EverseerValues Everseer = new();
        public VisionValues Vision = new();
        public KillerRoomValues KillerRoom = new();
        public BoostValues Boost = new();
        public RoundValues Round = new();
        public ExtractionValues Extraction = new();
        public MonsterValues Monsters = new();
        public SwordValues Sword = new();
        public MapValues Map = new();
        public ValuablesValues Valuables = new();
        public KillerValues Killer = new();
        public LevelValues Level = new();
        public RescueValues Rescue = new();
        public SpawnValues Spawn = new();
    }

    private static Data _data = new();

    internal static VisionValues Vision => _data.Vision;
    internal static ChaseValues Chase => _data.Chase;
    internal static KillerRoomValues KillerRoom => _data.KillerRoom;
    internal static BoostValues Boost => _data.Boost;
    internal static BangerValues Banger => _data.Banger;
    internal static EverseerValues Everseer => _data.Everseer;
    internal static SurvivorValues Survivor => _data.Survivor;
    internal static BotValues Bots => _data.Bots;
    internal static RoundValues Round => _data.Round;
    internal static ExtractionValues Extraction => _data.Extraction;
    internal static MonsterValues Monsters => _data.Monsters;
    internal static SwordValues Sword => _data.Sword;
    internal static MapValues Map => _data.Map;
    internal static ValuablesValues Valuables => _data.Valuables;
    internal static KillerValues Killer => _data.Killer;
    internal static LevelValues Level => _data.Level;
    internal static RescueValues Rescue => _data.Rescue;
    internal static SpawnValues Spawn => _data.Spawn;

    internal static void Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hunted.Tuning.json");
            if (stream == null)
            {
                Hunted.Logger.LogWarning("Tuning.json is not in the DLL; using the built-in defaults.");
                return;
            }
            using var reader = new StreamReader(stream);
            var loaded = new Data();
            // Newtonsoft, not Unity's JsonUtility: JsonUtility silently left every value at its default here (the log showed the killer at the
            // built-in scale of 1.4 while the file said 2).
            JsonConvert.PopulateObject(reader.ReadToEnd(), loaded);
            _data = loaded;
            Hunted.Logger.LogInfo($"Tuning values loaded (killer scale x{loaded.Killer.Scale:0.##}, sword held speed x{loaded.Sword.HeldSpeedMultiplier:0.##} of a survivor, bomber head damage x{loaded.Banger.DamageMultiplier:0.##}).");
        }
        catch (Exception e)
        {
            Hunted.Logger.LogError($"Could not read Tuning.json, using the built-in defaults: {e.Message}");
        }
    }
}
