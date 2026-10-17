using System;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Photon.Pun;
using UnityEngine;

namespace MinecraftRepo
{
    /// Saves the Minecraft-only stacks (tools, TNT, bows, arrows, ... with count, durability and a crossbow's load) next to R.E.P.O.'s
    /// save file, as saves/<save>/<save>.minecraft.json. Written whenever R.E.P.O. writes its save, read when that save
    /// is loaded, so the inventory always matches the R.E.P.O. progress it belongs to. R.E.P.O. objects in the
    /// inventory aren't written: R.E.P.O. keeps bought items itself, and valuables only count once extracted.
    /// R.E.P.O. saves only on the host, so a client keeps its own file on its own PC for the host's save
    /// (minecraft_joined/<host>_<save>.minecraft.json): the host publishes the save's name and a counter bumped on each
    /// save (player property), and the client reads its file the first time it sees that save and writes it on each bump.
    internal static class InventorySave
    {
        const int Version = 1;

        static string Folder(string save) => Application.persistentDataPath + "/saves/" + save;
        static string PathFor(string save, string file) => Folder(save) + "/" + file + ".minecraft.json";

        /// A new run (new save, loaded save, joined lobby, lost run): the old run's items are gone.
        public static void Clear()
        {
            followedKey = null; // a client joining reads its file for the host's save again
            var inv = McPlayer.Instance ? McPlayer.Instance.Inventory : null;
            if (inv == null) return;
            for (int i = 0; i < inv.Slots.Length; i++) inv.Slots[i] = null;
            inv.Cursor = null;
            inv.Selected = 0;
        }

        public static void Write(string save, int backupIndex)
        {
            var inv = McPlayer.Instance ? McPlayer.Instance.Inventory : null;
            if (inv == null) return;
            string path = PathFor(save, save);
            // R.E.P.O. just moved its previous save to _BACKUP<n>; the previous inventory goes with it
            string backup = Folder(save) + "/" + save + "_BACKUP" + backupIndex;
            if (File.Exists(path) && File.Exists(backup + ".es3")) Move(path, backup + ".minecraft.json");
            WriteTo(inv, path);
        }

        static void WriteTo(McInventory inv, string path)
        {
            var stacks = new JArray();
            for (int i = 0; i < inv.Slots.Length; i++) AddStack(stacks, inv.Slots[i], i);
            AddStack(stacks, inv.Cursor, -1);
            var json = new JObject { ["version"] = Version, ["selected"] = inv.Selected, ["stacks"] = stacks };
            File.WriteAllText(path + ".tmp", json.ToString());
            Move(path + ".tmp", path);
            Plugin.Log.LogInfo("inventory saved: " + stacks.Count + " stacks to " + path);
        }

        static void AddStack(JArray stacks, ItemStack s, int slot)
        {
            if (s == null || !s.IsVirtual || s.Count <= 0) return;
            var o = new JObject
            {
                ["slot"] = slot, ["kind"] = s.Kind.ToString(), ["id"] = s.Id, ["count"] = s.Count, ["damage"] = s.Damage,
            };
            if (s.Loaded != null) o["loaded"] = s.Loaded; // a charged crossbow
            int max = Durability.Max(s);
            if (max > 0) o["max"] = max; // damage is kept as a share of this when the maximum changes
            stacks.Add(o);
        }

        public static void Read(string save)
        {
            var inv = McPlayer.Instance ? McPlayer.Instance.Inventory : null;
            if (inv == null) return;
            Clear();
            string path = PathFor(save, save);
            if (!File.Exists(path)) return;
            ReadFrom(inv, path);
        }

        static void ReadFrom(McInventory inv, string path)
        {
            var json = JObject.Parse(File.ReadAllText(path));
            int n = 0;
            foreach (var t in (JArray)json["stacks"] ?? new JArray())
            {
                var s = StackOf((JObject)t);
                if (s == null) { Plugin.Log.LogWarning("inventory: skipped unknown stack " + t.ToString(Newtonsoft.Json.Formatting.None)); continue; }
                int slot = (int?)t["slot"] ?? -1;
                bool free = (slot >= 0 && slot < 36 || slot == McInventory.OffhandSlot) && inv.Slots[slot] == null;
                if (free) inv.Slots[slot] = s;
                else inv.Add(s);
                n++;
            }
            inv.Selected = Mathf.Clamp((int?)json["selected"] ?? 0, 0, 8);
            Plugin.Log.LogInfo("inventory loaded: " + n + " stacks from " + path);
        }

        static ItemStack StackOf(JObject t)
        {
            ItemStack s;
            switch ((string)t["kind"])
            {
                case "Pickaxe": s = ItemStack.Tool(ItemKind.Pickaxe); break;
                case "Sword": s = ItemStack.Tool(ItemKind.Sword); break;
                case "Mc":
                    var id = (string)t["id"];
                    if (!McItems.Known(id)) return null;
                    s = ItemStack.Mc(id);
                    break;
                default: return null;
            }
            s.VCount = Mathf.Clamp((int?)t["count"] ?? 1, 1, s.MaxStack);
            int max = Durability.Max(s);
            if (max > 0)
            {
                // saves from before the R.E.P.O.-sized durability have no "max" and counted against Minecraft's
                int savedMax = (int?)t["max"] ?? Durability.LegacyMax(s);
                int damage = (int?)t["damage"] ?? 0;
                if (savedMax > 0 && savedMax != max) damage = Mathf.RoundToInt(damage * (float)max / savedMax);
                s.Damage = Mathf.Clamp(damage, 0, max);
            }
            var loaded = (string)t["loaded"];
            if (s.IsMc("crossbow") && (loaded == "arrow" || loaded == "firework_rocket")) s.Loaded = loaded;
            return s;
        }

