using System.Collections.Generic;
using UnityEngine;

namespace MinecraftRepo
{
    /// Pickaxe/Sword/Mc are virtual Minecraft items (a count, no world object). Valuable/RepoItem stacks hold
    /// real PhysGrabObjects kept deactivated by Storage while they sit in the inventory.
    internal enum ItemKind { Pickaxe, Sword, Valuable, RepoItem, Mc }

    /// One inventory slot's contents.
    internal class ItemStack
    {
        public ItemKind Kind;
        public string Key;
        public string Name;
        public string Id;      // Minecraft item id for Kind.Mc ("tnt", "bow", ...), and for shop objects holding one
        public int VCount = 1; // count of a virtual stack
        public int Damage;     // Minecraft durability used up (see Durability)
        public float Charge;        // fraction of a durability point recharged at the charging station
        public float ChargingUntil; // Time.time until which the HUD shows it charging
        public int ShownBars = -1;  // battery segments the HUD last drew, for R.E.P.O.'s bar loss/gain flash
        public float BarFlashAt = -1f;
        public bool BarGained;
        public bool Single;    // R.E.P.O. objects with their own battery (weapons, drones) don't stack, like damageable items
        public string Loaded;  // a crossbow's charged projectile ("arrow", "firework_rocket"), null when not charged
        public readonly List<PhysGrabObject> Objects = new List<PhysGrabObject>();

        public bool IsTool => Kind == ItemKind.Pickaxe || Kind == ItemKind.Sword;
        public bool IsVirtual => IsTool || Kind == ItemKind.Mc;
        public bool IsMc(string id) => Kind == ItemKind.Mc && Id == id;
        public int Count => IsVirtual ? VCount : Objects.Count;
        public int MaxStack => IsTool || Single ? 1 : Kind == ItemKind.Mc ? McItems.MaxStack(Id) : Kind == ItemKind.Valuable ? 64 : 16;
        public bool IsEmpty => Count <= 0;

        public Texture2D Icon => Kind == ItemKind.Pickaxe ? McAssets.Pickaxe : Kind == ItemKind.Sword ? McAssets.Sword
            : Kind == ItemKind.Mc ? McItems.StateSprite(this) ?? McItems.Icon(Id) : Id != null ? McItems.Icon(Id) : IconCache.Get(this);

        public static ItemStack Tool(ItemKind k) => new ItemStack
        {
            Kind = k, Key = k.ToString(), Name = k == ItemKind.Pickaxe ? "Diamond Pickaxe" : "Diamond Sword",
        };

        public static ItemStack Mc(string id, int count = 1) => new ItemStack
        {
            Kind = ItemKind.Mc, Id = id, Key = "mc:" + id, Name = McItems.Name(id), VCount = count,
        };

        public static ItemStack ForObject(PhysGrabObject pgo)
        {
            var s = new ItemStack();
            var val = pgo.GetComponent<ValuableObject>();
            var item = pgo.GetComponent<ItemAttributes>();
            s.Kind = val ? ItemKind.Valuable : ItemKind.RepoItem;
            s.Key = KeyOf(pgo);
            s.Name = NameOf(pgo, val, item);
            var shop = pgo.GetComponent<McShopItem>();
            if (shop) s.Id = shop.Id;
            s.Single = pgo.GetComponent<ItemBattery>() || pgo.GetComponent<ItemGun>() || pgo.GetComponent<ItemMelee>();
            s.Objects.Add(pgo);
            return s;
        }

        public static string KeyOf(PhysGrabObject pgo)
        {
            var n = pgo.gameObject.name.Replace("(Clone)", "").Trim();
            return n;
        }

        internal static string NameOf(PhysGrabObject pgo, ValuableObject val, ItemAttributes item)
        {
            if (item && item.item) return string.IsNullOrEmpty(item.itemName) ? item.item.itemName : item.itemName;
            var n = KeyOf(pgo);
            if (n.StartsWith("Valuable ")) n = n.Substring(9);
            if (n.StartsWith("Item ")) n = n.Substring(5);
            return n;
        }

        public float Value
        {
            get
            {
                float v = 0;
                foreach (var o in Objects)
                    if (o && o.TryGetComponent<ValuableObject>(out var val)) v += val.dollarValueCurrent;
                return v;
            }
        }

