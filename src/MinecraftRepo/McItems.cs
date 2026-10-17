using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MinecraftRepo
{
    /// Minecraft items that aren't R.E.P.O. objects: TNT, flint and steel, bow, arrow, torch, ender pearl,
    /// shield, crossbow, firework rocket, fishing rod, and the R.E.P.O. flashlight turned into an item. Names, stack sizes, icons and models.
    internal static class McItems
    {
        public static readonly KeyValuePair<string, int>[] StarterKit =
        {
            new KeyValuePair<string, int>("bow", 1),
            new KeyValuePair<string, int>("tnt", 16),
            new KeyValuePair<string, int>("flint_and_steel", 1),
            new KeyValuePair<string, int>("torch", 64),
            new KeyValuePair<string, int>("ender_pearl", 16),
            new KeyValuePair<string, int>("arrow", 64),
            new KeyValuePair<string, int>("crossbow", 1),
            new KeyValuePair<string, int>("firework_rocket", 16),
            new KeyValuePair<string, int>("fishing_rod", 1),
        };

        public static int MaxStack(string id)
        {
            switch (id)
            {
                case "tnt": case "arrow": case "torch": case "firework_rocket": return 64;
                case "ender_pearl": return 16;
                default: return 1;
            }
        }

        public static string Name(string id)
        {
            switch (id)
            {
                case "tnt": return "TNT";
                case "flint_and_steel": return "Flint and Steel";
                case "bow": return "Bow";
                case "arrow": return "Arrow";
                case "torch": return "Torch";
                case "ender_pearl": return "Ender Pearl";
                case "shield": return "Shield";
                case "flashlight": return "Flashlight";
                case "crossbow": return "Crossbow";
                case "firework_rocket": return "Firework Rocket";
                case "fishing_rod": return "Fishing Rod";
                default: return id;
            }
        }

        public static bool Known(string id) => id != null && Name(id) != id;

        /// Flat items drawn as an extruded sprite (the rest are 3D models).
        public static bool IsSprite(string id) => id != "tnt" && id != "shield" && id != "flashlight";

        /// Held like a tool (diagonal in the hand): tools and the flat Minecraft items.
        public static bool HeldLikeTool(ItemStack s) => s != null && (s.IsTool || (s.Kind == ItemKind.Mc && IsSprite(s.Id)));

        public static Texture2D Sprite(string id)
        {
            switch (id)
            {
                case "bow": return McAssets.Bow;
                case "arrow": return McAssets.Arrow;
                case "flint_and_steel": return McAssets.FlintAndSteel;
                case "ender_pearl": return McAssets.EnderPearl;
                case "torch": return McAssets.Torch;
                case "crossbow": return McAssets.Crossbow;
                case "firework_rocket": return McAssets.FireworkRocket;
                case "fishing_rod": return McAssets.FishingRod;
                default: return McAssets.White;
            }
        }

        /// Bow sprite for seconds of drawing (Minecraft's "pull" predicate is use ticks / 20:
        /// pulling_0 below 0.65, pulling_1 below 0.9, then pulling_2). Negative = not drawing.
        public static Texture2D BowFrame(float seconds)
        {
            if (seconds < 0f) return McAssets.Bow;
            return McAssets.BowPulling[seconds < 0.65f ? 0 : seconds < 0.9f ? 1 : 2];
        }

        /// Crossbow sprite for its charge (Minecraft's "pull" predicate, use ticks / 25: pulling_0 below 0.58,
        /// pulling_1 below 1, then pulling_2).
        public static Texture2D CrossbowFrame(float pull) => McAssets.CrossbowPulling[pull < 0.58f ? 0 : pull < 1f ? 1 : 2];

        /// The sprite for a stack's current state, the way Minecraft's model overrides pick it: bow and crossbow
        /// pulling, crossbow loaded with an arrow or a rocket, fishing rod cast. Null for items without states.
        public static Texture2D StateSprite(ItemStack s)
        {
            if (s == null || s.Kind != ItemKind.Mc) return null;
            var p = McPlayer.Instance;
            return StateSprite(s, p ? p.UseSecondsOf(s) : -1f, BobberEntity.IsCastWith(s));
        }

        /// The same for any player's stack: `use` = seconds it has been in use (-1 = not in use), `cast` = its bobber is out.
        public static Texture2D StateSprite(ItemStack s, float use, bool cast)
        {
            if (s == null || s.Kind != ItemKind.Mc) return null;
            switch (s.Id)
            {
                case "bow": return BowFrame(use);
                case "crossbow":
                    if (use >= 0f) return CrossbowFrame(use / McPlayer.CrossbowChargeSeconds);
                    return s.Loaded == "firework_rocket" ? McAssets.CrossbowFirework : s.Loaded != null ? McAssets.CrossbowArrow : McAssets.Crossbow;
                case "fishing_rod": return cast ? McAssets.FishingRodCast : McAssets.FishingRod;
            }
            return null;
        }

        static Texture2D flashlightStandIn;

        public static Texture2D Icon(string id)
        {
            switch (id)
            {
                case "tnt": return IconCache.ForModel("mc:tnt", TntIconModel, McDisplay.BlockGui);
                case "shield": return IconCache.ForModel("mc:shield", () => ShieldItemModel(), McDisplay.ShieldGui);
                case "flashlight":
                    if (FlashlightController.Instance)
                        return IconCache.ForModelPerspective("mc:flashlight", () => FlashlightModel(1f), Quaternion.Euler(0, 120, 0)) ?? FlashlightStandIn;
                    return FlashlightStandIn;
                default: return Sprite(id);
            }
        }

        /// Drawn flashlight for before R.E.P.O.'s flashlight exists to copy.
        static Texture2D FlashlightStandIn
        {
            get
            {
                if (!flashlightStandIn)
                {
                    flashlightStandIn = new Texture2D(16, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            bool body = Mathf.Abs(x - y) <= 1 && x > 2 && x < 13;
                            bool head = Mathf.Abs(x - y) <= 2 && x >= 10 && x < 14;
                            flashlightStandIn.SetPixel(x, y, head ? new Color(1f, 0.95f, 0.6f) : body ? new Color(0.25f, 0.25f, 0.28f) : Color.clear);
                        }
                    flashlightStandIn.Apply();
                }
                return flashlightStandIn;
            }
        }

        /// World/held model, about `size` metres across.
        public static GameObject Model(string id, float size)
        {
            switch (id)
            {
                case "tnt": return TntModel(size);
                case "shield": return ShieldModel(size * 1.6f);
                case "flashlight": return FlashlightModel(size) ?? ModelUtil.SpriteModel(FlashlightStandIn, size);
                default: return ModelUtil.SpriteModel(Sprite(id), size);
            }
        }

        // ---------- TNT ----------

        static Texture2D tntAtlas, tntFlashAtlas;
        static Mesh tntMesh;
        static Material tntMat, tntFlashMat;

        static Texture2D TntAtlas
        {
            get
            {
                if (tntAtlas) return tntAtlas;
                tntAtlas = new Texture2D(48, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                tntFlashAtlas = new Texture2D(48, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                var faces = new[] { McAssets.TntSide, McAssets.TntTop, McAssets.TntBottom };
                for (int f = 0; f < 3; f++)
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            var t = faces[f];
                            var c = t.GetPixel(x * t.width / 16, y * t.height / 16);
                            c.a = 1f;
                            tntAtlas.SetPixel(f * 16 + x, y, c);
                            tntFlashAtlas.SetPixel(f * 16 + x, y, Color.Lerp(c, Color.white, 0.7f));
                        }
                tntAtlas.Apply();
                tntFlashAtlas.Apply();
                return tntAtlas;
            }
        }

        public static Mesh TntMesh
        {
            get
            {
                if (tntMesh) return tntMesh;
                var mb = new MeshBuilder();
                mb.UvBox(-Vector3.one * 0.5f, Vector3.one * 0.5f, new Rect(0, 0, 16, 16), new Rect(16, 0, 16, 16), new Rect(32, 0, 16, 16), TntAtlas);
                return tntMesh = mb.Build("mc_tnt");
            }
        }

        public static Material TntMat => tntMat ? tntMat : tntMat = Mats.LitTex(TntAtlas);
        public static Material TntFlashMat { get { var _ = TntAtlas; return tntFlashMat ? tntFlashMat : tntFlashMat = Mats.UnlitTex(tntFlashAtlas); } }

        static Texture2D tntIconAtlas;
        static Mesh tntIconMesh;
        static Material tntIconMat;

        /// TNT for its slot icon (drawn under white ambient light), with Minecraft's GUI block shading baked in (top 1.0, the face on the
        /// left 0.8, the face on the right 0.6; in the gui view those are the top, +X and +Z faces here).
        static GameObject TntIconModel()
        {
            if (!tntIconMesh)
            {
                var atlas = TntAtlas;
                tntIconAtlas = new Texture2D(96, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                float[] shade = { 1f, 1f, 0.5f, 0.8f, 0.6f, 0.6f };
                int[] from = { 0, 16, 32, 0, 0, 0 };
                for (int f = 0; f < 6; f++)
                    for (int y = 0; y < 16; y++)
                        for (int x = 0; x < 16; x++)
                        {
                            var c = atlas.GetPixel(from[f] + x, y);
                            tntIconAtlas.SetPixel(f * 16 + x, y, new Color(c.r * shade[f], c.g * shade[f], c.b * shade[f], 1f));
                        }
                tntIconAtlas.Apply();
                var mb = new MeshBuilder();
                Rect R(int i) => new Rect(i * 16, 0, 16, 16);
                mb.UvBox(-Vector3.one * 0.5f, Vector3.one * 0.5f, R(4), R(5), R(3), R(5), R(1), R(2), tntIconAtlas);
                tntIconMesh = mb.Build("mc_tnt_icon");
                tntIconMat = Mats.LitTex(tntIconAtlas);
            }
            var go = new GameObject("mc_tnt_icon");
            go.AddComponent<MeshFilter>().sharedMesh = tntIconMesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = tntIconMat;
            return go;
        }

        /// A TNT block `size` metres wide, centred on its origin.
        public static GameObject TntModel(float size)
        {
            var go = new GameObject("mc_tnt_model");
            var cube = new GameObject("cube");
            cube.transform.SetParent(go.transform, false);
            cube.transform.localScale = Vector3.one * size;
            cube.AddComponent<MeshFilter>().sharedMesh = TntMesh;
            cube.AddComponent<MeshRenderer>().sharedMaterial = TntMat;
            return go;
        }

        // ---------- torch ----------

        static Mesh torchMesh;
        static Material torchMat;

        /// Minecraft's torch model (2x10 pixels of block/torch), 1 block tall unit, base at the origin.
        public static Mesh TorchMesh
        {
            get
            {
                if (torchMesh) return torchMesh;
                var mb = new MeshBuilder();
                const float p = 1f / 16f;
                mb.UvBox(new Vector3(-p, 0, -p), new Vector3(p, 10 * p, p), new Rect(7, 6, 2, 10), new Rect(7, 6, 2, 2), new Rect(7, 13, 2, 2), McAssets.Torch);
                return torchMesh = mb.Build("mc_torch");
            }
        }

        /// Torches are drawn full-bright, like Minecraft's light-emitting blocks.
        public static Material TorchMat => torchMat ? torchMat : torchMat = Mats.UnlitTex(McAssets.Torch);

        // ---------- shield ----------

        static Mesh shieldMesh;
        static Material shieldMat;

        /// Minecraft's ShieldModel: a 12x22x1 plate and a 2x6x6 handle behind it. Design faces +Z.
        static Mesh ShieldMesh
        {
            get
            {
                if (shieldMesh) return shieldMesh;
                var mb = new MeshBuilder();
                mb.SkinBox(new Vector3(-6, -11, 1), new Vector3(6, 11, 2), 0, 0, 12, 22, 1, McAssets.ShieldTex, false);
                mb.SkinBox(new Vector3(-1, -3, -5), new Vector3(1, 3, 1), 26, 0, 2, 6, 6, McAssets.ShieldTex, false);
                return shieldMesh = mb.Build("mc_shield");
            }
        }

        /// A shield whose plate is `height` metres tall, centred on the plate.
        public static GameObject ShieldModel(float height, Material mat = null)
        {
            if (!shieldMat) shieldMat = Mats.LitTex(McAssets.ShieldTex);
            var go = new GameObject("mc_shield_model");
            var plate = new GameObject("plate");
            plate.transform.SetParent(go.transform, false);
            plate.transform.localScale = Vector3.one * (height / 22f);
            plate.AddComponent<MeshFilter>().sharedMesh = ShieldMesh;
            plate.AddComponent<MeshRenderer>().sharedMaterial = mat ? mat : shieldMat;
            return go;
        }

        /// The shield as Minecraft's item renderer places it: model pixels / 16, its centre on the corner of the
        /// item's block (the display transforms expect that), design facing Minecraft's +Z.
        public static GameObject ShieldItemModel(Material mat = null)
        {
            var go = new GameObject("mc_shield_item");
            var m = ShieldModel(22f / 16f, mat);
            m.transform.SetParent(go.transform, false);
            m.transform.localPosition = new Vector3(-0.5f, -0.5f, 0.5f);
            m.transform.localRotation = Quaternion.Euler(0, 180, 0);
            return go;
        }

        // ---------- flashlight ----------

        /// A copy of R.E.P.O.'s own flashlight mesh, pointing along +Z.
        public static GameObject FlashlightModel(float size)
        {
            var fc = FlashlightController.Instance;
            if (!fc || !fc.mesh) return null;
            var clone = ModelUtil.CloneVisual(fc.gameObject, out var b,
                r => r is MeshRenderer && r != fc.meshShadows && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly,
                t => t == fc.hideTransform || t == fc.clickTransform);
            if (clone.transform.childCount == 0) { Object.Destroy(clone); return null; }
            return ModelUtil.Normalised(clone, b, size);
        }
    }
}
