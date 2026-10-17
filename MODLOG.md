# MODLOG: Minecraft player in R.E.P.O.

## Goal (from the user)
Replace the R.E.P.O. player with a Minecraft player: a Minecraft-style inventory that holds R.E.P.O. items,
Minecraft interaction (mine valuables with a pickaxe, they drop as Minecraft item entities, pick them up,
place them), and Minecraft-style melee against R.E.P.O. enemies.

## Recon (2026-10-10)
- R.E.P.O. Steam app 3241660, `N:\SteamLibrary\steamapps\common\REPO`, Unity 2022.3.67f2, Mono backend.
- No anti-cheat. Multiplayer goes through Photon; this mod is for singleplayer or a lobby the user hosts.
- Loader: BepInEx 5 via r2modman (`%APPDATA%\r2modmanPlus-local\REPO\profiles\<profile>\BepInEx`).
  Doorstop proxy (`winhttp.dll`) is already in the game folder.
- Guest assets: the user's own Minecraft Java install, `%APPDATA%\.minecraft`. Version 1.21 has both its
  jar and its asset index (17); 1.21.5 has only a json. The mod picks the newest version with both.
- Prism Launcher (`%APPDATA%\PrismLauncher`) is searched too. Its layout: client jar at
  `libraries/com/mojang/minecraft/<v>/minecraft-<v>-client.jar`, version json (with `assetIndex.id`) at
  `meta/net.minecraft/<v>.json`, indexes and objects in `assets/` like vanilla. On this PC it has 1.21.8 (index 26),
  which `auto` now picks over vanilla 1.21. Every texture and sound path the mod reads is in 1.21.8 except the empty
  shield slot, which moved from `item/empty_armor_slot_shield` to `gui/sprites/container/slot/shield` in 1.21.2;
  both are tried.
- Decompiled `Assembly-CSharp.dll` with ilspycmd 9.1 into the session scratchpad (never into this repo).

## Route
Pattern 1 / subsystem transplant from the mashup skill: Minecraft doesn't run. The Minecraft player rules
(hotbar + 27-slot inventory, mining progress with crack stages, item entities with bob/spin/pickup delay,
placing, attack cooldown, crits and knockback) are rebuilt in a BepInEx plugin. Textures and sounds are
read at runtime from the user's own Minecraft install; nothing from Minecraft ships with the mod.
Running Minecraft beside R.E.P.O. (passthrough) would add a second process and a bridge for nothing:
the user wants the rules and the look, not Minecraft's simulation.

## Key R.E.P.O. hooks (from the decompile)
- Hiding an object while it is "in the inventory": `PhysGrabObject.OverrideDeactivate(t)` moves it to
  `AssetManager.physDisabledPosition` (0, 3000, 0), kinematic, no collisions. R.E.P.O. uses the same call
  for equipped items. `OverrideDeactivateReset()` + `Teleport()` brings it back.
- Enemy damage: `EnemyHealth.Hurt(int, Vector3)`, master/singleplayer only. Knockback through
  `Enemy.Rigidbody.rb`.
- Input: `InputManager.KeyDown/KeyHold/KeyUp(InputKey)`, patched so LMB no longer grabs, RMB grabs
  (Minecraft "use"), R rotates, Q drops, 1-9 select hotbar slots.
- First-person body: the local avatar renders `ShadowsOnly`; R.E.P.O. hides it the same way.

## Log
- 2026-10-10: recon, decompile, route chosen, project scaffolded.
- 2026-10-10: user asked for the full Minecraft UI: hotbar, hearts, hunger, XP, crosshair + attack
  indicator, held-item name, E inventory with 3D player preview and tooltips, scoreboard sidebar for the
  R.E.P.O. goal. R.E.P.O.'s health/stamina/inventory/goal/haul HUD and aim dot are hidden.
- 2026-10-10: first build clean. New r2modman profile `MinecraftRepo` (BepInEx 5.4.21 + this plugin).
  Launched via `steam -applaunch 3241660 --doorstop-enable true --doorstop-target <profile preloader>`.
  Log: plugin loads, reads Minecraft 1.21 jar, `Standard` shader available. The user is play-testing in game;
  not yet verified: hand pose, Steve orientation on the avatar, crack overlay, icon renders, placement.
