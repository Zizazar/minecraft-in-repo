# Contract: Minecraft player rules inside R.E.P.O.

No second process runs; Minecraft is only a source of textures and sounds. The contract is between the
rebuilt Minecraft rules and R.E.P.O.'s own systems.

## Ownership
- **Movement, health, stamina, death, revive, extraction, money:** R.E.P.O. The Minecraft hearts show
  `PlayerHealth.health` (5 HP per half heart) and the hunger bar shows `PlayerController.EnergyCurrent`.
- **Left mouse:** Minecraft. Hold on a valuable or item = mine. Click on an enemy = attack.
- **Right mouse:** Minecraft "use", decided on the press frame in Minecraft's order: a R.E.P.O. button
  (`StaticGrabObject`: truck screen, extraction point button) under the crosshair grabs first, like Minecraft's buttons;
  then the main-hand item's own use (bow, crossbow, fishing rod, shield, ender pearl, flashlight, grenade throw), then aiming at a R.E.P.O. object grabs it
  (Shift skips this), then the main-hand item used on the aimed surface (place, TNT, torch, flint and steel, firework rocket),
  then the same two steps for the off hand. Nothing applies = R.E.P.O.'s grab beam while held.
- **Crosshair:** the mod's own ray (mining, attacking, RMB decisions) only stops at solid colliders on R.E.P.O.'s
  vision layers (`SemiFunc.LayerMaskGetVisionObstruct`), enemies and Minecraft blocks, so invisible walls R.E.P.O.'s grab
  beam ignores (PlayerOnlyCollision and the like) don't hide valuables behind them.
- **E:** opens the inventory, except while R.E.P.O.'s cosmetic token machine shows its prompt or is busy with this player:
  then E is R.E.P.O.'s interact key.
- **Off hand:** Minecraft slot 40, swapped with F. Arrows are taken off hand first, then main hand, then
  inventory.
- **Flashlight:** R.E.P.O.'s own flashlight is kept hidden (`FlashlightController.hideFlashlight`) while the
  mod is active; the "Flashlight" item lights a copy of its spotlight while held in either hand.
- **Objects in the inventory or lying as item entities:** R.E.P.O. still owns the `PhysGrabObject`. The mod
  only keeps it deactivated every frame and remembers it. Placing gives it back to R.E.P.O. physics.