        // ---------- a client's own inventory file ----------

        const string SaveProp = "mcrepo_save";
        /// Host: bumped after each save it writes.
        public static int SaveCount;
        static string published, followedKey;
        static int followedCount;

        static string ClientPath(string key) => Application.persistentDataPath + "/minecraft_joined/" + key + ".minecraft.json";

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "host";
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// Host, while in a room: which save this run is and how often it was written (a player property, so players who
        /// join later get it too, in or out of a level).
        public static void Publish()
        {
            var sm = StatsManager.instance;
            var me = PhotonNetwork.LocalPlayer;
            if (!sm || me == null || string.IsNullOrEmpty(sm.saveFileCurrent)) return;
            string v = sm.saveFileCurrent + "|" + SaveCount;
            if (v == published && me.CustomProperties.ContainsKey(SaveProp)) return;
            published = v;
            me.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { SaveProp, v } });
        }

        /// Client: reads its file the first time it sees the host's save, then writes it each time the host saves.
        public static void Follow()
        {
            var host = PhotonNetwork.MasterClient;
            if (host == null || PhotonNetwork.IsMasterClient || !(host.CustomProperties[SaveProp] is string v)) return;
            int bar = v.LastIndexOf('|');
            if (bar <= 0 || !int.TryParse(v.Substring(bar + 1), out int count)) return;
            var p = McPlayer.Instance;
            if (!p) return;
            string key = Safe(host.NickName) + "_" + Safe(v.Substring(0, bar));
            string path = ClientPath(key);
            if (key != followedKey)
            {
                followedKey = key;
                followedCount = count;
                Try("load", () =>
                {
                    if (!File.Exists(path)) return; // a new run for this player: keeps what it has
                    // R.E.P.O. objects already picked up stay; the Minecraft stacks come from the file
                    var keep = new System.Collections.Generic.List<ItemStack>();
                    var inv = p.Inventory;
                    for (int i = 0; i < inv.Slots.Length; i++)
                        if (inv.Slots[i] != null && !inv.Slots[i].IsVirtual) keep.Add(inv.Slots[i]);
                    for (int i = 0; i < inv.Slots.Length; i++) inv.Slots[i] = null;
                    ReadFrom(inv, path);
                    foreach (var s in keep) if (!inv.Add(s)) p.ThrowStack(s);
                    inv.GiveStarterKit();
                });
            }
            else if (count != followedCount)
            {
                followedCount = count;
                Try("save", () =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    WriteTo(p.Inventory, path);
                });
            }
        }

        /// Out of a room: the next room gets the property again.
        public static void Offline() => published = null;

        /// R.E.P.O.'s "restore backup" replaced the save with its newest backup; the inventory follows.
        public static void RestoreBackup(string save, string backup)
        {
            string path = PathFor(save, save), from = PathFor(save, backup);
            if (File.Exists(from)) Move(from, path);
            else if (File.Exists(path)) File.Delete(path); // the save it belonged to was deleted by R.E.P.O.
        }

        static void Move(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }

        public static void Try(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { Plugin.Log.LogError("inventory " + what + " failed: " + e); }
        }
    }

    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.SaveGame))]
    internal static class InventorySavePatch
    {
        static void Postfix(StatsManager __instance, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !SemiFunc.IsMasterClientOrSingleplayer()) return; // R.E.P.O. didn't save either
            InventorySave.Try("save", () => InventorySave.Write(fileName, __instance.backupIndex));
            InventorySave.SaveCount++; // clients write theirs when they see it
        }
    }

    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.LoadGame))]
    internal static class InventoryLoadPatch
    {
        static void Postfix(StatsManager __instance, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || __instance.saveFileCurrent != fileName) return;
            InventorySave.Try("load", () => InventorySave.Read(fileName));
        }
    }

    [HarmonyPatch(typeof(RunManager), nameof(RunManager.ResetProgress))]
    internal static class InventoryResetPatch
    {
        static void Postfix() => InventorySave.Clear();
    }

    [HarmonyPatch(typeof(MenuPageSaves), nameof(MenuPageSaves.OnRestoreSave))]
    internal static class InventoryRestoreBackupPatch
    {
        static void Prefix(MenuPageSaves __instance, out string __state)
        {
            __state = null;
            foreach (var item in __instance.saveFiles)
                if (item && item.saveFileName == __instance.currentSaveFileName && item.saveFileBackups?.Count > 0)
                    __state = item.saveFileBackups[0];
        }

        static void Postfix(MenuPageSaves __instance, string __state)
        {
            string save = __instance.currentSaveFileName;
            if (__state == null || string.IsNullOrEmpty(save)) return;
            if (File.Exists(Application.persistentDataPath + "/saves/" + save + "/" + __state + ".es3")) return; // not restored
            InventorySave.Try("backup restore", () => InventorySave.RestoreBackup(save, __state));
        }
    }
}
