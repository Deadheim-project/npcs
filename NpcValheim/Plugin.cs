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
    [BepInDependency(VipList.VipListPlugin.PluginGuid)]
    // Soft: the arena reads Deadheim's arena zones and patches its ally rule by name, and both
    // only work if Deadheim is loaded first. Without Deadheim the arena still runs.
    [BepInDependency("Detalhes.Deadheim", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: guild founding is gated to the Guild Registrar by patching Guilds' buttons by name.
    [BepInDependency("org.bepinex.plugins.guilds", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.npcvalheim.mod";
        public const string Name = "NpcValheim";
        public const string Version = "0.1.59";

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
        internal static ConfigEntry<string> GuildCostItem;
        internal static ConfigEntry<int> GuildCostAmount;
        internal static ConfigEntry<string> DeadcoinShopItems;
        internal static ConfigEntry<string> BossPassBosses;
        internal static ConfigEntry<float> BossPassKillRadius;
        internal static ConfigEntry<string> MountTrainerOffers;
        internal static ConfigEntry<UnityEngine.KeyCode> QuestJournalKey;
        internal static ConfigEntry<UnityEngine.KeyCode> VipShopKey;
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
                // Without the lock ServerSync also accepts values pushed by clients, so any
                // player with ConfigurationManager could reprice every new teleporter.
                // Admins stay exempt and can still edit from in game.
                IsLocked = true,
            };

            TeleportCostItem = Config.Bind("Teleporter", "CostItem", "",
                "Prefab name of the item charged per teleport (empty = free)");
            TeleportCostAmount = Config.Bind("Teleporter", "CostAmount", 0,
                "How many of CostItem are consumed per teleport");
            TeleportCooldownSeconds = Config.Bind("Teleporter", "CooldownSeconds", 0f,
                "Seconds a player must wait between uses of the same teleporter");

            QuestJournalKey = Config.Bind("Quests", "JournalKey", UnityEngine.KeyCode.J,
                "Opens the player's quest journal from anywhere in the world.");

            VipShopKey = Config.Bind("VIP", "ShopKey", UnityEngine.KeyCode.F7,
                "VIP-only shortcut that opens the Deadcoins shop from anywhere in the world. It opens nothing else.");

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
            QuestButtonX = Config.Bind("Quests", "JournalButtonX", 24f,
                "Distance in pixels from the right edge of the screen to the journal button. It sits on the right because the left is where other mods stack their bars.");
            QuestButtonY = Config.Bind("Quests", "JournalButtonY", 260f,
                "Distance in pixels from the top edge of the screen to the journal button. Raise it to clear another mod's bar.");

            ListingDurationHours = Config.Bind("Marketplace", "ListingDurationHours", 48,
                "How long a listing stays up before it expires and the unsold stock is mailed back to the seller.");

            // Same text as the ShopItems line of the retired DonationShop mod, so the server's
            // value can be pasted in as it is. Synchronized for display only: the server reads
            // its own copy on every purchase and never takes a price from the client.
            DeadcoinShopItems = Config.Bind("DeadcoinShop", "Items", Npc.DeadcoinCatalog.DefaultItems,
                "What the Deadcoins NPC sells: prefab=<item>;amount=<units per purchase>;price=<Deadcoins>, " +
                "entries separated by |. Entries with a non-positive amount or price, or an unknown item, are skipped and named in the log. " +
                "Balances live in BepInEx/config/DonationShop/<player>-<account>.json, as they did with DonationShop.");

            // Same shape as DeadcoinShop.Items, for the same reason: one line an admin can read
            // and paste. Synchronized so the counter can show the prices; the server re-reads its
            // own copy on every payment and on every boss death.
            BossPassBosses = Config.Bind("BossPass", "Bosses", Npc.BossPassCatalog.DefaultBosses,
                "Bosses that can lock a Mercador's shop: boss=<creature prefab>;name=<shown name>;shop=<shop it opens>;" +
                "gold=<Coins>;deadcoins=<Deadcoins>, entries separated by |. A price of 0 means that way of paying is not offered. " +
                "Killing the boss (being within KillRadius when it dies, or having hit it) unlocks it for free. " +
                "The lock itself is chosen per NPC in its Admin tab. Passes live in bosspass.txt, in the mod folder next to market.db.");
            BossPassKillRadius = Config.Bind("BossPass", "KillRadius", 60f,
                "Metres from the boss, measured on the ground, within which a player counts as present at its death. " +
                "Players who hit the boss count regardless of distance.");

            GuildCostItem = Config.Bind("Guildas", "CostItem", "Coins",
                "Prefab name of the item charged to found a guild at the Guild Registrar");
            GuildCostAmount = Config.Bind("Guildas", "CostAmount", 0,
                "How many of CostItem founding a guild costs (0 = free). Charged only once the guild exists");

            // What the Mestre das Montarias sells. The levels and the mounts themselves are the
            // Montarias mod's; this only prices them. Synchronized for display, like the
            // Deadcoins list: the server reads its own copy on every purchase.
            MountTrainerOffers = Config.Bind("MountTrainer", "Offers", Npc.MountCatalog.DefaultOffers,
                "What the Mestre das Montarias sells: skill=<level> (riding skill, learned in order) or mount=<Montarias mount id>, " +
                "with price=<amount> and currency=coins|deadcoins, entries separated by |. " +
                "Deadcoins are charged from the server's ledger. Coins live in the buyer's inventory, which the server cannot see: " +
                "the buyer's client pays after the server grants, as at the merchant, so a modified client can skip paying. " +
                "Sell what must not be had for free for Deadcoins. Entries Montarias does not know are skipped and named in the log.");

            // Server-authoritative entries. The Quests/VIP keys and HUD positions are left out
            // on purpose: they are each player's own preferences.
            ConfigSync.AddConfigEntry(TeleportCostItem).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TeleportCostAmount).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TeleportCooldownSeconds).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(ListingDurationHours).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(DeadcoinShopItems).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BossPassBosses).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BossPassKillRadius).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(GuildCostItem).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(GuildCostAmount).SynchronizedConfig = true;

            // [Arena*] sections: synchronized and locked like the rest, except the panel key.
            Arena.ArenaConfig.Bind(Config, ConfigSync);
            ConfigSync.AddConfigEntry(MountTrainerOffers).SynchronizedConfig = true;

            // Everything above is read where it is used (a new teleporter, a new listing, the
            // HUD every frame), so a reload takes effect on its own. The HUD offsets are the
            // exception and QuestTracker/QuestHudButton rebuild themselves when they change.
            // Teleporters already placed keep the cost stored on them.
            Deadheim.Shared.ConfigWatcher.Watch(Config, Name);

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
                UI.VipShopShortcut.EnsureCreated();
                UI.ArenaHud.EnsureCreated();
            }

            _harmony = new Harmony(Guid);
            _harmony.PatchAll();
            Log.LogInfo($"{Name} {Version} loaded");
        }

        /// <summary>The arena is the only part of the mod that runs on a clock rather than on
        /// requests: the queue matches, the gates open, invitations lapse, the week turns.</summary>
        private void Update()
        {
            Arena.ArenaServer.Tick();
            // Returns at once without a local player, so a dedicated server pays nothing.
            Arena.ArenaClient.Tick();
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