        /// R.E.P.O. shop goods not paid for yet (`ItemAttributes.shopItem`: spawned in the shop; bought at the extraction
        /// point from the truck).
        public bool Unpurchased
        {
            get
            {
                if (IsVirtual) return false;
                foreach (var o in Objects)
                    if (o && o.TryGetComponent<ItemAttributes>(out var a) && a.shopItem) return true;
                return false;
            }
        }

        /// The shop price of the unpaid items, in thousands of dollars like R.E.P.O.'s shop.
        public int PriceK
        {
            get
            {
                int k = 0;
                foreach (var o in Objects)
                    if (o && o.TryGetComponent<ItemAttributes>(out var a) && a.shopItem) k += a.value;
                return k;
            }
        }

        public bool CanMerge(ItemStack other) => other != null && other != this && MaxStack > 1 && Key == other.Key && Kind == other.Kind
            && Unpurchased == other.Unpurchased;

        public ItemStack Split(int n)
        {
            var s = new ItemStack { Kind = Kind, Key = Key, Name = Name, Id = Id, VCount = 0, Damage = Damage, Single = Single, Loaded = Loaded };
            if (IsVirtual)
            {
                n = Mathf.Clamp(n, 0, VCount);
                s.VCount = n;
                VCount -= n;
                return s;
            }
            for (int i = 0; i < n && Objects.Count > 0; i++)
            {
                s.Objects.Add(Objects[Objects.Count - 1]);
                Objects.RemoveAt(Objects.Count - 1);
            }
            return s;
        }

        /// Moves up to n items from `from` into this stack (respecting MaxStack). Returns how many moved.
        public int Absorb(ItemStack from, int n)
        {
            if (!CanMerge(from)) return 0;
            n = Mathf.Min(n, Mathf.Min(from.Count, MaxStack - Count));
            if (n <= 0) return 0;
            if (IsVirtual) { VCount += n; from.VCount -= n; return n; }
            for (int i = 0; i < n; i++)
            {
                Objects.Add(from.Objects[from.Objects.Count - 1]);
                from.Objects.RemoveAt(from.Objects.Count - 1);
            }
            return n;
        }
    }

    /// Minecraft's player inventory: 0-8 hotbar, 9-35 main, 40 off hand (36-39 would be armor).
    internal class McInventory
    {
        public const int OffhandSlot = 40;
        public readonly ItemStack[] Slots = new ItemStack[41];
        public int Selected;
        public ItemStack Cursor; // stack carried by the mouse in the inventory screen

        public ItemStack Held => Slots[Selected];
        public ItemStack Offhand { get => Slots[OffhandSlot]; set => Slots[OffhandSlot] = value; }

        /// Hand 0 = main, 1 = off hand.
        public ItemStack Hand(int hand) => hand == 0 ? Held : Offhand;
        public int HandSlot(int hand) => hand == 0 ? Selected : OffhandSlot;
        public void SetHand(int hand, ItemStack s) { if (hand == 0) Slots[Selected] = s; else Offhand = s; }

        /// Removes `n` items from a hand's stack, clearing the slot when it runs out.
        public void ConsumeHand(int hand, int n = 1)
        {
            var s = Hand(hand);
            if (s == null) return;
            if (s.IsVirtual) s.VCount -= n;
            else for (int i = 0; i < n && s.Objects.Count > 0; i++) s.Objects.RemoveAt(s.Objects.Count - 1);
            if (s.Count <= 0) SetHand(hand, null);
        }

        public void SwapHands()
        {
            var m = Slots[Selected];
            Slots[Selected] = Offhand;
            Offhand = m;
        }

        public void GiveStarterKit()
        {
            // R.E.P.O.'s flashlight became an item, so everyone gets it back each level
            if (Count(s => s.IsMc("flashlight")) == 0) Add(ItemStack.Mc("flashlight"));
            if (!Plugin.StarterKit.Value) return;
            if (Count(s => s.Kind == ItemKind.Pickaxe) == 0) Add(ItemStack.Tool(ItemKind.Pickaxe));
            if (Count(s => s.Kind == ItemKind.Sword) == 0) Add(ItemStack.Tool(ItemKind.Sword));
            if (McShop.Enabled) return; // the Minecraft items are bought in the shop
            if (Offhand == null && Count(s => s.IsMc("shield")) == 0) Offhand = ItemStack.Mc("shield");
            foreach (var kv in McItems.StarterKit)
            {
                int have = Count(s => s.IsMc(kv.Key));
                if (have < kv.Value) Add(ItemStack.Mc(kv.Key, kv.Value - have), kv.Key == "arrow" ? 9 : 0);
            }
        }