- 2026-10-10: first play-test feedback (user screenshot) and fixes:
  - Body: user wants the normal R.E.P.O. robot in F5 and the inventory. Steve is now opt-in
    (`ReplaceWithSteve`, default off). The inventory preview renders the local robot alone (layer 31,
    shadows On for one camera render, then restored) and orbits toward the mouse.
  - Q dropped and tumbled: the drop ran in McPlayer.Update, then PlayerController read Tumble later in the
    same frame with an empty hand, so the "hand not empty" override no longer applied. Tumble is now always
    remapped to X.
  - Tools duplicated (hotbar full of swords): ItemEntity treated a tool's Count (always 1) as a leftover
    after Inventory.Add, put the entity back and picked it up again every second. Now uses Add's return
    value. Dropped tools despawn after 5 minutes like Minecraft; valuables don't (they're money).
  - Tool pose: replaced guessed Euler angles with a basis mapping (sprite diagonal -> blade direction,
    sprite face -> view-facing normal), grip at the handle pixel, smaller (0.32 m).
  - Grenades (ItemGrenade): RMB charges like a bow (full at 1 s), release throws at 3-18 m/s and arms it via
    ItemToggle.ToggleItem + SetActivator. Shift+RMB places. Shift replaces Ctrl as the force-place key.
  - Esc with the inventory open opened the pause menu: GameDirector reads InputKey.Menu in the same frame.
    Menu/Back are now swallowed while the inventory is open and for one frame after it closes.
- 2026-10-10: user asked for the Minecraft off hand, the flashlight as a separate item, TNT + flint and steel
  with fire, bow and arrows, torches with light, ender pearls, a shield, explosions that hurt/throw mobs and
  players and push items, and lighting from torches and fire.
  - Items: `ItemKind.Mc` virtual stacks (id + count) next to the R.E.P.O.-object stacks; slot 40 = off hand,
    F swaps. RMB follows Minecraft's use order (main item use, block interaction = R.E.P.O. grab, use on
    target, then off hand).
  - Flashlight: `FlashlightController.hideFlashlight` keeps R.E.P.O.'s left-hand flashlight away; the item
    copies its spotlight settings and model (cloned through local matrices, since the hidden flashlight's
    root is scaled to 0).
  - World: TNT/torch/fire blocks (`McWorld.cs`), primed TNT as a rigidbody (flash, swell, 4 s fuse),
    Minecraft explosion formula with line-of-sight exposure, burning status, arrows (gravity, drag, crit,
    stick, pickup, catch fire through fire), ender pearl teleport (5 damage, 1 s cooldown).
  - Shield: Harmony prefixes on `HurtCollider.PlayerHurt` and `PlayerHealth.Hurt` (enemy index) cancel hits
    from the front while raised.
  - Lights: point lights on torches/fire (nearest `MaxLights` on), held torch light, flashlight spot;
    forward rendering gets at least 8 pixel lights.
  - Units: `BlockSize` (default 0.6 m per block) scales block size, blast radius and projectile physics.
  - Build clean; plugin loads with the new patches and all new textures found in the 1.21 jar.
    Not yet verified in game: hand poses (bow draw, shield, off hand), TNT/fire/explosion look and balance,
    torch lighting, arrow/pearl flight.
- 2026-10-10: user feedback with a vanilla screenshot: shield icon wrong, items too low-res, in-hand items wrong.
  - Shield icon showed TNT: icons rendered in the same frame shared one spot and `Object.Destroy` is deferred,
    so the TNT cube was still there. Now `DestroyImmediate`.
  - Icons render at the HUD's pixel size; TNT/shield with Minecraft's gui transforms (orthographic).
  - Hands: rebuilt on Minecraft's exact pose chain (arm transform, swing, bow draw, shield blocking, bob, look
    lag) and model display transforms read from the 1.21 jar. Checked numerically against the user's vanilla
    screenshot (shield corners within a few pixels). Own full-resolution camera.
  - Flashlight icon (slot 1) looked empty. Likely cause, not confirmed: a zero-scaled parent in R.E.P.O.'s hidden
    flashlight. Zero scales are now treated as 1 when cloning, and an empty icon falls back to the drawn stand-in
    (logged as "icon 'mc:flashlight' rendered empty").
