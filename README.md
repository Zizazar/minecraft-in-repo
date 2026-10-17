# Minecraft Player in R.E.P.O.

A BepInEx plugin that turns the R.E.P.O. player into a Minecraft player: Minecraft first-person hands and
items, the Minecraft HUD and inventory with an off hand, mining valuables with a pickaxe into dropped items,
placing them back, Minecraft melee against R.E.P.O. enemies, and Minecraft items: TNT with flint and steel and
fire, a bow and arrows, a crossbow that can fire exploding firework rockets, a fishing rod that pulls valuables and
enemies to you, torches that light the level, ender pearls and a shield.

AI-built: written by Claude Code (Opus 5.5) for vusmedro.

## Needs
- R.E.P.O. (Steam) with BepInEx 5 (r2modman's BepInExPack).
- Minecraft Java Edition installed with the official launcher (`%APPDATA%\.minecraft`) or Prism Launcher
  (`%APPDATA%\PrismLauncher`), with at least one version launched once (so its jar and asset index are on disk).
  The mod reads the Steve skin, item sprites, GUI, font and sounds from there at runtime, using the newest release
  found in either. Without it, plain stand-in art is used and there are no sounds. For a portable Prism install or
  another folder, set `Directory` in the config to the launcher's data folder (the one with `libraries`, `meta` and
  `assets`).
- Singleplayer, or a lobby whose host also has the mod. R.E.P.O. runs damage and physics on the host, so when you join a
  host without the mod the visuals work but hitting enemies, mining, placing, grenades and guns don't.

