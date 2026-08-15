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

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_RequestQuests", (Action<long>)RPC_RequestQuests);
            Nview.Register("RPC_QuestData", (Action<long, string>)RPC_QuestData);
            Nview.Register("RPC_AcceptQuest", (Action<long, string>)RPC_AcceptQuest);
            Nview.Register("RPC_TurnInQuest", (Action<long, string>)RPC_TurnInQuest);
            Nview.Register("RPC_AbandonQuest", (Action<long, string>)RPC_AbandonQuest);
            Nview.Register("RPC_ReportKill", (Action<long, string, int>)RPC_ReportKill);
            Nview.Register("RPC_GrantExperience", (Action<long, int>)RPC_GrantExperience);
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

        private void RPC_ReportKill(long sender, string creatureName, int count)
        {
            if (!Nview.IsOwner() || count <= 0 || count > 100) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            foreach (var progress in QuestDatabase.GetAll(playerId))
            {
                if (progress.Status != QuestStatus.Active) continue;
                var quest = QuestStore.Get(progress.QuestId);
                if (quest == null || quest.Objective != QuestObjectiveKind.Kill) continue;
                if (!string.Equals(quest.Target, creatureName, StringComparison.OrdinalIgnoreCase)) continue;

                QuestDatabase.AddProgress(playerId, quest.Id, count, quest.Amount);
            }
        }

        /// <summary>Completes a quest and pays out. Collect objectives trust the client to
        /// have removed the items first (same boundary as selling on the marketplace); Kill
        /// objectives are checked against the counter the server itself accumulated.</summary>
        private void RPC_TurnInQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null) return;

            var progress = QuestDatabase.Get(playerId, questId);
            if (progress == null || progress.Status != QuestStatus.Active) return;

            if (quest.Objective == QuestObjectiveKind.Kill && progress.Counter < quest.Amount)
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

        private void SendQuestsTo(long target)
        {
            if (!Nview.IsOwner()) return;
            Nview.InvokeRPC(target, "RPC_QuestData", Pack(GameApi.GetPlayerId(target)));
        }

        // Wire format, one quest per line:
        // id;name;description;objectiveText;counter;goal;status;canTurnIn;levelLocked;
        //   requiredLevel;rewardText;coins;xp;items;objectiveKind;target;locked;lockReason
        // where items is "Prefab*Amount,Prefab*Amount" -- the separators are chosen to not
        // collide with the field/line separators, and Clean() strips those from free text.
        private const int FieldCount = 18;
        /// <summary>Same snapshot the panel gets, for the global quest journal -- which has
        /// no NPC to ask and so cannot go through this one's ZNetView.</summary>
        public static string PackFor(long playerId) => Pack(playerId);

        public static List<QuestView> UnpackPublic(string packed) => Unpack(packed);

        private static string Pack(long playerId)
        {
            var sb = new StringBuilder();
            int level = EpicMmoApi.GetLevel();

            foreach (var quest in QuestStore.All)
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
                  .Append(Clean(lockReason));
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