- 2026-10-10: user asked for all new items in the shop like R.E.P.O.'s weapons and grenades, TNT always with flint
  and steel, a bow always with arrows, arrows sold 16 per purchase. `McShop.cs`: items registered in R.E.P.O.'s
  item dictionary, prefabs cloned from the explosive grenade, companions added in shop generation (see CONTRACT
  "Shop"). Mining a bought item in a level unpacks it. Starter kit no longer hands these out (`ShopItems`, default on).
  Build clean. Not yet verified in game: template component list (logged as "shop: template ..."), shelf placement
  and size of the models, companion shelves (logged as "shop: on the shelves: ..."), truck delivery, unpacking.
  Known gap: Minecraft inventory contents aren't saved with the R.E.P.O. save file, so unpacked items are lost on quit.
- 2026-10-10: "Items not spawning in shop". Log: "no grenade prefab found" at the main menu: the prefabs were built
  before R.E.P.O. had loaded its item dictionary, and that failure switched the shop items off for the session.
  Now building retries (every 2 s, at level start and before `ShopManager.ShopInitialize`) until it works. The items only
  join the dictionary once their prefabs exist. The template falls back to any grenade, then any small item.
  The same log confirmed the first-person hands draw ("323673 visible pixels of 1920x1080").
- 2026-10-11: user asked to make R.E.P.O. weapons usable from the inventory and to add Minecraft's durability.
  `RepoWeapons.cs`, `Durability.cs` (see CONTRACT "R.E.P.O. weapons in the hotbar" and "Durability").
  - Guns: RMB pulls the trigger through `ItemToggle`; R.E.P.O.'s ItemGun does the rest (battery, reload, out-of-ammo
    click, tranq/laser behaviour). The parked gun's muzzle is redirected to a proxy at the first-person gun, aimed at the
    crosshair. First-person model turned so the muzzle points forward, with a kick per shot.
  - Melee: Minecraft attack with the weapon's damage and a damage-based cooldown; each enemy hit drains R.E.P.O.'s
    durability (battery). Held like a sword (long axis on the sprite diagonal, head at the tip).
  - Mines placed from the hotbar are armed.
  - Durability bars in every slot, break sound and particles, tooltip "Durability: x / y" or "Battery: n%".
  - Build clean. Not yet verified in game: shots leave the proxy and hit what the crosshair is on, damage numbers per
    weapon (logged once per weapon as "gun '...'" / "melee '...'"), held poses, durability bars, breaking.
- 2026-10-11: user asked to save the Minecraft inventory (unpacked items were lost on quit) next to R.E.P.O.'s save
  file, keyed by save slot. `InventorySave.cs` (see CONTRACT "Lifecycle"). R.E.P.O. saves only in
  `SemiFunc.OnSceneSwitch` (between levels, host/singleplayer, Private lobby); the inventory is written in the same
  `StatsManager.SaveGame` call, so it always matches the R.E.P.O. progress it belongs to (no duping by quitting
  mid-level). This also fixes the inventory carrying over between different saves in one session.
  Build clean; plugin loads with the new patches. Not yet verified in game: the file appears after a level change
  (logged as "inventory saved: n stacks to ..."), loading the save restores it ("inventory loaded: ..."),
  backup restore.
