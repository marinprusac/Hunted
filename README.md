# Hunted

> **Early development.** This mod is being worked on actively and changes often. Expect rough edges, and check the changelog when you update.

A BepInEx mod for R.E.P.O. Install it from [Thunderstore](https://thunderstore.io/c/repo/p/Prussian/Hunted/) with a mod manager such as Gale.

An asymmetric mode for R.E.P.O., inspired by Dead by Daylight. One player is **the killer**: invulnerable, fast, huge, armed with a sword. Everyone else is a **survivor** who has to fill the extraction points with valuables and get out through a truck before the killer finds them.

**Everyone in the lobby needs Hunted and MenuLib, on the same version.** Only the host's settings count, so nobody else has to touch the config.

## How a round works

- **Pick the killer.** In the lobby, the host ticks the box next to a player's name. If nobody is ticked, a random player is the killer.
- **Everyone spawns on the map**, not in the truck. Survivors start well away from the killer. The killer starts in the **killer room**, the room in the middle of the map.
- **Extraction.** There are more extraction points than survivors, and all of them are open from the start. Survivors need as many completed points as there are survivors (the extras are spares). Fill a point with valuables as usual.
- **Escape.** Once enough extraction points are done, both trucks open. The survivors win when every living survivor is inside a truck.
- **The killer wins** when every survivor is dead, or when the time limit (15 minutes by default) runs out first.
- **Death heads and revives work as in the vanilla game**, so a dead survivor can still be brought back by a teammate.
- **Valuables.** Only small valuables spawn, and they respawn over time. Carts only appear at extraction points.
- **No monsters** by default. The game's seasonal events (Halloween) are always turned off.

## The killer

- Cannot be damaged, tumbled or killed. Cannot sprint or tumble. Is faster than a survivor and much bigger, but walks 25% slower while holding the sword (stow it to run at full speed).
- Carries a **sword** that cannot be dropped, only stowed. It passes through survivors' bodies, so swings are not blocked. It hurts survivors only, and does not damage valuables, carts or monsters. Hits cost sword energy, and the sword slowly drains on its own. The energy recharges while the killer is in the killer room.
- Has **bomber heads** in inventory slot 2 (key 2), shown as a skull with dynamite and three charge bars: three uses, and one comes back every 30 seconds (twice as fast in the killer room). Press the key and a real bomber head (the game's Bang monster) appears in the killer's hands, held like a survivor holds anything. Let go of it to throw it out: left mouse button, the key again, or another slot key. It always knows where the survivors are, walks towards the nearest one at the killer's walking speed, lights its fuse when it gets close, and blows up 5 seconds after its fuse is lit (and by itself after 20 seconds if it never gets close), for triple the usual damage. A head can be stunned or killed like any Bang. There can be as many on the map as the killer can afford.
- Cannot pick up valuables, carts, monsters or survivors' heads, and cannot enter the trucks. Monsters, the Museum's red-light-green-light robot and other map hazards ignore the killer.
- Has a steady fog distance in every room, a zoomed-out map and a red light. Has white skin, a red skeleton outfit (with the skull belt) and glowing red eyes.
- **In the killer room** the killer walks faster, hits harder and the lights are brighter. Nobody can hide there: a survivor inside is **revealed** to the killer, as a faint yellow shape seen through walls (only where something is in the way), a yellow dot on the killer's map, a chime from the survivor's direction when they are revealed, and a quiet hum from them that lasts as long as they are.
- If the killer falls into a hole, they float back out.

## How the killer finds survivors

The killer sees survivors badly. Walls don't matter, but distance and direction do:

- A **standing** survivor is always visible.
- A **crouching or tumbled** survivor becomes visible only after the killer looks towards them from close by for a moment. A **crawling** one takes much longer.
- The nearer a survivor is and the more directly the killer looks at them, the faster they are noticed. Beside or behind the killer, or farther than about 20 m, they are not noticed at all.
- A survivor who takes damage is visible to the killer for at least 3 seconds.

## Survivors

- Start with more health (200) and slowly regenerate it.
- **Boost:** a survivor who is revived, or hit down to half their health or less, runs without tiring and cannot be tumbled by anything but themselves for a few seconds.
- Cannot hide in the killer room: they are always visible to the killer there.

## Playing

1. Install the mod and its dependencies (a mod manager does this for you), then restart the game fully.
2. The main menu changes: **Singleplayer, Public Game and Tutorial are hidden** while Hunted is installed. **Private Game** becomes **Host Hunted** and **Join** becomes **Join Hunted**. To play the vanilla game, disable the mod.
3. The host creates a lobby, ticks the killer's box next to a player, and starts the game.

### Testing alone

When you are the only player in the lobby, nobody is the killer unless you tick your own box (unticked, you play as a survivor). A **Bots** slider (0 to 5) under the lobby's player list, available with any number of players, adds stand-in survivors everyone can see; the host runs them and the host's value is read when the level is set up. Bots count as survivors: they raise the number of extraction points that must be completed, the killer needs them dead as well to win, and they must be in a truck (they head for one as soon as the trucks open) for the survivors to win. A game with only the killer and no bots cannot be started. A bot has a survivor's body in one random colour, starts where a survivor would (far from the killer) and plays like a good survivor: it only knows what it sees, hears or another bot reports; it picks the stance that keeps the killer's eyes off it; it takes routes the killer cannot cut off and saves its stamina for a chase; it hides in real hiding spots (the map's own hiding volumes, under furniture, alcoves), crawling in where the walking floor ends, never in the open, keeps away from bomber heads, monsters and the killer room, and works with the other bots on the extraction point closest to done. A bot is a real player avatar that the host drives, so whatever hurts a player hurts it, and it tumbles and dies like one. It leaves a real death head anyone but the killer can carry, and other bots carry heads too: in an extraction point as it completes, or in a truck once they are open, the head brings its owner back at half health with the boost. Players cannot grab a bot's body.

## Settings

The config file is `BepInEx/config/Prussian.Hunted.cfg`, created on first launch. In a lobby the host's values replace everyone else's while they are connected. (Balance values such as the killer's vision rules are part of the mod and are not configurable.)

| Setting | Default | What it does |
|---|---|---|
| Killer / WalkSpeedMultiplier | 2 | Killer walking speed relative to a normal player |
| Extraction / GoalPerPointInHundreds | 100 | Dollar value that completes one extraction point, in hundreds (100 is $10,000) |
| Extraction / TimeLimitMinutes | 15 | The killer wins when this runs out |
| Survivor / MaxHealth | 200 | Health everyone starts with |
| Survivor / RegenPerSecond | 0.5 | Health regenerated per second (0 turns it off) |
| Survivor / BoostSeconds | 5 | Length of the boost (0 turns it off) |
| Level / Columns, Rows | 4, 4 | Size of the map's inner grid of rooms |
| Sword / DamagePerHit | 40 | Damage to a survivor per hit |
| Sword / EnergyPerSurvivorHit | 10 | Sword energy lost per hit (out of 100) |
| Sword / EnergyDrainPerSecond | 0.55 | Sword energy lost per second |
| Sword / ChargePerSecond | 10 | Sword energy regained per second in the killer room |
| Vision / KillerFogStart, KillerFogEnd | 5, 30 | The killer's fog distances |

## Compatibility and known issues

- Requires **BepInExPack** and **MenuLib**. Everyone must run the same version of Hunted.
- Early version. Most of it has been tested in small lobbies only, so expect rough edges. Mods that change the main menu, the lobby list, player cosmetics or level generation may conflict.
- Needs a local install of the game with BepInEx, so it cannot be used on cloud-streamed versions of the game.
- Please send bug reports with the host's `BepInEx/LogOutput.log`. The log is short by default. For more detail, add `Debug` to `LogLevels` under `[Logging.Disk]` in `BepInEx/BepInEx.cfg`.

## Building from source

The project targets `netstandard2.1` and compiles against a local install of the game and a BepInEx profile.

1. Install the .NET SDK and the game, and set up a BepInEx profile that contains MenuLib.
2. Create `Directory.Repo.props` in the folder **above** this one and set `GameDirectory` and `BepInExDirectory` to your own paths. The file isn't in the repo because the paths are specific to each machine.
3. Run `dotnet build -c Release`. The result is `bin/Release/netstandard2.1/Hunted.dll`.

`Tuning.json` (balance values) and `Defaults.json` (defaults for the config file) are embedded in the DLL.

## License

[MIT](LICENSE)
