using UnityEngine;

namespace MinecraftRepo
{
    /// Minecraft's 8x8 bitmap font (textures/font/ascii.png), drawn with IMGUI in GUI pixels.
    internal static class McFont
    {
        static Texture2D tex;
        static readonly int[] widths = new int[256];
        static GUIStyle fallback;

        public static void Init(Texture2D t)
        {
            tex = t;
            if (tex == null) return;
            int cw = tex.width / 16, ch = tex.height / 16;
            var px = tex.GetPixels32();
            for (int c = 0; c < 256; c++)
            {
                int cx = (c % 16) * cw, cyTop = (c / 16) * ch;
                int w = 0;
                for (int x = cw - 1; x >= 0 && w == 0; x--)
                    for (int y = 0; y < ch; y++)
                    {
                        int iy = tex.height - 1 - (cyTop + y);
                        if (px[iy * tex.width + cx + x].a > 0) { w = x + 1; break; }
                    }
                widths[c] = c == ' ' ? 3 * cw / 8 : w;
            }
        }

        public static int Width(string s)
        {
            if (tex == null) return s.Length * 6;
            int w = 0;
            foreach (var ch in s) w += Advance(ch);
            return w;
        }

        static int Advance(char ch)
        {
            int c = ch < 256 ? ch : '?';
            return widths[c] * 8 / (tex.width / 16) + 1;
        }

        /// x, y in GUI pixels (already multiplied by scale by the caller is NOT expected: pass unscaled + scale).
        public static void Draw(string s, float x, float y, float scale, Color color, bool shadow = true)
        {
            if (shadow) DrawRaw(s, x + scale, y + scale, scale, new Color(color.r * 0.25f, color.g * 0.25f, color.b * 0.25f, color.a));
            DrawRaw(s, x, y, scale, color);
        }

        static void DrawRaw(string s, float x, float y, float scale, Color color)
        {
            var prev = GUI.color;
            GUI.color = color;
            if (tex == null)
            {
                if (fallback == null) fallback = new GUIStyle(GUI.skin.label) { fontSize = 12 };
                fallback.fontSize = Mathf.RoundToInt(8 * scale);
                fallback.normal.textColor = color;
                GUI.Label(new Rect(x, y - scale, 1000, 12 * scale), s, fallback);
                GUI.color = prev;
                return;
            }
            float cx = x;
            foreach (var ch in s)
            {
                int c = ch < 256 ? ch : '?';
                if (c != ' ')
                {
                    var uv = new Rect((c % 16) / 16f, 1f - (c / 16 + 1) / 16f, 1 / 16f, 1 / 16f);
                    GUI.DrawTextureWithTexCoords(new Rect(cx, y, 8 * scale, 8 * scale), tex, uv);
                }
                cx += Advance(ch) * scale;
            }
            GUI.color = prev;
        }
    }
}