## Controls
| Key | Minecraft | Notes |
|---|---|---|
| LMB (hold) on a valuable/item | mine it | Cracks, then it drops as a spinning item. A pickaxe is much faster than a hand. |
| LMB on an enemy | attack | Attack cooldown, crits while falling, sprint knockback, sword sweep. |
| Walk over a dropped item | pick up | Goes into the hotbar/inventory with the pop sound. |
| RMB with a stack, aimed at a surface | place | Hold to keep placing. Shift + RMB places even onto objects. |
| RMB (hold, release) with a grenade | throw | Like a bow: the longer you hold (up to 1 s), the harder it flies. Shift + RMB places it instead. |
| RMB (hold, release) with a bow | draw and shoot | Needs arrows (off hand first, then anywhere in the inventory). Full draw after 1 s = crit arrow. Arrows stick and can be picked back up. |
| RMB (hold) with a crossbow | load | Takes 1.25 s; loads by itself a moment later or on release. Ammo: an arrow or firework rocket in the off hand, otherwise arrows from the inventory. |
| RMB with a loaded crossbow | fire | Arrows fly faster than a bow's and always crit. A firework rocket explodes where it hits (or after ~20-30 m) and hurts everything within 3 m it can see, you included. |
| RMB with a fishing rod | cast / reel in | The bobber hooks the first enemy, player, valuable, item or dropped item it touches. Reeling in yanks it toward you (valuables don't take impact damage from the yank). Switching away from the rod reels in empty. |
| RMB with a firework rocket, aimed at a surface | launch | It climbs and bursts. |
| RMB with TNT / torch | place a block | TNT snaps onto other TNT. Torches go on floors and walls. |
| RMB with flint and steel | ignite | On TNT: lights it (4 s fuse). On a floor: starts a fire. |
| RMB with an ender pearl | throw | You teleport where it lands (2.5 hearts of damage, 1 s cooldown). |
| RMB (hold) with a shield | block | Blocks attacks and explosions from the front. Works from either hand. |
| RMB with the flashlight | light on/off | R.E.P.O.'s flashlight is now this item; hold it in either hand. |
| F | swap main hand and off hand | The off-hand slot is left of the hotbar and next to the player in the inventory. |
| LMB on TNT / torch / fire | break / put out | TNT and torches drop as items. |
| RMB on a R.E.P.O. button | press it | The truck screen, the extraction point button. Comes before any held item. |
| RMB otherwise | R.E.P.O. grab beam | Doors, carts, enemies, using items. |
| R | rotate grabbed object | Replaces R.E.P.O.'s RMB rotate. |
| 1-9 / mouse wheel | hotbar slot | The wheel still pushes/pulls while grabbing. |
| Q / Ctrl+Q | drop one / drop stack | |
| X | R.E.P.O. tumble | Moved off Q. |
| E | inventory | At the cosmetic token machine E is R.E.P.O.'s interact instead. Click, right-click (split/one), shift-click (move), F on a slot (swap it with the off hand), click outside to drop. Esc or E closes it. |
| F5 | third person (back / front) | |

Players keep the normal R.E.P.O. robot body in third person and in the inventory preview
(`ReplaceWithSteve = true` in the config turns everyone into a blocky Steve instead).

The robot (or Steve) holds your main-hand item in its right hand and your off-hand item in its left, in F5, in the
inventory preview and for other players with the mod. The arms pose like Minecraft's player model: the item pose, the
swing, drawing a bow, charging and aiming a crossbow, raising a shield, winding up a grenade throw; R.E.P.O. guns and
the flashlight point where you look. A flashlight or torch in another player's hand lights the level for you too.
`HeldItemScale` sets the size of held items on the robot.

HUD: hearts = R.E.P.O. health, hunger = stamina, XP level = thousands of dollars you carry. The sidebar
shows the level's goal, extraction progress and what you carry.
R.E.P.O.'s item prompts (the name of what you look at or hold, a valuable's value, a shop price, a cart's mode) show on
Minecraft's action bar above the hotbar. Shop goods you picked up but haven't paid for yet have a red frame in their slot,
and their tooltip shows the price; put them in the truck to buy them.

Durability works like R.E.P.O.'s weapons: each slot shows R.E.P.O.'s yellow battery, and tools and weapons last about
as long as R.E.P.O.'s (the sword lasts 20 enemy hits, the pickaxe 40 objects). An empty item doesn't break. It stops
working (an empty pickaxe or sword works like a bare hand) until you stand at the truck's charging station holding it, which charges
the items in your hands (main hand and off hand), R.E.P.O. items included. `DurabilityScale` in the config makes items last longer.

Starter kit (each level, consumables topped up): diamond pickaxe and sword, bow, 64 arrows, 16 TNT, flint and
steel, 64 torches, 16 ender pearls, a crossbow, 16 firework rockets, a fishing rod, and a shield in the off hand. With
`ShopItems` on (the default) only the pickaxe, sword and flashlight are given and the rest is bought in the shop. The
flashlight item is always given.

Explosions follow Minecraft's TNT (power 4): they hurt and throw enemies and players (players tumble), push
valuables, carts, doors and dropped items, set off nearby TNT, and knock out torches. Fire burns out after a
few seconds, sets enemies and you on fire, and lights TNT next to it. Torches, fire and a torch held in either
hand light up the level.

## Build
```
dotnet build src/MinecraftRepo -c Release -p:RepoDir="<R.E.P.O. folder>" -p:BepInExDir="<profile>\BepInEx"
```
Copy `MinecraftRepo.dll` to `<profile>\BepInEx\plugins\MinecraftRepo\`.

Config: `BepInEx\config\vusmedro.minecraftrepo.cfg` (damage scale, mining speed, block size, max lights,
Steve size, GUI scale...).

## Notes
- No Minecraft files are in this repository or the release; everything comes from your own install.
- The Minecraft items in your inventory (tools, TNT, bows, arrows, torches, pearls, shields, the flashlight, with
  their counts and durability) are saved with your R.E.P.O. save, in `saves\<save>\<save>.minecraft.json` next to
  R.E.P.O.'s own file. They are written whenever R.E.P.O. saves (between levels) and come back when you load that
  save, so they always match its progress: quitting in the middle of a level takes you back to the last save, items
  included. A new save starts empty, a lost run loses them, and restoring a backup restores its inventory. In someone
  else's lobby (when the host has the mod too) your items are saved on your own PC, in
  `minecraft_joined\<host>_<save>.minecraft.json` under R.E.P.O.'s data folder, whenever the host's game saves, and come
  back when you join that save again.
- Objects in your inventory are R.E.P.O.'s real objects parked out of the world, so their value is kept.
  They are lost if the level ends while you still carry them, like anything not extracted.
- Multiplayer: TNT (placed, lit and exploding), torches, fire, arrows and dropped items are shared, so every player with the mod
  sees and hears them and can break them. A dropped item or stuck arrow goes to the nearest player standing on it who has room,
  like Minecraft; the host decides who that is, so nothing can be picked up twice. Valuables you pick up from another player's
  drop can be placed back (the host moves them for you). The host decides explosions and arrow hits on enemies; a player is hurt
  by arrows on their own PC, so their shield counts. Ender pearls, firework rockets and the fishing bobber still exist only on
  your PC: other players see their effects (damage, objects flying) but not the things themselves. What each player holds,
  and what they are doing with it, is shared too, so their model holds it; R.E.P.O.'s own flashlight is hidden on modded
  players (it is an item for them).