- 2026-10-11: user asked for a crossbow ("load it once, then fire; firework ammo could explode") and a fishing rod
  ("pulls valuables or enemies toward you from a distance"). New `McRanged.cs` (`FireworkEntity`, `BobberEntity`), the
  rest in McPlayer/McItems/Steve/McShop (see CONTRACT "Crossbow", "Firework rocket", "Fishing rod").
  - Numbers, model display transforms (`crossbow.json`, `handheld_rod.json`, which differs from `handheld`), texture
    names, sound files and the spark particle order (`particles/firework.json`) checked in the user's 1.21 jar and
    asset index 17 rather than from memory.
  - Firework rockets are also an item of their own (crossbow ammo from the off hand, or launched from a surface).
  - R.E.P.O. side read from the decompile: `EnemyRigidbody.DisableFollowPosition`, `PhysGrabObjectImpactDetector.
    ImpactDisable`, `PlayerTumble.TumbleForce` (an impulse, so multiplied by the tumble body's mass).
  - Bow and crossbow hotbar icons now show their pulling/loaded sprites like Minecraft's GUI.
  - Build clean (0 warnings). Game launched with the profile: plugin loads, no missing textures. Not yet verified in
    game: poses (charging, charged), the line's start point on screen, hook/reel feel on enemies and valuables,
    firework damage balance, shop shelves for the three new items ("shop: on the shelves: ...").
- 2026-10-11: user asked for multiplayer sync of TNT, torches and arrows so other players see them. New `McNet.cs` (see CONTRACT
  "Shared entities"): one custom Photon event, net ids per entity, host-decided explosions with a 1.5 s fallback, drops on
  one client only (no duping), arrows hurting enemies through the host's copy and players through their own copy.
  - From the decompile: R.E.P.O.'s own `RaiseEvent` codes are 123/124/199 (`NetworkManager`); `LevelGenerator` waits for
    `AllPlayersReady`, so no event buffering before a level exists.
  - Side fixes: a client's arrow hitting an enemy no longer bounces off (the host's copy deals the damage); a remote player's
    arrow can now hit you; punch effects moved to `McBlock.BreakEffects` so remote breaks look the same.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): the
    "net: ..." line in the log at level start, blocks appearing on the other PC, chain reactions matching, arrow pickup.
- 2026-10-11: user asked to sync fire and item drops too: "Item picks up nearest player. Avoid item duplication". Fire is a
  shared block with a shared life. Dropped items are shared (`ItemEntity.Drop`), and one client (the host with the mod)
  gives each one to the nearest player in range with room, from claims the others send; it also merges and despawns them.
  Stuck arrows use the same claim/pickup, replacing the old "whoever touches it first" pickup, which could duplicate.
  - From the decompile: `PhysGrabObject.OverrideDeactivate` only teleports to `physDisabledPosition` on the master, and a
    client's `Teleport` is an RPC to the master. So the host keeps objects parked for every inventory, and a client placing
    one asks the host to release it (ReleaseObject). Clients can now place valuables they picked up.
  - Duplication guards: one creator per drop, one decider per pickup (`taken`), the taker gets the decider's stack, merges
    and despawns only on the decider, leftovers re-dropped as new items, late drop messages for already-taken ids ignored,
    leftover copies removed when their claim is answered "gone".
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): two
    players on one item (nearest gets it), a client placing a valuable it picked up, stack merges, fire burning out together.
- 2026-10-11: user asked to draw durability as R.E.P.O.'s yellow battery instead of Minecraft's bar (same place, for R.E.P.O.
  items too) and to rebalance the new items' durability to fit R.E.P.O.'s. User chose R.E.P.O. rules (empty instead of
  breaking, recharge in the truck) and the battery always shown. See CONTRACT "Durability".
  - From the decompile: `BatteryVisualLogic` (colours, spacing, red when empty, deduction blink), `ItemBattery`
    (`batteryBars` default 6, `batteryLifeInt`), `ItemMelee.durabilityDrainOnEnemiesAndPVP` default 5 and object hits 2.5,
    `ChargingStation.ChargeAreaCheck` (10%/s per item, `chargeRate` off the station per 0.05 s, "Charge Area" box).
  - New maxima: pickaxe 40, sword 20, bow 24, crossbow 30, shield 60, fishing rod 60, flint and steel 24 (`DurabilityScale`).
    Old saves keep each item's share left (rescaled from Minecraft's maxima).
  - Charging also covers R.E.P.O. battery items carried in the Minecraft inventory, which couldn't reach the station before.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Battery layout checked in an offline mock at GUI
    scales 2-4. Not yet verified in game: the battery in the hotbar/inventory, empty items refusing use, charging at the
    station (log "durability: ... is empty" when one runs out), the station's crystals going down.
