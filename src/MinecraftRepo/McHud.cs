using System.Collections.Generic;
using UnityEngine;

namespace MinecraftRepo
{
    /// Minecraft's in-game GUI drawn with IMGUI in Minecraft GUI pixels: crosshair + attack indicator, hotbar,
    /// hearts, hunger, XP bar, held-item name, scoreboard sidebar, and the E inventory screen with tooltips.
    internal class McHud : MonoBehaviour
    {
        float s; // GUI scale
        float W, H; // screen size in GUI pixels

        static readonly Color TextGray = new Color(0.25f, 0.25f, 0.25f);
        static readonly Color XpGreen = new Color(0.5f, 1f, 0.125f);
        static readonly Color UnpaidRed = new Color(1f, 0.2f, 0.2f);

        /// The inventory slot under the mouse while the inventory screen is open, -1 otherwise.
        public static int HoveredSlot = -1;
        System.Random shake = new System.Random();

        void OnGUI()
        {
            var p = McPlayer.Instance;
            if (p == null || !p.InventoryOpen) HoveredSlot = -1;
            if (p == null || !p.Active) return;
            // OnGUI draws over every canvas, so R.E.P.O.'s pause menu (and its settings pages) would sit under the HUD
            if (MenuManager.instance && MenuManager.instance.currentMenuPage) return;
            GUI.depth = -1000;

            int scale = Plugin.GuiScale.Value;
            if (scale <= 0)
            {
                scale = 1;
                while (scale < 6 && Screen.width / (scale + 1) >= 320 && Screen.height / (scale + 1) >= 240) scale++;
            }
            s = scale;
            W = Screen.width / s;
            H = Screen.height / s;
            IconCache.PixelSize = 16 * scale; // icons at the slot's real pixel size

            if (Event.current.type == EventType.Repaint || Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp)
            {
                if (Event.current.type == EventType.Repaint) DrawHud(p);
                if (p.InventoryOpen) InventoryScreen(p);
            }
        }

        // ---------- drawing helpers (all coordinates in GUI pixels) ----------

        void Tex(float x, float y, float w, float h, Texture2D t, Color fallback)
        {
            var r = new Rect(x * s, y * s, w * s, h * s);
            if (t) GUI.DrawTexture(r, t);
            else { var c = GUI.color; GUI.color = fallback; GUI.DrawTexture(r, McAssets.White); GUI.color = c; }
        }

        void TexUV(float x, float y, float w, float h, Texture2D t, Rect uv, Color fallback)
        {
            var r = new Rect(x * s, y * s, w * s, h * s);
            if (t) GUI.DrawTextureWithTexCoords(r, t, uv);
            else { var c = GUI.color; GUI.color = fallback; GUI.DrawTexture(r, McAssets.White); GUI.color = c; }
        }

        void Fill(float x, float y, float w, float h, Color c)
        {
            var prev = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(x * s, y * s, w * s, h * s), McAssets.White);
            GUI.color = prev;
        }

        void Text(string t, float x, float y, Color c, bool shadow = true) => McFont.Draw(t, x * s, y * s, s, c, shadow);
        void TextCentered(string t, float cx, float y, Color c) => Text(t, cx - McFont.Width(t) / 2f, y, c);

        void ItemIcon(ItemStack st, float x, float y)
        {
            if (st == null) return;
            var icon = st.Icon;
            if (icon) GUI.DrawTexture(new Rect(x * s, y * s, 16 * s, 16 * s), icon);
            // shop goods not paid for yet: a red frame around the slot
            if (st.Unpurchased)
            {
                Fill(x - 1, y - 1, 18, 1, UnpaidRed); Fill(x - 1, y + 16, 18, 1, UnpaidRed);
                Fill(x - 1, y, 1, 16, UnpaidRed); Fill(x + 16, y, 1, 16, UnpaidRed);
            }
            // Minecraft's item cooldown: a white veil that drains downward
            var p = McPlayer.Instance;
            if (p && st.IsMc("ender_pearl") && p.PearlCooldown01 > 0f)
            {
                float f = p.PearlCooldown01;
                Fill(x, y + 16f * (1f - f), 16, 16f * f, new Color(1, 1, 1, 0.5f));
            }
            if (st.Count > 1)
            {
                var n = st.Count.ToString();
                Text(n, x + 17 - McFont.Width(n), y + 9, Color.white);
            }
            if (Durability.Bars(st, out int bars, out int lit)) Battery(st, x, y, bars, lit);
        }

        // R.E.P.O.'s battery colours (BatteryVisualLogic)
        static readonly Color BatteryYellow = new Color(1f, 1f, 0f);
        static readonly Color BatteryBackground = new Color(0.1f, 0.1f, 0.1f);
        static readonly Color BatteryShadow = new Color(0.1f, 0.1f, 0.1f, 0.5f);
        static readonly Color BatteryCharge = new Color(0f, 1f, 0f);

