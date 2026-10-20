using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using NpcValheim.Integration;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>One weekly challenge of the season pool.</summary>
    internal sealed class SeasonChallenge
    {
        public string Id;
        public QuestObjectiveKind Kind;
        /// <summary>Prefab to match: "*" = anything, "Prefix*" = prefix, else exact (ignoring case).</summary>
        public string Target;
        public int Amount;
        public int Points;
        public string Label;

        public bool Matches(QuestObjectiveKind kind, string target)
        {
            if (kind != Kind) return false;
            if (Target == "*") return true;
            if (string.IsNullOrEmpty(target)) return false;
            if (Target.EndsWith("*", StringComparison.Ordinal))
                return target.StartsWith(Target.Substring(0, Target.Length - 1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(Target, target, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The season's settings, parsed from the [Season] config. Pure text, no game types.</summary>
    internal static class SeasonCatalog
    {
        internal const int Levels = 10;
        internal const int ChallengesPerWeek = 3;

        // id;kind;target;amount;points;label. PvP is not in the list on purpose, and no
        // challenge is about killing a player: QuestKillTracker never reports those either.
        internal const string DefaultChallenges =
            "cacar;Kill;*;40;80;Derrote 40 criaturas|" +
            "javalis;Kill;Boar;15;80;Cace 15 javalis|" +
            "madeira;Gather;Wood;100;80;Colete 100 de madeira|" +
            "pedra;Gather;Stone;80;80;Colete 80 de pedra|" +
            "peixe;Gather;Fish*;5;80;Pesque 5 peixes|" +
            "cogumelo;Gather;Mushroom;20;80;Colete 20 cogumelos|" +
            "couro;Gather;LeatherScraps;20;80;Colete 20 retalhos de couro|" +
            "conversa;Talk;*;3;80;Converse 3 vezes com NPCs";

        // level=chave. Pets are placeholders until the Pets catalog says which ones are season pets.
        internal const string DefaultRewards =
            "1=titulo:temporada1|2=vela:temporada|3=lapide:cruz|4=chama:azul|5=pet:temporada1|" +
            "6=abate:padrao|7=chama:roxa|8=lapide:caveira|9=pet:temporada2|10=titulo:campeao_t1";

        internal static List<SeasonChallenge> ParseChallenges(string raw, List<string> problems = null)
        {
            var result = new List<SeasonChallenge>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (raw ?? "").Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;
                var f = text.Split(';');
                if (f.Length < 5 || f[0].Trim().Length == 0 ||
                    !Enum.TryParse(f[1].Trim(), true, out QuestObjectiveKind kind) ||
                    kind == QuestObjectiveKind.Explore ||
                    !int.TryParse(f[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount) || amount < 1 ||
                    !int.TryParse(f[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int points) || points < 1 ||
                    f[2].Trim().Length == 0 || !seen.Add(f[0].Trim()))
                {
                    problems?.Add(text);
                    continue;
                }
                string id = f[0].Trim(), target = f[2].Trim();
                result.Add(new SeasonChallenge
                {
                    Id = id, Kind = kind, Target = target, Amount = amount, Points = points,
                    Label = f.Length > 5 && f[5].Trim().Length > 0 ? f[5].Trim() : id,
                });
            }
            return result;
        }

        /// <summary>Level -> cosmetic key. Levels outside 1..10 and empty keys are dropped.</summary>
        internal static Dictionary<int, string> ParseRewards(string raw)
        {
            var result = new Dictionary<int, string>();
            foreach (var entry in (raw ?? "").Split('|'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0) continue;
                if (!int.TryParse(entry.Substring(0, eq).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level) ||
                    level < 1 || level > Levels) continue;
                var key = entry.Substring(eq + 1).Trim();
                if (key.Length > 0) result[level] = key;
            }
            return result;
        }

        /// <summary>
        /// The week's challenges: the same for everybody and for every restart, because the pick
        /// is a pure function of the season id and the week number (FNV-1a seeded shuffle; not
        /// string.GetHashCode, which is not stable between runtimes).
        /// </summary>
        internal static List<SeasonChallenge> ForWeek(List<SeasonChallenge> pool, string seasonId, int week)
        {
            var list = new List<SeasonChallenge>(pool);
            uint seed = 2166136261u;
            foreach (char c in seasonId + "#" + week.ToString(CultureInfo.InvariantCulture))
                seed = (seed ^ c) * 16777619u;
            for (int i = list.Count - 1; i > 0; i--)
            {
                seed = seed * 1664525u + 1013904223u;
                int j = (int)((seed >> 8) % (uint)(i + 1));
                var t = list[i]; list[i] = list[j]; list[j] = t;
            }
            return list.Take(ChallengesPerWeek).ToList();
        }

        internal static int LevelOf(int points, int perLevel) =>
            perLevel <= 0 ? 0 : Math.Min(Levels, Math.Max(0, points) / perLevel);

        /// <summary>0 = not started, 1 = running, 2 = ended. Week is 0-based inside the season.</summary>
        internal static int Phase(DateTime nowUtc, DateTime start, DateTime end, out int week)
        {
            week = -1;
            if (nowUtc < start) return 0;
            if (nowUtc >= end) return 2;
            week = (int)Math.Floor((nowUtc.Date - start.Date).TotalDays / 7.0);
            return 1;
        }

        internal static bool TryDate(string text, out DateTime utc) =>
            DateTime.TryParseExact((text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out utc);
    }

    /// <summary>
    /// The season pass: 10 levels of cosmetic rewards, 3 free, the rest behind a one-off
    /// Deadcoins payment that unlocks them retroactively. Points come from three weekly
    /// non-PvP challenges.
    ///
    /// The server owns everything: progress is fed by QuestProgressNetwork's events (already
    /// capped and rate limited there; a player kill is never reported), rewards are granted
    /// through the Deadheim cosmetics registry, and the payment is charged on the server's
    /// Deadcoins file like the boss pass. The client only draws what the status answer says.
    /// </summary>
    internal static class Season
    {
        internal const string ActionStatus = "season_status";
        internal const string ActionBuy = "season_buy";
        private const string RpcResponse = "NpcValheim_SeasonResponse";
        private const float BuyTimeout = 8f;
        private const float TalkCooldown = 60f;

        // ---- client state ----

        internal sealed class ClientChallenge
        {
            public string Label;
            public int Progress, Goal, Points;
            public bool Done;
        }

        internal static bool Known { get; private set; }
        /// <summary>0 not started, 1 running, 2 ended, 3 disabled.</summary>
        internal static int State { get; private set; }
        internal static string Name { get; private set; } = "";
        internal static DateTime EndUtc { get; private set; }
        internal static int Points { get; private set; }
        internal static int PerLevel { get; private set; } = 1;
        internal static int FreeLevels { get; private set; }
        internal static bool Paid { get; private set; }
        internal static int Price { get; private set; }
        internal static int Balance { get; private set; } = -1;
        internal static readonly Dictionary<int, string> RewardKeys = new Dictionary<int, string>();
        internal static readonly HashSet<int> ClaimedLevels = new HashSet<int>();
        internal static readonly List<ClientChallenge> Challenges = new List<ClientChallenge>();
        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }
        internal static int Revision { get; private set; }
        private static float _buySentAt = -BuyTimeout;

        // ---- server state ----

        private static readonly Dictionary<long, float> LastTalk = new Dictionary<long, float>();

        internal static void Register(ZRoutedRpc rpc)
        {
            Known = false;
            Balance = -1;
            LastMessage = null;
            RewardKeys.Clear();
            ClaimedLevels.Clear();
            Challenges.Clear();
            LastTalk.Clear();
            _buySentAt = -BuyTimeout;
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
        }

        // ---- settings ----

        private static bool Enabled => Plugin.SeasonEnabled?.Value ?? false;
        private static string SeasonId => Clean(Plugin.SeasonId?.Value, "t1");
        private static string SeasonName => Clean(Plugin.SeasonName?.Value, "Temporada");
        private static int PerLevelCfg => Math.Max(1, Plugin.SeasonPointsPerLevel?.Value ?? 90);
        private static int FreeCfg => Math.Max(0, Math.Min(SeasonCatalog.Levels, Plugin.SeasonFreeLevels?.Value ?? 3));
        private static int PriceCfg => Math.Max(0, Plugin.SeasonPrice?.Value ?? 500);

        private static string Clean(string text, string fallback)
        {
            var t = (text ?? "").Trim().Replace('|', ' ').Replace(';', ' ').Replace('~', ' ').Replace('\n', ' ');
            return t.Length == 0 ? fallback : t;
        }

        private static int CurrentPhase(out int week)
        {
            week = -1;
            if (!Enabled) return 3;
            if (!SeasonCatalog.TryDate(Plugin.SeasonStart?.Value, out var start) ||
                !SeasonCatalog.TryDate(Plugin.SeasonEnd?.Value, out var end) || end <= start) return 3;
            return SeasonCatalog.Phase(DateTime.UtcNow, start, end, out week);
        }

        private static List<SeasonChallenge> WeekChallenges(int week) =>
            week < 0 ? new List<SeasonChallenge>()
                     : SeasonCatalog.ForWeek(SeasonCatalog.ParseChallenges(Plugin.SeasonChallenges?.Value), SeasonId, week);

        // ---- server: progress ----

        /// <summary>Called by QuestProgressNetwork for every accepted event (kill, gather, talk).</summary>
        internal static void OnEvent(long sender, long playerId, QuestObjectiveKind kind, string target, int count)
        {
            try
            {
                if (CurrentPhase(out int week) != 1) return;
                var today = WeekChallenges(week);
                if (!today.Any(c => c.Matches(kind, target))) return;

                if (kind == QuestObjectiveKind.Talk)
                {
                    float clock = Time.realtimeSinceStartup;
                    if (LastTalk.TryGetValue(playerId, out float last) && clock - last < TalkCooldown) return;
                    LastTalk[playerId] = clock;
                }

                var record = SeasonDatabase.Get(playerId, SeasonId);
                RollWeek(record, week);
                int max = SeasonCatalog.Levels * PerLevelCfg;
                bool changed = false;
                for (int i = 0; i < today.Count; i++)
                {
                    var c = today[i];
                    if ((record.DoneMask & (1 << i)) != 0 || !c.Matches(kind, target)) continue;
                    int progress = Math.Min(c.Amount, record.CounterAt(i) + Math.Max(1, count));
                    record.SetCounterAt(i, progress);
                    changed = true;
                    if (progress < c.Amount) continue;

                    record.DoneMask |= 1 << i;
                    int before = SeasonCatalog.LevelOf(record.Points, PerLevelCfg);
                    record.Points = Math.Min(max, record.Points + c.Points);
                    int after = SeasonCatalog.LevelOf(record.Points, PerLevelCfg);
                    string text = "Temporada: \"" + c.Label + "\" concluído! +" + c.Points + " pontos";
                    if (after > before) text += " (nível " + after + ")";
                    GrantDue(record);
                    Send(sender, "notice", text);
                }
                if (changed) SeasonDatabase.Save(record);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim: season progress failed: " + e.Message);
            }
        }

        private static void RollWeek(SeasonRecord record, int week)
        {
            if (week < 0 || record.Week == week) return;
            record.Week = week;
            record.Counters = new List<int>();
            record.DoneMask = 0;
        }

        /// <summary>Hands the registry every reward the record has earned and not yet received.
        /// Stops at the first one the registry cannot take, so it is retried on the next call.</summary>
        private static void GrantDue(SeasonRecord record)
        {
            var rewards = SeasonCatalog.ParseRewards(Plugin.SeasonRewards?.Value);
            int level = SeasonCatalog.LevelOf(record.Points, PerLevelCfg);
            for (int l = 1; l <= level; l++)
            {
                if (record.Claimed.Contains(l) || !(l <= FreeCfg || record.Paid)) continue;
                if (rewards.TryGetValue(l, out var key) &&
                    !DeadheimCosmeticsApi.TryGrant(record.PlayerId, key, out _)) return;
                record.Claimed.Add(l);
            }
        }

        // ---- server: status ----

        internal static void ServeStatus(long sender)
        {
            if (!NpcRequestGuard.AllowRate(sender, "season-status", 6, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Send(sender, "notice", "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            try
            {
                int phase = CurrentPhase(out int week);
                if (phase == 3)
                {
                    Send(sender, "status", "3|" + SeasonName + "|0\n0|1|0|0|0|-1\n\n");
                    return;
                }

                SeasonCatalog.TryDate(Plugin.SeasonEnd?.Value, out var end);
                var record = SeasonDatabase.Get(playerId, SeasonId);
                var today = WeekChallenges(week);
                bool dirty = false;
                if (phase == 1 && record.Week != week)
                {
                    RollWeek(record, week);
                    dirty = true;
                }
                int claimedBefore = record.Claimed.Count;
                GrantDue(record);
                if (dirty || record.Claimed.Count != claimedBefore) SeasonDatabase.Save(record);

                var rewards = SeasonCatalog.ParseRewards(Plugin.SeasonRewards?.Value);
                string rewardText = string.Join("|", rewards.OrderBy(r => r.Key)
                    .Select(r => r.Key + "~" + r.Value + "~" + (record.Claimed.Contains(r.Key) ? 1 : 0)));
                string challengeText = string.Join("|", today.Select((c, i) =>
                {
                    bool done = (record.DoneMask & (1 << i)) != 0;
                    return Clean(c.Label, c.Id) + "~" + (done ? c.Amount : record.CounterAt(i)) + "~" + c.Amount +
                           "~" + c.Points + "~" + (done ? 1 : 0);
                }));

                Send(sender, "status",
                    phase + "|" + SeasonName + "|" + end.Ticks + "\n" +
                    record.Points + "|" + PerLevelCfg + "|" + FreeCfg + "|" + (record.Paid ? 1 : 0) + "|" +
                    PriceCfg + "|" + ReadDeadcoins(sender) + "\n" + rewardText + "\n" +
                    (phase == 1 ? challengeText : ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim: season status failed: " + e.Message);
                Send(sender, "notice", "O servidor não conseguiu ler a sua temporada. Tente de novo.");
            }
        }

        private static int ReadDeadcoins(long sender)
        {
            try
            {
                return DeadcoinLedger.TryResolveAccount(sender, out string path, out _) &&
                       DeadcoinLedger.TryRead(path, out int balance) ? balance : -1;
            }
            catch
            {
                return -1;
            }
        }

        // ---- server: buying the paid track ----

        internal static void ServeBuy(long sender, string where, string payload)
        {
            if (!NpcRequestGuard.AllowRate(sender, "season-buy", 3, 5f)) return;
            if (CurrentPhase(out int week) != 1)
            {
                Refuse(sender, "A temporada não está aberta para compras agora.");
                return;
            }
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Refuse(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }
            int price = PriceCfg;
            if (!int.TryParse(payload, NumberStyles.Integer, CultureInfo.InvariantCulture, out int shown) || shown != price)
            {
                Refuse(sender, "O passe agora custa " + price + " Deadcoins. Confira e pague de novo.");
                return;
            }
            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Refuse(sender, "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            var record = SeasonDatabase.Get(playerId, SeasonId);
            if (record.Paid)
            {
                Refuse(sender, "Você já tem o passe desta temporada.");
                return;
            }

            int balance, after;
            try
            {
                if (!DeadcoinLedger.TryRead(path, out balance))
                {
                    Plugin.Log.LogError("NpcValheim: Deadcoins balance of " + who + " is not a number: " + path);
                    Refuse(sender, "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                if (balance < price)
                {
                    Refuse(sender, "Você não tem Deadcoins suficientes: saldo " + balance + ", custa " + price + ".", balance);
                    return;
                }
                after = balance - price;
                DeadcoinLedger.Write(path, after);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim: could not charge " + who + " for the season pass: " + e.Message);
                Refuse(sender, "Falha ao registrar o pagamento. Nada foi cobrado.");
                return;
            }

            // Charged first, written second: the track is never unlocked unpaid, and a failed
            // write puts the Deadcoins back before anyone is told.
            try
            {
                record.Paid = true;
                RollWeek(record, week);
                GrantDue(record);
                SeasonDatabase.Save(record);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim: season pass for " + who + " not written (" + e.Message + "); returning " + price + " Deadcoins");
                try
                {
                    DeadcoinLedger.Write(path, balance);
                }
                catch (Exception undo)
                {
                    Plugin.Log.LogError("NpcValheim: could NOT return " + price + " Deadcoins to " + who +
                                        " (balance file should read " + balance + "): " + undo.Message);
                }
                Refuse(sender, "Falha ao registrar o passe. Nada foi cobrado.", balance);
                return;
            }

            Plugin.Log.LogInfo("NpcValheim: " + who + " bought the season " + SeasonId + " pass for " + price +
                               " Deadcoins at " + where + " (" + balance + " -> " + after + ")");
            try
            {
                DeadcoinLedger.AppendLogLine(who + " bought the season " + SeasonId + " pass for " + price +
                                             " Deadcoins (balance " + balance + " -> " + after + ")");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim: Deadcoins purchase log not written: " + e.Message);
            }

            Send(sender, "bought", after + "\nPasse da temporada liberado! As recompensas que você já merecia foram entregues.");
        }

        // ---- server -> client ----

        private static void Refuse(long sender, string message, int balance = -1) =>
            Send(sender, "refused", balance + "\n" + message);

        private static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        // ---- client ----

        internal static bool TryBeginPurchase()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _buySentAt < BuyTimeout) return false;
            _buySentAt = now;
            return true;
        }

        private static void EndPurchase() => _buySentAt = -BuyTimeout;

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            var parts = (payload ?? "").Split('\n');
            switch (kind)
            {
                case "status" when parts.Length == 4:
                    ApplyStatus(parts);
                    break;
                case "notice" when parts.Length == 1:
                    Announce(parts[0]);
                    break;
                case "bought" when parts.Length == 2 && TryInt(parts[0], out int balance):
                    Paid = true;
                    Balance = balance;
                    EndPurchase();
                    Revision++;
                    Announce(parts[1]);
                    break;
                case "refused" when parts.Length == 2 && TryInt(parts[0], out int bal):
                    if (bal >= 0) Balance = bal;
                    EndPurchase();
                    Announce(parts[1]);
                    break;
                default:
                    Plugin.Log.LogWarning("NpcValheim: malformed season answer '" + kind + "': \"" + payload + "\"");
                    break;
            }
        }

        private static void ApplyStatus(string[] p)
        {
            var head = p[0].Split('|');
            var nums = p[1].Split('|');
            if (head.Length != 3 || nums.Length != 6 || !TryInt(head[0], out int state) ||
                !long.TryParse(head[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) ||
                !TryInt(nums[0], out int points) || !TryInt(nums[1], out int perLevel) || !TryInt(nums[2], out int free) ||
                !TryInt(nums[4], out int price) || !TryInt(nums[5], out int balance))
            {
                Plugin.Log.LogWarning("NpcValheim: malformed season status");
                return;
            }

            State = state;
            Name = head[1];
            Points = points;
            PerLevel = Math.Max(1, perLevel);
            FreeLevels = free;
            Paid = nums[3] == "1";
            Price = price;
            Balance = balance;
            EndUtc = ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : DateTime.MinValue;

            RewardKeys.Clear();
            ClaimedLevels.Clear();
            if (p[2].Length > 0)
                foreach (var r in p[2].Split('|'))
                {
                    var f = r.Split('~');
                    if (f.Length == 3 && TryInt(f[0], out int level))
                    {
                        RewardKeys[level] = f[1];
                        if (f[2] == "1") ClaimedLevels.Add(level);
                    }
                }

            Challenges.Clear();
            if (p[3].Length > 0)
                foreach (var c in p[3].Split('|'))
                {
                    var f = c.Split('~');
                    if (f.Length == 5 && TryInt(f[1], out int prog) && TryInt(f[2], out int goal) && TryInt(f[3], out int pts))
                        Challenges.Add(new ClientChallenge { Label = f[0], Progress = prog, Goal = goal, Points = pts, Done = f[4] == "1" });
                }
            Known = true;
            Revision++;
        }

        /// <summary>A reward key as the player reads it: "titulo:temporada1" -> "Título: temporada1".</summary>
        internal static string DescribeKey(string key)
        {
            int colon = (key ?? "").IndexOf(':');
            if (colon <= 0) return key ?? "";
            string rest = key.Substring(colon + 1).Replace('_', ' ');
            switch (key.Substring(0, colon))
            {
                case "titulo": return "Título: " + rest;
                case "vela": return "Vela: " + rest;
                case "chama": return "Chama " + rest;
                case "lapide": return "Lápide: " + rest;
                case "abate": return "Efeito de abate: " + rest;
                case "pet": return "Pet: " + rest;
                default: return key;
            }
        }

        private static void Announce(string message)
        {
            LastMessage = message;
            MessageRevision++;
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message, 0, null);
        }

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
