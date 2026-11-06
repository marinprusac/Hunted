# Hunted

An asymmetric mode for R.E.P.O., inspired by Dead by Daylight. One player is **the killer**: invulnerable, fast and huge, playing one of two killers with their own powers. Everyone else is a **survivor** who has to fill the extraction points with valuables and escape in a truck.

![The killer in a hallway](https://raw.githubusercontent.com/marinprusac/Hunted/main/images/killer-hallway.jpg)

**Everyone in the lobby needs Hunted and MenuLib, on the same version.** Install it from [Thunderstore](https://thunderstore.io/c/repo/p/Prussian/Hunted/) with a mod manager such as Gale.

## How a round works

- The host is the killer, and picks which killer to play in the lobby. Survivors don't see the choice until the round starts.
- The killer starts in the **killer room** in the middle of the map, and survivors start far from it.
- Survivors need one completed extraction point per survivor, and there are spares. More survivors make a longer map.
- Once enough points are done, both trucks open. Survivors win when every living survivor is inside one. Nothing can hurt, push or tumble a survivor inside a truck.
- The killer wins by killing every survivor, or when the time limit runs out (15 minutes by default).
- Dead survivors leave a head that teammates can bring back, as in the normal game.
- Only small valuables spawn, and they respawn over time. There are no monsters by default.

## The killer

- Cannot be hurt or tumbled, cannot sprint, and cannot enter the trucks or pick up valuables, carts or heads.
- **Killer room:** the killer always sees its corners marked through walls, in a light shade of their colour. The killer is faster and hits harder here, and any survivor inside is revealed through walls and on the map, until 3 seconds after they leave.
- **Map:** zoomed out, but shows no heads, carts, items or valuables. Survivors see the killer room like any other room.
- **Falling:** the killer floats back out of holes.
- **Sound:** the killer's steps, voice and weapons are deep and carry far.

![A bomber head closing in](https://raw.githubusercontent.com/marinprusac/Hunted/main/images/bomber-head-closeup.jpg)

## Killers

- **Skelespawn:** a skeleton, white with red armour, red eyes and a red glow.
  - **Sword (key 1):** oversized and hurts only survivors, pushing them back. It runs on energy, which drains over time and with each hit, and recharges in the killer room. It can be stowed but not dropped. With the sword out Skelespawn walks at 75% of a survivor's speed, and stowing it gives back its full speed.
  - **Bomber heads (key 2):** three uses, and one comes back every 30 seconds. Throw one and it hunts the nearest survivor, then explodes for triple damage.
- **Everseer:** a druid in a mask and antlers, all purple, with purple eyes and a purple glow. Its killer room is purple. It has two abilities of its own:
  - **Mask (key 1):** one shot of the eye laser, and it only opens with a shot ready. As it opens the laser builds up, with the cart laser's build-up sound and the screen and eyes growing more purple. When it's ready there's a click and a strong purple throb, and **E** fires it at once: purple beams from its eyes for 2 seconds. They go through walls, hit hard, knock back and tumble. The mask closes when the shot is over. With the mask on the Everseer walks at 75% of a survivor's speed, and survivors see it glow purple, brighter as the laser gets ready. The slot holds 8 shots, one bar each. The charge drains slowly and recharges in the killer room, like the sword's.
  - **Wand (key 2):** the Staff of Seeing, a purple, quieter version of the game's Void staff that is always charged (no need to raise it). Where its shot meets a ceiling, wall or floor, a small eye is planted. Three charges, and one comes back every 30 seconds (twice as fast in the killer room). An eye is silent and shut until it sees a survivor, then it opens and watches them, revealing them to the Everseer until 3 seconds after it loses sight. It makes no sound and puts nothing on the spotted survivor's screen; they only know if they see it open. The Everseer always sees its eyes through walls, in purple.
  - Survivors destroy an eye by holding grab on it for 5 seconds, from any distance, as long as nothing is in the way. Bots destroy the eyes they notice.

## How the killer finds survivors

- **Standing** survivors are always visible, even through walls.
- **Crouching or tumbled** survivors appear after the killer looks towards them from close by for a moment. **Crawling** takes much longer.
- Survivors beside or behind the killer, or farther than about 20 m, are not noticed.
- A survivor who takes damage is visible for at least 3 seconds.

## Survivors

- Start with 200 health and slowly regenerate.
- **Boost:** when revived, or hit down to half health or less, a survivor runs without tiring and cannot be tumbled for a few seconds. Their screen turns gold while it lasts.
- **Heartbeat:** within about 24 m of the killer, survivors hear a heartbeat that speeds up as the killer gets closer.
- **Chase music:** when the killer has a survivor in sight up close, both of them hear a shock sound and then the chase music until the killer loses them.

## Bots

A slider in the lobby adds up to 5 bot survivors, with any number of players. They gather valuables, sneak, flee, hide, carry heads to safety and escape in the trucks. They get hurt and die like players, and the killer has to kill them too. Players cannot grab them.

## Playing

Singleplayer, Public Game and Tutorial are hidden while the mod is installed. Use **Host Hunted** or **Join Hunted** in the main menu, and disable the mod to play the normal game.

If you play alone, you are the killer and need at least one bot.

## Settings

The config file is `BepInEx/config/Prussian.Hunted.cfg`. In a lobby, the host's values apply to everyone.

| Setting | Default | What it does |
|---|---|---|
| Killer / WalkSpeedMultiplier | 2 | Killer walking speed relative to a normal player |
| Extraction / GoalPerPointInHundreds | 150 | Value that completes an extraction point, in hundreds of dollars (150 is $15,000) |
| Extraction / TimeLimitMinutes | 15 | The killer wins when this runs out |
| Survivor / MaxHealth | 200 | Health everyone starts with |
| Survivor / RegenPerSecond | 0.5 | Health regenerated per second (0 turns it off) |
| Survivor / BoostSeconds | 5 | Length of the boost (0 turns it off) |
| Sword / DamagePerHit | 35 | Damage to a survivor per hit |
| Sword / EnergyPerSurvivorHit | 10 | Sword energy lost per hit (out of 100) |
| Sword / EnergyDrainPerSecond | 0.55 | Sword energy lost per second |
| Sword / ChargePerSecond | 10 | Sword energy regained per second in the killer room |
| Vision / KillerFogStart, KillerFogEnd | 5, 30 | The killer's fog distances |

## Compatibility

- Requires **BepInExPack** and **MenuLib**. Mod managers also install **REPOConfig**, which lets you change the settings in game; Hunted works without it.
- Mods that change the main menu, the lobby list, player cosmetics or level generation may conflict.
- Please send bug reports with the host's `BepInEx/LogOutput.log`.

## Credits

- Menu music: "Ambient Horror Track 01" by Cleyton Kauffman (CC0, OpenGameArt).
- Skelespawn's chase music: "The Hunt" by Sudocolon (CC0, OpenGameArt).
- Everseer's chase music: "Hunter-class Lifeform" by Vitalezzz (CC0, OpenGameArt).
- Chase sting: the piano sting from "String and piano horror stings" by congusbongus (CC0, OpenGameArt).

## License

[MIT](LICENSE)
