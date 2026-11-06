# Changelog

## 0.3.1

- Everseer's laser does 30 damage instead of 40, reaches 22.5 m instead of 30, and takes 1.25 seconds to build up instead of 1.
- Everseer walks at 1.8 times a player's speed; Skelespawn stays at 2.
- New settings, adjustable in game with REPOConfig: each killer's walking speed, the bomber head and staff recharge times, and the laser's damage, range, build-up time and shots, and the eyes' sight range and hold time. `Killer / WalkSpeedMultiplier` is replaced by `Skelespawn / WalkSpeedMultiplier`; set it again if you had changed it.
- The killer hears their own sounds at normal volume; only survivors hear them louder. They still sound deeper to everyone.
- New setting `Chase / Heartbeat`: turn it off and survivors no longer hear the heartbeat.
- The chase sting and music start sooner once the killer has a survivor in sight.

## 0.3.0

- New killer: **Everseer**, a purple druid in a mask and antlers with its own chase music. The first killer is now called **Skelespawn**.
- Everseer's mask (key 1): one shot of an eye laser per opening. It builds up as the mask opens and fires with E once ready. It goes through walls, hits hard, knocks back and tumbles. While the mask is on, the Everseer's screen turns purple, it walks at 75% of a survivor's speed, and survivors see it glow purple.
- Everseer's wand (key 2): the Staff of Seeing, a purple, quieter Void staff that is always charged, whose shot plants a small eye where it lands. Eyes reveal the survivors they see, until 3 seconds after, and the Everseer sees them through walls. Survivors and bots destroy one by holding grab on it for 5 seconds, from any distance.
- A boosted survivor's screen turns gold.
- Survivors inside a truck can't be hurt, knocked back or tumbled.
- The host is always the killer, and picks the killer with a slider in the lobby. Survivors don't see which one.
- The killer's glow and the killer room take the killer's colour.
- Survivors' maps show the killer room like any other room.
- The killer always sees the killer room's corners through walls.
- No money count at the top of the screen when an extraction point is completed.
- A shock sound plays as a chase starts.
- With the sword out the killer walks at 75% of a survivor's speed, and a hit pushes the survivor back.
- Survivors in the killer room stay revealed for 3 seconds after leaving. The alert is a single bell that flows into the hum, and the hum fades out.
- Only the inventory slots in use are shown: two for the killer, none for survivors. Items in use stay in their slot, brighter and bigger.
- The killer's sounds, now including their weapons, are deeper and carry further.
- Mod managers also install REPOConfig, for changing settings in game. Hunted works without it.

## 0.2.1

- Works with R.E.P.O. v0.5.1: hosting a game no longer fails.
- New: survivors hear a heartbeat when the killer is near, and chase music plays when the killer has a survivor in sight.
- New main menu: a picture of the killer, a "Hunted" title and its own music, instead of the driving truck.
- The killer is white again, with red armour, teeth, belt and grabber, and looks the same in their Escape menu.
- New icon.

## 0.2.0

**Bots**
- Add up to 5 bot survivors with a slider in the lobby, with any number of players.
- Bots gather valuables and deliver them, sneak, run from the killer, hide, carry each other's heads to safety and escape in the trucks.
- They get hurt, tumble and die like players, and they count as survivors for the win conditions and the number of extraction points. Players cannot grab them.

**Killer**
- New: bomber heads, in inventory slot 2. There are three uses, and one comes back every 30 seconds (twice as fast in the killer room). Throw one and it hunts the nearest survivor, then explodes for triple damage.
- The sword is 1.7 times bigger, with longer reach, and now does 35 damage instead of 40.
- The killer walks 25% slower with the sword in their hands. Stowing it restores full speed.
- The killer is now 1.7 times a player's size instead of 2.
- The killer's view is brighter, but they no longer have night vision and no longer see their own red light.
- The killer's map no longer shows heads, carts, items or valuables, and looking at them no longer outlines them.
- The energy bar no longer floats over the sword on the killer's screen.
- Crouching survivors are noticed about twice as fast, and crawling ones slightly faster.

**Killer room**
- A survivor inside is now revealed by a yellow shape through walls, a yellow dot on the map, a chime from their direction and a quiet hum.
- The killer room's lights are less bright.

**Survivors**
- Survivors have 50 stamina and sprint 20% faster.

**Map**
- The map grows to fit the extraction points the survivors need.
- The map is 3 rooms wide with the killer room in the middle column. The Rows and Columns settings are gone.

**Lobby and settings**
- "Host Hunted" goes straight to a new lobby. There is no region or save list any more, so old saves cannot be loaded.
- The time limit, fog distances, sword energy and boost length sliders now move in whole steps.
- Each extraction point now needs $15,000 by default, up from $10,000.
- The extraction goal is now set in hundreds of dollars (`GoalPerPointInHundreds`). If you had changed `GoalPerPoint`, set it again.

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
