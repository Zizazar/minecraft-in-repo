using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MinecraftRepo
{
    [BepInPlugin(Guid, "Minecraft Player in REPO", Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "vusmedro.minecraftrepo";
        public const string Version = "0.1.0";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        internal static ConfigEntry<string> MinecraftDir;
        internal static ConfigEntry<string> MinecraftVersion;
        internal static ConfigEntry<float> DamageScale;
        internal static ConfigEntry<float> MiningSpeed;
        internal static ConfigEntry<float> DurabilityScale;
        internal static ConfigEntry<float> SteveScale;
        internal static ConfigEntry<float> HeldItemScale;
        internal static ConfigEntry<int> GuiScale;
        internal static ConfigEntry<bool> ReplaceWithSteve;
        internal static ConfigEntry<bool> HideRepoHud;
        internal static ConfigEntry<bool> StarterKit;
        internal static ConfigEntry<bool> ShopItems;
        internal static ConfigEntry<float> SoundVolume;
        internal static ConfigEntry<float> BlockSize;
        internal static ConfigEntry<int> MaxLights;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            MinecraftDir = Config.Bind("Minecraft", "Directory", "",
                "Your .minecraft folder or Prism Launcher data folder (the one holding libraries, meta and assets). Empty = search %APPDATA%\\.minecraft and %APPDATA%\\PrismLauncher. Textures and sounds are read from it at runtime.");
            MinecraftVersion = Config.Bind("Minecraft", "Version", "auto",
                "Minecraft version whose jar and asset index are used. 'auto' = newest installed version that has both, in either launcher.");
            DamageScale = Config.Bind("Combat", "DamageScale", 5f,
                "Minecraft damage is multiplied by this. R.E.P.O. uses 100 HP where Minecraft uses 20.");
            MiningSpeed = Config.Bind("Mining", "SpeedMultiplier", 1f, "Multiplies mining speed.");
            DurabilityScale = Config.Bind("Gameplay", "DurabilityScale", 1f,
                "Multiplies the durability of the Minecraft tools, bow, crossbow, shield, fishing rod and flint and steel (1 = sized like R.E.P.O.'s weapons: the sword lasts 20 enemy hits, the pickaxe 40 objects). Empty items recharge at the truck's charging station.");
            SteveScale = Config.Bind("Visuals", "StevePixelSize", 0.05f, "Metres per skin pixel (Steve is 32 px tall).");
            HeldItemScale = Config.Bind("Visuals", "HeldItemScale", 1f,
                "Size of the Minecraft items in R.E.P.O. robots' hands (third person and other players). 1 = Minecraft's proportions to the robot's height.");
            GuiScale = Config.Bind("Visuals", "GuiScale", 0, "Minecraft GUI scale. 0 = auto, like Minecraft.");
            ReplaceWithSteve = Config.Bind("Visuals", "ReplaceWithSteve", false,
                "Draw every player as a blocky Steve on this PC instead of the R.E.P.O. robot.");
            HideRepoHud = Config.Bind("Visuals", "HideRepoHud", true, "Hide R.E.P.O.'s health, stamina, inventory and goal HUD.");
            StarterKit = Config.Bind("Gameplay", "StarterKit", true,
                "Each level, give the diamond pickaxe and sword (and, when ShopItems is off, a bow, arrows, TNT, flint and steel, torches, ender pearls, a shield, a crossbow, firework rockets and a fishing rod, with consumables topped up). The flashlight is always given.");
            ShopItems = Config.Bind("Gameplay", "ShopItems", true,
                "Sell TNT, flint and steel, bows, arrows (16 per purchase), torches, ender pearls, shields, crossbows, firework rockets (8 per purchase) and fishing rods in R.E.P.O.'s shop. TNT always comes with flint and steel, a bow or crossbow with arrows. Every modded player needs the same setting.");
            BlockSize = Config.Bind("Gameplay", "BlockSize", 0.6f,
                "Metres per Minecraft block. Scales TNT and torch size, explosion radius, and arrow/pearl speed and gravity.");
            MaxLights = Config.Bind("Visuals", "MaxLights", 16, "Most torch/fire lights lit at once (the ones nearest the camera).");
            SoundVolume = Config.Bind("Audio", "Volume", 0.8f, "Volume of the Minecraft sounds.");

            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);

            var go = new GameObject("MinecraftRepo");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<McAssets>();
            go.AddComponent<McPlayer>();
            go.AddComponent<McHud>();
            go.AddComponent<SteveManager>();
            go.AddComponent<McWorld>();
            go.AddComponent<McNet>();

            Log.LogInfo("Minecraft Player in REPO " + Version + " loaded");
        }
    }
}