- **Shop:** R.E.P.O. owns buying. The Minecraft items (TNT, flint and steel, bow, arrows x16, torch, ender pearl,
  shield, crossbow, firework rockets x8, fishing rod; new ones always appended so every modded player has the same
  dictionary order) are extra `Item`s in `StatsManager.itemDictionary`, added after R.E.P.O.'s own, with prefabs cloned at
  runtime from the explosive grenade (grenade scripts removed, Minecraft model and one box collider inside) and
  put into `RunManager.singleplayerPool` / `multiplayerPool.ResourceCache` at every level start. Price, paying at
  the extraction point, truck delivery and `itemsPurchased` are R.E.P.O.'s. Shop generation
  (`PunManager.SpawnShopItem` postfix): TNT brings flint and steel, a bow or crossbow brings arrows (once per shop), on the nearest free shelf
  of the same size (kept from R.E.P.O.'s loop), or on top of the item when none is free. Mining a bought one
  outside the shop unpacks it into the Minecraft stack and calls `StatsManager.ItemRemove`, as a used grenade
  does. With `ShopItems` on, the starter kit only gives the pickaxe, sword and flashlight.
- **R.E.P.O. weapons in the hotbar:** R.E.P.O. owns the shot. RMB with a gun presses its `ItemToggle`, so
  `ItemGun` fires, drains its battery and reloads as usual; while the gun is in the inventory its `gunMuzzle` (and
  `ItemGunLaser.muzzleTransform`) point at a per-hand proxy at the first-person gun aimed at the crosshair, given
  back when the gun leaves storage. Multiplayer (host with the mod): each shot sends GunFire (gun view id, the proxy's
  position and rotation, shooter's avatar view id) to everyone, then GunAim (unreliable, 20/s) while the gun is in
  Buildup or Shooting (lasers). Every PC points its copy of the gun's muzzle at a remote proxy there, so tracers, flashes
  and the host's raycasts start at the shooter's hand; a remote proxy is dropped and the muzzle given back once the gun
  leaves `physDisabledPosition`. A client doesn't press the trigger itself: the host's handler sets `lastPlayerGrabbing`
  to the shooter and toggles it (`RepoWeapons.Pull`), and R.E.P.O.'s ItemGun shoots, drains and reloads there. LMB with a melee weapon is a Minecraft attack with the weapon's `HurtCollider.enemyDamage`
  (divided by `DamageScale`), cooldown damage / 11.2 s, and R.E.P.O.'s `durabilityDrainOnEnemiesAndPVP` off its
  battery per enemy hit. A mine placed from the inventory is switched on (armed) by its toggle.
- **Durability:** sized like R.E.P.O.'s weapons (a R.E.P.O. melee weapon loses 5% per enemy hit = 20 hits), times
  config `DurabilityScale`: diamond pickaxe 40, diamond sword 20, bow 24, crossbow 30, shield 60, fishing rod 60, flint and
  steel 24. Minecraft's per-use costs stay: sword 1 per enemy hit and 2 per object mined, pickaxe the reverse, bow 1 per
  shot, crossbow 1 per arrow and 3 per rocket, fishing rod 5 per reeled mob or player, 3 per object or item, 2 from the
  ground, flint and steel 1 per use, shield 1 + floor(damage) for blocked hits of 3+ (Minecraft points). At zero nothing
  breaks (R.E.P.O. rules): the item plays the break sound and stays, empty. An empty pickaxe or sword mines and hits like a
  hand; an empty bow, crossbow, shield, rod or flint and steel does nothing (RMB falls through to the grab beam).
  Charging: standing within 1.5 m of the truck's `ChargingStation` charge area (not in the shop) charges the items in your
  hands (main hand and off hand; not the rest of the inventory): Minecraft items at 10% of their maximum per second,
  R.E.P.O. battery items through `ItemBattery.ChargeBattery`. Each charging item drains the station like one of R.E.P.O.'s
  (`chargeRate` per 0.05 s). The host runs the station and R.E.P.O. batteries: a client charges its Minecraft items itself
  and sends Charging (count of Minecraft items, view ids of R.E.P.O. items; unreliable, 10/s, good for 0.3 s); the host
  charges those R.E.P.O. items and drains the station for its own and every client's charging items (`StationTick`). The save keeps damage with its maximum (`max`) and rescales it when
  the maximum changes; older saves are read against Minecraft's 1.21 maximums.
  HUD: every item with durability or a battery shows R.E.P.O.'s battery where Minecraft's bar was (bottom of the slot):
  yellow frame and segments on a dark inside with a shadow (`BatteryVisualLogic` colours), 6 segments for Minecraft items
  (lit = ceil of the share left) or the item's own `batteryBars` / `batteryLifeInt`, red frame at 0, last bar blinking
  red for 0.4 s on a loss, green flash on a gain, next bar pulsing green while charging. R.E.P.O. items with a battery keep
  R.E.P.O.'s rules (empty = no shots, broken melee) and no longer stack.
- **Mod off:** in menus, outside `GameDirector`'s Main state (the loading screen while the level starts, the truck leaving), while dead or spectating, while chat or the pause menu is open. All keys then go
  to R.E.P.O. unchanged.

- **Crossbow** (`McPlayer`, Minecraft 1.21 `CrossbowItem`): charge 25 ticks, auto-load at charge + 3 ticks or on
  release after full charge; ammo is an arrow or firework rocket in the off hand, then main hand, then arrows anywhere in
  the inventory. The load (`ItemStack.Loaded`) is saved with the stack. Arrows 3.15 blocks/tick, critical, can be picked
  up; rockets 1.6 blocks/tick in a straight line. First-person poses from `ItemInHandRenderer` (charging, charged in the
  main hand, only that hand drawn), display transforms from `crossbow.json`.
- **Firework rocket** (`FireworkEntity`, one small-ball star, flight 2): lifetime (10 x 2 + rand 6 + rand 7) ticks,
  explodes at the end or on any hit. Damage 7 to a direct hit, otherwise 7 x sqrt((5 - d) / 5) within 5 blocks with line
  of sight to the target's feet or middle; enemies, all players (the shooter too; a raised shield blocks it). No block
  or physics damage, like Minecraft. Shared like arrows: Firework (pos, vel, angled, lifetime, star colour ids, time) and
  every PC flies its own copy, caught up by the latency (at most 0.5 s); a remote copy skips its shooter and can hit the
  local player. The host's copy hurts enemies; each player is hurt by their own copy (shield checked); the host stands in
  for players without the mod. Launched from a surface: +0.04 blocks/tick^2 up, horizontal x1.15 per tick.
- **Fishing rod** (`BobberEntity`, Minecraft `FishingHook`): cast speed 0.6 blocks/tick along the look plus half of
  (look on the ground, tan(pitch)), gravity 0.03, drag 0.92/tick. Hooks enemies, other players, non-kinematic
  `PhysGrabObject`s and the mod's dropped items; lands on floors, slides off walls. Reeling adds 2 x the gap (m/s) to the
  hooked thing, like Minecraft's 0.1 x gap per tick, plus a small lift (at most 4 m/s), clamped to 14 m/s (12 for
  objects). Enemies get `EnemyRigidbody.DisableFollowPosition(0.6 s)` so their legs don't cancel it; objects get
  `ImpactDisable(1.5 s)` so the yank and landing don't cost value; players are tumbled with `TumbleForce`
  (`McWorld.PushEnemy/YankPlayer/PushObject`). Those run on the host: a client sends PushEnemy (view id, velocity, yank),
  YankPlayer (avatar view id, velocity) or PushObject (view id, velocity) with the pull worked out on its screen. Dropped
  items use ItemPush. The others see the bobber: Bobber (position, hand; unreliable, 15/s) and BobberGone; a remote bobber
  is drawn with its line to the owner's third-person hand and removed after 1 s without news. The shield's knockback and an
  ender pearl's push on an enemy also go through `PushEnemy`. The line drops when the rod leaves both hands or the bobber is over 32
  blocks away.

- **Items in the player model's hands** (`HeldItems.cs`, Minecraft 1.21 `ItemInHandLayer`, `HumanoidModel.setupAnim`,
  `PlayerRenderer.getArmPose`): main hand = right arm, off hand = left arm, for every player in a level (the local one is
  `ShadowsOnly` in first person like R.E.P.O.'s body, visible in F5 and the inventory preview). Arm poses: item (walk swing
  halved, -18 degrees), shield block, bow (both arms), crossbow charge and hold (both arms, hold = loaded and not swinging),
  grenade wind-up (Minecraft's spear throw), the attack swing with its body twist, sneaking (+0.4 rad, Steve only) and the idle
  sway; the arm in use is posed last. R.E.P.O. guns point along the look (one arm of the crossbow hold), the flashlight item
  forward at half the look pitch. Items: Minecraft's chain from the bottom centre of the arm (2 px in front of it, -90 x,
  180 y, the model json's `thirdperson_righthand`/`_lefthand` display; jar values in `McRender.cs`), the models of the
  first-person hands, bow/crossbow/rod sprites by their use state; guns and the flashlight at their own size along the arm.
  - Steve: arms pivot at PlayerModel's (-5, 2, 0), so Minecraft's rotations apply as they are (its model space and ours
    differ by -I).
  - R.E.P.O. robot (`PlayerAvatarRightArm.rightArmTransform` / `PlayerAvatarLeftArm.leftArmTransform`, 1.73 m tall): hands
    at the arms' "Grabber Target" and "Flashlight Target Client"; each arm is turned (blended over 0.15 s) so its hand points
    where Minecraft's arm points, in the frame of the bone it hangs from (rest snapped to right angles), and the item is
    placed in Minecraft's arm frame at that hand. A block on the robot = height / 1.875 x `HeldItemScale` (0.93 m).
    R.E.P.O. keeps both arms while tumbling, crawling or disabled, and the right one while the grab beam or map is out (its
    item is hidden then).
  - Multiplayer (`McNet` Hands, Swing): each modded player sends both stacks (`WriteStack`), the hand in use + 1, the use
    (bow, crossbow, shield, throw), its start (`PhotonNetwork.Time`) and flags (mod active, flashlight on, rod cast) when any
    of it changes and every 2 s; swings go unreliably. A stack's R.E.P.O. object is found by its view id (retried every 1 s).
    While a modded player is active, their own R.E.P.O. flashlight is hidden here (`hideFlashlight`), and a flashlight item
    (spot copied from their `FlashlightController.spotlight`, aimed along their look) or torch in their hands lights the
    level here. Players without the mod are left alone.

- **Item prompts:** while the mod is on, `ItemInfoUI.ItemInfoText` (with R.E.P.O.'s own looking-at filter),
  `ItemInfoExtraUI.ItemInfoText` and `WorldSpaceUIValue.Show` (value / price, plus a valuable's name) are cancelled and drawn on
  Minecraft's action bar (68 GUI px above the bottom, shadowed, no backdrop; fades 0.5 s after R.E.P.O. stops repeating it).
- **Unpaid shop goods:** a stack holding objects with `ItemAttributes.shopItem` gets a red slot frame and a "Not purchased:
  $nK" tooltip; it doesn't merge with paid stacks. R.E.P.O. still decides the purchase (shopping list from the truck).

## Units
- 1 Minecraft block = `BlockSize` m (default 0.6). It scales TNT/torch/fire size, explosion radius
  (2 x power blocks), arrow speed (3 blocks/tick at full draw) and gravity (0.05 blocks/tick^2), and pearl
  speed and gravity. Reach stays 4.5 m for mining and placing, 3.5 m for attacks.
- Steve: 1 skin pixel = 0.05 m (1.6 m tall, configurable).
- First-person hands: Minecraft's ItemInHandRenderer chain in its own units (camera looks down -Z, 70 degree
  FOV), with the display transforms of the 1.21 model jsons (handheld, generated, bow, shield, shield_blocking,
  block). Converted to Unity by flipping Z on both sides (Flip * M * Flip). Drawn by a separate camera on an
  unnamed layer into a screen-size texture that McHud draws under the HUD, so R.E.P.O.'s low-resolution
  world texture and post effects don't apply to them.
- Icons: rendered at 16 x GUI scale pixels. TNT and shield use their "gui" display transform, orthographic,
  unlit with Minecraft's GUI block shading baked in (top 1.0, left 0.8, right 0.6).
- Damage: Minecraft damage × `DamageScale` (default 5), matching R.E.P.O.'s 100 HP scale (20 MC HP).
- Mining time: Minecraft formula (ticks = 30 × hardness / tool speed); hardness from rigidbody mass.
- Explosions: Minecraft's formula, impact i = (1 - d / 2P) x exposure, damage (i^2 + i) / 2 x 7 x 2P + 1,
  knockback i blocks/tick. Exposure = share of line-of-sight samples not blocked by level geometry.
  Players are thrown with R.E.P.O.'s tumble (`TumbleForce`), physics objects get a velocity change.
- Fire: "in fire" 1 damage per hit (0.5 s invulnerability), on fire 1 damage/s for 8 s.
- Shield: blocks hits whose source is within 90 degrees of the view direction, after a 0.25 s raise.
  Hooks: `HurtCollider.PlayerHurt` (not for instant-kill colliders) and `PlayerHealth.Hurt` with an enemy index.

## Lifecycle
- Level change destroys stored objects with the level. Stacks drop dead references each frame; tools stay.
- Saving (`InventorySave.cs`): R.E.P.O. owns when and whether the game is saved. A postfix on `StatsManager.SaveGame`
  writes the virtual stacks (Pickaxe, Sword, Mc: slot, kind, id, count, damage; the mouse-carried stack as slot -1)
  and the selected hotbar slot to `saves/<save>/<save>.minecraft.json`, under the same conditions R.E.P.O. saves
  (host or singleplayer, Private lobby). R.E.P.O. objects aren't written: bought items live in R.E.P.O.'s item
  dictionary, valuables only count once extracted. Backups follow R.E.P.O.'s: when it moves the save to
  `_BACKUP<n>.es3`, the inventory moves to `_BACKUP<n>.minecraft.json`; `MenuPageSaves.OnRestoreSave` moves it back.
  Deleting a save deletes the folder with both. `RunManager.ResetProgress` (new run, joining a lobby, lost run)
  clears the inventory; a postfix on `StatsManager.LoadGame` then fills it from the save's file. Unknown ids are
  skipped, counts and damage clamped.
- A client's inventory (R.E.P.O. never saves on a client): the host publishes player property `mcrepo_save` =
  `<saveFileCurrent>|<count>`, the count bumped after each `SaveGame`. A client with a modded host keeps its own file at
  `<persistentDataPath>/minecraft_joined/<host nickname>_<save>.minecraft.json` (same format, no backups). The first time it
  sees a save (after `ResetProgress`, i.e. each join) it reads that file if there is one, keeping R.E.P.O. objects it
  already holds and topping up the starter kit; each later bump writes it.
- Multiplayer: only the host (or singleplayer) can mine, place R.E.P.O. objects and hurt enemies directly, because R.E.P.O.
  runs those on the master client. On a client the hotbar and visuals work and the shared Minecraft entities (below) work fully;
  their damage is applied by the host's copy.
- Minecraft blocks and projectiles are GameObjects in the level scene; a level change removes them. Their effects on R.E.P.O.
  (enemy and player damage, physics pushes) go through R.E.P.O.'s networked calls.
- **Shared entities** (`McNet.cs`): TNT, primed TNT, torches, fire, arrows and dropped items (`ItemEntity`). One Photon event (code 173, tagged `"mcr1"`; R.E.P.O.
  uses 123, 124, 199) and no PhotonViews. Each entity has a net id `(creator actor & 0x7FF) << 20 | counter`. Messages:
  PlaceTnt, PlaceTorch (with its support's id), PlaceFire (floor, life, time), Break (effects flag), Prime (fuse, hop angle,
  pos, rot, who lit it, `PhotonNetwork.Time`), Explode (id, pos), Arrow (pos, vel, crit, pickup, sound, time), ArrowClaim,
  ArrowPickup, ArrowGone, Item (stack, pos, vel, pickup delay), ItemClaim, ItemPickup (taker, stack), ItemMerge, ItemGone,
  ItemMove, ItemPush, ReleaseObject, HurtEnemy, Unpack, ArmGrenade, GunFire, GunAim. Each client simulates its own copy; received fuses, fire and arrows are advanced by
  the latency (at most 1 s / 0.5 s). A stack is sent as kind, key, name, id, count, damage, single, loaded and the
  PhotonView ids of its R.E.P.O. objects. Events are
  dropped outside a generated level (R.E.P.O. starts a level only once every player has generated it).
  - Explosions: decided by the host when it has the mod (player property `mcrepo`), otherwise by whoever lit the TNT. The
    deciding client explodes, sends Explode, lights the TNT in range and knocks out torches for everyone; the others hold
    their primed TNT at full swell until the Explode arrives (1.5 s, then they decide it themselves) and only put out their
    own fire. Enemy, player and physics damage stays host-only inside `Explosion.Explode`, as before.
  - Fire: the life (6-12 s) is shared, so it burns out together. Every copy burns its own player; enemies burn on the host;
    only the deciding client (host, or the fire's owner without a modded host) lets it light nearby TNT.
  - Drops: every drop goes through `ItemEntity.Drop` on exactly one client (the puncher, the client deciding an explosion or
    a lost support, the miner, the player throwing it) and is created on the others from its message.
  - Pickup, like Minecraft's nearest player: the deciding client (host with the mod, otherwise the drop's creator) gives an
    item to the nearest player standing on it with room: itself, or a player who asked in the last 0.5 s (ItemClaim, sent
    every 0.2 s while in range) and stands in range on its screen (1.5 m slack). ItemPickup carries the deciding client's
    stack, which the taker adds; leftovers when the taker filled up meanwhile drop again as a new item. The same client
    merges stacks (ItemMerge sends the merged stack), despawns Minecraft items after 5 minutes and items that fell out
    (ItemGone), and snaps where an item comes to rest (ItemMove). Reeling an item with the rod is sent (ItemPush); explosion
    pushes run on every client. Stuck arrows are given out the same way (ArrowClaim/ArrowPickup). A claim for something the
    deciding client no longer has is answered with ItemGone/ArrowGone, which removes the asker's leftover copy.
  - R.E.P.O. objects in drops and inventories: only the host's `OverrideDeactivate` parks an object and only the host can
    teleport it, so the host keeps every stored object parked, including those in other players' inventories. A client
    placing one (`Storage.Release`) stops storing it locally and sends ReleaseObject; the host takes it out of storage
    where the client put it. Clients can place and mine objects when the host has the mod: a mined object's drop goes
    out as usual and the host parks it when the Item message arrives; a bought Minecraft item mined on a client is sent as
    Unpack (the host takes it off the purchases and destroys it) and stored locally until then.
  - Melee on a client (host with the mod): HurtEnemy (enemy PhotonView id, damage, dir, push, stun, weapon view id or 0) goes
    to the host, which runs `McWorld.HurtEnemy` and, when it lands, drains the R.E.P.O. weapon's battery. The client counts
    every hit as landed (Minecraft tool durability, sounds); without a modded host a client's LMB on an enemy only swings.
  - Grenades thrown by a client (host with the mod): ReleaseObject, then ArmGrenade (grenade view id, thrower's avatar view
    id). The host sets `lastPlayerGrabbing` to the thrower, toggles it on and calls `SetActivator` (`SetActivatorRPC` is
    `MasterOnlyRPC`, and ItemGrenade's Update would otherwise take the activator from `lastPlayerGrabbing`).
  - Arrows: the host's copy hurts enemies (other copies end on an enemy without damage). A player is hurt by their own copy
    (shield checked); the host stands in for players without the mod. A remote arrow skips its shooter's avatar and can hit
    the local player. Any player can pick up a stuck arrow; that removes it everywhere.
  - Not shared: ender pearls, firework rockets, the bobber.
