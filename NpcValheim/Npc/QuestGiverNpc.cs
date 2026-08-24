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
        /// breaks the moment the wording or the language changes.
        ///
        /// This is the *first* objective. Everything a quest asks for is in Objectives below;
        /// these two stay because almost every quest has exactly one and the nameplate marker,
        /// the tracker line and the journal row all want a single thing to show.</summary>
        public QuestObjectiveKind Objective;
        public string Target;

        /// <summary>Every objective with this player's progress against it, in the quest's own
        /// order. Never empty -- a one-objective quest has a list of one.</summary>
        public List<QuestObjectiveView> Objectives = new List<QuestObjectiveView>();

        /// <summary>Comes back on a timer -- what WoW calls a daily, and what its blue "!"
        /// marks. Carried to the client so the marker and the tracker can say so.</summary>
        public bool Repeats;
    }

    public class QuestRewardEntry
    {
        public string ItemName;
        public int Amount;
    }

    /// <summary>One line of a quest's to-do list, as the client sees it.</summary>
    public class QuestObjectiveView
    {
        public QuestObjectiveKind Kind;
        public string Target;
        public int Goal;
        public int Counter;

        /// <summary>Explore carries its radius in Goal for wire compatibility, but one
        /// verified arrival completes it. Other objective kinds retain their normal count.</summary>
        public int CompletionGoal => Kind == QuestObjectiveKind.Explore ? 1 : Math.Max(1, Goal);

        /// <summary>
        /// Whether this line is done.
        ///
        /// Collect is the exception, and has to be: the server never sees a remote player's
        /// bag, so its counter for a Collect objective stays at zero and the answer can only
        /// come from the inventory in front of us. Pass the player when there is one.
        /// </summary>
        public bool IsDone(Player player)
        {
            if (Kind != QuestObjectiveKind.Collect) return Counter >= CompletionGoal;
            return player != null &&
                   ItemNames.Count(player.GetInventory(), Target, -1) >= Goal;
        }

        /// <summary>What to show on the left of the "n/m" -- the bag for Collect, the server's
        /// counter for everything else.</summary>
        public int Progress(Player player)
        {
            if (Kind != QuestObjectiveKind.Collect) return Mathf.Min(CompletionGoal, Counter);
            return player == null || string.IsNullOrEmpty(Target)
                ? 0
                : Mathf.Min(Goal, ItemNames.Count(player.GetInventory(), Target, -1));
        }
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
        private static readonly HashSet<string> ReceivedCompletions =
            new HashSet<string>(StringComparer.Ordinal);

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

        protected override void OnProfileApplied(long sender)
        {
            SendQuestsTo(sender);
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
            InvokeAuthoritativeRpc("RPC_CreateQuest", packed ?? "");
        }

        private void RPC_CreateQuest(long sender, string packed)
        {
            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-create", 2, 5f) ||
                (packed?.Length ?? 0) > 4096) return;

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
            Plugin.Log.LogInfo($"NpcValheim: admin peer {sender} created quest '{quest.Id}'");
        }

        public void RequestSetQuests(Player requester, List<string> questIds)
        {
            if (Nview == null || !Nview.IsValid() || !CanLocalPlayerAdminister()) return;
            InvokeAuthoritativeRpc("RPC_SetQuests", string.Join("\n", questIds ?? new List<string>()));
        }

        internal override bool DispatchAdminMutation(long sender, string method, object[] arguments)
        {
            arguments ??= Array.Empty<object>();
            switch (method)
            {
                case "RPC_CreateQuest" when arguments.Length == 1 && arguments[0] is string quest:
                    RPC_CreateQuest(sender, quest);
                    return true;
                case "RPC_SetQuests" when arguments.Length == 1 && arguments[0] is string quests:
                    RPC_SetQuests(sender, quests);
                    return true;
                default:
                    return base.DispatchAdminMutation(sender, method, arguments);
            }
        }

        private void RPC_SetQuests(long sender, string packed)
        {
            if (!CanAdminister(sender)) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-set-offers", 4, 5f) ||
                (packed?.Length ?? 0) > 16384) return;
            Nview.GetZDO().Set(KeyQuests, packed ?? "");
            PersistProfileSnapshot();
            SendQuestsTo(sender);
            Plugin.Log.LogInfo($"NpcValheim: admin peer {sender} assigned quests to '{GetHoverName()}'");
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
            Nview.Register("RPC_GrantExperience", (Action<long, int>)RPC_GrantExperience);
            Nview.Register("RPC_CollectRewards", (Action<long, string>)RPC_CollectRewards);
            Nview.Register("RPC_ClaimDelivered", (Action<long, string>)RPC_ClaimDelivered);
        }

        // ---- client-side requests ----

        public void RequestQuests()
        {
            ServiceNpcAuthority.RequestQuestAction(this, "RPC_RequestQuests");
        }

        public void RequestAccept(string questId)
        {
            int level = -1;
            if (EpicMmoApi.IsAvailable)
            {
                int detected = EpicMmoApi.GetLevel();
                if (detected > 0) level = detected;
            }
            ServiceNpcAuthority.RequestQuestAction(this, "RPC_AcceptQuest",
                (questId ?? "") + "\n" + level.ToString(CultureInfo.InvariantCulture));
        }

        public void RequestTurnIn(string questId)
        {
            ServiceNpcAuthority.RequestQuestAction(this, "RPC_TurnInQuest", questId ?? "");
        }

        public void RequestAbandon(string questId)
        {
            ServiceNpcAuthority.RequestQuestAction(this, "RPC_AbandonQuest", questId ?? "");
        }

        /// <summary>Called by QuestKillTracker on the client that made the kill.</summary>
        public void ReportKill(string creatureName, int count)
        {
            QuestProgressNetwork.Report(QuestObjectiveKind.Kill, creatureName, count: count);
        }

        /// <summary>Called on the client that picked the item up, for Gather quests.</summary>
        public void ReportPickup(string itemName, int count)
        {
            QuestProgressNetwork.Report(QuestObjectiveKind.Gather, itemName, count: count);
        }

        /// <summary>Called when the local player opens a panel on an NPC, for Talk quests.</summary>
        public void ReportTalk(string npcName)
        {
            QuestProgressNetwork.ReportTalk(this);
        }

        /// <summary>Called when the local player reaches an Explore quest's destination.</summary>
        public void ReportArrival(string questId)
        {
            QuestProgressNetwork.Report(QuestObjectiveKind.Explore, "", questId ?? "");
        }

        // ---- authoritative handlers ----

        internal bool DispatchQuestAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case "RPC_RequestQuests":
                    RPC_RequestQuests(sender);
                    return true;
                case "RPC_AcceptQuest":
                    RPC_AcceptQuest(sender, payload ?? "");
                    return true;
                case "RPC_TurnInQuest":
                    RPC_TurnInQuest(sender, payload ?? "");
                    return true;
                case "RPC_AbandonQuest":
                    RPC_AbandonQuest(sender, payload ?? "");
                    return true;
                case "RPC_ClaimDelivered":
                    RPC_ClaimDelivered(sender, payload ?? "");
                    return true;
                default:
                    return false;
            }
        }

        private void RPC_RequestQuests(long sender)
        {
            if (!Nview.IsOwner() || GameApi.GetPlayerId(sender) == 0L ||
                !NpcRequestGuard.AllowRate(sender, "quest-list", 6, 5f)) return;
            SendQuestsTo(sender);
        }

        private void RPC_QuestData(long sender, string packed)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            ReceiveQuestResponse("data", packed);
        }

        internal void ReceiveQuestResponse(string response, string payload)
        {
            switch (response)
            {
                case "data":
                    CachedQuests = Unpack(payload);
                    HasSyncedOnce = true;
                    break;
                case "experience" when int.TryParse(payload, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int amount) && amount > 0:
                    EpicMmoApi.AddExp(amount);
                    break;
                case "rewards":
                    ReceiveQuestRewards(payload);
                    break;
            }
        }

        /// <summary>Receives results that belong to the player, rather than to a loaded NPC
        /// instance. The completion token makes duplicate routed responses harmless.</summary>
        internal static void ReceivePlayerResponse(string response, string payload)
        {
            if (response == "experience" && int.TryParse(payload, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int amount) && amount > 0)
            {
                EpicMmoApi.AddExp(amount);
                return;
            }

            if (response != "turnin-complete") return;

            var fields = (payload ?? "").Split(new[] { ';' }, 3);
            if (fields.Length != 3 || string.IsNullOrWhiteSpace(fields[0]) || fields[0].Length > 512 ||
                !int.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int experience) ||
                experience < 0) return;

            var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(fields[2]))
            {
                foreach (string chunk in fields[2].Split('|'))
                {
                    var item = chunk.Split('*');
                    if (item.Length != 2 || string.IsNullOrWhiteSpace(item[0]) ||
                        !int.TryParse(item[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                        count <= 0) return;
                    requirements[item[0]] = count;
                }
            }

            var player = Player.m_localPlayer;
            if (player == null) return;
            if (!ReceivedCompletions.Add(fields[0])) return;

            var inventory = player.GetInventory();
            bool canConsume = true;
            foreach (var requirement in requirements)
            {
                if (ItemNames.Count(inventory, requirement.Key, -1) >= requirement.Value) continue;
                canConsume = false;
                break;
            }

            if (canConsume)
                foreach (var requirement in requirements)
                    ItemNames.Remove(inventory, requirement.Key, requirement.Value, -1);
            else
                Plugin.Log.LogWarning("NpcValheim: quest completed after its collect items left the local inventory");

            if (experience > 0) EpicMmoApi.AddExp(experience);
            player.Message(MessageHud.MessageType.Center,
                "Missão concluída. Recompensas enviadas ao Correio.", 0, null);
        }

        private void RPC_AcceptQuest(long sender, string packed)
        {
            if (!Nview.IsOwner()) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-accept", 6, 5f)) return;
            string questId = packed ?? "";
            int reportedLevel = -1;
            int separator = questId.LastIndexOf('\n');
            if (separator >= 0)
            {
                if (!int.TryParse(questId.Substring(separator + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out reportedLevel)) reportedLevel = -1;
                questId = questId.Substring(0, separator);
            }
            if (!Offers(questId)) return;   // this giver does not deal in that quest
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null) return;

            // EpicMMO exposes only the local player's level. The honest client reports that
            // value with the request; this has the same trust boundary as Collect inventory.
            if (quest.RequiredLevel > 0 && reportedLevel >= 0 && reportedLevel < quest.RequiredLevel)
            {
                Plugin.Log.LogInfo($"NpcValheim: accept refused for '{questId}' -- " +
                                   $"reported level {reportedLevel}/{quest.RequiredLevel}");
                ServiceNpcAuthority.SendStatus(sender, $"Esta missão requer nível {quest.RequiredLevel}.");
                SendQuestsTo(sender);
                return;
            }

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
                ServiceNpcAuthority.SendStatus(sender,
                    "Missão bloqueada. Requisitos: " + string.Join(", ", missing));
                SendQuestsTo(sender);
                return;
            }

            QuestDatabase.Accept(playerId, questId);
            ServiceNpcAuthority.SendStatus(sender, $"Missão aceita: {quest.Name}");
            SendQuestsTo(sender);
        }

        private void RPC_AbandonQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-abandon", 4, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            QuestDatabase.Abandon(playerId, questId);
            ServiceNpcAuthority.SendStatus(sender, "Missão abandonada.");
            SendQuestsTo(sender);
        }

        /// <summary>Completes a quest and pays out. Collect objectives use the same client
        /// inventory trust boundary as the marketplace, but the client consumes them only
        /// after this authoritative handler confirms the completion.</summary>
        private void RPC_TurnInQuest(long sender, string questId)
        {
            if (!Nview.IsOwner()) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-turn-in", 4, 5f)) return;
            if (!Offers(questId)) return;   // handing in somewhere that never offered it
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var quest = QuestStore.Get(questId);
            if (quest == null) return;

            var progress = QuestDatabase.Get(playerId, questId);
            if (progress == null || progress.Status != QuestStatus.Active)
            {
                ServiceNpcAuthority.SendStatus(sender, "Esta missão não está ativa.");
                SendQuestsTo(sender);
                return;
            }

            // Everything except Collect is measured by the counter the server itself kept.
            // Collect lives only in the remote bag and is validated by the requesting client.
            var steps = quest.Steps();
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind == QuestObjectiveKind.Collect) continue;
                int goal = QuestProgressRules.Goal(steps[i]);
                if (progress.CounterAt(i) >= goal) continue;

                Plugin.Log.LogInfo($"NpcValheim: turn-in refused for '{questId}' -- " +
                                   $"{steps[i].Target} {progress.CounterAt(i)}/{goal}");
                ServiceNpcAuthority.SendStatus(sender, "A missão ainda não está pronta para entrega.");
                SendQuestsTo(sender);
                return;
            }

            int completionNumber = progress.TimesCompleted + 1;
            if (!GrantRewards(playerId, quest, completionNumber))
            {
                ServiceNpcAuthority.SendStatus(sender,
                    "O Correio está cheio. Libere espaço antes de entregar a missão.");
                return;
            }

            QuestDatabase.Complete(playerId, questId, quest.Repeatable);
            ServiceNpcAuthority.SendQuestPlayerResponse(sender, "turnin-complete",
                PackTurnInCompletion(playerId, quest, completionNumber, steps));
            SendQuestsTo(sender);
        }

        /// <summary>Runs on the rewarded player's own client, where EpicMMO's local-player
        /// API actually means something.</summary>
        private void RPC_GrantExperience(long sender, int amount)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender) || amount <= 0) return;
            EpicMmoApi.AddExp(amount);
        }

        private static bool GrantRewards(long playerId, QuestDefinition quest, int completionNumber)
        {
            var rewards = quest.Rewards;
            if (rewards == null) return true;

            string prefix = $"quest:{playerId}:{quest.Id}:{completionNumber}";
            var deliveryIds = new List<string>();
            if (rewards.Coins > 0) deliveryIds.Add(prefix + ":coins");

            if (rewards.Items != null)
                for (int i = 0; i < rewards.Items.Count; i++)
                {
                    var item = rewards.Items[i];
                    if (item == null || string.IsNullOrEmpty(item.ItemName) || item.Amount <= 0) continue;
                    deliveryIds.Add(prefix + ":item:" + i.ToString(CultureInfo.InvariantCulture));
                }

            if (deliveryIds.Count == 0) return true;
            if (!MailDatabase.CanInsertDeliveries(playerId, deliveryIds)) return false;

            if (rewards.Coins > 0)
                if (MailDatabase.SendCoins(playerId, $"Recompensa: {quest.Name}", rewards.Coins,
                    prefix + ":coins") == null) return false;

            if (rewards.Items == null) return true;
            for (int i = 0; i < rewards.Items.Count; i++)
            {
                var item = rewards.Items[i];
                if (item == null || string.IsNullOrEmpty(item.ItemName) || item.Amount <= 0) continue;
                if (MailDatabase.SendItem(playerId, $"Recompensa: {quest.Name}", item.ItemName,
                    item.Quality, item.Amount,
                    prefix + ":item:" + i.ToString(CultureInfo.InvariantCulture)) == null) return false;
            }
            return true;
        }

        private static string PackTurnInCompletion(long playerId, QuestDefinition quest,
            int completionNumber, List<QuestObjective> steps)
        {
            var requirements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in steps)
            {
                if (step.Kind != QuestObjectiveKind.Collect || string.IsNullOrWhiteSpace(step.Target)) continue;
                string target = CleanObjectiveTarget(step.Target);
                requirements.TryGetValue(target, out int current);
                long combined = (long)current + Math.Max(1, step.Amount);
                requirements[target] = combined > int.MaxValue ? int.MaxValue : (int)combined;
            }

            var packedItems = new StringBuilder();
            foreach (var requirement in requirements)
            {
                if (packedItems.Length > 0) packedItems.Append('|');
                packedItems.Append(requirement.Key).Append('*')
                    .Append(requirement.Value.ToString(CultureInfo.InvariantCulture));
            }

            string token = Clean($"{playerId}:{quest.Id}:{completionNumber}");
            int experience = Math.Max(0, quest.Rewards?.Experience ?? 0);
            return token + ";" + experience.ToString(CultureInfo.InvariantCulture) + ";" + packedItems;
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

            ServiceNpcAuthority.SendQuestResponse(sender, this, "rewards", packed.ToString());
        }

        /// <summary>Client side: try to take each parcel into the bag. Whatever fits is
        /// claimed from the mailbox; whatever does not is left there on purpose, so a full
        /// inventory costs the player a trip rather than the reward.</summary>
        private void RPC_CollectRewards(long sender, string packed)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            ReceiveQuestRewards(packed);
        }

        private void ReceiveQuestRewards(string packed)
        {
            var player = Player.m_localPlayer;
            if (player == null || string.IsNullOrEmpty(packed) || packed.Length > 65536) return;

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
                ServiceNpcAuthority.RequestQuestAction(this, "RPC_ClaimDelivered", p[0]);
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Recebido: {amount}x {ItemNames.Display(p[1])}", amount, null);
            }
        }

        /// <summary>Server side: the client confirmed a parcel reached the bag, so remove it
        /// from the mailbox. Guarded by the recipient check inside MailDatabase.Claim.</summary>
        private void RPC_ClaimDelivered(long sender, string mailId)
        {
            if (!Nview.IsOwner()) return;
            if (!NpcRequestGuard.AllowRate(sender, "quest-claim-reward", 20, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            MailDatabase.Claim(mailId, playerId);
        }

        private void SendQuestsTo(long target)
        {
            if (!Nview.IsOwner()) return;
            ServiceNpcAuthority.SendQuestResponse(target, this, "data",
                Pack(GameApi.GetPlayerId(target), OfferedQuests()));
        }

        // Wire format, one quest per line:
        // id;name;description;objectiveText;counter;goal;status;canTurnIn;levelLocked;
        //   requiredLevel;rewardText;coins;xp;items;objectiveKind;target;locked;lockReason;
        //   repeats;objectives
        // where items is "Prefab*Amount,Prefab*Amount" and objectives is
        // "kind*target*goal*counter|kind*target*goal*counter". The separators are chosen to
        // not collide with the field/line separators; Clean() strips those from free text, and
        // an objective's target keeps its commas because Explore stores a place as "x,z".
        private const int FieldCount = 20;
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

            foreach (var quest in quests ?? QuestStore.All)
            {
                // Ask for the refreshed status first: a daily whose window has passed is
                // put back on offer here rather than looking permanently finished.
                var status = QuestDatabase.RefreshAndGetStatus(playerId, quest);
                var progress = QuestDatabase.Get(playerId, quest.Id);
                var steps = quest.Steps();
                int counter = progress?.CounterAt(0) ?? 0;
                var untilReset = QuestDatabase.TimeUntilReset(playerId, quest);

                // EpicMMO's level API describes the local player. A dedicated server has no
                // local player, so the receiving client applies this gate after unpacking.
                bool levelLocked = false;

                // Optimistic for Collect, and it has to be -- the server cannot see a remote
                // bag. CanCompleteNow re-decides this on the client, where the bag is.
                bool serverSideDone = true;
                for (int i = 0; i < steps.Count && serverSideDone; i++)
                    serverSideDone = steps[i].Kind == QuestObjectiveKind.Collect ||
                                     (progress?.CounterAt(i) ?? 0) >= QuestProgressRules.Goal(steps[i]);
                bool canTurnIn = status == QuestStatus.Active && serverSideDone;

                // Prerequisites only gate picking a quest up; one already in progress stays
                // playable even if an admin edits the chain underneath it.
                var missing = status == QuestStatus.NotStarted
                    ? QuestDatabase.MissingPrerequisites(playerId, quest)
                    : new List<string>();

                bool onCooldown = untilReset > TimeSpan.Zero;
                bool locked = missing.Count > 0 || onCooldown;
                string lockReason =
                    missing.Count > 0 ? "Requer: " + string.Join(", ", missing) :
                    onCooldown ? $"Disponivel de novo em {DescribeWait(untilReset)}" : "";

                if (sb.Length > 0) sb.Append('\n');
                sb.Append(Clean(quest.Id)).Append(';')
                  .Append(Clean(quest.Name)).Append(';')
                  .Append(Clean(quest.Description)).Append(';')
                  .Append(Clean(DescribeObjective(quest))).Append(';')
                  .Append(counter.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(steps[0].Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append((int)status).Append(';')
                  .Append(canTurnIn ? '1' : '0').Append(';')
                  .Append(levelLocked ? '1' : '0').Append(';')
                  .Append(quest.RequiredLevel.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(Clean(DescribeRewards(quest))).Append(';')
                  .Append((quest.Rewards?.Coins ?? 0).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append((quest.Rewards?.Experience ?? 0).ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(PackRewardItems(quest)).Append(';')
                  .Append((int)steps[0].Kind).Append(';')
                  .Append(Clean(steps[0].Target)).Append(';')
                  .Append(locked ? '1' : '0').Append(';')
                  .Append(Clean(lockReason)).Append(';')
                  .Append(quest.ResetHours > 0 ? '1' : '0').Append(';')
                  .Append(PackObjectives(steps, progress));
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
                    Objectives = UnpackObjectives(p[19]),
                });
            }
            ApplyLocalLevelGate(result);
            return result;
        }

        private static void ApplyLocalLevelGate(List<QuestView> quests)
        {
            if (!EpicMmoApi.IsAvailable) return;

            int level = EpicMmoApi.GetLevel();
            if (level <= 0) return;

            foreach (var quest in quests)
            {
                quest.LevelLocked = quest.RequiredLevel > 0 && level < quest.RequiredLevel;
                if (!quest.LevelLocked) continue;

                quest.Locked = true;
                quest.LockReason = $"Requer nível {quest.RequiredLevel}";
            }
        }

        /// <summary>Can this player hand the quest in right now? Judged on the client, and it
        /// has to be: for a Collect objective the server cannot see a remote inventory, so it
        /// optimistically reports CanTurnIn=true. Using that directly is what made the "?"
        /// marker appear over a quest giver whose items the player did not actually have.</summary>
        public static bool CanCompleteNow(QuestView quest, Player player)
        {
            if (quest == null || player == null || quest.Status != QuestStatus.Active) return false;
            if (quest.Objectives == null || quest.Objectives.Count == 0) return false;

            var collected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in quest.Objectives)
            {
                if (step.Kind != QuestObjectiveKind.Collect)
                {
                    if (!step.IsDone(player)) return false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(step.Target)) return false;
                collected.TryGetValue(step.Target, out int current);
                long combined = (long)current + Math.Max(1, step.Goal);
                collected[step.Target] = combined > int.MaxValue ? int.MaxValue : (int)combined;
            }

            foreach (var requirement in collected)
                if (ItemNames.Count(player.GetInventory(), requirement.Key, -1) < requirement.Value)
                    return false;
            return true;
        }

        private static string PackObjectives(List<QuestObjective> steps, QuestProgress progress)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < steps.Count; i++)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append((int)steps[i].Kind).Append('*')
                  .Append(CleanObjectiveTarget(steps[i].Target)).Append('*')
                  .Append(steps[i].Amount.ToString(CultureInfo.InvariantCulture)).Append('*')
                  .Append((progress?.CounterAt(i) ?? 0).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<QuestObjectiveView> UnpackObjectives(string packed)
        {
            var result = new List<QuestObjectiveView>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var chunk in packed.Split('|'))
            {
                var p = chunk.Split('*');
                if (p.Length != 4) continue;
                result.Add(new QuestObjectiveView
                {
                    Kind = int.TryParse(p[0], out var k) ? (QuestObjectiveKind)k : QuestObjectiveKind.Collect,
                    Target = p[1],
                    Goal = int.TryParse(p[2], out var g) ? g : 1,
                    Counter = int.TryParse(p[3], out var c) ? c : 0,
                });
            }
            return result;
        }

        /// <summary>Keeps commas, which an Explore target needs ("x,z"), and drops only the
        /// two characters that would break the objective encoding.</summary>
        private static string CleanObjectiveTarget(string s) =>
            Clean(s).Replace('|', ' ').Replace('*', ' ');

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
            string.Join(", ", quest.Steps().ConvertAll(DescribeStep).ToArray());

        /// <summary>The client-side twin of DescribeStep, for the tracker and the journal.
        /// Same wording, because a line that reads one way in the panel and another in the
        /// tracker looks like two different objectives.</summary>
        public static string Describe(QuestObjectiveView step) =>
            DescribeStep(new QuestObjective { Kind = step.Kind, Target = step.Target, Amount = step.Goal });

        private static string DescribeStep(QuestObjective step)
        {
            switch (step.Kind)
            {
                case QuestObjectiveKind.Kill:
                    return $"Matar {step.Amount}x {ItemNames.Display(step.Target)}";
                case QuestObjectiveKind.Gather:
                    return $"Coletar {step.Amount}x {ItemNames.Display(step.Target)}";
                case QuestObjectiveKind.Talk:
                    return $"Falar com {step.Target}";
                case QuestObjectiveKind.Explore:
                    return $"Chegar a {step.Target}";
                default:
                    return $"Entregar {step.Amount}x {ItemNames.Display(step.Target)}";
            }
        }

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




