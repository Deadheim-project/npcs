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
        public const string Version = "0.1.84";

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
        internal static ConfigEntry<int> MaxListingsPerPlayer;
        internal static ConfigEntry<int> VipMaxListings;
        internal static ConfigEntry<int> VipMaxAttachments;
        internal static ConfigEntry<string> GuildCostItem;
        internal static ConfigEntry<int> GuildCostAmount;
        internal static ConfigEntry<bool> GuildWindowNpcOnly;
        internal static ConfigEntry<string> DeadcoinShopItems;
        internal static ConfigEntry<string> BossPassBosses;
        internal static ConfigEntry<float> BossPassKillRadius;
        internal static ConfigEntry<bool> BossPassDeadcoinsAfterFirstKill;
        internal static ConfigEntry<string> MountTrainerOffers;
        internal static ConfigEntry<float> TrainerStudyBonus;
        internal static ConfigEntry<int> TrainerStudyMinutes;
        internal static ConfigEntry<int> TrainerStudyMaxSkill;
        internal static ConfigEntry<float> TrainerProtectionFactor;
        internal static ConfigEntry<bool> TrainerProtectionPveOnly;
        internal static ConfigEntry<string> TrainerLessonCaps;
        internal static ConfigEntry<int> TrainerLessonMaxLevel;
        internal static ConfigEntry<int> TrainerLessonsPerWeek;
        internal static ConfigEntry<string> TrainerPrices;
        internal static ConfigEntry<bool> BarberEnabled;
        internal static ConfigEntry<string> BarberTokenPrefab;
        internal static ConfigEntry<string> BarberSkinColors;
        internal static ConfigEntry<string> BarberHairColors;
        internal static ConfigEntry<bool> BarberAllowSkin;
        internal static ConfigEntry<bool> TailorEnabled;
        internal static ConfigEntry<int> TailorPriceCoins;
        internal static ConfigEntry<int> TailorMinArenaRating;
        internal static ConfigEntry<int> TailorArenaBracket;
        // Per armor type (Helmet, Chest, Legs, Shoulder), in the order of Tailor.Slots
        internal static ConfigEntry<bool>[] TailorSlotEnabled;
        internal static ConfigEntry<int>[] TailorSlotPrice;
        internal static ConfigEntry<int>[] TailorSlotMinRating;
        internal static ConfigEntry<bool> SeasonEnabled;
        internal static ConfigEntry<string> SeasonId;
        internal static ConfigEntry<string> SeasonName;
        internal static ConfigEntry<string> SeasonStart;
        internal static ConfigEntry<string> SeasonEnd;
        internal static ConfigEntry<int> SeasonPointsPerLevel;
        internal static ConfigEntry<int> SeasonFreeLevels;
        internal static ConfigEntry<int> SeasonPrice;
        internal static ConfigEntry<string> SeasonRewards;
        internal static ConfigEntry<string> SeasonChallenges;
        internal static ConfigEntry<UnityEngine.KeyCode> QuestJournalKey;
        internal static ConfigEntry<UnityEngine.KeyCode> VipShopKey;
        internal static ConfigEntry<UnityEngine.KeyCode> AdminPanelKey;
        internal static ConfigEntry<bool> ShowQuestButton;
        internal static ConfigEntry<bool> ShowQuestTracker;
        internal static ConfigEntry<float> QuestTrackerX;
        internal static ConfigEntry<float> QuestTrackerY;
        internal static ConfigEntry<int> QuestTrackerMax;
        internal static ConfigEntry<float> QuestButtonX;
        internal static ConfigEntry<float> QuestButtonY;

        /// <summary>
        /// Keys the mod no longer binds (the arena's Brackets and Solo, say) stay in the cfg as
        /// orphans and are written back on every save. With every entry bound above, whatever is
        /// still orphaned is gone from the code, so it goes from the file too.
        /// </summary>
        private static void PruneOrphans(ConfigFile config)
        {
            try
            {
                var orphans = AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config, null)
                    as System.Collections.Generic.Dictionary<ConfigDefinition, string>;
                if (orphans == null || orphans.Count == 0) return;
                var names = new System.Collections.Generic.List<string>();
                foreach (var key in orphans.Keys) names.Add($"[{key.Section}] {key.Key}");
                orphans.Clear();
                config.Save();
                Log.LogInfo($"NpcValheim: {names.Count} entrada(s) que o mod nao usa mais sairam do cfg: {string.Join(", ", names)}");
            }
            catch (System.Exception e)
            {
                Log.LogWarning("NpcValheim: nao foi possivel limpar as entradas orfas do cfg: " + e.Message);
            }
        }

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

            AdminPanelKey = Config.Bind("Admin", "PanelKey", UnityEngine.KeyCode.F8,
                "Admin-only: opens the Deadheim admin panel (global message, go to / pull a player, buff and heal in an area).");

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

            MaxListingsPerPlayer = Config.Bind("Marketplace", "MaxListingsPerPlayer", 50,
                "Máximo de anúncios simultâneos por jogador não-VIP em qualquer quadro de leilão.");
            VipMaxListings = Config.Bind("Marketplace", "VipMaxListings", 100,
                "Máximo de anúncios simultâneos para jogadores VIP. Só vale quando o quadro tem menos de 400 anúncios no total.");

            VipMaxAttachments = Config.Bind("Mail", "VipMaxAttachments", 3,
                "Quantos stacks de itens um jogador VIP pode anexar a uma única carta. " +
                "Não-VIP pode enviar apenas 1 stack por carta. " +
                "O destinatário nunca é limitado para receber.");

            // Same text as the ShopItems line of the retired DonationShop mod, so the server's
            // value can be pasted in as it is. Synchronized for display only: the server reads
            // its own copy on every purchase and never takes a price from the client.
            DeadcoinShopItems = Config.Bind("DeadcoinShop", "Items", Npc.DeadcoinCatalog.DefaultItems,
                "What the Deadcoins NPC sells: prefab=<item>;amount=<units per purchase>;price=<Deadcoins>, " +
                "entries separated by |. An entry may add from=<yyyy-MM-dd> and/or to=<yyyy-MM-dd> for a sale window " +
                "(both dates included); outside it the item is hidden and a purchase is refused, checked on the server's own clock. " +
                "Entries with a non-positive amount or price, an unknown item, or a bad date are skipped and named in the log. " +
                "Admins can also edit amount/price at the NPC, tab Itens (admin) -- a window set here survives that edit. " +
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
            BossPassDeadcoinsAfterFirstKill = Config.Bind("BossPass", "DeadcoinsAfterFirstKill", true,
                "Liga: o preço em Deadcoins de um chefe só aparece e só é aceito depois que esse chefe morreu " +
                "neste servidor pela primeira vez (por qualquer jogador). Antes disso o passe só sai matando o " +
                "chefe, ou por Coins se o admin puser gold>0 em [BossPass] Bosses. A marca da 1ª morte fica em " +
                "bossfirstkill.txt, na pasta do mod ao lado de bosspass.txt -- separada dela porque uma linha ali " +
                "seria lida como passe de personagem pelo teto das aulas do Treinador. Desliga: Deadcoins sempre " +
                "liberado, como antes desta trava.");

            GuildCostItem = Config.Bind("Guildas", "CostItem", "Coins",
                "Prefab name of the item charged to found a guild at the Guild Registrar");
            GuildCostAmount = Config.Bind("Guildas", "CostAmount", 0,
                "How many of CostItem founding a guild costs (0 = free). Charged only once the guild exists");
            GuildWindowNpcOnly = Config.Bind("Guildas", "SomentePeloNpc", true,
                "Liga: a janela de guildas (do mod Guilds) so abre no Registrador de Guildas; a tecla do Guilds " +
                "nao abre nada. Desliga: a tecla volta a abrir a janela (fundar continua so no Registrador).");

            // What the Mestre das Montarias sells. The levels and the mounts themselves are the
            // Montarias mod's; this only prices them. Synchronized for display, like the
            // Deadcoins list: the server reads its own copy on every purchase.
            MountTrainerOffers = Config.Bind("MountTrainer", "Offers", Npc.MountCatalog.DefaultOffers,
                "What the Mestre das Montarias sells: skill=<level> (riding skill, learned in order) or mount=<Montarias mount id>, " +
                "with price=<amount> and currency=coins|deadcoins, entries separated by |. " +
                "Deadcoins are charged from the server's ledger. Coins live in the buyer's inventory, which the server cannot see: " +
                "the buyer's client pays after the server grants, as at the merchant, so a modified client can skip paying. " +
                "Sell what must not be had for free for Deadcoins. Entries Montarias does not know are skipped and named in the log.");
            if (System.Array.IndexOf(Npc.MountCatalog.OldDefaultOffers, MountTrainerOffers.Value) >= 0)
                MountTrainerOffers.Value = Npc.MountCatalog.DefaultOffers;

            // The Treinador. Every number is the server's: synchronized for display only, and
            // the server reads its own copy on every purchase and sends the buff's numbers with
            // the grant. The buffs themselves run in Deadheim (Deadheim.TreinadorApi).
            const string trainer = "Trainer";
            TrainerStudyBonus = Config.Bind(trainer, "StudyBonus", 0.5f,
                "Estudo: XP de skill a mais enquanto dura (0.5 = +50%). Multiplica o ganho que ja passou pelo SkillMultiplier " +
                "do Deadheim e pelo de PvE/PvP: com SkillMultiplier 0.5 o ganho vai a 0.75 do vanilla. 0 desliga a venda.");
            TrainerStudyMinutes = Config.Bind(trainer, "StudyMinutes", 60,
                "Estudo: minutos de jogo que dura (so corre com o jogo aberto). Comprar com um ativo soma o tempo.");
            TrainerStudyMaxSkill = Config.Bind(trainer, "StudyMaxSkill", 50,
                "Estudo: so vale nas skills abaixo deste nivel.");
            TrainerProtectionFactor = Config.Bind(trainer, "ProtectionFactor", 0.5f,
                "Protecao: fracao da perda de skill que ela corta na proxima morte em que vale (0.5 = metade). 0 desliga a venda. " +
                "A pena de PK nunca e reduzida, e morte que nao tira skill (arena, castelo) nao gasta a protecao.");
            TrainerProtectionPveOnly = Config.Bind(trainer, "ProtectionPveOnly", true,
                "Protecao: so em morte PvE (monstro, queda, afogamento). Desligado, vale tambem em morte para jogador sem PK.");
            TrainerLessonCaps = Config.Bind(trainer, "LessonCaps", Npc.TrainerCatalog.DefaultCaps,
                "Aula: ate que nivel o Treinador ensina, pelo passe de chefe do jogador (o do Mestre dos Passes): " +
                "<boss>:<nivel> separados por |, e none:<nivel> para quem nao tem passe nenhum. Vale o maior a que o jogador tem direito. " +
                "Sem none, quem nao tem passe nao tem aula.");
            TrainerLessonMaxLevel = Config.Bind(trainer, "LessonMaxLevel", 50,
                "Aula: teto absoluto. Nenhum passe faz a aula passar deste nivel.");
            TrainerLessonsPerWeek = Config.Bind(trainer, "LessonsPerWeek", 5,
                "Aula: quantas por personagem por semana (segunda a domingo, relogio do servidor). 0 = sem limite. " +
                "As aulas dadas ficam em treinador.txt, na pasta do mod ao lado do bosspass.txt.");
            TrainerPrices = Config.Bind(trainer, "Prices", Npc.TrainerCatalog.DefaultPrices,
                "Precos: item=study ou item=protection, e item=lesson;levels=<de>-<ate> por faixa de nivel alvo (o nivel depois da aula), " +
                "cada um com gold=<Coins>;deadcoins=<Deadcoins>, entradas separadas por |. Preco 0 = nao vende naquela moeda. " +
                "Deadcoins saem do saldo do servidor; Coins ficam na bolsa do jogador e o cliente paga depois de receber, " +
                "como no Mestre das Montarias (um cliente modificado pode nao pagar).");

            // The Barber
            const string barber = "Barber";
            BarberEnabled = Config.Bind(barber, "Enabled", false,
                "Liga o Barbeiro NPC. Desligado, o NPC não interage.");
            BarberTokenPrefab = Config.Bind(barber, "TokenPrefab", "FichaBarbeiro",
                "Nome do prefab da ficha que o Barbeiro cobra.");
            BarberSkinColors = Config.Bind(barber, "SkinColors", "",
                "Paleta de cores de pele permitidas (RGB como \"r,g,b\" separados por |). Vazio = qualquer cor natural.");
            BarberHairColors = Config.Bind(barber, "HairColors", "",
                "Paleta de cores de cabelo permitidas (RGB como \"r,g,b\" separados por |). Vazio = qualquer cor.");
            BarberAllowSkin = Config.Bind(barber, "AllowSkin", false,
                "Permite mudança de cor de pele. Falso por padrão (evita disfarce extremo).");

            // The Alfaiate (Deadheim's transmog behind a counter)
            const string tailor = "Tailor";
            TailorEnabled = Config.Bind(tailor, "Enabled", false,
                "Liga o Alfaiate NPC (transmog do Deadheim). Desligado, o NPC não interage.");
            TailorPriceCoins = Config.Bind(tailor, "PriceCoins", 500,
                "Moedas (Coins) por peça que muda de aparência. Voltar ao visual real é grátis. " +
                "As moedas saem da bolsa do jogador depois que o servidor aplica (um cliente modificado pode não pagar).");
            TailorMinArenaRating = Config.Bind(tailor, "MinArenaRating", 1200,
                "Rating de arena mínimo para trocar a aparência (o mesmo do Intendente: o menor entre o pessoal e o do time). 0 = sem exigência.");
            TailorArenaBracket = Config.Bind(tailor, "ArenaBracket", 1,
                "Tamanho mínimo do time cujo rating vale (1 = qualquer time, 3 = só 3v3 ou maior).");
            int slots = NpcValheim.Npc.Tailor.Slots.Length;
            TailorSlotEnabled = new ConfigEntry<bool>[slots];
            TailorSlotPrice = new ConfigEntry<int>[slots];
            TailorSlotMinRating = new ConfigEntry<int>[slots];
            for (int i = 0; i < slots; i++)
            {
                string slot = NpcValheim.Npc.Tailor.Slots[i];
                string nome = NpcValheim.Npc.Tailor.SlotNames[i];
                TailorSlotEnabled[i] = Config.Bind(tailor, slot + "Enabled", true,
                    $"Permite trocar a aparência de {nome} ({slot}) no Alfaiate. Falso = esse tipo não pode ser trocado (voltar ao real continua grátis).");
                TailorSlotPrice[i] = Config.Bind(tailor, slot + "PriceCoins", -1,
                    $"Moedas por peça de {nome} ({slot}) que muda de aparência. -1 = usa PriceCoins.");
                TailorSlotMinRating[i] = Config.Bind(tailor, slot + "MinArenaRating", -1,
                    $"Rating de arena mínimo para trocar {nome} ({slot}). -1 = usa MinArenaRating; 0 = sem exigência.");
            }

            // The Season Herald (season pass). The server decides everything, so none of this is synced.
            const string season = "Season";
            SeasonEnabled = Config.Bind(season, "Enabled", false,
                "Liga o passe de temporada. Desligado, o Arauto da Temporada não mostra nada e nenhum ponto é contado.");
            SeasonId = Config.Bind(season, "SeasonId", "t1",
                "Identificador da temporada. Trocar o id começa uma temporada nova do zero (a antiga fica guardada).");
            SeasonName = Config.Bind(season, "Name", "Temporada 1", "Nome mostrado na tela do Arauto.");
            SeasonStart = Config.Bind(season, "Start", "2026-11-01",
                "Primeiro dia (aaaa-mm-dd, UTC). A semana 1 começa neste dia.");
            SeasonEnd = Config.Bind(season, "End", "2026-11-29",
                "Dia em que a temporada acaba (aaaa-mm-dd, UTC; este dia já não conta). Depois disso o progresso congela.");
            SeasonPointsPerLevel = Config.Bind(season, "PointsPerLevel", 90,
                "Pontos por nível (10 níveis). Com 3 desafios de 80 pontos por semana, 90 fecha os 10 níveis em ~4 semanas.");
            SeasonFreeLevels = Config.Bind(season, "FreeLevels", 3, "Quantos dos primeiros níveis são grátis.");
            SeasonPrice = Config.Bind(season, "Price", 500,
                "Deadcoins para liberar a trilha paga (retroativo: o que já foi ganho em pontos é entregue na hora).");
            SeasonRewards = Config.Bind(season, "Rewards", Npc.SeasonCatalog.DefaultRewards,
                "nível=chave do registro de cosméticos do Deadheim, separados por |. Só cosméticos: nunca item de combate.");
            SeasonChallenges = Config.Bind(season, "Challenges", Npc.SeasonCatalog.DefaultChallenges,
                "Pool de desafios semanais, separados por |: id;tipo;alvo;quantidade;pontos;texto. Tipo: Kill, Gather ou Talk. " +
                "Alvo: prefab, 'Prefixo*' ou '*' (qualquer). Três deles, sorteados por semana, valem para todos. Nada de PvP.");

            // Server-authoritative entries. The Quests/VIP keys and HUD positions are left out
            // on purpose: they are each player's own preferences.
            ConfigSync.AddConfigEntry(TeleportCostItem).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TeleportCostAmount).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TeleportCooldownSeconds).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(ListingDurationHours).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(MaxListingsPerPlayer).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(VipMaxListings).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(VipMaxAttachments).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(DeadcoinShopItems).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BossPassBosses).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BossPassKillRadius).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BossPassDeadcoinsAfterFirstKill).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(GuildCostItem).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(GuildCostAmount).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(GuildWindowNpcOnly).SynchronizedConfig = true;

            // [Arena*] sections: synchronized and locked like the rest, except the panel key.
            Arena.ArenaConfig.Bind(Config, ConfigSync);

            // [Teste]: off by default; a test world with maxed skills, coins and a free shop.
            Testing.TestWorldConfig.Bind(Config, ConfigSync);

            ConfigSync.AddConfigEntry(MountTrainerOffers).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerStudyBonus).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerStudyMinutes).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerStudyMaxSkill).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerProtectionFactor).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerProtectionPveOnly).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerLessonCaps).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerLessonMaxLevel).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerLessonsPerWeek).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TrainerPrices).SynchronizedConfig = true;

            ConfigSync.AddConfigEntry(BarberEnabled).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BarberTokenPrefab).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BarberSkinColors).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BarberHairColors).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(BarberAllowSkin).SynchronizedConfig = true;

            ConfigSync.AddConfigEntry(TailorEnabled).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TailorPriceCoins).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(SeasonPrice).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TailorMinArenaRating).SynchronizedConfig = true;
            ConfigSync.AddConfigEntry(TailorArenaBracket).SynchronizedConfig = true;
            for (int i = 0; i < TailorSlotEnabled.Length; i++)
            {
                ConfigSync.AddConfigEntry(TailorSlotEnabled[i]).SynchronizedConfig = true;
                ConfigSync.AddConfigEntry(TailorSlotPrice[i]).SynchronizedConfig = true;
                ConfigSync.AddConfigEntry(TailorSlotMinRating[i]).SynchronizedConfig = true;
            }

            // Everything above is read where it is used (a new teleporter, a new listing, the
            // HUD every frame), so a reload takes effect on its own. The HUD offsets are the
            // exception and QuestTracker/QuestHudButton rebuild themselves when they change.
            // Teleporters already placed keep the cost stored on them.
            PruneOrphans(Config);
            Deadheim.Shared.ConfigWatcher.Watch(Config, Name);

            var databaseDirectory = NpcStoragePaths.DatabaseDirectory;
            Directory.CreateDirectory(databaseDirectory);
            var dbPath = Path.Combine(databaseDirectory, "market.db");
            MarketDatabase.Init(dbPath);
            MailDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "mail.db"));
            MarketDatabase.FlushOutbox();
            QuestDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "quests.db"));
            SeasonDatabase.Init(Path.Combine(Path.GetDirectoryName(dbPath)!, "season.db"));


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
                UI.AdminPanelShortcut.EnsureCreated();
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
            // Server only, every 30 s: files whoever is connected in the mail address book.
            Npc.MailboxNpc.RememberOnlinePlayers();
            // Returns at once without a local player, so a dedicated server pays nothing.
            Arena.ArenaClient.Tick();
            Testing.TestWorld.Tick();
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            MarketDatabase.Shutdown();
            MailDatabase.Shutdown();
            QuestDatabase.Shutdown();
            SeasonDatabase.Shutdown();
        }
    }
}
