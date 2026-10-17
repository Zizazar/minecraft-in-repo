using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace MinecraftRepo
{
    /// Durability on R.E.P.O.'s terms. Minecraft items keep Minecraft's per-use costs (sword 1 per hit, pickaxe 1 per
    /// object, ...) against a charge sized like R.E.P.O.'s weapons (a R.E.P.O. melee weapon lasts 20 enemy hits). At
    /// zero they don't break: they stop working (tools hit and mine like a hand) until recharged at the truck's
    /// charging station, like R.E.P.O.'s own weapons. R.E.P.O. weapons keep their battery as their durability.
    /// Both are drawn as R.E.P.O.'s yellow battery (McHud).
    internal static class Durability
    {
        /// Segments of the battery drawn for Minecraft items (R.E.P.O.'s ItemBattery default).
        public const int McBars = 6;

        public static int Max(ItemStack s)
        {
            int baseMax = BaseMax(s);
            return baseMax > 0 ? Mathf.Max(1, Mathf.RoundToInt(baseMax * Mathf.Max(0.1f, Plugin.DurabilityScale.Value))) : 0;
        }

        /// Uses per charge: pickaxe 40 objects (20 enemy hits), sword 20 enemy hits (10 objects), bow 24 shots,
        /// crossbow 30 arrows (10 rockets), shield ~12 typical blocked hits, fishing rod 12 reeled enemies (20 objects),
        /// flint and steel 24 lights.
        static int BaseMax(ItemStack s)
        {
            if (s == null) return 0;
            if (s.Kind == ItemKind.Pickaxe) return 40;
            if (s.Kind == ItemKind.Sword) return 20;
            if (s.Kind != ItemKind.Mc) return 0;
            switch (s.Id)
            {
                case "bow": return 24;
                case "flint_and_steel": return 24;
                case "shield": return 60;
                case "crossbow": return 30;
                case "fishing_rod": return 60;
                default: return 0;
            }
        }

        /// Minecraft 1.21's maximums, which saves from before the rebalance counted damage against.
        public static int LegacyMax(ItemStack s)
        {
            if (s == null) return 0;
            if (s.IsTool) return 1561;
            switch (s.Id)
            {
                case "bow": return 384;
                case "flint_and_steel": return 64;
                case "shield": return 336;
                case "crossbow": return 465;
                case "fishing_rod": return 64;
                default: return 0;
            }
        }

        public static ItemBattery Battery(ItemStack s)
        {
            if (s == null || s.Kind != ItemKind.RepoItem || s.Objects.Count == 0) return null;
            var o = s.Objects[s.Objects.Count - 1];
            return o ? o.GetComponent<ItemBattery>() : null;
        }

        /// Out of charge: a Minecraft item that can't be used, or a R.E.P.O. item with no bar left.
        public static bool Empty(ItemStack s)
        {
            int max = Max(s);
            if (max > 0) return s.Damage >= max;
            var b = Battery(s);
            return b && b.batteryLifeInt <= 0;
        }

        /// The battery to draw: its segment count and how many are lit. False when the item has none.
        public static bool Bars(ItemStack s, out int bars, out int lit)
        {
            bars = lit = 0;
            int max = Max(s);
            if (max > 0)
            {
                bars = McBars;
                lit = Mathf.Clamp(Mathf.CeilToInt((max - s.Damage) * (float)McBars / max), 0, McBars);
                return true;
            }
            var b = Battery(s);
            if (!b) return false;
            bars = Mathf.Max(1, b.batteryBars);
            lit = Mathf.Clamp(b.batteryLifeInt, 0, bars);
            return true;
        }

        /// Uses up `amount`; the use that empties the item plays the break sound, and the item stays (empty).
        public static void Hurt(McInventory inv, int slot, int amount)
        {
            var s = slot >= 0 && slot < inv.Slots.Length ? inv.Slots[slot] : null;
            int max = Max(s);
            if (max <= 0 || amount <= 0 || s.Damage >= max) return;
            s.Damage = Mathf.Min(max, s.Damage + amount);
            if (s.Damage >= max) Broke(s);
        }

        /// Minecraft's "entity.item.break" sound for an item that just ran out.
        public static void Broke(ItemStack s)
        {
            var cam = Camera.main;
            if (!cam) return;
            var pos = cam.transform.position + cam.transform.forward * 0.5f - cam.transform.up * 0.15f;
            McAssets.Play("break", pos, 0.8f, 0.8f + Random.value * 0.4f);
            Plugin.Log.LogInfo("durability: " + s.Name + " is empty");
        }

        public static string Tooltip(ItemStack s)
        {
            int max = Max(s);
            if (max > 0) return "Durability: " + (max - s.Damage) + " / " + max;
            var b = Battery(s);
            return b ? "Battery: " + Mathf.RoundToInt(b.batteryLife) + "%" : null;
        }

        // ---------- charging ----------

        static float drainTimer, sendAt;
        static int localCharging;
        static float localChargingAt = -10f;
        static readonly List<int> sendIds = new List<int>();

        /// What a client charges at the station (Charging, sent 10 times a second while it charges).
        class RemoteCharge { public float Until; public int Mc; public int[] Ids; }
        static readonly Dictionary<int, RemoteCharge> remote = new Dictionary<int, RemoteCharge>();

        /// While you stand at the truck's charging station, the items in your hands (main hand and off hand) charge like an
        /// item in its charge area: 10% of a full charge per second. The host runs the station and R.E.P.O. batteries: a
        /// client charges its Minecraft items itself and sends what it charges; the host charges the client's R.E.P.O.
        /// items and drains the station for everything (StationTick).
        public static void ChargeTick(McInventory inv)
        {
            var st = ChargingStation.instance;
            if (!st || !st.chargeArea || st.chargeFloat <= 0f || SemiFunc.RunIsShop() || RunManager.instance.levelIsShop) return;
            var cam = Camera.main;
            if (!cam || !AtStation(st.chargeArea, cam.transform.position)) return;

            bool master = SemiFunc.IsMasterClientOrSingleplayer();
            int charging = 0;
            sendIds.Clear();
            for (int hand = 0; hand < 2; hand++)
            {
                var s = inv.Hand(hand);
                if (s == null) continue;
                int max = Max(s);
                if (max > 0)
                {
                    if (s.Damage <= 0) continue;
                    s.Charge += max * 0.1f * Time.deltaTime;
                    int whole = Mathf.FloorToInt(s.Charge);
                    s.Charge -= whole;
                    s.Damage = Mathf.Max(0, s.Damage - whole);
                    s.ChargingUntil = Time.time + 0.2f;
                    charging++;
                    continue;
                }
                var b = Battery(s);
                if (!b || b.isUnchargable || b.batteryLifeInt >= b.batteryBars) continue;
                s.ChargingUntil = Time.time + 0.2f;
                if (master)
                {
                    b.ChargeBattery(st.gameObject, 10f);
                    charging++;
                }
                else
                {
                    var bpv = b.GetComponent<PhotonView>();
                    if (bpv) sendIds.Add(bpv.ViewID);
                }
            }
            if (master)
            {
                localCharging = charging;
                localChargingAt = Time.time;
            }
            else if ((charging > 0 || sendIds.Count > 0) && Time.time >= sendAt)
            {
                sendAt = Time.time + 0.1f;
                McNet.SendCharging(charging, sendIds.ToArray());
            }
        }

        /// A client's Charging arrived (on the host).
        public static void OnRemoteCharging(int actor, int mc, int[] ids)
            => remote[actor] = new RemoteCharge { Until = Time.time + 0.3f, Mc = mc, Ids = ids };

        /// Host, every frame: charges clients' R.E.P.O. items and takes ChargingStation.ChargeAreaCheck's toll off the
        /// station (chargeRate per 0.05 s for each charging item), for its own items and the clients'.
        public static void StationTick()
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer()) { remote.Clear(); return; }
            var st = ChargingStation.instance;
            if (!st) return;
            int charging = Time.time - localChargingAt < 0.1f ? localCharging : 0;
            foreach (var r in remote.Values)
            {
                if (Time.time > r.Until || st.chargeFloat <= 0f) continue;
                charging += r.Mc;
                foreach (int id in r.Ids)
                {
                    var pv = id != 0 ? PhotonView.Find(id) : null;
                    var b = pv ? pv.GetComponent<ItemBattery>() : null;
                    if (!b || b.isUnchargable || b.batteryLifeInt >= b.batteryBars) continue;
                    b.ChargeBattery(st.gameObject, 10f);
                    charging++;
                }
            }
            if (charging == 0 || st.chargeFloat <= 0f) return;

            drainTimer += Time.deltaTime;
            if (drainTimer < 0.05f) return;
            drainTimer = 0f;
            st.chargeFloat = Mathf.Max(0f, st.chargeFloat - st.chargeRate * charging);
            st.chargeTotal = Mathf.Clamp((int)(st.chargeFloat * 100f), 0, 100);
            if (StatsManager.instance) StatsManager.instance.runStats["chargingStationChargeTotal"] = st.chargeTotal;
            st.CrystalsItShouldHave();
        }

        /// Within reach of the station's charge area (where R.E.P.O. items are put to charge).
        static bool AtStation(Transform area, Vector3 pos)
        {
            var local = area.InverseTransformPoint(pos);
            var closest = area.TransformPoint(new Vector3(Mathf.Clamp(local.x, -0.5f, 0.5f), Mathf.Clamp(local.y, -0.5f, 0.5f), Mathf.Clamp(local.z, -0.5f, 0.5f)));
            return (closest - pos).sqrMagnitude <= 1.5f * 1.5f;
        }
    }
}
