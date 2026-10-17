using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace MinecraftRepo
{
    /// Marks a R.E.P.O. item object that holds Minecraft items. Mining it outside the shop unpacks it.
    internal class McShopItem : MonoBehaviour
    {
        public string Id;
        public int Count = 1;
    }

    /// The Minecraft items sold in R.E.P.O.'s shop the way its grenades and weapons are: each one is an `Item` in
    /// StatsManager.itemDictionary with a prefab (R.E.P.O.'s explosive grenade with the grenade scripts removed and
    /// the Minecraft model inside). Shop shelves, price tag, paying at the extraction point, delivery to the truck
    /// next level and the itemsPurchased count are all R.E.P.O.'s own. Shop generation adds companions: TNT brings
    /// flint and steel, a bow brings arrows.
    internal static class McShop
    {
        internal class Def
        {
            public string Id, Asset, Description, Companion;
            public int Count = 1, MaxAmount = 1, MaxInShop = 1;
            public SemiFunc.itemType Type;
            public float ValueMin, ValueMax, Size;
        }

        // Shop price = ceil(random(ValueMin, ValueMax) * 4 / 1000) thousand dollars, at least 1 (ItemAttributes.GetValue).
        public static readonly Def[] Defs =
        {
            new Def { Id = "tnt", Asset = "Item Minecraft TNT", Type = SemiFunc.itemType.grenade, ValueMin = 750, ValueMax = 1000,
                MaxAmount = 10, MaxInShop = 2, Size = 0.25f, Companion = "flint_and_steel",
                Description = "Place it, then light it with flint and steel. Explodes 4 seconds later." },
            new Def { Id = "flint_and_steel", Asset = "Item Minecraft Flint and Steel", Type = SemiFunc.itemType.tool, ValueMin = 250, ValueMax = 400,
                MaxAmount = 2, MaxInShop = 1, Size = 0.22f, Description = "Lights TNT and sets fire to things." },
            new Def { Id = "bow", Asset = "Item Minecraft Bow", Type = SemiFunc.itemType.gun, ValueMin = 2000, ValueMax = 2750,
                MaxAmount = 2, MaxInShop = 1, Size = 0.32f, Companion = "arrow", Description = "Hold right mouse to draw. Needs arrows." },
            new Def { Id = "arrow", Asset = "Item Minecraft Arrows", Count = 16, Type = SemiFunc.itemType.grenade, ValueMin = 250, ValueMax = 500,
                MaxAmount = 10, MaxInShop = 2, Size = 0.3f, Description = "16 arrows for the bow." },
            new Def { Id = "torch", Asset = "Item Minecraft Torch", Type = SemiFunc.itemType.tool, ValueMin = 100, ValueMax = 200,
                MaxAmount = 10, MaxInShop = 2, Size = 0.25f, Description = "A light you can place on walls and floors." },
            new Def { Id = "ender_pearl", Asset = "Item Minecraft Ender Pearl", Type = SemiFunc.itemType.grenade, ValueMin = 500, ValueMax = 750,
                MaxAmount = 10, MaxInShop = 2, Size = 0.18f, Description = "Throw it to teleport where it lands. Costs 5 damage." },
            new Def { Id = "shield", Asset = "Item Minecraft Shield", Type = SemiFunc.itemType.melee, ValueMin = 1500, ValueMax = 2000,
                MaxAmount = 2, MaxInShop = 1, Size = 0.34f, Description = "Hold right mouse to block hits from the front." },
            // new items go last: the dictionary order is the same for every modded player
            new Def { Id = "crossbow", Asset = "Item Minecraft Crossbow", Type = SemiFunc.itemType.gun, ValueMin = 2500, ValueMax = 3250,
                MaxAmount = 2, MaxInShop = 1, Size = 0.34f, Companion = "arrow",
                Description = "Hold right mouse to load, click to fire. A firework rocket in the off hand loads instead of an arrow." },
            new Def { Id = "firework_rocket", Asset = "Item Minecraft Firework Rockets", Count = 8, Type = SemiFunc.itemType.grenade, ValueMin = 750, ValueMax = 1000,
                MaxAmount = 10, MaxInShop = 2, Size = 0.25f, Description = "8 rockets. Crossbow ammo from the off hand, or launch one from the floor. They explode." },
            new Def { Id = "fishing_rod", Asset = "Item Minecraft Fishing Rod", Type = SemiFunc.itemType.tool, ValueMin = 750, ValueMax = 1250,
                MaxAmount = 2, MaxInShop = 1, Size = 0.34f, Description = "Cast it, then click again to reel in what it hooked: valuables, items, enemies." },
        };

        internal const string PathPrefix = "MinecraftRepo/";
        const string AssetPrefix = "Item Minecraft ";

        static readonly Dictionary<string, Item> items = new Dictionary<string, Item>();      // by asset name
        static readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>(); // by resource path
        static GameObject holder;
        static bool prefabsFailed;

        public static bool Enabled => Plugin.ShopItems.Value && !prefabsFailed;

        public static Def DefFor(string id)
        {
            foreach (var d in Defs) if (d.Id == id) return d;
            return null;
        }

        static Def DefForAsset(string asset)
        {
            foreach (var d in Defs) if (d.Asset == asset) return d;
            return null;
        }

        // ---------- registration ----------

        static float nextBuildTry;

        /// Adds the items to R.E.P.O.'s item dictionary (after its own, so every modded player gets the same indices),
        /// but only once their prefabs exist: the shop must never pick an item it can't spawn.
        static void RegisterItems()
        {
            var sm = StatsManager.instance;
            if (!Enabled || !sm || prefabs.Count == 0) return;
            foreach (var d in Defs)
                if (items.TryGetValue(d.Asset, out var item) && item && !sm.itemDictionary.ContainsKey(item.name))
                    sm.itemDictionary.Add(item.name, item);
        }

        /// Every frame: builds the prefabs as soon as R.E.P.O.'s items are loaded, and re-adds the items whenever
        /// R.E.P.O. rebuilds its dictionary (new run, loaded save).
        public static void EnsureRegistered()
        {
            var sm = StatsManager.instance;
            if (!Enabled || !sm || sm.itemDictionary.Count == 0) return;
            if (prefabs.Count == 0)
            {
                if (Time.unscaledTime < nextBuildTry || !RunManager.instance) return;
                nextBuildTry = Time.unscaledTime + 2f;
                BuildPrefabs();
            }
            if (!sm.itemDictionary.ContainsKey(Defs[0].Asset)) RegisterItems();
        }

        /// Puts the prefabs into R.E.P.O.'s prefab pools, which every level start clears.
        public static void RegisterPrefabs()
        {
            if (!Enabled || !RunManager.instance) return;
            if (prefabs.Count == 0) BuildPrefabs();
            RegisterItems();
            foreach (var kv in prefabs)
            {
                if (!kv.Value) continue;
                RunManager.instance.singleplayerPool[kv.Key] = kv.Value;
                RunManager.instance.multiplayerPool.ResourceCache[kv.Key] = kv.Value;
            }
        }

        static Item TemplateItem()
        {
            var sm = StatsManager.instance;
            if (!sm) return null;
            // best: the explosive grenade, then any grenade, then any small item
            Item best = null;
            int bestScore = 0;
            foreach (var it in sm.itemDictionary.Values)
            {
                if (!it || it.name.StartsWith(AssetPrefix) || it.prefab == null || !it.prefab.IsValid()) continue;
                bool grenade = it.itemType == SemiFunc.itemType.grenade || it.name.Contains("Grenade");
                int score = grenade && it.name.Contains("Explosive") ? 3 : grenade ? 2 : it.itemVolume == SemiFunc.itemVolume.small ? 1 : 0;
                if (score > bestScore) { best = it; bestScore = score; }
            }
            return best;
        }

        static Item MakeItem(Def d)
        {
            var t = TemplateItem();
            var item = ScriptableObject.CreateInstance<Item>();
            item.name = d.Asset;
            item.hideFlags = HideFlags.DontUnloadUnusedAsset;
            item.itemName = d.Count > 1 ? McItems.Name(d.Id) + "s x" + d.Count : McItems.Name(d.Id);
            item.description = d.Description;
            item.itemType = d.Type;
            item.itemVolume = t ? t.itemVolume : SemiFunc.itemVolume.small;
            item.itemSecretShopType = SemiFunc.itemSecretShopType.none;
            item.spawnRotationOffset = t ? t.spawnRotationOffset : Quaternion.identity;
            if (t)
            {
                item.colorPreset = t.colorPreset;
                item.emojiIcon = t.emojiIcon;
            }
            var v = ScriptableObject.CreateInstance<Value>();
            v.name = "Value - " + d.Asset;
            v.hideFlags = HideFlags.DontUnloadUnusedAsset;
            v.valueMin = d.ValueMin;
            v.valueMax = d.ValueMax;
            item.value = v;
            item.maxAmount = d.MaxAmount;
            item.maxAmountInShop = d.MaxInShop;
            item.minPlayerCount = 1;
            item.physicalItem = true;
            item.prefab = new PrefabRef { prefabName = d.Asset, resourcePath = PathPrefix + d.Asset };
            return item;
        }

        static void BuildPrefabs()
        {
            var sm = StatsManager.instance;
            if (!sm || sm.itemDictionary.Count == 0) return; // R.E.P.O. hasn't loaded its items yet: try again later
            var t = TemplateItem();
            if (!t)
            {
                Fail("none of R.E.P.O.'s " + sm.itemDictionary.Count + " items can serve as a template");
                return;
            }
            var template = t.prefab.Prefab;
            if (!template)
            {
                Plugin.Log.LogWarning("shop: template '" + t.name + "' (" + t.prefab.ResourcePath + ") didn't load; trying again next level");
                return;
            }
            if (!holder)
            {
                holder = new GameObject("MinecraftRepo shop prefabs");
                holder.SetActive(false); // prefabs stay asleep; R.E.P.O.'s instantiate wakes the copies
                holder.hideFlags = HideFlags.HideAndDontSave;
                Object.DontDestroyOnLoad(holder);
            }
            foreach (var d in Defs)
                if (!items.TryGetValue(d.Asset, out var it) || !it) items[d.Asset] = MakeItem(d);
            var parts = new List<string>();
            foreach (var c in template.GetComponents<Component>()) if (c) parts.Add(c.GetType().Name);
            Plugin.Log.LogInfo("shop: template '" + t.name + "' -> '" + template.name + "' (" + string.Join(", ", parts) + ")");
            try
            {
                foreach (var d in Defs) prefabs[PathPrefix + d.Asset] = BuildPrefab(d, template);
            }
            catch (System.Exception e)
            {
                Fail("building the shop items failed: " + e);
                return;
            }
            Plugin.Log.LogInfo("shop: " + prefabs.Count + " Minecraft items for sale");
        }

        static void Fail(string why)
        {
            Plugin.Log.LogError("shop: " + why + ". Minecraft items won't be sold.");
            prefabsFailed = true;
            foreach (var p in prefabs.Values) if (p) Object.Destroy(p);
            prefabs.Clear();
            var sm = StatsManager.instance;
            if (sm) foreach (var d in Defs) sm.itemDictionary.Remove(d.Asset);
        }

        static GameObject BuildPrefab(Def d, GameObject template)
        {
            var go = Object.Instantiate(template, holder.transform);
            go.name = d.Asset;
            var root = go.transform;
            var tb = LocalBounds(root, go.GetComponentsInChildren<Renderer>(true), out int visLayer);
            if (tb.size == Vector3.zero) tb = new Bounds(Vector3.zero, Vector3.one * 0.15f);

            // the grenade's scripts and looks go; physics, grabbing, price tag and networking stay
            for (int pass = 0; pass < 3; pass++)
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb && mb.GetType().Name.StartsWith("ItemGrenade")) Object.DestroyImmediate(mb);
            foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (var l in go.GetComponentsInChildren<Light>(true)) l.enabled = false;
            var pv = go.GetComponent<PhotonView>();
            if (pv && pv.ObservedComponents != null) pv.ObservedComponents.RemoveAll(c => c == null);

            // the Minecraft model, standing where the grenade stood
            var wrap = new GameObject("mc_model").transform;
            wrap.SetParent(root, false);
            var model = McItems.Model(d.Id, 1f);
            model.transform.SetParent(wrap, false);
            foreach (var tr in model.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = visLayer;
            ModelUtil.SetShadows(model, UnityEngine.Rendering.ShadowCastingMode.On);
            var mb0 = LocalBounds(wrap, model.GetComponentsInChildren<Renderer>(true), out _);
            float scale = d.Size / Mathf.Max(0.001f, Mathf.Max(mb0.size.x, Mathf.Max(mb0.size.y, mb0.size.z)));
            wrap.localScale = Vector3.one * scale;
            wrap.localPosition = new Vector3(tb.center.x, tb.min.y, tb.center.z) - new Vector3(mb0.center.x, mb0.min.y, mb0.center.z) * scale;

            // one box collider around it replaces the grenade's solid colliders
            PhysicMaterial mat = null;
            int colLayer = go.layer;
            bool found = false;
            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c.isTrigger) continue;
                if (!found) { mat = c.sharedMaterial; colLayer = c.gameObject.layer; found = true; }
                Object.DestroyImmediate(c);
            }
            foreach (var pc in go.GetComponentsInChildren<PhysGrabObjectCollider>(true)) Object.DestroyImmediate(pc);
            var colGo = new GameObject("mc_collider") { layer = colLayer };
            colGo.transform.SetParent(root, false);
            var mb1 = LocalBounds(root, model.GetComponentsInChildren<Renderer>(true), out _);
            var box = colGo.AddComponent<BoxCollider>();
            box.center = mb1.center;
            box.size = Vector3.Max(mb1.size, Vector3.one * 0.06f);
            box.sharedMaterial = mat;
            colGo.AddComponent<PhysGrabObjectCollider>();

            var ia = go.GetComponent<ItemAttributes>();
            if (!ia) throw new System.Exception("template has no ItemAttributes");
            ia.item = items[d.Asset];
            var icon = McItems.Icon(d.Id);
            if (icon)
            {
                var copy = Object.Instantiate(icon);
                copy.filterMode = FilterMode.Point;
                Object.DontDestroyOnLoad(copy);
                ia.icon = Sprite.Create(copy, new Rect(0, 0, copy.width, copy.height), new Vector2(0.5f, 0.5f));
            }

            var tag = go.AddComponent<McShopItem>();
            tag.Id = d.Id;
            tag.Count = d.Count;
            return go;
        }

        /// Bounds of the renderers' meshes in `space`'s local coordinates. Works on inactive objects, where
        /// Renderer.bounds is empty.
        static Bounds LocalBounds(Transform space, Renderer[] rs, out int layer)
        {
            layer = space.gameObject.layer;
            var b = new Bounds();
            bool any = false;
            var w2l = space.worldToLocalMatrix;
            foreach (var r in rs)
            {
                if (!r.enabled || r is ParticleSystemRenderer) continue;
                Mesh m = null;
                if (r is SkinnedMeshRenderer sk) m = sk.sharedMesh;
                else { var mf = r.GetComponent<MeshFilter>(); if (mf) m = mf.sharedMesh; }
                if (!m) continue;
                var mtx = w2l * r.transform.localToWorldMatrix;
                var mbb = m.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mbb.center + Vector3.Scale(mbb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = mtx.MultiplyPoint3x4(corner);
                    if (!any) { b = new Bounds(p, Vector3.zero); any = true; layer = r.gameObject.layer; }
                    else b.Encapsulate(p);
                }
            }
            return b;
        }

        // ---------- unpacking ----------

        /// A bought Minecraft item mined outside the shop turns into its Minecraft stack. Like a used grenade, it
        /// leaves R.E.P.O.'s purchased items so the truck doesn't bring it again. In the shop it stays an object, so
        /// it can still be carried to the extraction point and paid for.
        public static ItemStack Unpack(PhysGrabObject pgo)
        {
            var tag = pgo ? pgo.GetComponent<McShopItem>() : null;
            if (!tag || SemiFunc.RunIsShop() || (RunManager.instance && RunManager.instance.levelIsShop)) return null;
            if (!SemiFunc.IsMasterClientOrSingleplayer())
            {
                // only the host can take it off the purchases and destroy it; out of reach here until it's gone
                McNet.SendUnpack(pgo);
                Storage.Store(pgo);
                return ItemStack.Mc(tag.Id, tag.Count);
            }
            var ia = pgo.GetComponent<ItemAttributes>();
            var sm = StatsManager.instance;
            if (ia && sm && !string.IsNullOrEmpty(ia.instanceName) && sm.item.ContainsKey(ia.instanceName))
                sm.ItemRemove(ia.instanceName);
            pgo.DestroyPhysGrabObject();
            return ItemStack.Mc(tag.Id, tag.Count);
        }

        // ---------- shop generation: companions ----------

        static readonly HashSet<ItemVolume> usedVolumes = new HashSet<ItemVolume>();
        static readonly HashSet<ItemVolume> reservedVolumes = new HashSet<ItemVolume>();
        static readonly HashSet<string> spawnedInShop = new HashSet<string>();
        static readonly List<string> shopLog = new List<string>();
        static bool spawningCompanion;

        public static void ShopPopulateBegin()
        {
            RegisterPrefabs();
            usedVolumes.Clear();
            reservedVolumes.Clear();
            spawnedInShop.Clear();
            shopLog.Clear();
        }

        public static void ShopPopulateEnd()
        {
            if (shopLog.Count > 0) Plugin.Log.LogInfo("shop: on the shelves: " + string.Join(", ", shopLog));
            usedVolumes.Clear();
            reservedVolumes.Clear();
        }

        /// A shelf kept for a companion: R.E.P.O.'s own loop must not put anything else there.
        public static bool Blocks(ItemVolume v) => !spawningCompanion && v && reservedVolumes.Contains(v);

        public static void OnShopItemSpawned(ItemVolume volume, Item item)
        {
            if (!item) return;
            if (volume) usedVolumes.Add(volume);
            spawnedInShop.Add(item.name);
            var d = DefForAsset(item.name);
            if (d == null) return;
            shopLog.Add(item.itemName);
            if (d.Companion == null) return;
            var cd = DefFor(d.Companion);
            if (cd == null || spawnedInShop.Contains(cd.Asset) || !items.TryGetValue(cd.Asset, out var companion) || !companion) return;
            SpawnCompanion(companion, volume);
        }

        /// Puts the companion on the nearest free shelf of its size, or on top of the item when none is left.
        static void SpawnCompanion(Item companion, ItemVolume near)
        {
            var shop = ShopManager.instance;
            spawnedInShop.Add(companion.name);
            shop.potentialItems.Remove(companion); // one fewer to come up the normal way
            ItemVolume best = null;
            float bestD = float.MaxValue;
            var from = near ? near.transform.position : Vector3.zero;
            foreach (var v in shop.itemVolumes)
            {
                if (!v || v == near || v.itemVolume != companion.itemVolume || usedVolumes.Contains(v) || reservedVolumes.Contains(v)) continue;
                float dist = (v.transform.position - from).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = v; }
            }
            if (best)
            {
                reservedVolumes.Add(best);
                int n = 0;
                spawningCompanion = true;
                try { PunManager.instance.SpawnShopItem(best, new List<Item> { companion }, ref n); }
                finally { spawningCompanion = false; }
                return;
            }
            if (!near) return;
            var pos = near.transform.position + Vector3.up * 0.3f;
            if (SemiFunc.IsMultiplayer()) PhotonNetwork.InstantiateRoomObject(companion.prefab.ResourcePath, pos, near.transform.rotation, 0);
            else Object.Instantiate(companion.prefab.Prefab, pos, near.transform.rotation);
            shopLog.Add(companion.itemName + " (stacked, no free shelf)");
        }
    }

    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.LoadItemsFromFolder))]
    internal static class ShopItemsLoadPatch
    {
        static void Postfix() => McShop.RegisterPrefabs();
    }

    /// Before the shop picks its candidates from the item dictionary.
    [HarmonyPatch(typeof(ShopManager), nameof(ShopManager.ShopInitialize))]
    internal static class ShopInitializePatch
    {
        static void Prefix() => McShop.RegisterPrefabs();
    }

    [HarmonyPatch(typeof(LevelGenerator), nameof(LevelGenerator.Start))]
    internal static class ShopPrefabsLevelPatch
    {
        static void Postfix() => McShop.RegisterPrefabs();
    }

    /// Network spawns (host and clients) find the prefabs even if the pool was cleared since.
    [HarmonyPatch(typeof(MultiplayerPool), nameof(MultiplayerPool.Instantiate))]
    internal static class ShopPrefabsPoolPatch
    {
        static void Prefix(string prefabId)
        {
            if (prefabId != null && prefabId.StartsWith(McShop.PathPrefix)) McShop.RegisterPrefabs();
        }
    }

    [HarmonyPatch(typeof(PunManager), nameof(PunManager.ShopPopulateItemVolumes))]
    internal static class ShopPopulatePatch
    {
        static void Prefix() => McShop.ShopPopulateBegin();
        static void Postfix() => McShop.ShopPopulateEnd();
    }

    [HarmonyPatch(typeof(PunManager), nameof(PunManager.TruckPopulateItemVolumes))]
    internal static class TruckPopulatePatch
    {
        static void Prefix() => McShop.RegisterPrefabs();
    }

    [HarmonyPatch(typeof(PunManager), nameof(PunManager.SpawnShopItem))]
    internal static class SpawnShopItemPatch
    {
        static bool Prefix(ItemVolume itemVolume, List<Item> itemList, ref bool __result, out List<Item> __state)
        {
            __state = null;
            if (McShop.Blocks(itemVolume)) { __result = false; return false; }
            __state = new List<Item>(itemList);
            return true;
        }

        static void Postfix(ItemVolume itemVolume, List<Item> itemList, bool __result, List<Item> __state)
        {
            if (!__result || __state == null) return;
            // SpawnShopItem removes the item it spawned from the list
            Item spawned = null;
            for (int i = 0; i < __state.Count; i++)
                if (i >= itemList.Count || itemList[i] != __state[i]) { spawned = __state[i]; break; }
            McShop.OnShopItemSpawned(itemVolume, spawned);
        }
    }
}
