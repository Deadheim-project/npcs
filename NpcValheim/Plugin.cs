using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.npcvalheim.mod";
        public const string Name = "NpcValheim";
        public const string Version = "0.1.25";

        internal static ManualLogSource Log;
        private Harmony _harmony;

        // Server -> client config sync (ServerSync/blaxxun-boop) so every player connecting
        // to a dedicated server automatically uses the host's teleporter cost/cooldown
        // instead of whatever is in their own local config file.
        // Built in Awake, not in a field initialiser. As a static initialiser it ran the
        // moment anything so much as mentioned this type, which pulled ServerSync's own
        // static setup -- and that reaches into BepInEx internals. A plugin should do its
        // work when BepInEx tells it to, not when the class loader happens to touch it.
        private static ConfigSync ConfigSync;

        /// <summary>
        /// ServerSync sends the server's admin-list result to each remote client as its
        /// lock-exemption bit. Vanilla's LocalPlayerIsAdminOrHost does not reliably expose
        /// that result on a dedicated-server client, even though the server already knows
        /// the player is an admin. The NPC UI uses this client-side signal only to decide
        /// which tabs to draw; every mutation is still authorized again by the server RPC.
        /// </summary>
        internal static bool LocalPlayerIsServerSyncAdmin
        {
            get
            {
                if (ConfigSync == null) return false;

                // Host/single-player is the source of truth and needs no network sync.
                if (ZNet.instance != null && ZNet.instance.IsServer()) return true;

                // Before the initial package arrives ConfigSync temporarily starts as the
                // source of truth on every process. Never treat that transient state as an
                // admin grant on a remote client.
                return ConfigSync.InitialSyncDone && ConfigSync.IsAdmin;
            }
        }

        // Defaults applied to newly bound teleporters; existing ones keep whatever was set
        // via TeleporterNpc.ConfigureCost. Kept simple on purpose -- per-NPC overrides can
        // be added later without changing this config's shape.
        internal static ConfigEntry<string> TeleportCostItem;
        internal static ConfigEntry<int> TeleportCostAmount;
        internal static ConfigEntry<float> TeleportCooldownSeconds;
        internal static ConfigEntry<int> ListingDurationHours;
        internal static ConfigEntry<UnityEngine.KeyCode> QuestJournalKey;
        internal static ConfigEntry<bool> ShowQuestButton;
        internal static ConfigEntry<bool> ShowQuestTracker;
        internal static ConfigEntry<float> QuestTrackerX;
        internal static ConfigEntry<float> QuestTrackerY;
        internal static ConfigEntry<int> QuestTrackerMax;
        internal static ConfigEntry<float> QuestButtonX;
        internal static ConfigEntry<float> QuestButtonY;

        private void Awake()
        {
            Log = Logger;

            ConfigSync = new ConfigSync(Guid)
            {
                DisplayName = Name,
                CurrentVersion = Version,
                MinimumRequiredVersion = Version,
                ModRequired = true,
            };

            TeleportCostItem = Config.Bind("Teleporter", "CostItem", "",
                "Prefab name of the item charged per teleport (empty = free)");
            TeleportCostAmount = Config.Bind("Teleporter", "CostAmount", 0,
                "How many of CostItem are consumed per teleport");
            TeleportCooldownSeconds = Config.Bind("Teleporter", "CooldownSeconds", 0f,
                "Seconds a player must wait between uses of the same teleporter");

            QuestJournalKey = Config.Bind("Quests", "JournalKey", UnityEngine.KeyCode.J,
                "Opens the player's quest journal from anywhere in the world.");

            ShowQuestTracker = Config.Bind("Quests", "ShowTracker", true,
                "Shows the on-screen objective tracker: what you are doing and how far along, without opening a menu.");
            QuestTrackerX = Config.Bind("Quests", "TrackerX", 24f,
                "Distance in pixels from the right edge of the screen to the tracker.");
            QuestTrackerY = Config.Bind("Quests", "TrackerY", 200f,
                "Distance in pixels from the top edge of the screen to the tracker.");
            QuestTrackerMax = Config.Bind("Quests", "TrackerMaxQuests", 5,
                "How many quests the tracker shows at once. A tracker that fills the screen has stopped being a glance.");

            ShowQuestButton = Config.Bind("Quests", "ShowJournalButton", true,
                "Shows a button on the HUD that opens the quest journal and counts what is in progress.");
            QuestButtonX = Config.Bind("Quests", "JournalButtonX", 16f,
                "Distance in pixels from the left edge of the screen to the journal button.");
            QuestButtonY = Config.Bind("Quests", "JournalButtonY", 260f,
                "Distance in pixels from the top edge of the screen to the journal button. Raise it to clear another mod's bar.");

            ListingDurationHours = Config.Bind("Marketplace", "ListingDurationHours", 48,
                "How long a listing stays up before it expires and the unsold stock is mailed back to the seller.");

            ConfigSync.AddConfigEntry(TeleportCostItem);
            ConfigSync.AddConfigEntry(TeleportCostAmount);
            ConfigSync.AddConfigEntry(TeleportCooldownSeconds);

            var databaseDirectory = NpcStoragePaths.DatabaseDirectory;
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "market.db");
            MarketDatabase.Init(dbPath);
            MailDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "mail.db"));
            MarketDatabase.FlushOutbox();
            QuestDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "quests.db"));


            // Before anything reads the quests folder, so the shipped content is already there
            // the first time a quest giver is asked what it offers.
            ContentSeeder.Run();

            UiRoot.EnsureCreated();

            // No mail HUD. Reading your post is something you go to the Caixa Postal for --
            // an always-on stamp with a shortcut key turns a place in the world into a
            // menu, and the mailbox stops being a reason to walk into town.
            if (!UnityEngine.Application.isBatchMode)
            {
                QuestJournal.EnsureCreated();
                UI.QuestMapPins.EnsureCreated();
                UI.QuestHudButton.EnsureCreated();
                UI.QuestTracker.EnsureCreated();
            }

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            MarketDatabase.Shutdown();
            MailDatabase.Shutdown();
            QuestDatabase.Shutdown();
        }
    }
}