        public int Count(System.Func<ItemStack, bool> match)
        {
            int n = 0;
            foreach (var s in Slots) if (s != null && match(s)) n += s.Count;
            if (Cursor != null && match(Cursor)) n += Cursor.Count;
            return n;
        }

        /// Adds as much as fits; returns true when everything fit. Leftovers stay in `stack`.
        /// Like Minecraft: fills the held stack, then the off hand, then other matching stacks, then empty slots.
        public bool Add(ItemStack stack, int firstEmpty = 0)
        {
            if (stack == null || stack.Count <= 0) return true;
            if (Held != null) Held.Absorb(stack, stack.Count);
            if (Offhand != null) Offhand.Absorb(stack, stack.Count);
            for (int i = 0; i < 36 && stack.Count > 0; i++)
                if (Slots[i] != null) Slots[i].Absorb(stack, stack.Count);
            for (int k = 0; k < 36 && stack.Count > 0; k++)
            {
                int i = (k + firstEmpty) % 36;
                if (Slots[i] != null) continue;
                Slots[i] = stack.Count <= stack.MaxStack ? TakeAll(stack) : stack.Split(stack.MaxStack);
            }
            return stack.Count == 0;
        }

        // keeps the original object when the whole stack fits (tools keep their identity)
        static ItemStack TakeAll(ItemStack s)
        {
            var copy = new ItemStack { Kind = s.Kind, Key = s.Key, Name = s.Name, Id = s.Id, VCount = s.VCount, Damage = s.Damage, Single = s.Single, Loaded = s.Loaded };
            copy.Objects.AddRange(s.Objects);
            s.Objects.Clear();
            s.VCount = 0;
            return copy;
        }

        public bool HasRoomFor(ItemStack stack)
        {
            for (int i = 0; i < 36; i++)
            {
                var s = Slots[i];
                if (s == null) return true;
                if (s.CanMerge(stack) && s.Count < s.MaxStack) return true;
            }
            return Offhand != null && Offhand.CanMerge(stack) && Offhand.Count < Offhand.MaxStack;
        }

        /// Finds ammo the Minecraft way: off hand, main hand, then the rest of the inventory.
        public int FindSlot(System.Func<ItemStack, bool> match)
        {
            if (Offhand != null && match(Offhand)) return OffhandSlot;
            if (Held != null && match(Held)) return Selected;
            for (int i = 0; i < 36; i++) if (Slots[i] != null && match(Slots[i])) return i;
            return -1;
        }

        public void ConsumeSlot(int i, int n = 1)
        {
            var s = Slots[i];
            if (s == null) return;
            if (s.IsVirtual) s.VCount -= n;
            else for (int k = 0; k < n && s.Objects.Count > 0; k++) s.Objects.RemoveAt(s.Objects.Count - 1);
            if (s.Count <= 0) Slots[i] = null;
        }

        /// Drops references to objects that R.E.P.O. destroyed (level change, extraction, etc.).
        public void Prune()
        {
            for (int i = 0; i < Slots.Length; i++) Slots[i] = PruneStack(Slots[i]);
            Cursor = PruneStack(Cursor);
        }

        static ItemStack PruneStack(ItemStack s)
        {
            if (s == null) return null;
            if (!s.IsVirtual) s.Objects.RemoveAll(o => o == null);
            return s.Count <= 0 ? null : s;
        }

        public IEnumerable<PhysGrabObject> AllObjects()
        {
            foreach (var s in Slots)
                if (s != null) foreach (var o in s.Objects) yield return o;
            if (Cursor != null) foreach (var o in Cursor.Objects) yield return o;
        }

        public float TotalValue()
        {
            float v = 0;
            foreach (var s in Slots) if (s != null) v += s.Value;
            return v;
        }
    }
}