- 2026-10-11: user asked for the held item rendered in the player model's hand, posed correctly, the off hand too, and visible
  to other players. New `HeldItems.cs` (`HandsState`, `ArmPoses`, `HeldRig`), driven from `SteveManager`; see CONTRACT "Items in
  the player model's hands".
  - Third-person display transforms read from the user's 1.21 jar (handheld, generated, bow, crossbow, shield,
    shield_blocking, handheld_rod, block). Arm poses and the item chain rebuilt from Minecraft's ItemInHandLayer /
    HumanoidModel; the chain checked numerically against Minecraft's (scratch script: sword in the right hand, blade forward at
    hand height, our result = Minecraft's mirrored in x for the handedness change).
  - From the decompile: `PlayerAvatarRightArm`/`PlayerAvatarLeftArm` set the arms' local Euler angles in Update (the left one
    through a spring that keeps its own state), so a LateUpdate override doesn't disturb them; `PlayerLocalCamera` is synced for
    every avatar (head pitch); `FlashlightController.hideFlashlight` is only ever set for the local player, so modded players'
    R.E.P.O. flashlights were still drawn on other PCs; `OverrideDeactivate` only teleports, so parked objects keep their
    renderers on clients (clonable for another player's hand).
  - Steve's arms now pivot at Minecraft's (they were 2 pixels low).
  - Runtime log (oracle for the rig): "held items on the robot: right arm code_arm_r (hand 'Grabber Target' at (0, 0, 0.513)),
    body 'ANIM ARM R' rest (0, 0, 270); left arm code_arm_l (hand 'Flashlight Target Client' at (0, -0.04, 0.471)), body 'ANIM
    ARM L' rest (0, 0, 90); height 1.73 m, a block is 0.93 m".
  - Verified in game (singleplayer, F5 front view and the inventory preview, robot and `ReplaceWithSteve`): flashlight, pickaxe,
    sword, bow, TNT, torch, crossbow in the right hand, shield in the left; bow draw, shield block, crossbow charge and loaded
    hold poses; first-person hands unchanged; no exceptions. Not verified (needs two PCs): other players seeing the items, the
    hidden R.E.P.O. flashlight and the flashlight/torch light on other players, swings arriving.
  - Game automation at 1920x1080 (borderless): Singleplayer (256, 662), New Game (312, 326), Yes (754, 758); F5 twice = front
    view. Capture with ffmpeg `ddagrab` (the installed ffmpeg builds have no `gfxcapture`). A new game leaves no save behind
    until a level is finished.
- 2026-10-11: user feedback (screenshots: shop, grenade wind-up in first person and on the robot; vanilla action bar):
  - Minecraft HUD and item use were live on the loading screen: `LevelGenerator.Generated` is set before the loading screen
    lifts (`GameDirector` Start waits for every player's level animation). The mod now also needs `gameState.Main`, which
    also turns it off while the truck leaves (Outro).
  - Grenade wind-up: first person used Minecraft's trident pose, which lifts the model 0.7 up from the hand, so a round grenade
    sat huge at the top of the view. Now drawn back and up toward the shoulder with the 1 s charge, trembling at full. Third
    person used the spear pose (arm straight up), which put the grenade on the robot's head; now the arm rises up, a little
    back and 0.5 rad out to the side with the charge.
  - Inventory: no click sound on slots; F swaps the hovered slot with the off hand (Minecraft's swap key in containers,
    nothing while a stack is carried).
  - R.E.P.O.'s item prompts go to Minecraft's action bar (see CONTRACT "Item prompts").
  - Unpaid shop goods (`ItemAttributes.shopItem`) get a red slot frame and the price in the tooltip.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile; the game loads it with the new patches (no Harmony
    errors). Not yet verified in game: no HUD during the loading screen, the grenade poses, the action bar text and position,
    the red frame in the shop.
- 2026-10-12: user reported that with an item in either hand the truck's level-end button and the shop's coin machine
  didn't respond, and some level objects stopped grabbing/mining valuables (fine with empty hands).
  - Cause 1: the RMB decision only knew `PhysGrabObject`s. R.E.P.O.'s buttons are `StaticGrabObject`s (`TruckScreenText`,
    `ExtractionPoint.buttonGrabObject`), so the press went to the held item (flashlight toggle, off-hand shield, placing).
    Buttons now grab before any item use (Shift skips).
  - Cause 2: `McPlayer.Raycast` stopped at any solid collider (`~0`), including invisible ones R.E.P.O.'s grab beam ignores
    (its mask is `LayerMaskGetVisionObstruct` minus Player); with an item in hand the press then fell through to the item.
    Solid hits are now limited to those layers (enemies and Minecraft blocks still count on any layer).
  - Cause 3: the cosmetic token machine (`CosmeticShopMachine`) uses E (`InputKey.Interact`) behind an `InfoTextTrigger`;
    E opened the inventory and the inventory screen swallowed Interact. E now goes to the machine while its prompt is up.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game.
- 2026-10-12: user reported the Minecraft HUD drawn over R.E.P.O.'s Esc menu. `McHud` draws in OnGUI, which renders after every
  uGUI canvas, so it can't sit under the menu; the HUD is now skipped while `MenuManager.currentMenuPage` is set (pause menu and
  its settings pages; the same check that already stops item use). Build clean, DLL copied. Not yet verified in game.
