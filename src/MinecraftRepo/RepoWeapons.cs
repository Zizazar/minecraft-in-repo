using System.Collections.Generic;
using UnityEngine;

namespace MinecraftRepo
{
    /// R.E.P.O. weapons used straight from the hotbar, without the physics grab.
    /// Guns: RMB presses the gun's own trigger (ItemToggle), so R.E.P.O.'s ItemGun runs its usual shot, battery
    /// drain, reload and out-of-ammo click. The gun object stays parked by Storage; its muzzle (and the laser's)
    /// is pointed at a proxy that follows the first-person hand and aims at the crosshair. In multiplayer every shot is
    /// sent with that proxy's pose (GunFire, then GunAim while it shoots): the other PCs point their copy of the gun there,
    /// and a client's trigger is pulled by the host, which runs R.E.P.O.'s shot.
    /// Melee: LMB is a Minecraft attack with the weapon's damage and R.E.P.O.'s durability drain per enemy hit.
    internal static class RepoWeapons
    {
        static readonly Transform[] proxies = new Transform[2];
        static readonly Dictionary<ItemGun, Transform> gunMuzzles = new Dictionary<ItemGun, Transform>();
        static readonly Dictionary<ItemGunLaser, Transform> laserMuzzles = new Dictionary<ItemGunLaser, Transform>();
        static readonly Dictionary<ItemGun, Transform> remoteMuzzles = new Dictionary<ItemGun, Transform>(); // another player's gun
        static readonly List<ItemGun> remoteDone = new List<ItemGun>();
        static float aimSendAt;
        static readonly HashSet<string> logged = new HashSet<string>();

        public static PhysGrabObject Top(ItemStack s) =>
            s != null && s.Kind == ItemKind.RepoItem && s.Objects.Count > 0 ? s.Objects[s.Objects.Count - 1] : null;

        public static ItemGun Gun(ItemStack s)
        {
            var o = Top(s);
            return o ? o.GetComponent<ItemGun>() : null;
        }

        public static ItemMelee Melee(ItemStack s)
        {
            var o = Top(s);
            return o ? o.GetComponent<ItemMelee>() : null;
        }

        public static bool IsMine(PhysGrabObject o) => o && o.GetComponent<ItemMine>();

        /// The gun's own muzzle, also while it points at a proxy.
        public static Transform Muzzle(ItemGun g) => g && gunMuzzles.TryGetValue(g, out var m) ? m : g ? g.gunMuzzle : null;

        // ---------- guns ----------

        /// Keeps each hand's muzzle proxy at the first-person gun and aimed at what the crosshair is on.
        public static void Tick(McPlayer p)
        {
            TickRemote();
            var cam = Camera.main;
            if (!cam) return;
            bool sendAim = McNet.Online && Time.time >= aimSendAt;
            if (sendAim) aimSendAt = Time.time + 0.05f;
            for (int hand = 0; hand < 2; hand++)
            {
                var g = Gun(p.Inventory.Hand(hand));
                if (!g && !proxies[hand]) continue;
                var t = Proxy(hand);
                int k = hand == 0 ? 1 : -1;
                var c = cam.transform;
                t.position = c.position + c.right * (0.2f * k) - c.up * 0.16f + c.forward * 0.5f;
                float range = g ? g.gunRange : 50f;
                var mask = (int)SemiFunc.LayerMaskGetVisionObstruct() | LayerMask.GetMask("Enemy");
                var aim = Physics.Raycast(c.position, c.forward, out var hit, range, mask, QueryTriggerInteraction.Ignore)
                    ? hit.point : c.position + c.forward * range;
                var dir = aim - t.position;
                t.rotation = dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir, c.up) : c.rotation;
                // a laser keeps shooting for a while (and some guns build up first): the others follow the aim
                if (sendAim && g && (g.stateCurrent == ItemGun.State.Buildup || g.stateCurrent == ItemGun.State.Shooting))
                    McNet.SendGunAim(g, t);
            }
        }

        static Transform Proxy(int hand)
        {
            if (!proxies[hand])
            {
                var go = new GameObject(hand == 0 ? "mc_muzzle_main" : "mc_muzzle_off");
                Object.DontDestroyOnLoad(go);
                proxies[hand] = go.transform;
            }
            return proxies[hand];
        }

        /// Presses the trigger of the gun in `hand`. Returns false when there is no gun or it is still reloading.
        public static bool Fire(McPlayer p, int hand)
        {
            var s = p.Inventory.Hand(hand);
            var g = Gun(s);
            if (!g) return false;
            if (g.stateCurrent != ItemGun.State.Idle && g.stateCurrent != ItemGun.State.OutOfAmmo) return false;
            var toggle = g.GetComponent<ItemToggle>();
            if (!toggle) return false;
            Tick(p);
            Aim(g, Proxy(hand));
            LogOnce(g.gameObject, "gun '" + s.Name + "': bullets " + g.numberOfBullets + ", spread " + g.gunRandomSpread + ", range " + g.gunRange
                + ", cooldown " + g.shootCooldown + "s, battery drain " + (g.batteryDrainFullBar ? g.batteryDrainFullBars + " bar(s)" : g.batteryDrain.ToString()));

            var me = PlayerAvatar.instance;
            McNet.SendGunFire(g, Proxy(hand), me); // before the trigger, so the others' muzzle is in place for ShootRPC
            if (SemiFunc.IsMasterClientOrSingleplayer()) Pull(g, me);
            return true;
        }

