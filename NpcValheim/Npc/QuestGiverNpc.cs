using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using NpcValheim.Integration;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>One quest as the client sees it: the definition plus this player's state.</summary>
    public class QuestView
    {
        public string Id;
        public string Name;
        public string Description;
        public string ObjectiveText;
        public int Counter;
        public int Goal;
        public QuestStatus Status;
        public bool CanTurnIn;
        public bool LevelLocked;
        public int RequiredLevel;

        /// <summary>Locked for any reason -- level, or an unfinished earlier quest -- with a
        /// sentence explaining which. One flag, so the panel and the nameplate marker never
        /// disagree about whether a quest is offerable.</summary>
        public bool Locked;
        public string LockReason;
        public string RewardText;

        /// <summary>Rewards in structured form as well as prose, so the panel can draw the
        /// real item icons instead of only printing names.</summary>
        public int RewardCoins;
        public int RewardExperience;
        public List<QuestRewardEntry> RewardItems = new List<QuestRewardEntry>();

        /// <summary>The objective in machine-readable form. The prose in ObjectiveText is for
        /// display only -- deciding anything by parsing it (which an earlier version did)
        /// breaks the moment the wording or the language changes.</summary>
        public QuestObjectiveKind Objective;
        public string Target;

        /// <summary>Comes back on a timer -- what WoW calls a daily, and what its blue "!"
        /// marks. Carried to the client so the marker and the tracker can say so.</summary>
        public bool Repeats;
    }

    public class QuestRewardEntry
    {
        public string ItemName;
        public int Amount;
    }

    /// <summary>
    /// Hands out quests defined in YAML and pays out on completion. All state changes run on
    /// the peer that owns the ZDO (the server in multiplayer), and rewards are posted to the
    /// mailbox rather than dropped, so finishing a quest works the same whether or not the
    /// player has room or is about to log off.
    ///
    /// Kill objectives are counted from the client that landed the kill (QuestKillTracker)
    /// and reported over RPC. That is the same trust boundary the marketplace already
    /// accepts for "did you really have this item" -- a dedicated server cannot see a remote
    /// client's kills any more than it can see their inventory.
    /// </summary>
    public class QuestGiverNpc : NpcBase
    {
        public List<QuestView> CachedQuests { get; private set; } = new List<QuestView>();
        public bool HasSyncedOnce { get; private set; }

        private const string KeyQuests = "npcv_qg_quests";

        /// <summary>
        /// The quests this particular giver offers, by id.
        ///
        /// An empty list means the whole folder, which is how a freshly placed NPC behaves
        /// before anyone has configured it -- useful out of the box, and the moment an admin
        /// assigns anything the NPC becomes a character with their own errands instead of one
        /// more copy of the same board.
        /// </summary>
        public List<string> GetOfferedQuestIds()
        {
            var result = new List<string>();
            if (Nview == null || !Nview.IsValid()) return result;

            var packed = Nview.GetZDO().GetString(KeyQuests, "");
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var id in packed.Split('\n'))
                if (!string.IsNullOrWhiteSpace(id)) result.Add(id.Trim());
            return result;
        }

        /// <summary>The quests actually on offer here, resolved against what exists on disk.</summary>
        private IEnumerable<QuestDefinition> OfferedQuests()
        {
            var ids = GetOfferedQuestIds();
            if (ids.Count == 0) return QuestStore.All;

            var chosen = new List<QuestDefinition>();
            foreach (var id in ids)
            {
                var quest = QuestStore.Get(id);
                if (quest != null) chosen.Add(quest);
            }
            return chosen;
        }

        /// <summary>True when this giver is willing to deal in a quest at all. Every
        /// authoritative handler asks, so a client cannot accept or hand in a quest at an NPC
        /// that does not offer it.</summary>
        private bool Offers(string questId)
        {
            var ids = GetOfferedQuestIds();
            return ids.Count == 0 || ids.Contains(questId);
        }

        public override NpcProfile BuildProfile()
        {
            var profile = base.BuildProfile();
            profile.QuestGiver = new QuestGiverSettings { Quests = GetOfferedQuestIds() };
            return profile;
        }

        protected override void ApplyTypeSpecificProfile(NpcProfile profile)
        {
            // A template with no list leaves the NPC's own quests alone, the same rule the
            // teleporter uses for destinations: applying a *look* must not silently empty the
            // board of a working quest giver.
            var quests = profile.QuestGiver?.Quests;
            if (quests == null || quests.Count == 0) return;

            Nview.GetZDO().Set(KeyQuests, string.Join("\n", quests));
        }

        /// <summary>
        /// Creates a quest from the admin panel. `packed` is
        /// "id;name;objective;target;amount;coins;xp;resetHours;description".
        ///
        /// It goes to the server because that is where the quest files live -- an admin on a
        /// remote client has no quests folder of their own, and a quest that existed only on
        /// the machine that typed it would be invisible to everyone else.
        /// </summary>
        public void RequestCreateQuest(Player requester, string packed)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_CreateQuest", packed ?? "");
        }

        private void RPC_CreateQuest(long sender, string packed)
        {
            if (!CanAdminister(sender)) return;

            var p = (packed ?? "").Split(';');
            if (p.Length < 8) return;

            if (!int.TryParse(p[2], out int objective)) return;
            if (!int.TryParse(p[4], out int amount)) return;
            int.TryParse(p[5], out int coins);
            int.TryParse(p[6], out int experience);
            int.TryParse(p[7], out int resetHours);

            var quest = new QuestDefinition
            {
                Id = p[0],
                Name = p[1],
                Description = p.Length > 8 ? p[8] : "",
                Objective = (QuestObjectiveKind)Mathf.Clamp(objective, 0, 4),
                Target = p[3],
                Amount = Mathf.Max(1, amount),
                ResetHours = Mathf.Max(0, resetHours),
                Rewards = new QuestRewards { Coins = Mathf.Max(0, coins), Experience = Mathf.Max(0, experience) },
            };

            if (!QuestStore.Save(quest, out string error))
            {
                Plugin.Log.LogWarning($"NpcValheim: quest not created: {error}");
                return;
            }

            // Offered by the NPC that created it, otherwise an admin would make a quest and
            // then have to go and assign it as a separate step.
            var ids = GetOfferedQuestIds();
            if (ids.Count > 0 && !ids.Contains(quest.Id))
            {
                ids.Add(quest.Id);
                Nview.GetZDO().Set(KeyQuests, string.Join("\n", ids));
            }

            PersistProfileSnapshot();
            SendQuestsTo(sender);
        }

        public void RequestSetQuests(Player requester, List<string> questIds)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            Nview.InvokeRPC("RPC_SetQuests", string.Join("\n", questIds ?? new List<string>()));
        }

        private void RPC_SetQuests(long sender, string packed)
        {
            if (!CanAdminister(sender)) return;
            Nview.GetZDO().Set(KeyQuests, packed ?? "");
            PersistProfileSnapshot();
            SendQuestsTo(sender);
        }

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_SetQuests", (Action<long, string>)RPC_SetQuests);
            Nview.Register("RPC_CreateQuest", (Action<long, string>)RPC_CreateQuest);
            Nview.Register("RPC_RequestQuests", (Action<long>)RPC_RequestQuests);
            Nview.Register("RPC_QuestData", (Action<long, string>)RPC_QuestData);
            Nview.Register("RPC_AcceptQuest", (Action<long, string>)RPC_AcceptQuest);
            Nview.Register("RPC_TurnInQuest", (Action<long, string>)RPC_TurnInQuest);
            Nview.Register("RPC_AbandonQuest", (Action<long, string>)RPC_AbandonQuest);
            Nview.Register("RPC_ReportKill", (Action<long, string, int>)RPC_ReportKill);
            Nview.Register("RPC_GrantExperience", (Action<long, int>)RPC_GrantExperience);
            Nview.Register("RPC_CollectRewards", (Action<long, string>)RPC_CollectRewards);
            Nview.Register("RPC_ClaimDelivered", (Action<long, string>)RPC_ClaimDelivered);
            Nview.Register("RPC_ReportPickup", (Action<long, string, int>)RPC_ReportPickup);
            Nview.Register("RPC_ReportTalk", (Action<long, string, int>)RPC_ReportTalk);
            Nview.Register("RPC_ReportArrival", (Action<long, string, int>)RPC_ReportArrival);
            Nview.Register("RPC_ProgressNotice", (Action<long, string, string>)RPC_ProgressNotice);
        }

        // ---- client-side requests ----

        public void RequestQuests()
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_RequestQuests");
        }

        public void RequestAccept(string questId)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_AcceptQuest", questId);
        }

        public void RequestTurnIn(string questId)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_TurnInQuest", questId);
        }

        public void RequestAbandon(string questId)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_AbandonQuest", questId);
        }

        /// <summary>Called by QuestKillTracker on the client that made the kill.</summary>
        public void ReportKill(string creatureName, int count)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_ReportKill", creatureName, count);
        }

        /// <summary>Called on the client that picked the item up, for Gather quests.</summary>
        public void ReportPickup(string itemName, int count)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_ReportPickup", itemName, count);
        }

        /// <summary>Called when the local player opens a panel on an NPC, for Talk quests.</summary>
        public void ReportTalk(string npcName)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_ReportTalk", npcName ?? "", 1);
        }

        /// <summary>Called when the local player reaches an Explore quest's destination.</summary>
        public void ReportArrival(string questId)
        {
            if (Nview != null && Nview.IsValid()) Nview.InvokeRPC("RPC_ReportArrival", questId ?? "", 1);
        }

        /// <summary>Any quest giver currently loaded. The counters live in one database keyed
        /// by player, so it does not matter which one carries the message.</summary>
        internal static QuestGiverNpc AnyLoaded()
        {
            foreach (var giver in FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None))
                if (giver != null) return giver;
            return null;
        }

        // ---- authoritative handlers ----

        private void RPC_RequestQuests(long sender) => SendQuestsTo(sender);

        private void RPC_QuestData(long sender, string packed)
        {
            CachedQuests = Unpack(packed);
            HasSyncedOnce = true;
        }

        private void RPC_AcceptQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            if (!Offers(questId)) return;   // this giver does not deal in that quest
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null) return;

            // Refresh first so a daily that came due is acceptable again, then refuse
            // anything still finished.
            var status = QuestDatabase.RefreshAndGetStatus(playerId, quest);
            if (status == QuestStatus.Completed && !quest.Repeatable) return;

            // Authoritative, not just hidden in the UI: a client that asks for a locked
            // quest anyway gets refused here.
            var missing = QuestDatabase.MissingPrerequisites(playerId, quest);
            if (missing.Count > 0)
            {
                Plugin.Log.LogInfo($"NpcValheim: accept refused for '{questId}' -- missing {string.Join(", ", missing)}");
                SendQuestsTo(sender);
                return;
            }

            QuestDatabase.Accept(playerId, questId);
            SendQuestsTo(sender);
        }

        private void RPC_AbandonQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            QuestDatabase.Abandon(playerId, questId);
            SendQuestsTo(sender);
        }

        private void RPC_ReportKill(long sender, string creatureName, int count) =>
            CreditEvent(sender, QuestObjectiveKind.Kill, creatureName, count);

        /// <summary>Reported by the client each time it picks an item up, for Gather quests.</summary>
        private void RPC_ReportPickup(long sender, string itemName, int count) =>
            CreditEvent(sender, QuestObjectiveKind.Gather, itemName, count);

        /// <summary>Reported when the player opens a panel on an NPC, for Talk quests.</summary>
        private void RPC_ReportTalk(long sender, string npcName, int count) =>
            CreditEvent(sender, QuestObjectiveKind.Talk, npcName, 1);

        /// <summary>Reported when the player reaches a place, for Explore quests. The client
        /// watches its own position because the server does not simulate where a remote player
        /// is standing frame by frame.</summary>
        private void RPC_ReportArrival(long sender, string questId, int count) =>
            CreditQuest(sender, questId, QuestObjectiveKind.Explore, 1);

        /// <summary>
        /// Advances every active quest of a given kind whose target matches.
        ///
        /// One funnel for all four event kinds: they differ only in what counts as a match,
        /// and having a single place that decides "is this quest active, is this the right
        /// kind, does the target match, cap the increment" is what keeps a new objective type
        /// from quietly skipping one of those checks.
        /// </summary>
        private void CreditEvent(long sender, QuestObjectiveKind kind, string target, int count)
        {
            if (!Nview.IsOwner() || count <= 0 || count > 100) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L || string.IsNullOrEmpty(target)) return;

            bool advanced = false;
            foreach (var progress in QuestDatabase.GetAll(playerId))
            {
                if (progress.Status != QuestStatus.Active) continue;
                var quest = QuestStore.Get(progress.QuestId);
                if (quest == null || quest.Objective != kind) continue;
                if (!string.Equals(quest.Target, target, StringComparison.OrdinalIgnoreCase)) continue;

                int now = QuestDatabase.AddProgress(playerId, quest.Id, count, quest.Amount);
                advanced = true;
                Nview.InvokeRPC(sender, "RPC_ProgressNotice", quest.Name, now + ";" + quest.Amount);
            }

            if (advanced) SendQuestsTo(sender);
        }

        /// <summary>Advances one named quest, for events that identify the quest rather than a
        /// target -- arriving somewhere is only meaningful against the quest that asked.</summary>
        private void CreditQuest(long sender, string questId, QuestObjectiveKind kind, int count)
        {
            if (!Nview.IsOwner() || count <= 0) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null || quest.Objective != kind) return;

            var progress = QuestDatabase.Get(playerId, questId);
            if (progress == null || progress.Status != QuestStatus.Active) return;

            int now = QuestDatabase.AddProgress(playerId, questId, count, quest.Amount);
            Nview.InvokeRPC(sender, "RPC_ProgressNotice", quest.Name, now + ";" + quest.Amount);
            SendQuestsTo(sender);
        }

        /// <summary>Client side: tell the player their quest moved, on the same centre-screen
        /// channel the game uses for "picked up an item" -- progress you only find by opening
        /// a menu may as well not have happened.</summary>
        private void RPC_ProgressNotice(long sender, string questName, string packed)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var p = (packed ?? "").Split(';');
            if (p.Length != 2) return;

            bool done = int.TryParse(p[0], out int now) && int.TryParse(p[1], out int goal) && now >= goal;
            player.Message(MessageHud.MessageType.Center,
                done ? $"{questName}: {p[0]}/{p[1]} — pronto para entregar"
                     : $"{questName}: {p[0]}/{p[1]}", 0, null);
        }

        /// <summary>Completes a quest and pays out. Collect objectives trust the client to
        /// have removed the items first (same boundary as selling on the marketplace); Kill
        /// objectives are checked against the counter the server itself accumulated.</summary>
        private void RPC_TurnInQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            if (!Offers(questId)) return;   // handing in somewhere that never offered it
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null) return;

            var progress = QuestDatabase.Get(playerId, questId);
            if (progress == null || progress.Status != QuestStatus.Active) return;

            // Everything except Collect is measured by the counter the server itself kept.
            // Collect is the one objective checked against the bag instead, at the moment of
            // hand-in, and the client removes the items before asking.
            if (quest.Objective != QuestObjectiveKind.Collect && progress.Counter < quest.Amount)
            {
                Plugin.Log.LogInfo($"NpcValheim: turn-in refused for '{questId}' -- {progress.Counter}/{quest.Amount}");
                return;
            }

            GrantRewards(playerId, quest);

            // XP is the one reward that cannot be handed out here. EpicMMO's API acts on the
            // *local* player, and on a dedicated server there is no local player -- calling
            // it here throws and would credit nobody even if it didn't. So the server asks
            // the client that turned the quest in to award it to itself.
            if (quest.Rewards?.Experience > 0)
                Nview.InvokeRPC(sender, "RPC_GrantExperience", quest.Rewards.Experience);

            QuestDatabase.Complete(playerId, questId, quest.Repeatable);
            DeliverRewardsNow(sender, playerId);
            SendQuestsTo(sender);
        }

        /// <summary>Runs on the rewarded player's own client, where EpicMMO's local-player
        /// API actually means something.</summary>
        private void RPC_GrantExperience(long sender, int amount)
        {
            if (amount <= 0) return;
            EpicMmoApi.AddExp(amount);
        }

        /// <summary>Test hook for the mail-only reward path. Not an RPC and unreachable from
        /// a client; the real flow goes through RPC_TurnInQuest.</summary>
        internal static void GrantRewardsForSelfTest(long playerId, QuestDefinition quest)
        {
            if (Plugin.EnableSelfTest?.Value != true) return;
            GrantRewards(playerId, quest);
        }

        private static void GrantRewards(long playerId, QuestDefinition quest)
        {
            var rewards = quest.Rewards;
            if (rewards == null) return;

            if (rewards.Coins > 0)
                MailDatabase.SendCoins(playerId, $"Recompensa: {quest.Name}", rewards.Coins);

            if (rewards.Items == null) return;
            foreach (var item in rewards.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.ItemName) || item.Amount <= 0) continue;
                MailDatabase.SendItem(playerId, $"Recompensa: {quest.Name}", item.ItemName, item.Quality, item.Amount);
            }
        }

        /// <summary>
        /// Hands the reward to the player who just turned the quest in.
        ///
        /// Mail is written first and stays the record of what is owed, because the server
        /// cannot reach into a remote inventory and must not depend on the client still being
        /// there. This asks the client to collect it straight away, so in the normal case the
        /// reward lands in the bag instead of forcing a walk to the post office -- and if the
        /// bag is full, or the player disconnects mid-hand-in, the parcel is simply still
        /// waiting at the Correio. Nothing can be lost by the collection failing.
        /// </summary>
        private void DeliverRewardsNow(long sender, long playerId)
        {
            var owed = MailDatabase.GetMail(playerId);
            if (owed.Count == 0) return;

            var packed = new StringBuilder();
            foreach (var parcel in owed)
            {
                if (packed.Length > 0) packed.Append('\n');
                packed.Append(parcel.Id).Append(';')
                      .Append(parcel.IsCoins ? MarketplaceNpc.CoinPrefabName : parcel.ItemName).Append(';')
                      .Append(parcel.IsCoins ? parcel.Coins : parcel.Amount).Append(';')
                      .Append(Mathf.Max(1, parcel.Quality));
            }

            Nview.InvokeRPC(sender, "RPC_CollectRewards", packed.ToString());
        }

        /// <summary>Client side: try to take each parcel into the bag. Whatever fits is
        /// claimed from the mailbox; whatever does not is left there on purpose, so a full
        /// inventory costs the player a trip rather than the reward.</summary>
        private void RPC_CollectRewards(long sender, string packed)
        {
            var player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(packed)) return;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != 4) continue;
                if (!int.TryParse(p[2], out int amount) || amount <= 0) continue;
                if (!int.TryParse(p[3], out int quality) || quality <= 0) quality = 1;

                if (player.GetInventory().AddItem(p[1], amount, quality, 0, 0L, "") == null)
                {
                    player.Message(MessageHud.MessageType.Center,
                        $"InventÃ¡rio cheio: {ItemNames.Display(p[1])} aguarda no correio", 0, null);
                    continue;
                }

                // Only now is it safe to take it out of the mailbox -- the item is already in
                // the bag, so the two can never both be true or both be false.
                Nview.InvokeRPC("RPC_ClaimDelivered", p[0]);
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Recebido: {amount}x {ItemNames.Display(p[1])}", amount, null);
            }
        }

        /// <summary>Server side: the client confirmed a parcel reached the bag, so remove it
        /// from the mailbox. Guarded by the recipient check inside MailDatabase.Claim.</summary>
        private void RPC_ClaimDelivered(long sender, string mailId)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            MailDatabase.Claim(mailId, playerId);
        }

        private void SendQuestsTo(long target)
        {
            if (!Nview.IsOwner()) return;
            Nview.InvokeRPC(target, "RPC_QuestData", Pack(GameApi.GetPlayerId(target), OfferedQuests()));
        }

        // Wire format, one quest per line:
        // id;name;description;objectiveText;counter;goal;status;canTurnIn;levelLocked;
        //   requiredLevel;rewardText;coins;xp;items;objectiveKind;target;locked;lockReason
        // where items is "Prefab*Amount,Prefab*Amount" -- the separators are chosen to not
        // collide with the field/line separators, and Clean() strips those from free text.
        private const int FieldCount = 19;
        /// <summary>Same snapshot the panel gets, for the global quest journal -- which has
        /// no NPC to ask and so cannot go through this one's ZNetView.</summary>
        public static string PackFor(long playerId) => Pack(playerId);

        public static List<QuestView> UnpackPublic(string packed) => Unpack(packed);

        /// <summary>Packs a player's view of a set of quests. The set is passed in because the
        /// two callers want different ones: an NPC sends only what it offers, while the global
        /// journal has no NPC and reports on everything the player has going.</summary>
        private static string Pack(long playerId, IEnumerable<QuestDefinition> quests = null)
        {
            var sb = new StringBuilder();
            int level = EpicMmoApi.GetLevel();

            foreach (var quest in quests ?? QuestStore.All)
            {
                // Ask for the refreshed status first: a daily whose window has passed is
                // put back on offer here rather than looking permanently finished.
                var status = QuestDatabase.RefreshAndGetStatus(playerId, quest);
                var progress = QuestDatabase.Get(playerId, quest.Id);
                int counter = progress?.Counter ?? 0;
                var untilReset = QuestDatabase.TimeUntilReset(playerId, quest);

                // A level requirement can only be enforced where EpicMMO is actually loaded;
                // without it GetLevel returns 0 and the quest stays open to everyone.
                bool levelLocked = EpicMmoApi.IsAvailable && quest.RequiredLevel > 0 && level < quest.RequiredLevel;
                bool canTurnIn = status == QuestStatus.Active &&
                    (quest.Objective == QuestObjectiveKind.Collect || counter >= quest.Amount);

                // Prerequisites only gate picking a quest up; one already in progress stays
                // playable even if an admin edits the chain underneath it.
                var missing = status == QuestStatus.NotStarted
                    ? QuestDatabase.MissingPrerequisites(playerId, quest)
                    : new List<string>();

                bool onCooldown = untilReset > TimeSpan.Zero;
                bool locked = levelLocked || missing.Count > 0 || onCooldown;
                string lockReason =
                    levelLocked ? $"Requer nivel {quest.RequiredLevel}" :
                    missing.Count > 0 ? "Requer: " + string.Join(", ", missing) :
                    onCooldown ? $"Disponivel de novo em {DescribeWait(untilReset)}" : "";

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Clean(quest.Id)).Append(';')
                  .Append(Clean(quest.Name)).Append(';')
                  .Append(Clean(quest.Description)).Append(';')
                  .Append(Clean(DescribeObjective(quest))).Append(';')
                  .Append(counter.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(quest.Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append((int)status).Append(';')
                  .Append(canTurnIn ? '1' : '0').Append(';')
                  .Append(levelLocked ? '1' : '0').Append(';')
                  .Append(quest.RequiredLevel.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Clean(DescribeRewards(quest))).Append(';')
                  .Append((quest.Rewards?.Coins ?? 0).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append((quest.Rewards?.Experience ?? 0).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(PackRewardItems(quest)).Append(';')
                  .Append((int)quest.Objective).Append(';')
                  .Append(Clean(quest.Target)).Append(';')
                  .Append(locked ? '1' : '0').Append(';')
                  .Append(Clean(lockReason)).Append(';')
                  .Append(quest.ResetHours > 0 ? '1' : '0');
            }
            return sb.ToString();
        }

        private static List<QuestView> Unpack(string packed)
        {
            var result = new List<QuestView>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != FieldCount) continue;
                result.Add(new QuestView
                {
                    Id = p[0],
                    Name = p[1],
                    Description = p[2],
                    ObjectiveText = p[3],
                    Counter = int.TryParse(p[4], out var c) ? c : 0,
                    Goal = int.TryParse(p[5], out var g) ? g : 1,
                    Status = int.TryParse(p[6], out var s) ? (QuestStatus)s : QuestStatus.NotStarted,
                    CanTurnIn = p[7] == "1",
                    LevelLocked = p[8] == "1",
                    RequiredLevel = int.TryParse(p[9], out var rl) ? rl : 0,
                    RewardText = p[10],
                    RewardCoins = int.TryParse(p[11], out var rc) ? rc : 0,
                    RewardExperience = int.TryParse(p[12], out var rx) ? rx : 0,
                    RewardItems = UnpackRewardItems(p[13]),
                    Objective = int.TryParse(p[14], out var ok) ? (QuestObjectiveKind)ok : QuestObjectiveKind.Collect,
                    Target = p[15],
                    Locked = p[16] == "1",
                    LockReason = p[17],
                    Repeats = p[18] == "1",
                });
            }
            return result;
        }

        /// <summary>Can this player hand the quest in right now? Judged on the client, and it
        /// has to be: for a Collect objective the server cannot see a remote inventory, so it
        /// optimistically reports CanTurnIn=true. Using that directly is what made the "?"
        /// marker appear over a quest giver whose items the player did not actually have.</summary>
        public static bool CanCompleteNow(QuestView quest, Player player)
        {
            if (quest == null || player == null || quest.Status != QuestStatus.Active) return false;

            if (quest.Objective == QuestObjectiveKind.Kill)
                return quest.Counter >= quest.Goal;

            return !string.IsNullOrEmpty(quest.Target) &&
                   ItemNames.Count(player.GetInventory(), quest.Target, -1) >= quest.Goal;
        }

        private static string PackRewardItems(QuestDefinition quest)
        {
            var items = quest.Rewards?.Items;
            if (items == null) return "";

            var sb = new StringBuilder();
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrEmpty(item.ItemName) || item.Amount <= 0) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(Clean(item.ItemName).Replace(',', ' ').Replace('*', ' '))
                  .Append('*').Append(item.Amount.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<QuestRewardEntry> UnpackRewardItems(string packed)
        {
            var result = new List<QuestRewardEntry>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var chunk in packed.Split(','))
            {
                var parts = chunk.Split('*');
                if (parts.Length != 2 || !int.TryParse(parts[1], out var amount) || amount <= 0) continue;
                result.Add(new QuestRewardEntry { ItemName = parts[0], Amount = amount });
            }
            return result;
        }

        private static string DescribeWait(TimeSpan left) =>
            left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}min" : $"{Math.Max(1, left.Minutes)}min";

        public static string DescribeObjective(QuestDefinition quest) =>
            quest.Objective == QuestObjectiveKind.Kill
                ? $"Matar {quest.Amount}x {quest.Target}"
                : $"Entregar {quest.Amount}x {quest.Target}";

        private static string DescribeRewards(QuestDefinition quest)
        {
            var parts = new List<string>();
            var r = quest.Rewards;
            if (r != null)
            {
                if (r.Coins > 0) parts.Add($"{r.Coins} moedas");
                if (r.Experience > 0) parts.Add($"{r.Experience} XP");
                if (r.Items != null)
                    foreach (var item in r.Items)
                        if (item != null && !string.IsNullOrEmpty(item.ItemName) && item.Amount > 0)
                            parts.Add($"{item.Amount}x {item.ItemName}");
            }
            return parts.Count > 0 ? string.Join(", ", parts) : "(sem recompensa)";
        }

        private static string Clean(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ');
    }
}




