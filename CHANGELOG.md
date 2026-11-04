# Changelog

## Unreleased

- "Host Hunted" goes straight to the lobby of a new game on the fastest region. There is no region list and no save list any more, so old saves cannot be loaded.
- Survivors have 50 stamina and sprint 20% faster. Bots run on the same stamina and speed as players.
- Settings with big values are whole numbers now, so the sliders move in steps of 1: the time limit, the killer's fog distances, the sword's energy per hit and charge rate, and the boost length. The extraction goal is a count of hundreds of dollars (`GoalPerPointInHundreds`, 100 is $10,000, steps of $100), which replaces `GoalPerPoint`: set it again if you had changed it. Health regeneration tops out at 10 per second.
- The "Dummy survivor" testing switch is now a **Bots** slider (0 to 5) that works with any number of players. Bots look like survivors (the game's player model in a random flat colour), walk and run with the player animations and footsteps, start far from the killer like a survivor, bring the valuables worth the walk to the extraction point closest to done, know how the killer's vision works and use stance to stay unseen, share sightings, flee along routes that cannot be cut off, hide in real hiding spots (the map's own hiding volumes, under furniture, alcoves) rather than in the open, avoid bomber heads and the killer room, and leave a real death head when killed, which anyone can carry to a truck to revive them. Everyone sees them (the host runs them). They take damage from everything that hurts players, tumble with real physics, and glow gold during the boost. They count as survivors: for the number of extraction points, for the killer's win (all of them dead as well) and for the survivors' win (the living ones in a truck, which they head for once the trucks open). With just the killer in the lobby, the game cannot be started without at least one bot.
- Revealing a survivor is now one effect that anything can use (the killer room today): a yellow shape seen through walls (only where something is in the way), a yellow dot on the killer's map, a deep chime from the survivor's direction, and a quiet hum from them for as long as they are revealed. The dot is smaller and yellow now.
- The killer no longer has night vision (and the two night-vision settings are gone), and still has no flashlight. The killer no longer sees their own red light; survivors still do.
- New: bomber heads. The killer has them in inventory slot 2 (a skull with dynamite, three charge bars): three uses, one comes back every 30 seconds, twice as fast in the killer room. The key puts a real bomber head in the killer's hands; letting go of it throws it. A thrown head hunts the nearest survivor at the killer's speed, and blows up 5 seconds after its fuse is lit, for triple the damage; one that never gets close blows up by itself after 20 seconds.
- The killer walks 25% slower while the sword is in their hands. Stowing the sword restores their full speed.

## 0.1.2

- The killer also wears the skull belt. Their skin is white and every cosmetic item on them is red.
- The game's seasonal events (Halloween) are always turned off.

## 0.1.1

A large update over 0.1.0. Everyone in the lobby must update, and MenuLib is now required.

- The killer is chosen with a checkbox next to each player in the lobby, using MenuLib.
- Main menu: Singleplayer, Public Game and Tutorial are hidden; Private Game and Join are renamed Host Hunted and Join Hunted.
- The killer's sword passes through players, valuables and carts, can never hit the killer, and needs less swing speed to register a hit.
- The killer cannot grab valuables, carts, monsters or heads, and no longer sees grab indicators on them.
- New vision rules: the killer's chance to notice a survivor depends on how near they are and how directly the killer looks at them, and on whether they stand, crouch or crawl. A survivor who takes damage is visible for at least 3 seconds.
- The killer looks like a white skeleton with glowing red eyes.
- The killer floats back out of holes, and map hazards such as the Museum robot ignore the killer.
- Many balance values moved out of the config into the mod, and the remaining settings load their defaults from an embedded file.
- Default map is a 4x4 grid. Monsters are off by default.
- Shorter default log.
