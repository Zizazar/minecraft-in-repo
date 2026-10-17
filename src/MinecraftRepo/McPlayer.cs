using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MinecraftRepo
{
    /// The local Minecraft player: hotbar and off hand, mining, attacking, placing, dropping, item use
    /// (bow, crossbow, fishing rod, firework rocket, shield, ender pearl, flint and steel, flashlight, grenades,
    /// R.E.P.O. guns and melee weapons),
    /// durability, and the R.E.P.O. grab on RMB.
    internal class McPlayer : MonoBehaviour
    {
        public static McPlayer Instance;

        public readonly McInventory Inventory = new McInventory();

        /// Mod is driving the local player (in a level, alive, not in a menu).
        public bool Active { get; private set; }
        /// Active and the player may act (no chat, pause menu or cutscene input lock).
        public bool CanAct { get; private set; }
        public bool InventoryOpen { get; private set; }

        public const float Reach = 4.5f;
        public const float AttackReach = 3.5f;

        // swing / attack
        public float SwingTime = -1f;           // seconds since swing start, -1 = idle
        public float OffSwingTime = -1f;
        public const float SwingDuration = 0.3f;
        float sinceAttack = 10f;
        public float AttackCharge => Mathf.Clamp01(sinceAttack / CooldownFor(Inventory.Held));

        // mining
        PhysGrabObject mineTarget;
        public float MineProgress { get; private set; }
        float mineSoundTimer, mineCooldown;
        readonly List<KeyValuePair<Mesh, Renderer>> mineMeshes = new List<KeyValuePair<Mesh, Renderer>>();
        Material[] crackMats;

        // RMB: Minecraft "use" (place, throw, draw, block, ...) or the R.E.P.O. grab
        enum Rmb { None, Grab, Place, Throw, Bow, Crossbow, Shield, Instant }
        int rmbDecisionFrame = -1;
        Rmb rmbMode;
        int rmbHand;          // 0 main hand, 1 off hand
        ItemStack useStack;   // the stack the current use started with
        bool instantPending, crossbowStartSound, crossbowMidSound;
        float placeRepeat, useStart;
        public int InventoryClosedFrame = -10;

        public bool FlashlightOn = true;
        public float PearlCooldownUntil;
        public float PearlCooldown01 => Mathf.Clamp01(PearlCooldownUntil - Time.time);
        float lastShieldSound;
        /// Time of the last shot per hand, for the first-person gun kick.
        public readonly float[] GunFiredAt = { -10f, -10f };

        static bool RmbHeld => Mouse.current != null && Mouse.current.rightButton.isPressed;

        /// 0..1 while charging a grenade throw, -1 otherwise.
        public float ThrowCharge => rmbMode == Rmb.Throw && RmbHeld ? Mathf.Clamp01((Time.time - useStart) / 1f) : -1f;

        /// Bow draw power 0..1 (Minecraft's (t^2 + 2t) / 3 over one second), -1 when not drawing.
        public float BowPull => rmbMode == Rmb.Bow && RmbHeld ? DrawPower(Time.time - useStart) : -1f;

        /// Minecraft's crossbow charges in 25 ticks (no Quick Charge).
        public const float CrossbowChargeSeconds = 1.25f;

        public bool RaisingShield => rmbMode == Rmb.Shield && RmbHeld && CanAct;
        /// Minecraft's shield takes 5 ticks to come up.
        public bool Blocking => RaisingShield && Time.time - useStart >= 0.25f;

        /// The hand whose item is in use (drawn bow, raised shield, charged grenade), or -1.
        public int UsingHand => (rmbMode == Rmb.Bow || rmbMode == Rmb.Crossbow || rmbMode == Rmb.Shield || rmbMode == Rmb.Throw) && RmbHeld ? rmbHand : -1;

        /// What the item in UsingHand is doing (HandsState.Use*), for the third-person arm pose.
        public byte UseKind => UsingHand < 0 ? HandsState.UseNone : rmbMode == Rmb.Bow ? HandsState.UseBow
            : rmbMode == Rmb.Crossbow ? HandsState.UseCrossbow : rmbMode == Rmb.Shield ? HandsState.UseShield : HandsState.UseThrow;

        /// Seconds the item in UsingHand has been in use, -1 when none.
        public float UseSeconds => UsingHand >= 0 ? Time.time - useStart : -1f;

        /// UseSeconds when `s` is the stack in use, -1 otherwise.
        public float UseSecondsOf(ItemStack s) => s != null && UsingHand >= 0 && Inventory.Hand(UsingHand) == s ? UseSeconds : -1f;

        static float DrawPower(float seconds)
        {
            float f = seconds;
            f = (f * f + 2f * f) / 3f;
            return Mathf.Min(f, 1f);
        }

        // hotbar feedback
        public float SlotChangedAt = -10f;
        public float EquipProgress = 1f, OffEquipProgress = 1f; // Minecraft lowers and raises the hand on item change
        int lastSelected = -1;
        ItemStack lastHeld, lastOff;

        object levelSeen;

        void Awake() => Instance = this;

        public Vector3 FeetPosition => PlayerController.instance ? PlayerController.instance.transform.position : Vector3.zero;

        static Camera Cam => Camera.main;

        bool ComputeActive()
        {
            var pa = PlayerAvatar.instance;
            if (!pa || pa.isDisabled || pa.deadSet || !PlayerController.instance) return false;
            if (!RunManager.instance || !LevelGenerator.Instance || !LevelGenerator.Instance.Generated) return false;
            if (SemiFunc.MenuLevel()) return false;
            // the level is generated before the loading screen lifts (Start) and stays until the truck leaves (Outro):
            // only R.E.P.O.'s Main state is play
            if (!GameDirector.instance || GameDirector.instance.currentState != GameDirector.gameState.Main) return false;
            if (!Cam) return false;
            return true;
        }

        bool ComputeCanAct()
        {
            if (!Active || InventoryOpen) return false;
            if (MenuManager.instance && MenuManager.instance.currentMenuPage) return false;
            if (ChatManager.instance && ChatManager.instance.chatActive) return false;
            if (PlayerController.instance.InputDisableTimer > 0f) return false;
            return true;
        }

        void Update()
        {
            Active = ComputeActive();
            if (!Active && InventoryOpen) CloseInventory();
            CanAct = ComputeCanAct();

            Inventory.Prune();
            Storage.Tick();
            McShop.EnsureRegistered();

            // the flashlight is an item now: R.E.P.O.'s own off-hand flashlight stays put away
            if (FlashlightController.Instance) FlashlightController.Instance.hideFlashlight = Active;

            if (!Active) { mineTarget = null; rmbMode = Rmb.None; return; }

            if (!ReferenceEquals(levelSeen, LevelGenerator.Instance))
            {
                levelSeen = LevelGenerator.Instance;
                Inventory.GiveStarterKit();
            }
            Durability.ChargeTick(Inventory);

            sinceAttack += Time.deltaTime;
            if (SwingTime >= 0f) { SwingTime += Time.deltaTime; if (SwingTime > SwingDuration) SwingTime = -1f; }
            if (OffSwingTime >= 0f) { OffSwingTime += Time.deltaTime; if (OffSwingTime > SwingDuration) OffSwingTime = -1f; }
            if (mineCooldown > 0) mineCooldown -= Time.deltaTime;

            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // inventory screen
            bool grabbing = PhysGrabber.instance && PhysGrabber.instance.grabbed;
            if (InventoryOpen)
            {
                if (kb.eKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame) CloseInventory();
                else
                {
                    KeepInventoryCursor();
                    if (kb.fKey.wasPressedThisFrame) SwapHoveredWithOffhand();
                }
            }
            else if (CanAct && kb.eKey.wasPressedThisFrame && !grabbing && !RepoWantsInteract())
            {
                OpenInventory();
            }
            if (!CanAct) { mineTarget = null; MineProgress = 0; rmbMode = Rmb.None; UpdateEquip(); return; }

            HotbarInput(kb, mouse, grabbing);
            if (kb.fKey.wasPressedThisFrame)
            {
                Inventory.SwapHands(); // Minecraft's swap-with-off-hand key
                rmbMode = Rmb.None;
            }
            UpdateEquip();
            RepoWeapons.Tick(this);

            bool world = SemiFunc.IsMasterClientOrSingleplayer();
            // a client hits enemies, mines and throws grenades through a host with the mod (it deals the damage, moves the objects)
            bool hostActs = world || McNet.HostHasMod;

            // LMB: attack / break blocks / mine
            var target = Raycast(Reach);
            if (mouse.leftButton.wasPressedThisFrame)
            {
                if (target.Enemy && target.Distance <= AttackReach) { if (hostActs) Attack(target); else Swing(); }
                else if (target.Block) BreakBlock(target.Block);
                else Swing();
            }
            else if (mouse.leftButton.isPressed && target.Block && mineCooldown <= 0f)
                BreakBlock(target.Block); // Minecraft keeps breaking instant blocks every 5 ticks while held

            if (mouse.leftButton.isPressed && hostActs && target.Mineable && mineCooldown <= 0f)
                Mine(target.Pgo);
            else
            {
                mineTarget = null;
                MineProgress = 0f;
            }

            // RMB (grab is answered through the input patch)
            EnsureRmbDecision();
            if (instantPending) { instantPending = false; UseInstant(); }
            if (rmbMode != Rmb.None && rmbMode != Rmb.Grab && rmbMode != Rmb.Instant && Inventory.Hand(rmbHand) != useStack)
                rmbMode = Rmb.None; // the item in that hand changed mid-use
            switch (rmbMode)
            {
                case Rmb.Place:
                    if (mouse.rightButton.isPressed)
                    {
                        placeRepeat -= Time.deltaTime;
                        if (placeRepeat <= 0f) { TryPlace(rmbHand, world); placeRepeat = 0.25f; }
                    }
                    break;
                case Rmb.Throw:
                    if (mouse.rightButton.wasReleasedThisFrame && hostActs) ThrowGrenade(rmbHand, Mathf.Clamp01((Time.time - useStart) / 1f));
                    break;
                case Rmb.Bow:
                    if (mouse.rightButton.isPressed)
                    {
                        SlowDown();
                        if (CameraZoom.Instance)
                            CameraZoom.Instance.OverrideZoomSet(CameraZoom.Instance.playerZoomDefault * (1f - 0.15f * BowPull), 0.1f, 6f, 6f, gameObject, 50);
                    }
                    if (mouse.rightButton.wasReleasedThisFrame) ShootBow(DrawPower(Time.time - useStart));
                    break;
                case Rmb.Crossbow:
                    if (mouse.rightButton.isPressed) ChargeCrossbow(Time.time - useStart);
                    else if (mouse.rightButton.wasReleasedThisFrame && Time.time - useStart >= CrossbowChargeSeconds) LoadCrossbow(rmbHand);
                    break;
                case Rmb.Shield:
                    if (mouse.rightButton.isPressed) SlowDown();
                    break;
            }

            // Q: drop
            if (kb.qKey.wasPressedThisFrame && Inventory.Held != null)
                DropHeld(kb.ctrlKey.isPressed || kb.leftCtrlKey.isPressed);
        }

        void LateUpdate()
        {
            if (Active && Plugin.HideRepoHud.Value) HideRepoHud();
            DrawCrack();
        }

        /// Minecraft slows you to a sneak while drawing a bow or holding up a shield.
        static void SlowDown()
        {
            if (PlayerController.instance) PlayerController.instance.OverrideSpeed(0.35f, 0.1f);
        }

        // ---------- RMB decision ----------

        /// Decides on the RMB press frame what this press does, in Minecraft's order: the main-hand item's own
        /// use, then using it on what you aim at, then the same for the off hand. Aiming at a R.E.P.O. object
        /// grabs it first (Minecraft lets blocks like doors answer a right click before items); Shift skips that.
        public void EnsureRmbDecision()
        {
            var mouse = Mouse.current;
            if (mouse == null || rmbDecisionFrame == Time.frameCount) return;
            rmbDecisionFrame = Time.frameCount;
            if (mouse.rightButton.wasPressedThisFrame)
            {
                rmbMode = Decide(out rmbHand);
                useStack = Inventory.Hand(rmbHand);
                instantPending = rmbMode == Rmb.Instant;
                crossbowStartSound = crossbowMidSound = false;
                placeRepeat = 0f;
                useStart = Time.time;
            }
            else if (!mouse.rightButton.isPressed && !mouse.rightButton.wasReleasedThisFrame)
            {
                rmbMode = Rmb.None;
            }
        }

        Rmb Decide(out int hand)
        {
            hand = 0;
            if (!CanAct) return Rmb.Grab;
            var kb = Keyboard.current;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            var t = Raycast(Reach);
            var main = Inventory.Held;
            var off = Inventory.Offhand;

            // R.E.P.O.'s buttons answer before any item, like Minecraft's buttons and levers
            if (!shift && t.Hit && t.Static && !t.Enemy) return Rmb.Grab;
            var m = UseItem(main, shift);
            if (m != Rmb.None) return m;
            if (!shift && t.Hit && t.Pgo && !t.Enemy) return Rmb.Grab;
            m = UseOn(main, t, shift);
            if (m != Rmb.None) return m;
            hand = 1;
            m = UseItem(off, shift);
            if (m != Rmb.None) return m;
            m = UseOn(off, t, shift);
            if (m != Rmb.None) return m;
            hand = 0;
            return Rmb.Grab;
        }

        /// Items whose use doesn't need a target.
        Rmb UseItem(ItemStack s, bool shift)
        {
            if (s == null) return Rmb.None;
            if (IsGrenade(s) && !shift) return Rmb.Throw;
            if (RepoWeapons.Gun(s) && !shift) return Rmb.Instant; // pulls the trigger
            if (s.Kind != ItemKind.Mc || Durability.Empty(s)) return Rmb.None; // out of charge until the truck
            switch (s.Id)
            {
                case "bow": return Inventory.FindSlot(x => x.IsMc("arrow")) >= 0 ? Rmb.Bow : Rmb.None;
                case "crossbow": return s.Loaded != null ? Rmb.Instant : CrossbowAmmoSlot() >= 0 ? Rmb.Crossbow : Rmb.None;
                case "fishing_rod": return Rmb.Instant; // cast, or reel in
                case "shield": return Rmb.Shield;
                case "ender_pearl": return Time.time >= PearlCooldownUntil ? Rmb.Instant : Rmb.None;
                case "flashlight": return Rmb.Instant;
            }
            return Rmb.None;
        }

        /// Items used on the surface or block under the crosshair.
        Rmb UseOn(ItemStack s, Target t, bool shift)
        {
            if (s == null || !t.Hit || t.Enemy) return Rmb.None;
            bool solidBlock = t.Block == null || t.Block is TntBlock; // can't build onto torches or fire
            if (s.Kind == ItemKind.Mc)
            {
                if (Durability.Empty(s)) return Rmb.None;
                switch (s.Id)
                {
                    case "tnt":
                    case "torch":
                        return t.Pgo == null && solidBlock ? Rmb.Place : Rmb.None;
                    case "flint_and_steel":
                        return (t.Block is TntBlock || (t.Pgo == null && t.Block == null)) ? Rmb.Instant : Rmb.None;
                    case "firework_rocket":
                        return t.Pgo == null ? Rmb.Instant : Rmb.None; // lifts off from the clicked surface
                }
                return Rmb.None;
            }
            if (s.IsTool) return Rmb.None;
            return (t.Pgo == null || shift) && solidBlock ? Rmb.Place : Rmb.None;
        }

        public bool GrabDown() { EnsureRmbDecision(); return rmbMode == Rmb.Grab && Mouse.current.rightButton.wasPressedThisFrame; }
        public bool GrabHold() { EnsureRmbDecision(); return rmbMode == Rmb.Grab && Mouse.current.rightButton.isPressed; }
        public bool GrabUp() { EnsureRmbDecision(); return rmbMode == Rmb.Grab && Mouse.current.rightButton.wasReleasedThisFrame; }

        static bool IsGrenade(ItemStack s) => s != null && s.Objects.Count > 0 && s.Objects[0] && s.Objects[0].GetComponent<ItemGrenade>();

        // ---------- item uses ----------

        void UseInstant()
        {
            var s = Inventory.Hand(rmbHand);
            if (s == null) return;
            if (RepoWeapons.Gun(s)) FireGun(rmbHand);
            else if (s.IsMc("ender_pearl")) ThrowPearl(rmbHand);
            else if (s.IsMc("flashlight")) ToggleFlashlight(rmbHand);
            else if (s.IsMc("flint_and_steel")) UseFlintAndSteel(rmbHand);
            else if (s.IsMc("crossbow")) ShootCrossbow(rmbHand);
            else if (s.IsMc("fishing_rod")) UseFishingRod(rmbHand);
            else if (s.IsMc("firework_rocket")) LaunchFirework(rmbHand);
        }

        /// R.E.P.O. guns run their own shot on the host; a client's trigger is pulled there (needs the mod on the host).
        void FireGun(int hand)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() && !McNet.HostHasMod) return;
            if (RepoWeapons.Fire(this, hand)) GunFiredAt[hand] = Time.time;
        }

        Vector3 PlayerVelocity => PlayerController.instance && PlayerController.instance.rb ? PlayerController.instance.rb.velocity : Vector3.zero;

        /// Like a Minecraft bow: hold RMB longer to throw harder (full power after 1 s).
        void ThrowGrenade(int hand, float charge)
        {
            var held = Inventory.Hand(hand);
            if (!IsGrenade(held)) return;
            var obj = held.Objects[held.Objects.Count - 1];
            held.Objects.RemoveAt(held.Objects.Count - 1);
            if (held.Count == 0) Inventory.SetHand(hand, null);

            var cam = Cam.transform;
            var pos = cam.position + cam.forward * 0.6f - Vector3.up * 0.1f;
            var vel = cam.forward * Mathf.Lerp(3f, 18f, charge) + Vector3.up * 1.5f + PlayerVelocity;
            Storage.Release(obj, pos, cam.rotation, vel);
            if (SemiFunc.IsMasterClientOrSingleplayer()) ArmGrenade(obj, PlayerAvatar.instance);
            else McNet.SendArmGrenade(obj, PlayerAvatar.instance); // after ReleaseObject, so the host releases it first

            McAssets.Play("throw", cam.position, 0.5f, 1f / (Random.Range(0.8f, 1.2f) + charge * 0.5f));
            SwingHand(hand);
        }

        /// Host only: pulls a thrown grenade's pin for `by`. ItemGrenade's own Update sets the activator from
        /// lastPlayerGrabbing when it starts ticking, and SetActivatorRPC only accepts the host's call.
        public static void ArmGrenade(PhysGrabObject obj, PlayerAvatar by)
        {
            if (!obj) return;
            if (by) obj.lastPlayerGrabbing = by;
            var toggle = obj.GetComponent<ItemToggle>();
            if (toggle) toggle.ToggleItem(true, by && by.photonView ? by.photonView.ViewID : -1);
            var grenade = obj.GetComponent<ItemGrenade>();
            if (grenade && by) grenade.SetActivator(by);
        }

        /// Minecraft's bow: arrow speed 3 blocks/tick at full draw, crit at full draw; uses arrows from the
        /// off hand, main hand, then the inventory.
        void ShootBow(float power)
        {
            if (power < 0.1f) return;
            int slot = Inventory.FindSlot(x => x.IsMc("arrow"));
            if (slot < 0) return;
            Inventory.ConsumeSlot(slot);
            Durability.Hurt(Inventory, Inventory.HandSlot(rmbHand), 1);
            var cam = Cam.transform;
            float B = McWorld.B;
            var dir = (cam.forward + Random.insideUnitSphere * 0.0075f).normalized;
            McNet.ShootArrow(cam.position + cam.forward * 0.4f - cam.up * 0.1f, dir * power * 3f * 20f * B + PlayerVelocity, power >= 1f, true, 1);
            McAssets.Play("throw", cam.position, 1f, 1f / (Random.value * 0.4f + 1.2f) + power * 0.5f);
        }

        // ---------- crossbow ----------

        /// Minecraft's crossbow ammo: an arrow or firework rocket held in the off hand (then main hand), otherwise
        /// arrows from the inventory. Rockets only load from a hand.
        int CrossbowAmmoSlot()
        {
            bool InHand(ItemStack x) => x != null && (x.IsMc("arrow") || x.IsMc("firework_rocket"));
            if (InHand(Inventory.Offhand)) return McInventory.OffhandSlot;
            if (InHand(Inventory.Held)) return Inventory.Selected;
            for (int i = 0; i < 36; i++) if (Inventory.Slots[i] != null && Inventory.Slots[i].IsMc("arrow")) return i;
            return -1;
        }

        /// While RMB is held: slowed like any item use, the loading sounds at 20% and 50%, and at Minecraft's use
        /// duration (charge + 3 ticks) it loads even if RMB stays down.
        void ChargeCrossbow(float seconds)
        {
            SlowDown();
            float f = seconds / CrossbowChargeSeconds;
            var pos = Cam.transform.position;
            if (f >= 0.2f && !crossbowStartSound) { crossbowStartSound = true; McAssets.Play("crossbow_start", pos, 0.5f); }
            if (f >= 0.5f && !crossbowMidSound) { crossbowMidSound = true; McAssets.Play("crossbow_middle", pos, 0.5f); }
            if (seconds >= CrossbowChargeSeconds + 0.15f) { LoadCrossbow(rmbHand); rmbMode = Rmb.None; }
        }

        void LoadCrossbow(int hand)
        {
            var xb = Inventory.Hand(hand);
            if (xb == null || !xb.IsMc("crossbow") || xb.Loaded != null) return;
            int slot = CrossbowAmmoSlot();
            if (slot < 0) return;
            xb.Loaded = Inventory.Slots[slot].Id;
            Inventory.ConsumeSlot(slot);
            McAssets.Play("crossbow_end", Cam.transform.position, 1f, 1f / (Random.value * 0.5f + 1f) + 0.2f);
        }

        /// A charged crossbow fires on the press: an arrow at 3.15 blocks/tick (always critical), or a firework
        /// rocket at 1.6 that explodes where it hits. Durability 1 per arrow, 3 per rocket.
        void ShootCrossbow(int hand)
        {
            var xb = Inventory.Hand(hand);
            if (xb == null || xb.Loaded == null) return;
            var ammo = xb.Loaded;
            xb.Loaded = null;
            var cam = Cam.transform;
            float B = McWorld.B;
            var dir = (cam.forward + Random.insideUnitSphere * 0.0075f).normalized;
            var pos = cam.position + cam.forward * 0.4f - cam.up * 0.1f;
            if (ammo == "firework_rocket") FireworkEntity.Launch(pos, dir * 1.6f * 20f * B, true);
            else McNet.ShootArrow(pos, dir * 3.15f * 20f * B, true, true, 2);
            McAssets.Play("crossbow_shoot", cam.position, 1f, 1f);
            Durability.Hurt(Inventory, Inventory.HandSlot(hand), ammo == "firework_rocket" ? 3 : 1);
        }

        // ---------- fishing rod and fireworks ----------

        /// Casts the bobber, or reels in what it hooked (Minecraft's durability: 5 for a mob or player, 3 for an item,
        /// 2 for a bobber lying on the ground).
        void UseFishingRod(int hand)
        {
            var rod = Inventory.Hand(hand);
            var cam = Cam.transform;
            if (BobberEntity.Current)
            {
                int wear = BobberEntity.Current.Retrieve(FeetPosition + Vector3.up * 1.2f);
                McAssets.Play("reel", cam.position, 1f, 2.4f * 0.4f / (Random.value * 0.4f + 0.8f));
                Durability.Hurt(Inventory, Inventory.HandSlot(hand), wear);
            }
            else
            {
                BobberEntity.Cast(rod, cam);
                McAssets.Play(McAssets.Has("cast") ? "cast" : "throw", cam.position, 0.5f, 0.4f / (Random.value * 0.4f + 0.8f));
            }
            SwingHand(hand);
        }

        /// Minecraft's firework rocket used on a block: it lifts off from the clicked face.
        void LaunchFirework(int hand)
        {
            var t = Raycast(Reach);
            if (!t.Hit || t.Enemy) return;
            FireworkEntity.Launch(t.Ray.point + t.Ray.normal * 0.15f * McWorld.B, Vector3.zero, false);
            Inventory.ConsumeHand(hand);
            SwingHand(hand);
        }

        void ThrowPearl(int hand)
        {
            Inventory.ConsumeHand(hand);
            PearlCooldownUntil = Time.time + 1f; // Minecraft: 20-tick cooldown
            var cam = Cam.transform;
            PearlEntity.Throw(cam.position + cam.forward * 0.4f - cam.up * 0.1f, cam.forward * 1.5f * 20f * McWorld.B + PlayerVelocity);
            McAssets.Play("throw", cam.position, 0.5f, 0.4f / (Random.value * 0.4f + 0.8f));
            SwingHand(hand);
        }

        void ToggleFlashlight(int hand)
        {
            FlashlightOn = !FlashlightOn;
            var fc = FlashlightController.Instance;
            var pos = Cam.transform.position;
            var snd = fc ? (FlashlightOn ? fc.lightOnAudio : fc.lightOffAudio) : null;
            if (snd != null) snd.Play(pos);
            else McAssets.Play("click", pos, 0.4f);
            SwingHand(hand);
        }

        void UseFlintAndSteel(int hand)
        {
            var t = Raycast(Reach);
            if (!t.Hit || t.Enemy) return;
            if (t.Block is TntBlock tnt) tnt.Prime(4f);
            else if (!McWorld.PlaceFire(t.Ray.point, t.Ray.normal)) return;
            McAssets.Play("ignite", t.Ray.point, 1f, Random.Range(0.8f, 1.2f));
            SwingHand(hand);
            Durability.Hurt(Inventory, Inventory.HandSlot(hand), 1);
        }

        /// While the shield is up, hits from the front (within 90 degrees of where you look) are blocked.
        public bool ShieldBlocks(Vector3 source)
        {
            if (!Blocking || !Cam) return false;
            var to = Vector3.ProjectOnPlane(source - Cam.transform.position, Vector3.up);
            var fwd = Vector3.ProjectOnPlane(Cam.transform.forward, Vector3.up);
            return to.sqrMagnitude < 0.01f || Vector3.Dot(to.normalized, fwd.normalized) > 0f;
        }

        /// `mcDamage`: the blocked hit in Minecraft points. Minecraft's shield loses 1 + floor(damage) durability
        /// for hits of 3 or more.
        public void OnShieldBlock(Vector3 source, Enemy attacker, float mcDamage = 0f)
        {
            if (Time.time - lastShieldSound < 0.4f) return;
            lastShieldSound = Time.time;
            if (mcDamage >= 3f && rmbMode == Rmb.Shield)
            {
                Durability.Hurt(Inventory, Inventory.HandSlot(rmbHand), 1 + Mathf.FloorToInt(mcDamage));
                if (Durability.Empty(Inventory.Hand(rmbHand))) rmbMode = Rmb.None; // an empty shield drops
            }
            McAssets.Play("shield", Cam.transform.position, 1f, 0.8f + Random.value * 0.4f);
            // Minecraft knocks the attacker back off the shield
            if (attacker)
            {
                var away = Vector3.ProjectOnPlane(McWorld.EnemyCenter(attacker) - Cam.transform.position, Vector3.up).normalized;
                McWorld.PushEnemy(attacker, away * 4f + Vector3.up * 1.5f, false);
            }
        }

        // ---------- targeting ----------

        public struct Target
        {
            public bool Hit;
            public RaycastHit Ray;
            public float Distance;
            public PhysGrabObject Pgo;
            public StaticGrabObject Static; // R.E.P.O.'s buttons and screens (truck screen, extraction point button)
            public Enemy Enemy;
            public McBlock Block;
            public bool Mineable;
        }

        static int solidMask = -1;

        /// The solid colliders R.E.P.O.'s grab beam can't see through (SemiFunc.LayerMaskGetVisionObstruct). Others, like
        /// PlayerOnlyCollision walls, are invisible to it and don't stop the crosshair either.
        static int SolidMask => solidMask != -1 ? solidMask : solidMask = SemiFunc.LayerMaskGetVisionObstruct();

        public Target Raycast(float reach)
        {
            var t = new Target();
            var cam = Cam;
            if (!cam) return t;
            var hits = Physics.RaycastAll(cam.transform.position, cam.transform.forward, reach, ~0, QueryTriggerInteraction.Collide);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var col = h.collider;
                if (McWorld.IsLocalPlayer(col)) continue;
                var enemy = McWorld.FindEnemy(col);
                var block = col.GetComponentInParent<McBlock>();
                if (!enemy && !block && (col.isTrigger || (SolidMask & (1 << col.gameObject.layer)) == 0)) continue;
                var pgo = col.GetComponentInParent<PhysGrabObject>();
                if (pgo && Storage.IsStored(pgo)) continue;
                var stat = pgo ? null : col.GetComponentInParent<StaticGrabObject>();
                t.Hit = true; t.Ray = h; t.Distance = h.distance; t.Pgo = pgo; t.Enemy = enemy; t.Block = block;
                t.Static = stat && stat.enabled ? stat : null;
                t.Mineable = !enemy && IsMineable(pgo);
                return t;
            }
            return t;
        }

        static bool IsMineable(PhysGrabObject pgo)
        {
            if (!pgo || pgo.isEnemy || pgo.isPlayer) return false;
            if (pgo.GetComponent<PhysGrabCart>()) return false;
            if (pgo.GetComponent<PlayerDeathHead>()) return false;
            var eq = pgo.GetComponent<ItemEquippable>();
            if (eq && eq.IsEquipped()) return false;
            return pgo.GetComponent<ValuableObject>() || pgo.GetComponent<ItemAttributes>();
        }

        // ---------- combat ----------

        static float CooldownFor(ItemStack s)
        {
            var melee = RepoWeapons.Melee(s);
            if (melee && !RepoWeapons.Broken(melee)) return RepoWeapons.Cooldown(melee);
            if (s == null || !s.IsTool || Durability.Empty(s)) return 0.25f; // hand (or an empty tool): attack speed 4
            return s.Kind == ItemKind.Sword ? 0.625f : 0.833f;   // sword 1.6, pickaxe 1.2
        }

        static float DamageFor(ItemStack s)
        {
            var melee = RepoWeapons.Melee(s);
            if (melee && !RepoWeapons.Broken(melee)) return RepoWeapons.Damage(melee); // a broken one hits like a fist
            if (s == null || !s.IsTool || Durability.Empty(s)) return 1f; // an empty tool hits like a fist too
            return s.Kind == ItemKind.Sword ? 7f : 5f;           // diamond sword / diamond pickaxe
        }

        public void Swing()
        {
            if (SwingTime < 0f || SwingTime >= SwingDuration * 0.5f) SwingTime = 0f;
            sinceAttack = 0f;
        }

        /// Arm swing for using an item (no attack cooldown reset).
        public void SwingHand(int hand)
        {
            if (hand == 0) { if (SwingTime < 0f || SwingTime >= SwingDuration * 0.5f) SwingTime = 0f; }
            else if (OffSwingTime < 0f || OffSwingTime >= SwingDuration * 0.5f) OffSwingTime = 0f;
        }

        void Attack(Target t)
        {
            var held = Inventory.Held;
            float charge = AttackCharge;
            float dmg = DamageFor(held) * (0.2f + charge * charge * 0.8f);
            var pc = PlayerController.instance;
            bool strong = charge > 0.9f;
            bool falling = pc.rb && pc.rb.velocity.y < -0.1f && !PlayerAvatar.instance.isGrounded;
            bool crit = strong && falling && !pc.sprinting;
            bool sprintKb = strong && pc.sprinting;
            bool sweep = strong && !crit && !sprintKb && held != null && held.Kind == ItemKind.Sword && !Durability.Empty(held) && PlayerAvatar.instance.isGrounded;
            if (crit) dmg *= 1.5f;
            Swing();

            var cam = Cam.transform;
            var dir = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (HitEnemy(t.Enemy, dmg, dir, sprintKb ? 2f : 1f, strong, RepoWeapons.Melee(held)) && held != null)
            {
                // Minecraft: swords lose 1 durability per hit, other tools 2
                if (held.IsTool) Durability.Hurt(Inventory, Inventory.Selected, held.Kind == ItemKind.Sword ? 1 : 2);
                var melee = RepoWeapons.Melee(held);
                if (melee)
                {
                    RepoWeapons.LogMelee(held, melee);
                    if (RepoWeapons.Wear(melee)) Durability.Broke(held);
                }
            }

            var pos = t.Ray.point;
            if (sweep)
            {
                foreach (var c in Physics.OverlapSphere(pos, 1.2f, ~0, QueryTriggerInteraction.Collide))
                {
                    var e = McWorld.FindEnemy(c);
                    if (e && e != t.Enemy) HitEnemy(e, 1f, dir, 0.6f, false);
                }
            }

            if (crit) { McAssets.Play("crit", pos, 1f); Particles.Crit(pos, 14); }
            else if (sprintKb) McAssets.Play("knockback", pos, 1f);
            else if (sweep) McAssets.Play("sweep", pos, 1f);
            else McAssets.Play(strong ? "strong" : "weak", pos, 1f);
        }

        /// `melee`: the R.E.P.O. weapon dealing it; on a client the host drains its battery (Wear only acts there).
        bool HitEnemy(Enemy e, float mcDamage, Vector3 dir, float knockback, bool stun, ItemMelee melee = null)
        {
            if (!e) return false;
            var push = dir * 4f * knockback + Vector3.up * 2.5f;
            float stunTime = stun ? 0.35f * knockback : 0f;
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                // the host deals it (and may still turn it away with the invulnerability); counted as a hit here
                McNet.SendHurtEnemy(e, mcDamage, dir, push, stunTime, melee);
                return true;
            }
            if (McWorld.HurtEnemy(e, mcDamage, dir, push, stunTime)) return true;
            McAssets.Play("nodamage", e.transform.position, 0.6f); // Minecraft's 10-tick invulnerability
            return false;
        }

        // ---------- blocks ----------

        void BreakBlock(McBlock b)
        {
            if (!b) return;
            b.BreakSynced(true, true);
            Swing();
            mineCooldown = 0.25f;
        }

        // ---------- mining ----------

        static float BreakSeconds(PhysGrabObject pgo, ItemStack held)
        {
            float mass = pgo.rb ? pgo.rb.mass : 1f;
            float hardness = Mathf.Clamp(0.6f + mass * 0.5f, 0.5f, 6f);
            bool pick = held != null && held.Kind == ItemKind.Pickaxe && !Durability.Empty(held); // empty: mines like a hand
            float ticks = pick ? 30f * hardness / 8f : 45f * hardness; // Minecraft: 30*H/speed, wrong tool much slower
            return Mathf.Max(0.05f, ticks / 20f / Mathf.Max(0.05f, Plugin.MiningSpeed.Value));
        }

        void Mine(PhysGrabObject pgo)
        {
            if (pgo != mineTarget)
            {
                mineTarget = pgo;
                MineProgress = 0f;
                mineSoundTimer = 0f;
                CacheMineMeshes(pgo);
            }
            if (SwingTime < 0f) SwingTime = 0f; // arm keeps swinging while mining
            MineProgress += Time.deltaTime / BreakSeconds(pgo, Inventory.Held);
            mineSoundTimer -= Time.deltaTime;
            if (mineSoundTimer <= 0f)
            {
                mineSoundTimer = 0.25f;
                McAssets.Play("hit", pgo.centerPoint, 0.25f, 0.5f);
                Particles.Burst(pgo.centerPoint, Vector3.one * 0.15f, IconCache.ForObject(pgo), 2, 1f);
            }
            if (MineProgress >= 1f) Break(pgo);
        }

        void Break(PhysGrabObject pgo)
        {
            var center = pgo.centerPoint;
            // Minecraft: the pickaxe loses 1 durability per block, a sword 2 (instant blocks like TNT cost nothing)
            var tool = Inventory.Held;
            if (tool != null && tool.IsTool) Durability.Hurt(Inventory, Inventory.Selected, tool.Kind == ItemKind.Pickaxe ? 1 : 2);
            var stack = McShop.Unpack(pgo) ?? ItemStack.ForObject(pgo);
            var icon = stack.Icon;
            Particles.Burst(center, Vector3.one * 0.25f, icon, 30, 2f);
            McAssets.Play("dig", center, 1f, 0.8f);
            var vel = new Vector3(Random.Range(-1f, 1f), 3f, Random.Range(-1f, 1f));
            ItemEntity.Drop(stack, center - Vector3.up * 0.1f, vel, 0.5f);
            mineTarget = null;
            MineProgress = 0f;
            mineCooldown = 0.25f;
        }

        void CacheMineMeshes(PhysGrabObject pgo)
        {
            mineMeshes.Clear();
            if (!pgo) return;
            foreach (var mr in pgo.GetComponentsInChildren<MeshRenderer>())
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mr.enabled && mf && mf.sharedMesh) mineMeshes.Add(new KeyValuePair<Mesh, Renderer>(mf.sharedMesh, mr));
            }
        }

        void DrawCrack()
        {
            if (!mineTarget || MineProgress <= 0f) return;
            if (crackMats == null)
            {
                crackMats = new Material[10];
                for (int i = 0; i < 10; i++) crackMats[i] = Mats.TransparentTex(McAssets.DestroyStages[i]);
            }
            int stage = Mathf.Clamp((int)(MineProgress * 10f), 0, 9);
            var mat = crackMats[stage];
            foreach (var kv in mineMeshes)
            {
                if (!kv.Value) continue;
                var m = kv.Value.transform.localToWorldMatrix;
                for (int sub = 0; sub < kv.Key.subMeshCount; sub++)
                    Graphics.DrawMesh(kv.Key, m, mat, 0, null, sub, null, false, false);
            }
        }

        // ---------- placing and dropping ----------

        void TryPlace(int hand, bool world)
        {
            var held = Inventory.Hand(hand);
            if (held == null || held.IsTool || held.Count == 0) return;
            var t = Raycast(Reach);
            if (!t.Hit || t.Enemy) return;
            if (held.IsMc("tnt")) { PlaceTnt(hand, t); return; }
            if (held.IsMc("torch")) { PlaceTorch(hand, t); return; }
            // a client can place R.E.P.O. objects it picked up when the host has the mod (it moves them for us)
            if (held.Kind == ItemKind.Mc || !(world || McNet.HostHasMod)) return;

            var obj = held.Objects[held.Objects.Count - 1];
            if (!obj) return;
            held.Objects.RemoveAt(held.Objects.Count - 1);
            if (held.Count == 0) Inventory.SetHand(hand, null);

            var local = LocalBounds(obj);
            float yaw = Cam.transform.eulerAngles.y + 180f;
            var rot = Quaternion.Euler(0, yaw, 0);
            var n = t.Ray.normal;
            float r = Mathf.Max(local.extents.x, local.extents.z);
            float along = Mathf.Abs(n.x) * r + Mathf.Abs(n.y) * local.extents.y + Mathf.Abs(n.z) * r;
            var pos = t.Ray.point + n * (along + 0.03f) - rot * local.center;
            Storage.Release(obj, pos, rot, Vector3.zero);
            if (RepoWeapons.IsMine(obj)) ArmMine(obj);
            McAssets.Play("dig", pos, 1f, 0.8f);
            SwingHand(hand);
        }

        void PlaceTnt(int hand, Target t)
        {
            float B = McWorld.B;
            var n = t.Ray.normal;
            Vector3 center;
            Quaternion rot;
            if (t.Block is TntBlock other)
            {
                // snap onto the face of the TNT we clicked, like building in Minecraft
                var o = other.transform;
                var local = o.InverseTransformDirection(n);
                var axis = Mathf.Abs(local.x) > Mathf.Abs(local.y) && Mathf.Abs(local.x) > Mathf.Abs(local.z) ? new Vector3(Mathf.Sign(local.x), 0, 0)
                    : Mathf.Abs(local.y) > Mathf.Abs(local.z) ? new Vector3(0, Mathf.Sign(local.y), 0) : new Vector3(0, 0, Mathf.Sign(local.z));
                center = o.position + o.rotation * axis * B;
                rot = o.rotation;
            }
            else
            {
                rot = Mathf.Abs(n.y) > 0.7f
                    ? Quaternion.Euler(0, Cam.transform.eulerAngles.y, 0)
                    : Quaternion.LookRotation(-Vector3.ProjectOnPlane(n, Vector3.up).normalized, Vector3.up);
                center = t.Ray.point + n * (B * 0.5f + 0.01f);
            }
            if (!McWorld.CanPlaceBlock(center, rot)) return;
            McNet.PlaceTnt(center, rot);
            Inventory.ConsumeHand(hand);
            McAssets.Play("grass", center, 1f, 0.8f);
            SwingHand(hand);
        }

        void PlaceTorch(int hand, Target t)
        {
            var n = t.Ray.normal;
            if (n.y < -0.5f) return; // no ceiling torches in Minecraft
            McNet.PlaceTorch(t.Ray.point, n, t.Block);
            Inventory.ConsumeHand(hand);
            McAssets.Play("wood", t.Ray.point, 1f, 0.8f);
            SwingHand(hand);
        }

        /// A mine placed from the inventory is switched on, as if it was armed in hand and set down.
        static void ArmMine(PhysGrabObject o)
        {
            var toggle = o.GetComponent<ItemToggle>();
            var me = PlayerAvatar.instance;
            if (!toggle || toggle.toggleState) return;
            if (me) { o.lastPlayerGrabbing = me; o.grabbedTimer = 3f; }
            toggle.ToggleItem(true, me && me.photonView ? me.photonView.ViewID : -1);
        }

        static Bounds LocalBounds(PhysGrabObject o)
        {
            var w2l = o.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (var c in o.GetComponentsInChildren<Collider>())
            {
                if (c.isTrigger) continue;
                var wb = c.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    p = w2l.MultiplyPoint3x4(p);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                }
            }
            return any ? b : new Bounds(Vector3.zero, Vector3.one * 0.3f);
        }

        public void DropHeld(bool wholeStack)
        {
            var s = Inventory.Held;
            if (s == null) return;
            ItemStack dropped;
            if (wholeStack || s.Count == 1) { dropped = s; Inventory.Slots[Inventory.Selected] = null; }
            else dropped = s.Split(1);
            ThrowStack(dropped);
            Swing();
        }

        public void ThrowStack(ItemStack s)
        {
            var cam = Cam.transform;
            var pos = cam.position - Vector3.up * 0.3f + cam.forward * 0.3f;
            ItemEntity.Drop(s, pos, cam.forward * 4f + Vector3.up * 1.2f, 2f);
        }

        // ---------- hotbar ----------

        void HotbarInput(Keyboard kb, Mouse mouse, bool grabbing)
        {
            var keys = new[] { kb.digit1Key, kb.digit2Key, kb.digit3Key, kb.digit4Key, kb.digit5Key, kb.digit6Key, kb.digit7Key, kb.digit8Key, kb.digit9Key };
            for (int i = 0; i < 9; i++) if (keys[i].wasPressedThisFrame) Inventory.Selected = i;
            if (!grabbing)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (scroll > 0.1f) Inventory.Selected = (Inventory.Selected + 8) % 9;
                else if (scroll < -0.1f) Inventory.Selected = (Inventory.Selected + 1) % 9;
            }
        }

        void UpdateEquip()
        {
            var held = Inventory.Held;
            if (Inventory.Selected != lastSelected || held != lastHeld)
            {
                if (Inventory.Selected != lastSelected) SlotChangedAt = Time.time;
                bool sameKind = held != null && lastHeld != null && held.Key == lastHeld.Key;
                if (!sameKind) EquipProgress = 0f;
                lastSelected = Inventory.Selected;
                lastHeld = held;
            }
            var off = Inventory.Offhand;
            if (off != lastOff)
            {
                bool sameKind = off != null && lastOff != null && off.Key == lastOff.Key;
                if (!sameKind) OffEquipProgress = 0f;
                lastOff = off;
            }
            EquipProgress = Mathf.MoveTowards(EquipProgress, 1f, Time.deltaTime / 0.2f);
            OffEquipProgress = Mathf.MoveTowards(OffEquipProgress, 1f, Time.deltaTime / 0.2f);
        }

        // ---------- inventory screen ----------

        /// E is R.E.P.O.'s interact key for the cosmetic token machine: while its prompt shows (or it is busy with this
        /// player), E goes to the machine instead of opening the inventory.
        static bool RepoWantsInteract()
        {
            var m = CosmeticShopMachine.instance;
            if (!m) return false;
            if (m.infoTextTrigger && m.infoTextTrigger.active) return true;
            return m.stateCurrent != CosmeticShopMachine.State.Idle && m.interactingPlayer && m.interactingPlayer.isLocal;
        }

        void OpenInventory()
        {
            InventoryOpen = true;
            rmbMode = Rmb.None;
            KeepInventoryCursor();
        }

        public void CloseInventory()
        {
            InventoryOpen = false;
            InventoryClosedFrame = Time.frameCount;
            if (Inventory.Cursor != null)
            {
                var c = Inventory.Cursor;
                Inventory.Cursor = null;
                if (!Inventory.Add(c) && Active) ThrowStack(c);
            }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        /// Minecraft's swap-with-off-hand key on the slot under the mouse (nothing while a stack is carried): the hovered
        /// item goes to the off hand, and what was there takes its slot.
        void SwapHoveredWithOffhand()
        {
            int i = McHud.HoveredSlot;
            if (i < 0 || i == McInventory.OffhandSlot || Inventory.Cursor != null) return;
            if (Inventory.Slots[i] == null && Inventory.Offhand == null) return;
            var s = Inventory.Slots[i];
            Inventory.Slots[i] = Inventory.Offhand;
            Inventory.Offhand = s;
            rmbMode = Rmb.None;
        }

        void KeepInventoryCursor()
        {
            if (CursorManager.instance) CursorManager.instance.Unlock(0.2f);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (InputManager.instance) InputManager.instance.DisableMovement(0.2f);
        }

        // ---------- R.E.P.O. HUD ----------

        static void HideRepoHud()
        {
            if (HealthUI.instance) HealthUI.instance.Hide();
            if (EnergyUI.instance) EnergyUI.instance.Hide();
            if (InventoryUI.instance) InventoryUI.instance.Hide();
            if (GoalUI.instance) GoalUI.instance.Hide();
            if (HaulUI.instance) HaulUI.instance.Hide();
            if (Aim.instance) Aim.instance.transform.localScale = Vector3.zero;
        }
    }
}