- 2026-10-13: user reported that on a client (not the host) the pickaxe and sword did nothing, while placing and dropping
  worked. `McPlayer.Update` gated attacking and mining on `IsMasterClientOrSingleplayer` (`EnemyHealth.Hurt` only acts on
  the host). Now, like placing, a client does both when the host has the mod: melee goes to the host as HurtEnemy (the host
  also drains a R.E.P.O. weapon's battery), and mining drops the object through the shared drop, which the host parks. A
  bought Minecraft item mined on a client is destroyed by the host (Unpack): from the decompile,
  `DestroyPhysGrabObjectRPC` is `MasterOnlyRPC`, so the client's own call did nothing.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): a client's
    sword/pickaxe hits hurting enemies, a client mining a valuable (it vanishes for everyone, drops once), weapon battery.
- 2026-10-13: user asked to fix grenade throwing on a client too (it was host-only). The client now releases the grenade
  through the host as before and sends ArmGrenade; the host pulls the pin and sets the thrower as the activator. From the
  decompile: `ItemGrenade.SetActivatorRPC` is `MasterOnlyRPC`, and its Update sets the activator from
  `PhysGrabObject.lastPlayerGrabbing` when it starts ticking, so the host also points that at the thrower.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): a client's
    throw flying where aimed, its fuse and explosion, the shopkeeper reacting to the right player.
- 2026-10-13: user asked to fix R.E.P.O. guns on a client too (host-only before). From the decompile: `ItemGun.ShootLogic` only
  runs on the host, and `ShootRPC`/`ShootBulletRPC` fire from each PC's own `gunMuzzle`, which for a gun in the inventory is
  the parked gun's muzzle at (0, 3000, 0) everywhere but on the holder's PC. Each shot now sends GunFire with the holder's
  muzzle proxy pose (GunAim follows while a laser shoots); every PC aims its copy there, and the host pulls a client's trigger
  (`RepoWeapons.Pull`, which also credits the shooter). Side effect: other players now see the host's own shots leave its hand
  instead of the parked gun.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): a client's
    shot hitting what its crosshair is on, battery drain and reload, laser guns following the aim, tracers on a third PC.
- 2026-10-13: user asked to fix the rest of what didn't work on a client, including inventory saving, and to charge only
  the item in hand instead of the whole inventory.
  - Fireworks are shared like arrows (each PC flies its own copy; the host's copy hurts enemies, each player is hurt by their
    own). Before, a client's rocket exploded on its own screen only and hurt nobody.
  - Fishing rod pulls, shield knockback and the pearl's push go through new `McWorld.PushEnemy/YankPlayer/PushObject`, which
    ask the host on a client. From the decompile: `PlayerTumble.TumbleRequest/TumbleForce` are RPCs to the master checked
    with `MasterAndOwnerOnlyRPC`, so a client can't tumble another player itself. Other players now see a bobber (position
    15/s) with its line to the caster's hand.
  - Charging: only the main-hand and off-hand items charge (off hand included so a shield charges without swapping). A
    client sends what it charges; the host charges its R.E.P.O. batteries (`ItemBattery.ChargeBattery` acts only on the host)
    and drains the station for clients' items too, which it didn't before. The drain moved to `Durability.StationTick`,
    which runs on the host even when the host isn't at the station.
  - Inventory save for clients: the host publishes its save's name and a save counter as a player property; each client
    writes its own file on its own PC when the counter goes up and reads it when it joins that save.
  - Build clean (0 warnings), DLL copied to the `MinecraftRepo` profile. Not yet verified in game (needs two PCs): a client's
    rocket hurting enemies and showing for the host, the rod yanking an enemy/valuable/player from a client, the remote
    bobber and line, shield knockback from a client, a client's gun charging in hand at the station (crystals going down),
    "inventory saved/loaded" in the client's log after a level change and after rejoining.