        /// Host only: pulls the trigger for `by`; the gun's muzzle already points where they aim.
        public static void Pull(ItemGun g, PlayerAvatar by)
        {
            if (!g || (g.stateCurrent != ItemGun.State.Idle && g.stateCurrent != ItemGun.State.OutOfAmmo)) return;
            var toggle = g.GetComponent<ItemToggle>();
            if (!toggle) return;
            var pgo = g.GetComponent<PhysGrabObject>();
            if (pgo && by)
            {
                pgo.lastPlayerGrabbing = by; // R.E.P.O. credits the shot (and the shopkeeper's anger) to the holder
                pgo.grabbedTimer = 3f;
            }
            var battery = g.GetComponent<ItemBattery>();
            bool loaded = !battery || battery.batteryLifeInt > 0;
            toggle.ToggleItem(!toggle.toggleState, by && by.photonView ? by.photonView.ViewID : -1);
            // ItemGun alerts enemies around the gun itself, which is parked far away while in the inventory
            if (loaded && g.investigateRadius > 0f && EnemyDirector.instance)
                EnemyDirector.instance.SetInvestigate(g.gunMuzzle.position, g.investigateRadius);
        }

        /// Points this PC's copy of another player's gun where they aim (GunFire / GunAim).
        public static void AimRemote(ItemGun g, Vector3 pos, Quaternion rot)
        {
            if (!g) return;
            if (!remoteMuzzles.TryGetValue(g, out var t) || !t)
                remoteMuzzles[g] = t = new GameObject("mc_muzzle_remote").transform;
            t.SetPositionAndRotation(pos, rot);
            Aim(g, t);
        }

        /// A remote-aimed gun back in the world (placed, dropped) gets its own muzzle back.
        static void TickRemote()
        {
            if (remoteMuzzles.Count == 0) return;
            var parked = AssetManager.instance ? AssetManager.instance.physDisabledPosition : new Vector3(0f, 3000f, 0f);
            remoteDone.Clear();
            foreach (var kv in remoteMuzzles)
                if (!kv.Key || !kv.Value || (kv.Key.transform.position - parked).sqrMagnitude > 100f) remoteDone.Add(kv.Key);
            foreach (var g in remoteDone)
            {
                var t = remoteMuzzles[g];
                remoteMuzzles.Remove(g);
                if (g && t && g.gunMuzzle == t) Restore(g.GetComponent<PhysGrabObject>()); // not if this player holds it now
                if (t) Object.Destroy(t.gameObject);
            }
        }

        static void Aim(ItemGun g, Transform proxy)
        {
            if (!gunMuzzles.ContainsKey(g)) gunMuzzles[g] = g.gunMuzzle;
            g.gunMuzzle = proxy;
            var laser = g.GetComponent<ItemGunLaser>();
            if (laser)
            {
                if (!laserMuzzles.ContainsKey(laser)) laserMuzzles[laser] = laser.muzzleTransform;
                laser.muzzleTransform = proxy;
            }
        }

        /// Gives a gun its own muzzle back when it leaves the inventory.
        public static void Restore(PhysGrabObject o)
        {
            if (!o) return;
            var g = o.GetComponent<ItemGun>();
            if (g && gunMuzzles.TryGetValue(g, out var m)) { g.gunMuzzle = m; gunMuzzles.Remove(g); }
            var laser = o.GetComponent<ItemGunLaser>();
            if (laser && laserMuzzles.TryGetValue(laser, out var lm)) { laser.muzzleTransform = lm; laserMuzzles.Remove(laser); }
        }

        public static void Clear()
        {
            gunMuzzles.Clear();
            laserMuzzles.Clear();
            foreach (var t in remoteMuzzles.Values) if (t) Object.Destroy(t.gameObject);
            remoteMuzzles.Clear();
        }

        // ---------- melee ----------

        public static bool Broken(ItemMelee m)
        {
            if (!m || !m.usesBatteryLogic) return false;
            var b = m.GetComponent<ItemBattery>();
            return b && b.batteryLife <= 0f;
        }

        /// The weapon's R.E.P.O. enemy damage in Minecraft points (the inverse of McWorld.HurtEnemy's scale).
        public static float Damage(ItemMelee m)
        {
            var hc = m.hurtCollider ? m.hurtCollider : m.GetComponentInChildren<HurtCollider>(true);
            int dmg = hc ? hc.enemyDamage : 0;
            return Mathf.Max(1f, dmg / Mathf.Max(0.01f, Plugin.DamageScale.Value));
        }

        /// Minecraft trades damage for speed (sword 7 at 0.625 s, axe 9 at 1 s): heavier hits recover slower.
        public static float Cooldown(ItemMelee m) => Mathf.Clamp(Damage(m) / 11.2f, 0.4f, 1.25f);

        /// R.E.P.O.'s battery drain for a weapon hit on an enemy. Returns true when the hit used it up.
        public static bool Wear(ItemMelee m)
        {
            if (!m || !m.usesBatteryLogic || !SemiFunc.IsMasterClientOrSingleplayer()) return false;
            var b = m.GetComponent<ItemBattery>();
            if (!b || b.batteryLife <= 0f) return false;
            b.batteryLife = Mathf.Max(0f, b.batteryLife - m.durabilityDrainOnEnemiesAndPVP);
            b.TriggerSmallBatteryDeduction();
            return b.batteryLife <= 0f;
        }

        public static void LogMelee(ItemStack s, ItemMelee m) =>
            LogOnce(m.gameObject, "melee '" + s.Name + "': " + Damage(m).ToString("0.#") + " damage, " + Cooldown(m).ToString("0.##")
                + " s cooldown, -" + m.durabilityDrainOnEnemiesAndPVP + "% battery per enemy hit");

        static void LogOnce(GameObject go, string line)
        {
            var key = go.name.Replace("(Clone)", "").Trim();
            if (logged.Add(key)) Plugin.Log.LogInfo(line);
        }
    }
}
