using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace MinecraftRepo
{
    /// Reads textures and sounds from the user's own Minecraft Java install (vanilla launcher or Prism Launcher) at runtime.
    /// Nothing from Minecraft ships with the mod; without an install, plain generated stand-ins are used.
    internal class McAssets : MonoBehaviour
    {
        public static bool FromMinecraft;
        public static string SourceVersion = "none";

        public static Texture2D Skin, Pickaxe, Sword;
        public static Texture2D Hotbar, HotbarSelection, Crosshair;
        public static Texture2D HeartContainer, HeartFull, HeartHalf;
        public static Texture2D FoodEmpty, FoodFull, FoodHalf;
        public static Texture2D XpBackground, XpProgress;
        public static Texture2D AttackBackground, AttackProgress, AttackFull;
        public static Texture2D InventoryBackground, Font;
        public static Texture2D[] DestroyStages = new Texture2D[10];
        public static Texture2D White;

        // items, blocks and effects for TNT, fire, bow, torch, ender pearl, shield and the off hand
        public static Texture2D Bow, Arrow, FlintAndSteel, EnderPearl, Torch, TntSide, TntTop, TntBottom, ShieldTex;
        public static Texture2D HotbarOffhand, EmptyShieldSlot, Flame;
        public static Texture2D[] BowPulling = new Texture2D[3];
        public static Texture2D[] Fire0, Fire1;                  // animation frames, 16x16 each
        public static Texture2D[] Explosion = new Texture2D[16];
        public static Texture2D[] Generic = new Texture2D[8];    // smoke / portal particles

        // crossbow, fishing rod and firework rocket
        public static Texture2D Crossbow, CrossbowArrow, CrossbowFirework, FishingRod, FishingRodCast, FishingHook, FireworkRocket, FireworkFlash;
        public static Texture2D[] CrossbowPulling = new Texture2D[3];
        public static Texture2D[] Spark = new Texture2D[8];      // firework particles, in particles/firework.json order (spark_7 first)

        static readonly Dictionary<string, List<AudioClip>> sounds = new Dictionary<string, List<AudioClip>>();
        static readonly System.Random rng = new System.Random();

        private void Awake()
        {
            White = Solid(new Color(1, 1, 1, 1));
            try { LoadFromMinecraft(); }
            catch (Exception e) { Plugin.Log.LogWarning("Could not read Minecraft assets: " + e.Message); }
            FillFallbacks();
            McFont.Init(Font);
        }

        // ---------- Minecraft install discovery ----------

        /// Folders searched for versions: the configured one, or the vanilla launcher's and Prism Launcher's defaults.
        static List<string> McRoots()
        {
            var cfg = Plugin.MinecraftDir.Value;
            if (!string.IsNullOrEmpty(cfg)) return new List<string> { cfg };
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return new List<string> { Path.Combine(appData, ".minecraft"), Path.Combine(appData, "PrismLauncher") };
        }

        /// Versions in a root as (name, jar, version json), in either layout:
        /// vanilla launcher: versions/<v>/<v>.jar + versions/<v>/<v>.json;
        /// Prism Launcher: libraries/com/mojang/minecraft/<v>/minecraft-<v>-client.jar + meta/net.minecraft/<v>.json.
        /// Both keep the asset index and objects in assets/.
        static IEnumerable<(string name, string jar, string json)> Versions(string root)
        {
            var vanilla = Path.Combine(root, "versions");
            if (Directory.Exists(vanilla))
                foreach (var dir in Directory.GetDirectories(vanilla))
                {
                    var n = Path.GetFileName(dir);
                    yield return (n, Path.Combine(dir, n + ".jar"), Path.Combine(dir, n + ".json"));
                }
            var prism = Path.Combine(root, "libraries", "com", "mojang", "minecraft");
            if (Directory.Exists(prism))
                foreach (var dir in Directory.GetDirectories(prism))
                {
                    var n = Path.GetFileName(dir);
                    yield return (n, Path.Combine(dir, "minecraft-" + n + "-client.jar"), Path.Combine(root, "meta", "net.minecraft", n + ".json"));
                }
        }

        static int[] ParseVersion(string v)
        {
            var parts = v.Split('.');
            var r = new int[3];
            for (int i = 0; i < parts.Length && i < 3; i++)
                if (!int.TryParse(parts[i], out r[i])) return null;
            return r;
        }

        /// Picks (jar, asset index, assets folder) of the newest plain release, across all roots, that has both on disk.
        /// On a tie the earlier root wins, so the vanilla launcher's copy comes before Prism's.
        static bool FindVersion(List<string> roots, out string jar, out string index, out string assets, out string name)
        {
            jar = index = assets = name = null;
            var wanted = Plugin.MinecraftVersion.Value;
            var candidates = new List<(int[] v, string root, string name, string jar, string json)>();
            foreach (var root in roots)
                foreach (var (n, j, js) in Versions(root))
                {
                    if (wanted != "auto" && n != wanted) continue;
                    var v = ParseVersion(n);
                    if (v == null && wanted == "auto") continue;
                    candidates.Add((v ?? new int[3], root, n, j, js));
                }
            foreach (var c in candidates.OrderByDescending(c => c.v[0] * 1000000 + c.v[1] * 1000 + c.v[2]))
            {
                if (!File.Exists(c.jar) || !File.Exists(c.json)) continue;
                string idx = null;
                try
                {
                    var id = (string)JObject.Parse(File.ReadAllText(c.json))["assetIndex"]?["id"];
                    if (id != null) idx = Path.Combine(c.root, "assets", "indexes", id + ".json");
                }
                catch { }
                if (idx == null || !File.Exists(idx)) continue;
                jar = c.jar; index = idx; assets = Path.Combine(c.root, "assets"); name = c.name;
                return true;
            }
            return false;
        }

        void LoadFromMinecraft()
        {
            var roots = McRoots();
            if (!FindVersion(roots, out var jarPath, out var indexPath, out var assetsDir, out var version))
            {
                Plugin.Log.LogWarning("No Minecraft Java install with a jar and asset index found in " + string.Join(" or ", roots) + ". Using stand-in art.");
                return;
            }
            SourceVersion = version;
            Plugin.Log.LogInfo("Reading Minecraft " + version + " assets from " + jarPath);

            using (var zip = new ZipArchive(File.OpenRead(jarPath), ZipArchiveMode.Read))
            {
                Texture2D T(string p) => ReadTex(zip, "assets/minecraft/textures/" + p + ".png");
                Skin = T("entity/player/wide/steve");
                Pickaxe = T("item/diamond_pickaxe");
                Sword = T("item/diamond_sword");
                Hotbar = T("gui/sprites/hud/hotbar");
                HotbarSelection = T("gui/sprites/hud/hotbar_selection");
                Crosshair = T("gui/sprites/hud/crosshair");
                HeartContainer = T("gui/sprites/hud/heart/container");
                HeartFull = T("gui/sprites/hud/heart/full");
                HeartHalf = T("gui/sprites/hud/heart/half");
                FoodEmpty = T("gui/sprites/hud/food_empty");
                FoodFull = T("gui/sprites/hud/food_full");
                FoodHalf = T("gui/sprites/hud/food_half");
                XpBackground = T("gui/sprites/hud/experience_bar_background");
                XpProgress = T("gui/sprites/hud/experience_bar_progress");
                AttackBackground = T("gui/sprites/hud/crosshair_attack_indicator_background");
                AttackProgress = T("gui/sprites/hud/crosshair_attack_indicator_progress");
                AttackFull = T("gui/sprites/hud/crosshair_attack_indicator_full");
                InventoryBackground = T("gui/container/inventory");
                Font = T("font/ascii");
                for (int i = 0; i < 10; i++) DestroyStages[i] = T("block/destroy_stage_" + i);

                Bow = T("item/bow");
                for (int i = 0; i < 3; i++) BowPulling[i] = T("item/bow_pulling_" + i);
                Arrow = T("item/arrow");
                FlintAndSteel = T("item/flint_and_steel");
                EnderPearl = T("item/ender_pearl");
                Torch = T("block/torch");
                TntSide = T("block/tnt_side");
                TntTop = T("block/tnt_top");
                TntBottom = T("block/tnt_bottom");
                ShieldTex = T("entity/shield_base_nopattern");
                HotbarOffhand = T("gui/sprites/hud/hotbar_offhand_left");
                // moved to a GUI sprite in 1.21.2
                EmptyShieldSlot = zip.GetEntry("assets/minecraft/textures/item/empty_armor_slot_shield.png") != null
                    ? T("item/empty_armor_slot_shield") : T("gui/sprites/container/slot/shield");
                Flame = T("particle/flame");
                Fire0 = Frames(T("block/fire_0"));
                Fire1 = Frames(T("block/fire_1"));
                for (int i = 0; i < 16; i++) Explosion[i] = T("particle/explosion_" + i);
                for (int i = 0; i < 8; i++) Generic[i] = T("particle/generic_" + i);

                Crossbow = T("item/crossbow_standby");
                for (int i = 0; i < 3; i++) CrossbowPulling[i] = T("item/crossbow_pulling_" + i);
                CrossbowArrow = T("item/crossbow_arrow");
                CrossbowFirework = T("item/crossbow_firework");
                FishingRod = T("item/fishing_rod");
                FishingRodCast = T("item/fishing_rod_cast");
                FishingHook = T("entity/fishing_hook");
                FireworkRocket = T("item/firework_rocket");
                FireworkFlash = T("particle/flash");
                for (int i = 0; i < 8; i++) Spark[i] = T("particle/spark_" + (7 - i));
            }
            FromMinecraft = Skin != null && Hotbar != null;

            var objects = (JObject)JObject.Parse(File.ReadAllText(indexPath))["objects"];
            var objectsDir = Path.Combine(assetsDir, "objects");
            void S(string key, string prefix, int count)
            {
                for (int i = 1; i <= count; i++)
                {
                    var name = "minecraft/sounds/" + prefix + (count > 1 ? i.ToString() : "") + ".ogg";
                    var hash = (string)objects[name]?["hash"];
                    if (hash == null) continue;
                    var file = Path.Combine(objectsDir, hash.Substring(0, 2), hash);
                    if (File.Exists(file)) StartCoroutine(LoadClip(key, file));
                }
            }
            S("pop", "random/pop", 1);
            S("dig", "dig/stone", 4);
            S("hit", "step/stone", 6);
            S("strong", "entity/player/attack/strong", 6);
            S("weak", "entity/player/attack/weak", 4);
            S("crit", "entity/player/attack/crit", 3);
            S("sweep", "entity/player/attack/sweep", 7);
            S("knockback", "entity/player/attack/knockback", 4);
            S("nodamage", "entity/player/attack/nodamage", 4);
            S("click", "random/click", 1);
            S("throw", "random/bow", 1);
            S("fuse", "random/fuse", 1);
            S("explode", "random/explode", 4);
            S("ignite", "fire/ignite", 1);
            S("fire", "fire/fire", 1);
            S("fizz", "random/fizz", 1);
            S("shield", "item/shield/block", 5);
            S("bowhit", "random/bowhit", 4);
            S("teleport", "mob/endermen/portal", 1);
            S("teleport", "mob/endermen/portal2", 1);
            S("wood", "dig/wood", 4);
            S("grass", "dig/grass", 4);
            S("hurt", "damage/hit", 3);
            S("break", "random/break", 1);
            S("crossbow_start", "item/crossbow/loading_start", 1);
            S("crossbow_middle", "item/crossbow/loading_middle", 4);
            S("crossbow_end", "item/crossbow/loading_end", 1);
            S("crossbow_shoot", "item/crossbow/shoot", 3);
            S("cast", "entity/bobber/castfast", 1);
            S("reel", "entity/bobber/retrieve", 2);
            S("launch", "fireworks/launch1", 1);
            S("blast", "fireworks/blast1", 1);
            S("blast_far", "fireworks/blast_far1", 1);
        }

        /// Splits a vertical animation strip (Minecraft's animated block textures) into square frames, top first.
        static Texture2D[] Frames(Texture2D strip)
        {
            if (strip == null) return null;
            int w = strip.width, n = Mathf.Max(1, strip.height / w);
            var frames = new Texture2D[n];
            for (int i = 0; i < n; i++)
            {
                var t = new Texture2D(w, w, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                t.SetPixels(strip.GetPixels(0, strip.height - (i + 1) * w, w, w));
                t.Apply();
                t.name = strip.name + "_" + i;
                frames[i] = t;
            }
            return frames;
        }

        static Texture2D ReadTex(ZipArchive zip, string path)
        {
            var e = zip.GetEntry(path);
            if (e == null) { Plugin.Log.LogWarning("Missing in jar: " + path); return null; }
            using (var s = e.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(ms.ToArray());
                t.filterMode = FilterMode.Point;
                t.wrapMode = TextureWrapMode.Clamp;
                t.name = Path.GetFileNameWithoutExtension(path);
                return t;
            }
        }

        static IEnumerator LoadClip(string key, string file)
        {
            using (var req = UnityWebRequestMultimedia.GetAudioClip(new Uri(file).AbsoluteUri, AudioType.OGGVORBIS))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) yield break;
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null) yield break;
                if (!sounds.TryGetValue(key, out var list)) sounds[key] = list = new List<AudioClip>();
                list.Add(clip);
            }
        }

        // ---------- sound playback ----------

        public static void Play(string key, Vector3 pos, float volume = 1f, float pitch = 1f, float range = 24f)
        {
            if (!sounds.TryGetValue(key, out var list) || list.Count == 0) return;
            var clip = list[rng.Next(list.Count)];
            var go = new GameObject("mc_sound_" + key);
            go.transform.position = pos;
            var src = go.AddComponent<AudioSource>();
            src.clip = clip;
            src.volume = Mathf.Clamp01(volume * Plugin.SoundVolume.Value);
            src.pitch = pitch;
            src.spatialBlend = 0.7f;
            src.minDistance = 2f;
            src.maxDistance = range;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.Play();
            Destroy(go, clip.length / Mathf.Max(0.1f, pitch) + 0.1f);
        }

        public static bool Has(string key) => sounds.TryGetValue(key, out var list) && list.Count > 0;

        public static float RandPitch(float spread = 0.2f) => 1f + ((float)rng.NextDouble() - 0.5f) * spread;

        // ---------- stand-ins when there's no Minecraft install ----------

        static Texture2D Solid(Color c, int w = 1, int h = 1)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            var px = Enumerable.Repeat((Color32)c, w * h).ToArray();
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static Texture2D FromRows(string[] rows, Dictionary<char, Color32> palette)
        {
            int h = rows.Length, w = rows[0].Length;
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    char c = rows[y][x];
                    t.SetPixel(x, h - 1 - y, palette.TryGetValue(c, out var col) ? col : new Color32(0, 0, 0, 0));
                }
            t.Apply();
            return t;
        }

        static void FillFallbacks()
        {
            var pal = new Dictionary<char, Color32>
            {
                ['#'] = new Color32(40, 30, 20, 255), ['b'] = new Color32(110, 80, 40, 255),
                ['c'] = new Color32(70, 220, 210, 255), ['d'] = new Color32(30, 120, 115, 255),
            };
            if (Pickaxe == null) Pickaxe = FromRows(new[]
            {
                "................", "....dcccccc.....", "...dccccccccd...", "..dc......bccd..",
                "..d.......b.cd..", "..........b..d..", ".........b......", "........b.......",
                ".......b........", "......b.........", ".....b..........", "....b...........",
                "...b............", "..b.............", ".#..............", "................",
            }, pal);
            if (Sword == null) Sword = FromRows(new[]
            {
                "................", ".............dd.", "............dcd.", "...........dcd..",
                "..........dcd...", ".........dcd....", "........dcd.....", ".......dcd......",
                "..d...dcd.......", "...d.dcd........", "....bcd.........", "....bb..........",
                "...b..d.........", "..b.............", ".#..............", "................",
            }, pal);
            if (Skin == null)
            {
                var t = new Texture2D(64, 64, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                var skin = new Color32(190, 140, 110, 255); var shirt = new Color32(0, 170, 170, 255);
                var pants = new Color32(60, 60, 160, 255); var hair = new Color32(60, 40, 20, 255);
                for (int y = 0; y < 64; y++)
                    for (int x = 0; x < 64; x++)
                    {
                        int py = 63 - y; // image row from top
                        Color32 c = new Color32(0, 0, 0, 0);
                        if (py < 16 && x < 32) c = py < 8 || py < 10 ? hair : skin;
                        else if (py >= 16 && py < 32 && x >= 16 && x < 40) c = shirt;
                        else if (py >= 16 && py < 32 && x >= 40 && x < 56) c = py < 26 ? shirt : skin;
                        else if (py >= 48 && x >= 32 && x < 48) c = py < 58 ? shirt : skin;
                        else if ((py >= 16 && py < 32 && x < 16) || (py >= 48 && x >= 16 && x < 32)) c = pants;
                        t.SetPixel(x, y, c);
                    }
                t.Apply();
                Skin = t;
            }
            Texture2D Box(Color c) => Solid(c, 16, 16);
            if (Bow == null) Bow = Box(new Color(0.45f, 0.3f, 0.15f));
            for (int i = 0; i < 3; i++) if (BowPulling[i] == null) BowPulling[i] = Bow;
            if (Arrow == null) Arrow = Box(new Color(0.6f, 0.6f, 0.6f));
            if (FlintAndSteel == null) FlintAndSteel = Box(new Color(0.3f, 0.3f, 0.35f));
            if (EnderPearl == null) EnderPearl = Box(new Color(0.1f, 0.45f, 0.4f));
            if (Torch == null) Torch = Box(new Color(1f, 0.8f, 0.3f));
            if (TntSide == null) TntSide = Box(new Color(0.85f, 0.2f, 0.15f));
            if (TntTop == null) TntTop = TntSide;
            if (TntBottom == null) TntBottom = TntSide;
            if (ShieldTex == null) ShieldTex = Solid(new Color(0.55f, 0.4f, 0.25f), 64, 64);
            if (Flame == null) Flame = Solid(new Color(1f, 0.7f, 0.2f), 8, 8);
            if (Fire0 == null) Fire0 = new[] { Solid(new Color(1f, 0.55f, 0.1f, 0.85f), 16, 16) };
            if (Fire1 == null) Fire1 = Fire0;
            if (Crossbow == null) Crossbow = Box(new Color(0.4f, 0.28f, 0.15f));
            for (int i = 0; i < 3; i++) if (CrossbowPulling[i] == null) CrossbowPulling[i] = Crossbow;
            if (CrossbowArrow == null) CrossbowArrow = Crossbow;
            if (CrossbowFirework == null) CrossbowFirework = Crossbow;
            if (FishingRod == null) FishingRod = Box(new Color(0.5f, 0.35f, 0.2f));
            if (FishingRodCast == null) FishingRodCast = FishingRod;
            if (FishingHook == null) FishingHook = Solid(new Color(0.8f, 0.15f, 0.15f), 8, 8);
            if (FireworkRocket == null) FireworkRocket = Box(new Color(0.85f, 0.3f, 0.3f));
            if (FireworkFlash == null) FireworkFlash = Solid(new Color(1f, 1f, 1f, 0.3f), 8, 8);
            for (int i = 0; i < 8; i++) if (Spark[i] == null) Spark[i] = Solid(Color.white, 4, 4);
            for (int i = 0; i < 16; i++) if (Explosion[i] == null) Explosion[i] = Solid(new Color(0.8f, 0.8f, 0.8f, 0.8f), 8, 8);
            for (int i = 0; i < 8; i++) if (Generic[i] == null) Generic[i] = Solid(new Color(1f, 1f, 1f, 0.8f), 8, 8);
            for (int i = 0; i < 10; i++)
                if (DestroyStages[i] == null)
                {
                    var t = new Texture2D(16, 16, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                    var r = new System.Random(i);
                    for (int p = 0; p < 256; p++)
                        t.SetPixel(p % 16, p / 16, r.NextDouble() < (i + 1) * 0.06 ? new Color(0, 0, 0, 0.6f) : new Color(0, 0, 0, 0));
                    t.Apply();
                    DestroyStages[i] = t;
                }
        }
    }
}