        /// Fill with its edges snapped to screen pixels (coordinates in GUI pixels).
        void FillSnapped(float x0, float y0, float x1, float y1, Color c)
        {
            var r = Rect.MinMaxRect(Mathf.Round(x0 * s), Mathf.Round(y0 * s), Mathf.Round(x1 * s), Mathf.Round(y1 * s));
            if (r.width <= 0f || r.height <= 0f) return;
            var prev = GUI.color; GUI.color = c;
            GUI.DrawTexture(r, McAssets.White);
            GUI.color = prev;
        }

        /// R.E.P.O.'s item battery where Minecraft draws its durability bar (along the bottom of the slot): a yellow
        /// battery with a shadow, a dark inside and one segment per bar, red with none left. Like R.E.P.O., the last bar
        /// blinks red when one is used up, a regained bar flashes green, and the next bar pulses green while charging.
        void Battery(ItemStack st, float x, float y, int bars, int lit)
        {
            if (st.ShownBars >= 0 && lit != st.ShownBars)
            {
                st.BarFlashAt = Time.time;
                st.BarGained = lit > st.ShownBars;
            }
            st.ShownBars = lit;

            const float u = 0.5f; // half a GUI pixel
            float bx = x + 3 * u, by = y + 24 * u, bw = 25 * u, bh = 7 * u; // body
            float tx = bx + bw, ty = by + 2 * u, tw = 2 * u, th = 3 * u;     // tip (the + pole)
            var main = lit > 0 ? BatteryYellow : Color.red;

            FillSnapped(bx + u, by + u, bx + bw + u, by + bh + u, BatteryShadow);
            FillSnapped(tx + u, ty + u, tx + tw + u, ty + th + u, BatteryShadow);
            FillSnapped(bx, by, bx + bw, by + bh, main);
            FillSnapped(tx, ty, tx + tw, ty + th, main);
            FillSnapped(bx + u, by + u, bx + bw - u, by + bh - u, BatteryBackground);

            float ix = bx + 2 * u, iy = by + 2 * u, iw = bw - 4 * u, ih = bh - 4 * u;
            float gap = bars <= 8 ? u : u * 0.5f;
            float seg = (iw - gap * (bars - 1)) / bars;
            float since = Time.time - st.BarFlashAt;
            bool charging = st.ChargingUntil > Time.time && lit < bars;
            for (int i = 0; i < bars; i++)
            {
                Color c;
                if (i < lit)
                {
                    c = BatteryYellow;
                    if (st.BarGained && i == lit - 1 && since < 0.3f) c = BatteryCharge;
                }
                else if (charging && i == lit) c = Color.Lerp(BatteryBackground, BatteryCharge, Mathf.PingPong(Time.time * 3f, 1f));
                else continue;
                float sx = ix + i * (seg + gap);
                FillSnapped(sx, iy, sx + seg, iy + ih, c);
            }
            // SmallBatteryDeductionEffect: the last lit bar blinks red at 8 Hz for 0.4 s
            if (!st.BarGained && since < 0.4f && Mathf.FloorToInt(since * 8f) % 2 == 0)
            {
                int i = Mathf.Max(0, lit - 1);
                float sx = ix + i * (seg + gap);
                FillSnapped(sx, iy, sx + seg, iy + ih, Color.red);
            }
        }

        static string Money(float v) => "$" + Mathf.RoundToInt(v).ToString("N0");

        // ---------- HUD ----------

