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
        public const string Version = "0.1.1";

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

        // Defaults applied to newly bound teleporters; existing ones keep whatever was set
        // via TeleporterNpc.ConfigureCost. Kept simple on purpose -- per-NPC overrides can
        // be added later without changing this config's shape.
        internal static ConfigEntry<string> TeleportCostItem;
        internal static ConfigEntry<int> TeleportCostAmount;
        internal static ConfigEntry<float> TeleportCooldownSeconds;
        internal static ConfigEntry<int> ListingDurationHours;
        internal static ConfigEntry<UnityEngine.KeyCode> MarkWaypointKey;
        internal static ConfigEntry<UnityEngine.KeyCode> QuestJournalKey;
        internal static ConfigEntry<bool> ShowQuestButton;
        internal static ConfigEntry<bool> ShowQuestTracker;
        internal static ConfigEntry<float> QuestTrackerX;
        internal static ConfigEntry<float> QuestTrackerY;
        internal static ConfigEntry<int> QuestTrackerMax;
        internal static ConfigEntry<float> QuestButtonX;
        internal static ConfigEntry<float> QuestButtonY;

        // Not synced -- purely a local dev toggle, flip it in the .cfg file before launching
        // to get an automated pass/fail report in LogOutput.log with zero manual interaction
        // ("SELFTEST ..." lines). Off by default so it never runs in a normal player's game.
        internal static ConfigEntry<bool> EnableSelfTest;

        // Narrower dev convenience: just auto-confirms the character-selection screen, for
        // use with a `+connect ip:port password` launch so a whole join can happen with zero
        // clicks. Independent of EnableSelfTest -- this one doesn't spawn any test NPCs.
        internal static ConfigEntry<bool> AutoConfirmCharacterOnJoin;
        internal static ConfigEntry<string> AutoJoinPassword;
        internal static ConfigEntry<bool> ShowcaseMode;
        internal static ConfigEntry<bool> SimulateNonAdmin;
        internal static ConfigEntry<string> WorldName;
        internal static ConfigEntry<bool> AutoStartWorld;
        internal static ConfigEntry<bool> DemoScenarioMode;

        /// <summary>Runtime switch behind SimulateNonAdmin, rather than reading the config
        /// directly: the showcase has to dress the NPCs up as an admin *before* dropping to
        /// visitor rights, so it needs to turn this on at a specific moment.</summary>
        internal static bool NonAdminPreviewActive;

        /// <summary>True when any dev mode is on, so the intro/tutorial/raven skips apply to
        /// the showcase too -- Hugin walking into frame mid-capture is exactly the kind of
        /// noise those patches exist to remove.</summary>
        internal static bool TestModeActive =>
            (EnableSelfTest != null && EnableSelfTest.Value) || (ShowcaseMode != null && ShowcaseMode.Value);

        private void Awake()
        {
            Log = Logger;

            ConfigSync = new ConfigSync(Guid)
            {
                DisplayName = Name, CurrentVersion = Version, ModRequired = false,
            };

            TeleportCostItem = Config.Bind("Teleporter", "CostItem", "",
                "Prefab name of the item charged per teleport (empty = free)");
            TeleportCostAmount = Config.Bind("Teleporter", "CostAmount", 0,
                "How many of CostItem are consumed per teleport");
            TeleportCooldownSeconds = Config.Bind("Teleporter", "CooldownSeconds", 0f,
                "Seconds a player must wait between uses of the same teleporter");

            MarkWaypointKey = Config.Bind("Teleporter", "MarkWaypointKey", UnityEngine.KeyCode.F6,
                "Press anywhere in the world to remember that spot, then attach it to a teleporter from its Admin tab. Needed because the panel blocks movement, so a destination can't be recorded while it is open.");

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

            EnableSelfTest = Config.Bind("Testing", "EnableSelfTest", false,
                "Runs an automated smoke test shortly after spawning into a world and logs SELFTEST PASS/FAIL lines. Dev use only.");

            ShowcaseMode = Config.Bind("Testing", "ShowcaseMode", false,
                "Stages a demo scene on world entry (both NPCs, dressed, panel open) and leaves it standing, for capturing what the feature looks like. Dev use only.");

            DemoScenarioMode = Config.Bind("Testing", "DemoScenarioMode", false,
                "With ShowcaseMode on, runs the scripted end-to-end demo (buy from another player, mail, quests, teleport) instead of the static showcase. Dev use only.");



            AutoStartWorld = Config.Bind("Testing", "AutoStartWorld", false,
                "Loads the world named by WorldName straight from launch, in singleplayer, skipping the menu. Independent of EnableSelfTest, so a local world can be opened hands-off without also spawning the test NPCs. Dev use only.");

            WorldName = Config.Bind("Testing", "WorldName", "",
                "Which world the dev auto-start loads. Empty picks the first world that is already generated, because loading an ungenerated one costs minutes of world generation before a test can start. Dev use only.");

            SimulateNonAdmin = Config.Bind("Testing", "SimulateNonAdmin", false,
                "With ShowcaseMode on, hands the staged NPCs to another player and drops admin rights, so the panel renders exactly as an ordinary visitor sees it. Dev use only.");

            AutoConfirmCharacterOnJoin = Config.Bind("Testing", "AutoConfirmCharacterOnJoin", false,
                "Auto-clicks past the password and character-selection screens. Meant to pair with a `+connect ip:port password` launch for a fully hands-off join. Dev use only.");
            AutoJoinPassword = Config.Bind("Testing", "AutoJoinPassword", "",
                "Password to auto-submit on the server password screen when AutoConfirmCharacterOnJoin is on (should match the +connect launch arg).");

            ConfigSync.AddConfigEntry(TeleportCostItem);
            ConfigSync.AddConfigEntry(TeleportCostAmount);
            ConfigSync.AddConfigEntry(TeleportCooldownSeconds);

            var dbPath = Path.Combine(Paths.PluginPath, "NpcValheim", "market.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            MarketDatabase.Init(dbPath);
            MailDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "mail.db"));
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
                Npc.WaypointMarker.EnsureCreated();
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