        void DrawHud(McPlayer p)
        {
            float cx = W / 2f;

            // first-person hands and held items, rendered at full resolution by SteveManager
            var hands = SteveManager.HandTexture;
            if (hands) GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), hands);

            // on fire: Minecraft's first-person flames at the bottom corners
            if (Burning.PlayerOnFire && SteveManager.ViewMode == 0)
            {
                var frame = FireBlock.Frame(McAssets.Fire1, Time.time, 0);
                var prev = GUI.color;
                GUI.color = new Color(1, 1, 1, 0.9f);
                float fw = W * 0.45f, fh = H * 0.55f;
                GUI.DrawTexture(new Rect(-W * 0.02f * s, (H - fh * 0.8f) * s, fw * s, fh * s), frame);
                GUI.DrawTexture(new Rect((W - fw + W * 0.02f) * s, (H - fh * 0.8f) * s, fw * s, fh * s), frame);
                GUI.color = prev;
            }

            // crosshair and attack indicator
            if (SteveManager.ViewMode == 0 && !p.InventoryOpen)
            {
                Tex(cx - 7.5f, H / 2f - 7.5f, 15, 15, McAssets.Crosshair, Color.white);
                float charge = p.ThrowCharge >= 0f ? p.ThrowCharge : p.AttackCharge;
                if (p.ThrowCharge >= 0f && charge >= 1f)
                    Tex(cx - 8, H / 2f - 7 + 8, 16, 16, McAssets.AttackFull, Color.white);
                else if (charge < 1f)
                {
                    Tex(cx - 8, H / 2f - 7 + 16, 16, 4, McAssets.AttackBackground, new Color(0, 0, 0, 0.5f));
                    TexUV(cx - 8, H / 2f - 7 + 16, 16 * charge, 4, McAssets.AttackProgress, new Rect(0, 0, charge, 1), Color.white);
                }
                else
                {
                    var t = p.Raycast(McPlayer.AttackReach);
                    if (t.Enemy) Tex(cx - 8, H / 2f - 7 + 8, 16, 16, McAssets.AttackFull, Color.white);
                }
            }

            // hotbar
            float hx = cx - 91, hy = H - 22;
            Tex(hx, hy, 182, 22, McAssets.Hotbar, new Color(0, 0, 0, 0.5f));
            Tex(hx - 1 + p.Inventory.Selected * 20, hy - 1, 24, 23, McAssets.HotbarSelection, new Color(1, 1, 1, 0.5f));
            for (int i = 0; i < 9; i++) ItemIcon(p.Inventory.Slots[i], hx + 3 + i * 20, hy + 3);

            // off-hand slot, left of the hotbar like Minecraft
            var off = p.Inventory.Offhand;
            if (off != null)
            {
                Tex(hx - 29, H - 23, 29, 24, McAssets.HotbarOffhand, new Color(0, 0, 0, 0.5f));
                ItemIcon(off, hx - 26, H - 19);
            }

            // XP bar: level = thousands of dollars carried, progress = the remainder
            float total = p.Inventory.TotalValue();
            int level = Mathf.FloorToInt(total / 1000f);
            float prog = (total % 1000f) / 1000f;
            float xy = H - 29;
            Tex(hx, xy, 182, 5, McAssets.XpBackground, new Color(0, 0, 0, 0.6f));
            if (prog > 0) TexUV(hx, xy, 182 * prog, 5, McAssets.XpProgress, new Rect(0, 0, prog, 1), XpGreen);
            if (level > 0)
            {
                var l = level.ToString();
                float lx = cx - McFont.Width(l) / 2f, ly = H - 31 - 4;
                foreach (var o in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
                    Text(l, lx + o.x, ly + o.y, Color.black, false);
                Text(l, lx, ly, XpGreen, false);
            }

            // hearts (R.E.P.O. health, 5 HP per half heart at 100 max)
            var ph = PlayerAvatar.instance ? PlayerAvatar.instance.playerHealth : null;
            if (ph)
            {
                int max = Mathf.Max(1, ph.maxHealth);
                int points = Mathf.CeilToInt(Mathf.Clamp01(ph.health / (float)max) * 20f);
                for (int i = 0; i < 10; i++)
                {
                    float x = hx + i * 8, y = H - 39;
                    if (points <= 4) y += shake.Next(2);
                    Tex(x, y, 9, 9, McAssets.HeartContainer, new Color(0.2f, 0, 0));
                    if (points >= i * 2 + 2) Tex(x, y, 9, 9, McAssets.HeartFull, Color.red);
                    else if (points == i * 2 + 1) Tex(x, y, 9, 9, McAssets.HeartHalf, new Color(0.7f, 0, 0));
                }
            }

            // hunger (R.E.P.O. stamina)
            var pc = PlayerController.instance;
            if (pc)
            {
                int points = Mathf.CeilToInt(Mathf.Clamp01(pc.EnergyCurrent / Mathf.Max(1f, pc.EnergyStart)) * 20f);
                for (int i = 0; i < 10; i++)
                {
                    float x = cx + 91 - i * 8 - 9, y = H - 39;
                    Tex(x, y, 9, 9, McAssets.FoodEmpty, new Color(0.2f, 0.1f, 0));
                    if (points >= i * 2 + 2) Tex(x, y, 9, 9, McAssets.FoodFull, new Color(0.8f, 0.5f, 0.2f));
                    else if (points == i * 2 + 1) Tex(x, y, 9, 9, McAssets.FoodHalf, new Color(0.6f, 0.4f, 0.1f));
                }
            }

            // held item name, fading out like Minecraft
            float since = Time.time - p.SlotChangedAt;
            var held = p.Inventory.Held;
            if (held != null && since < 2.5f)
            {
                float a = Mathf.Clamp01((2.5f - since) / 0.5f);
                var name = held.Name + (held.Kind == ItemKind.Valuable ? "  " + Money(held.Value) : "");
                TextCentered(name, cx, H - 59, new Color(1, 1, 1, a));
            }

            ActionBar(cx);
            Sidebar(p);
        }

        // ---------- action bar ----------

        public const int ActionInfo = 0, ActionValue = 1, ActionExtra = 2;
        static readonly string[] actionText = new string[3];
        static readonly Color[] actionColor = new Color[3];
        static readonly float[] actionAt = { -10f, -10f, -10f };
        static readonly System.Text.RegularExpressions.Regex RichTags = new System.Text.RegularExpressions.Regex("<[^>]*>");

        /// Puts one part of R.E.P.O.'s item prompt on Minecraft's action bar: ActionInfo (ItemInfoUI: what you look at or
        /// hold), ActionValue (WorldSpaceUIValue: its value or price), ActionExtra (ItemInfoExtraUI, e.g. a cart's mode).
        /// R.E.P.O. repeats its prompts every frame while they apply; a part fades once they stop.
        public static void ShowAction(int part, string text, Color color)
        {
            text = text == null ? null : RichTags.Replace(text, "").Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(text)) return;
            actionText[part] = text;
            actionColor[part] = color;
            actionAt[part] = Time.time;
        }

        /// Minecraft's overlay message: centred 68 GUI pixels above the bottom, shadowed text, no backdrop.
        void ActionBar(float cx)
        {
            const float hold = 0.15f, fade = 0.5f, gap = 8f;
            float width = 0f;
            int shown = 0;
            for (int i = 0; i < 3; i++)
            {
                if (actionText[i] == null || Time.time - actionAt[i] > hold + fade) continue;
                width += McFont.Width(actionText[i]) + (shown > 0 ? gap : 0f);
                shown++;
            }
            if (shown == 0) return;
            float x = Mathf.Round(cx - width / 2f), y = H - 72;
            bool first = true;
            for (int i = 0; i < 3; i++)
            {
                float since = Time.time - actionAt[i];
                if (actionText[i] == null || since > hold + fade) continue;
                if (!first) x += gap;
                first = false;
                var c = actionColor[i];
                c.a *= Mathf.Clamp01(1f - (since - hold) / fade);
                Text(actionText[i], x, y, c);
                x += McFont.Width(actionText[i]);
            }
        }

        void Sidebar(McPlayer p)
        {
            var rd = RoundDirector.instance;
            var lines = new List<KeyValuePair<string, string>>();
            if (rd && SemiFunc.RunIsLevel())
            {
                if (rd.extractionPointActive)
                    lines.Add(new KeyValuePair<string, string>("Extraction", Money(rd.currentHaul) + "/" + Money(rd.extractionHaulGoal)));
                lines.Add(new KeyValuePair<string, string>("Goal", Money(rd.haulGoal)));
                lines.Add(new KeyValuePair<string, string>("Extracted", Money(rd.totalHaul)));
                lines.Add(new KeyValuePair<string, string>("Points", rd.extractionPointsCompleted + "/" + rd.extractionPoints));
            }
            lines.Add(new KeyValuePair<string, string>("Carrying", Money(p.Inventory.TotalValue())));

            const string title = "R.E.P.O.";
            float width = McFont.Width(title);
            foreach (var l in lines) width = Mathf.Max(width, McFont.Width(l.Key + ": ") + McFont.Width(l.Value));
            float right = W - 3, left = right - width - 2;
            float y = H / 2f - (lines.Count + 1) * 9 / 2f;
            Fill(left - 2, y, width + 4, 9, new Color(0, 0, 0, 0.4f));
            TextCentered(title, (left + right) / 2f, y + 1, Color.white);
            y += 9;
            foreach (var l in lines)
            {
                Fill(left - 2, y, width + 4, 9, new Color(0, 0, 0, 0.3f));
                Text(l.Key, left, y, new Color(0.67f, 0.67f, 0.67f));
                Text(l.Value, right - McFont.Width(l.Value), y, new Color(1f, 0.33f, 0.33f));
                y += 9;
            }
        }

        // ---------- inventory screen ----------

        const float InvW = 176, InvH = 166;

        static readonly int[] ScreenSlots = BuildScreenSlots();

        static int[] BuildScreenSlots()
        {
            var list = new int[37];
            for (int i = 0; i < 36; i++) list[i] = i;
            list[36] = McInventory.OffhandSlot;
            return list;
        }

        Vector2 SlotPos(int i, float ox, float oy)
        {
            if (i == McInventory.OffhandSlot) return new Vector2(ox + 77, oy + 62);
            if (i < 9) return new Vector2(ox + 8 + i * 18, oy + 142);
            int j = i - 9;
            return new Vector2(ox + 8 + (j % 9) * 18, oy + 84 + (j / 9) * 18);
        }

        void InventoryScreen(McPlayer p)
        {
            var e = Event.current;
            float ox = Mathf.Floor((W - InvW) / 2f), oy = Mathf.Floor((H - InvH) / 2f);
            var mouse = e.mousePosition / s;
            var inv = p.Inventory;

            HoveredSlot = -1;
            foreach (int i in ScreenSlots)
            {
                var sp = SlotPos(i, ox, oy);
                if (mouse.x >= sp.x - 1 && mouse.x < sp.x + 17 && mouse.y >= sp.y - 1 && mouse.y < sp.y + 17) HoveredSlot = i;
            }

            if (e.type == EventType.MouseDown)
            {
                bool inside = mouse.x >= ox && mouse.x < ox + InvW && mouse.y >= oy && mouse.y < oy + InvH;
                if (HoveredSlot >= 0) ClickSlot(inv, HoveredSlot, e.button, e.shift);
                else if (!inside && inv.Cursor != null)
                {
                    // click outside the window drops the carried stack (right click drops one)
                    if (e.button == 1 && inv.Cursor.Count > 1) p.ThrowStack(inv.Cursor.Split(1));
                    else { p.ThrowStack(inv.Cursor); inv.Cursor = null; }
                }
                e.Use();
                return;
            }
            if (e.type != EventType.Repaint) return;

            // Minecraft darkens the world behind container screens
            Fill(0, 0, W, H, new Color(0.06f, 0.06f, 0.06f, 0.75f));
            TexUV(ox, oy, InvW, InvH, McAssets.InventoryBackground, new Rect(0, 1 - InvH / 256f, InvW / 256f, InvH / 256f), new Color(0.78f, 0.78f, 0.78f));
            if (!McAssets.InventoryBackground)
                foreach (int i in ScreenSlots) { var sp = SlotPos(i, ox, oy); Fill(sp.x - 1, sp.y - 1, 18, 18, new Color(0.55f, 0.55f, 0.55f)); }
            Text("Crafting", ox + 97, oy + 8, TextGray, false);
            DrawPreview(ox + 26, oy + 8, mouse);

            if (inv.Offhand == null && McAssets.EmptyShieldSlot)
            {
                var sp = SlotPos(McInventory.OffhandSlot, ox, oy);
                Tex(sp.x, sp.y, 16, 16, McAssets.EmptyShieldSlot, Color.clear);
            }
            foreach (int i in ScreenSlots)
            {
                var sp = SlotPos(i, ox, oy);
                ItemIcon(inv.Slots[i], sp.x, sp.y);
                if (i == HoveredSlot) Fill(sp.x, sp.y, 16, 16, new Color(1, 1, 1, 0.5f));
            }

            if (inv.Cursor != null) ItemIcon(inv.Cursor, mouse.x - 8, mouse.y - 8);
            else if (HoveredSlot >= 0 && inv.Slots[HoveredSlot] != null) Tooltip(inv.Slots[HoveredSlot], mouse);
        }

        void ClickSlot(McInventory inv, int i, int button, bool shift)
        {
            var slot = inv.Slots[i];
            if (shift && inv.Cursor == null && slot != null)
            {
                // shift-click moves between hotbar and main inventory (off hand goes to the main inventory)
                inv.Slots[i] = null;
                int from = i < 9 || i == McInventory.OffhandSlot ? 9 : 0, to = i < 9 || i == McInventory.OffhandSlot ? 36 : 9;
                for (int k = from; k < to && slot.Count > 0; k++)
                {
                    var t = inv.Slots[k];
                    if (t != null) t.Absorb(slot, slot.Count);
                }
                for (int k = from; k < to && slot.Count > 0; k++)
                    if (inv.Slots[k] == null) { inv.Slots[k] = slot; slot = null; break; }
                if (slot != null && slot.Count > 0) inv.Slots[i] = slot;
                return;
            }
            if (button == 0)
            {
                if (inv.Cursor != null && slot != null && slot.CanMerge(inv.Cursor))
                {
                    slot.Absorb(inv.Cursor, inv.Cursor.Count);
                    if (inv.Cursor.Count == 0) inv.Cursor = null;
                }
                else { inv.Slots[i] = inv.Cursor; inv.Cursor = slot; }
            }
            else if (button == 1)
            {
                if (inv.Cursor == null && slot != null)
                {
                    if (slot.Count == 1) { inv.Cursor = slot; inv.Slots[i] = null; }
                    else inv.Cursor = slot.Split((slot.Count + 1) / 2);
                }
                else if (inv.Cursor != null && slot == null)
                {
                    if (inv.Cursor.Count == 1) { inv.Slots[i] = inv.Cursor; inv.Cursor = null; }
                    else inv.Slots[i] = inv.Cursor.Split(1);
                }
                else if (inv.Cursor != null && slot.CanMerge(inv.Cursor) && slot.Count < slot.MaxStack)
                {
                    slot.Absorb(inv.Cursor, 1);
                    if (inv.Cursor.Count == 0) inv.Cursor = null;
                }
                else { inv.Slots[i] = inv.Cursor; inv.Cursor = slot; }
            }
        }

        void Tooltip(ItemStack st, Vector2 mouse)
        {
            var lines = new List<KeyValuePair<string, Color>> { new KeyValuePair<string, Color>(st.Name, Color.white) };
            if (st.Unpurchased)
            {
                lines.Add(new KeyValuePair<string, Color>("Not purchased: $" + st.PriceK + "K", UnpaidRed));
                lines.Add(new KeyValuePair<string, Color>("Put it in the truck to buy it", new Color(0.67f, 0.67f, 0.67f)));
            }
            if (st.Kind == ItemKind.Valuable)
            {
                lines.Add(new KeyValuePair<string, Color>("Value: " + Money(st.Value), new Color(1f, 0.67f, 0f)));
                if (st.Count > 1) lines.Add(new KeyValuePair<string, Color>("Each: " + Money(st.Value / st.Count), new Color(0.67f, 0.67f, 0.67f)));
            }
            else if (st.Kind == ItemKind.RepoItem) lines.Add(new KeyValuePair<string, Color>("R.E.P.O. item", new Color(0.33f, 0.33f, 1f)));
            else if (st.Kind == ItemKind.Sword) { lines.Add(new KeyValuePair<string, Color>("", Color.white)); lines.Add(new KeyValuePair<string, Color>(" 7 Attack Damage", new Color(0f, 0.67f, 0f))); lines.Add(new KeyValuePair<string, Color>(" 1.6 Attack Speed", new Color(0f, 0.67f, 0f))); }
            else if (st.Kind == ItemKind.Pickaxe) { lines.Add(new KeyValuePair<string, Color>("", Color.white)); lines.Add(new KeyValuePair<string, Color>(" 5 Attack Damage", new Color(0f, 0.67f, 0f))); lines.Add(new KeyValuePair<string, Color>(" 1.2 Attack Speed", new Color(0f, 0.67f, 0f))); }
            var melee = RepoWeapons.Melee(st);
            if (melee && !RepoWeapons.Broken(melee))
            {
                lines.Add(new KeyValuePair<string, Color>("", Color.white));
                lines.Add(new KeyValuePair<string, Color>(" " + RepoWeapons.Damage(melee).ToString("0.#") + " Attack Damage", new Color(0f, 0.67f, 0f)));
                lines.Add(new KeyValuePair<string, Color>(" " + (1f / RepoWeapons.Cooldown(melee)).ToString("0.#") + " Attack Speed", new Color(0f, 0.67f, 0f)));
            }
            else if (melee) lines.Add(new KeyValuePair<string, Color>("Broken: recharge it in the truck", new Color(1f, 0.33f, 0.33f)));
            if (st.IsMc("crossbow") && st.Loaded != null)
                lines.Add(new KeyValuePair<string, Color>("Projectile: [" + McItems.Name(st.Loaded) + "]", Color.white));
            if (st.IsMc("firework_rocket"))
                lines.Add(new KeyValuePair<string, Color>("Flight Duration: " + FireworkEntity.Flight, new Color(0.67f, 0.67f, 0.67f)));
            var dur = Durability.Tooltip(st);
            if (dur != null) lines.Add(new KeyValuePair<string, Color>(dur, Color.white));
            if (!melee && Durability.Empty(st)) lines.Add(new KeyValuePair<string, Color>("Empty: recharge it in the truck", new Color(1f, 0.33f, 0.33f)));

            float w = 0;
            foreach (var l in lines) w = Mathf.Max(w, McFont.Width(l.Key));
            float h = 8 + (lines.Count - 1) * 10 + (lines.Count > 1 ? 2 : 0);
            float x = mouse.x + 12, y = mouse.y - 12;
            if (x + w + 4 > W) x = mouse.x - 16 - w;
            var bg = new Color(0.063f, 0f, 0.063f, 0.94f);
            Fill(x - 3, y - 4, w + 6, 1, bg); Fill(x - 3, y + h + 3, w + 6, 1, bg);
            Fill(x - 3, y - 3, w + 6, h + 6, bg); Fill(x - 4, y - 3, 1, h + 6, bg); Fill(x + w + 3, y - 3, 1, h + 6, bg);
            var border = new Color(0.31f, 0f, 1f, 0.31f);
            Fill(x - 3, y - 3, w + 6, 1, border); Fill(x - 3, y + h + 2, w + 6, 1, border);
            Fill(x - 3, y - 2, 1, h + 4, border); Fill(x + w + 2, y - 2, 1, h + 4, border);
            for (int i = 0; i < lines.Count; i++)
            {
                Text(lines[i].Key, x, y, lines[i].Value);
                y += i == 0 ? 12 : 10;
            }
        }

        // 3D player in the inventory, turning toward the mouse like Minecraft
        RenderTexture previewRT;
        Camera previewCam;
        SteveModel preview;
        Light previewLight;

        void DrawPreview(float x, float y, Vector2 mouse)
        {
            if (Plugin.ReplaceWithSteve.Value) DrawStevePreview(x, y, mouse);
            else DrawAvatarPreview(x, y, mouse);
        }

        Camera avatarCam;
        Light avatarLight;
        RenderTexture avatarRT;
        readonly List<Renderer> avatarRenderers = new List<Renderer>();
        readonly List<Renderer> heldRenderers = new List<Renderer>();
        readonly HeldRig previewHeld = new HeldRig();
        static readonly bool[] bothHands = { true, true };
        readonly List<int> savedLayers = new List<int>();
        readonly List<UnityEngine.Rendering.ShadowCastingMode> savedShadows = new List<UnityEngine.Rendering.ShadowCastingMode>();
        readonly List<bool> savedForceOff = new List<bool>();

        /// The player's own R.E.P.O. robot (with cosmetics), rendered alone and turned toward the mouse.
        void DrawAvatarPreview(float x, float y, Vector2 mouse)
        {
            const float pw = 49, ph = 70;
            var vis = PlayerAvatar.instance ? PlayerAvatar.instance.playerAvatarVisuals : null;
            if (!vis || !vis.meshParent) return;
            if (!avatarCam)
            {
                var cgo = new GameObject("mc_inv_avatar_cam");
                DontDestroyOnLoad(cgo);
                avatarCam = cgo.AddComponent<Camera>();
                avatarCam.enabled = false;
                avatarCam.cullingMask = 1 << 31;
                avatarCam.clearFlags = CameraClearFlags.SolidColor;
                avatarCam.backgroundColor = new Color(0, 0, 0, 0);
                avatarCam.orthographic = true;
                avatarCam.allowHDR = false;
                avatarCam.nearClipPlane = 0.05f;
                avatarCam.farClipPlane = 20f;
                var lgo = new GameObject("light");
                lgo.transform.SetParent(cgo.transform, false);
                lgo.transform.localRotation = Quaternion.Euler(25, 20, 0);
                avatarLight = lgo.AddComponent<Light>();
                avatarLight.type = LightType.Directional;
                avatarLight.cullingMask = 1 << 31;
                avatarLight.enabled = false;
            }
            int rw = Mathf.RoundToInt(pw * s), rh = Mathf.RoundToInt(ph * s);
            if (!avatarRT || avatarRT.width != rw || avatarRT.height != rh)
            {
                if (avatarRT) avatarRT.Release();
                avatarRT = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGB32);
            }

            // the items in its hands are drawn too, but the framing stays the robot's
            heldRenderers.Clear();
            SteveManager.AddLocalHeld(heldRenderers);
            avatarRenderers.Clear();
            foreach (var r in vis.meshParent.GetComponentsInChildren<Renderer>())
                if (r.enabled && !(r is ParticleSystemRenderer) && !(r is TrailRenderer) && !(r is LineRenderer) && !heldRenderers.Contains(r)) avatarRenderers.Add(r);
            if (avatarRenderers.Count == 0) return;
            var b = avatarRenderers[0].bounds;
            foreach (var r in avatarRenderers) b.Encapsulate(r.bounds);
            avatarRenderers.AddRange(heldRenderers);

            // camera orbits in front of the body; the mouse turns it like Minecraft's inventory player
            float dx = (x + pw / 2f) - mouse.x, dy = (y + ph * 0.3f) - mouse.y;
            var fwd = Vector3.ProjectOnPlane(vis.transform.forward, Vector3.up).normalized;
            var orbit = Quaternion.AngleAxis(-Mathf.Atan(dx / 40f) * 40f, Vector3.up) * Quaternion.AngleAxis(Mathf.Atan(dy / 40f) * 15f, Vector3.Cross(Vector3.up, fwd));
            var dir = orbit * fwd;
            avatarCam.transform.position = b.center + dir * 5f;
            avatarCam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
            avatarCam.orthographicSize = Mathf.Max(b.size.y * 0.55f, b.size.x * 0.75f * ph / pw * 0.5f);
            avatarCam.aspect = pw / ph;

            savedLayers.Clear(); savedShadows.Clear(); savedForceOff.Clear();
            foreach (var r in avatarRenderers)
            {
                savedLayers.Add(r.gameObject.layer); savedShadows.Add(r.shadowCastingMode); savedForceOff.Add(r.forceRenderingOff);
                r.gameObject.layer = 31; r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; r.forceRenderingOff = false;
            }
            var fog = RenderSettings.fog; var amb = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode;
            RenderSettings.fog = false; RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
            avatarLight.enabled = true;
            avatarCam.targetTexture = avatarRT;
            avatarCam.Render();
            avatarCam.targetTexture = null;
            avatarLight.enabled = false;
            RenderSettings.fog = fog; RenderSettings.ambientMode = mode; RenderSettings.ambientLight = amb;
            for (int i = 0; i < avatarRenderers.Count; i++)
            {
                var r = avatarRenderers[i];
                if (!r) continue;
                r.gameObject.layer = savedLayers[i]; r.shadowCastingMode = savedShadows[i]; r.forceRenderingOff = savedForceOff[i];
            }

            GUI.DrawTexture(new Rect(x * s, y * s, pw * s, ph * s), avatarRT);
        }

        void DrawStevePreview(float x, float y, Vector2 mouse)
        {
            const float pw = 49, ph = 70;
            if (!previewCam)
            {
                var root = new GameObject("mc_inv_preview");
                DontDestroyOnLoad(root);
                root.transform.position = new Vector3(0, -6000, 0);
                preview = SteveModel.Build(root.transform);
                ModelUtil.SetLayer(root, 31);
                var cgo = new GameObject("cam");
                cgo.transform.SetParent(root.transform, false);
                previewCam = cgo.AddComponent<Camera>();
                previewCam.enabled = false;
                previewCam.cullingMask = 1 << 31;
                previewCam.clearFlags = CameraClearFlags.SolidColor;
                previewCam.backgroundColor = new Color(0, 0, 0, 0);
                previewCam.orthographic = true;
                previewCam.allowHDR = false;
                previewCam.nearClipPlane = 0.1f;
                previewCam.farClipPlane = 10f;
                var lgo = new GameObject("light");
                lgo.transform.SetParent(root.transform, false);
                previewLight = lgo.AddComponent<Light>();
                previewLight.type = LightType.Directional;
                previewLight.cullingMask = 1 << 31;
                previewLight.transform.rotation = Quaternion.Euler(30, 160, 0);
                previewLight.enabled = false;
            }
            int rw = Mathf.RoundToInt(pw * s), rh = Mathf.RoundToInt(ph * s);
            if (!previewRT || previewRT.width != rw || previewRT.height != rh)
            {
                if (previewRT) previewRT.Release();
                previewRT = new RenderTexture(rw, rh, 24, RenderTextureFormat.ARGB32);
            }
            float tall = 32f * Plugin.SteveScale.Value;
            var root2 = preview.Root.parent;
            previewCam.orthographicSize = tall * 0.55f;
            previewCam.aspect = pw / ph;
            previewCam.transform.position = root2.position + new Vector3(0, tall * 0.5f, 3f);
            previewCam.transform.rotation = Quaternion.LookRotation(Vector3.back);

            // body and head follow the mouse; the hands hold what the player holds
            float dx = (x + pw / 2f) - mouse.x, dy = (y + ph * 0.3f) - mouse.y;
            float pitch = -Mathf.Atan(dy / 40f) * 20f;
            preview.Root.localRotation = Quaternion.Euler(0, Mathf.Atan(dx / 40f) * 20f, 0);
            var hands = SteveManager.Local;
            preview.Animate(0f, 0f, pitch, false, false, ArmPoses.Compute(hands, pitch * Mathf.Deg2Rad, 0f, 0f, 0f, Time.time * 20f, false));
            preview.Head.localRotation = Quaternion.Euler(pitch, Mathf.Atan(dx / 40f) * 20f, 0);
            previewHeld.UpdateSteve(preview, hands, bothHands, UnityEngine.Rendering.ShadowCastingMode.On, 31, null);

            var fog = RenderSettings.fog; var amb = RenderSettings.ambientLight; var mode = RenderSettings.ambientMode;
            RenderSettings.fog = false; RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = new Color(0.6f, 0.6f, 0.6f);
            previewLight.enabled = true;
            previewCam.targetTexture = previewRT;
            previewCam.Render();
            previewCam.targetTexture = null;
            previewLight.enabled = false;
            RenderSettings.fog = fog; RenderSettings.ambientMode = mode; RenderSettings.ambientLight = amb;

            GUI.DrawTexture(new Rect(x * s, y * s, pw * s, ph * s), previewRT);
        }
    }
}
