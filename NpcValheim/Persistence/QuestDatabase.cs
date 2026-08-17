using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;

namespace NpcValheim.Persistence
{
    public enum QuestStatus { NotStarted, Active, Completed }

    /// <summary>What one player has done with one quest. Keyed by RPC sender id, like the
    /// market ledger and the mailbox -- see the identity note in MarketplaceNpc.</summary>
    public class QuestProgress
    {
        [BsonId]
        public string Key { get; set; }
        public long PlayerId { get; set; }
        public string QuestId { get; set; }

        /// <summary>One counter per objective, in the quest's own order. A list rather than a
        /// single number because a quest asks for several things at once; the list grows on
        /// demand, so a record written before an admin added an objective still reads.</summary>
        public List<int> Counters { get; set; } = new List<int>();

        /// <summary>Progress on the first objective. Almost every quest has exactly one, and
        /// the tracker, the nameplate marker and the journal line all want a single number.
        /// Computed, and marked so LiteDB doesn't store it twice.</summary>
        [BsonIgnore]
        public int Counter => CounterAt(0);

        public int CounterAt(int index) =>
            Counters != null && index >= 0 && index < Counters.Count ? Counters[index] : 0;

        public void SetCounterAt(int index, int value)
        {
            if (index < 0) return;
            if (Counters == null) Counters = new List<int>();
            while (Counters.Count <= index) Counters.Add(0);
            Counters[index] = value;
        }

        public QuestStatus Status { get; set; }
        public DateTime UpdatedUtc { get; set; }

        /// <summary>How many times this player has handed the quest in. Needed because a
        /// repeatable quest resets to NotStarted, so Status alone cannot answer "have you
        /// ever done this one" -- which is exactly what a quest chain has to ask.</summary>
        public int TimesCompleted { get; set; }

        /// <summary>When it was last handed in, for quests that come back on a timer, as
        /// raw UTC ticks.
        ///
        /// LiteDB round-trips a DateTime through the local timezone, so a value written as
        /// UtcNow reads back shifted by the server's offset -- measured as 21h left on a 24h
        /// timer in UTC-3, i.e. every daily would have reset three hours early. Raw ticks
        /// take the conversion out of the picture.</summary>
        public long CompletedUtcTicks { get; set; }

        // [BsonIgnore] is the actual fix, not the ticks field on its own: LiteDB maps every
        // public property, so without this it kept serialising this DateTime too and its
        // timezone-shifted value clobbered the ticks on the way back in.
        [BsonIgnore]
        public DateTime CompletedUtc
        {
            get => CompletedUtcTicks == 0L
                ? DateTime.MinValue
                : new DateTime(CompletedUtcTicks, DateTimeKind.Utc);
            set => CompletedUtcTicks = value.Ticks;
        }

        public static string MakeKey(long playerId, string questId) => playerId + "|" + questId;
    }

    /// <summary>
    /// Per-player quest state, server-side. Definitions come from YAML on disk
    /// (QuestDefinition); this only tracks who accepted what and how far along they are.
    /// </summary>
    public static class QuestDatabase
    {
        private static LiteDbFile _file;

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db => db.GetCollection<QuestProgress>("quests").EnsureIndex(x => x.PlayerId));
        }

        /// <summary>Nothing to close: every operation opens and closes its own connection.
        /// Kept so the plugin's shutdown path stays symmetrical with Init.</summary>
        public static void Shutdown() => _file = null;

        private static T Read<T>(Func<ILiteCollection<QuestProgress>, T> body) =>
            _file.Read(db => body(db.GetCollection<QuestProgress>("quests")));

        private static void Write(Action<ILiteCollection<QuestProgress>> body) =>
            _file.Write(db => body(db.GetCollection<QuestProgress>("quests")));

        public static QuestProgress Get(long playerId, string questId) =>
            Read(quests => quests.FindById(QuestProgress.MakeKey(playerId, questId)));

        public static List<QuestProgress> GetAll(long playerId) =>
            Read(quests => quests.Find(x => x.PlayerId == playerId).ToList());

        public static QuestStatus GetStatus(long playerId, string questId) =>
            Get(playerId, questId)?.Status ?? QuestStatus.NotStarted;

        /// <summary>Puts a timed quest back on offer once its window has passed, and reports
        /// the status the player should actually see.
        ///
        /// The reset is written back rather than computed on the fly every read: the quest
        /// genuinely becomes available again, and anything chained off it must keep seeing a
        /// completion that already happened (TimesCompleted survives).</summary>
        public static QuestStatus RefreshAndGetStatus(long playerId, QuestDefinition quest)
        {
            if (quest == null) return QuestStatus.NotStarted;

            var entry = Get(playerId, quest.Id);
            if (entry == null) return QuestStatus.NotStarted;
            if (entry.Status != QuestStatus.Completed || quest.ResetHours <= 0) return entry.Status;

            if (DateTime.UtcNow < entry.CompletedUtc.AddHours(quest.ResetHours)) return entry.Status;

            entry.Status = QuestStatus.NotStarted;
            entry.Counters = new List<int>();
            entry.UpdatedUtc = DateTime.UtcNow;
            Write(quests => quests.Update(entry));
            return QuestStatus.NotStarted;
        }

#if DEVTOOLS
        /// <summary>Test-only: writes a progress record back as-is, so the suite can wind a
        /// completion timestamp backwards instead of waiting 24 real hours. Gated on the dev
        /// flag and unreachable from any client.</summary>
        internal static void SaveForSelfTest(QuestProgress entry)
        {
            if (Plugin.EnableSelfTest?.Value != true || entry == null) return;
            Write(quests => quests.Update(entry));
        }
#endif

        /// <summary>How long until a timed quest comes back, or zero when it is available or
        /// never resets.</summary>
        public static TimeSpan TimeUntilReset(long playerId, QuestDefinition quest)
        {
            if (quest == null || quest.ResetHours <= 0) return TimeSpan.Zero;
            var entry = Get(playerId, quest.Id);
            if (entry == null || entry.Status != QuestStatus.Completed) return TimeSpan.Zero;

            var ready = entry.CompletedUtc.AddHours(quest.ResetHours);
            var left = ready - DateTime.UtcNow;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        public static QuestProgress Accept(long playerId, string questId)
        {
            var existing = Get(playerId, questId);
            if (existing != null && existing.Status == QuestStatus.Active) return existing;

            var entry = new QuestProgress
            {
                Key = QuestProgress.MakeKey(playerId, questId),
                PlayerId = playerId,
                QuestId = questId,
                Counters = new List<int>(),
                Status = QuestStatus.Active,
                UpdatedUtc = DateTime.UtcNow,
            };
            Write(quests => quests.Upsert(entry));
            return entry;
        }

        /// <summary>Advances one objective's counter, capped at its goal so it can't overshoot.
        /// No-op unless the quest is actually active for that player.</summary>
        public static int AddProgress(long playerId, string questId, int objectiveIndex, int delta, int goal)
        {
            var entry = Get(playerId, questId);
            if (entry == null || entry.Status != QuestStatus.Active) return 0;

            int now = Math.Min(goal, entry.CounterAt(objectiveIndex) + Math.Max(0, delta));
            entry.SetCounterAt(objectiveIndex, now);
            entry.UpdatedUtc = DateTime.UtcNow;
            Write(quests => quests.Update(entry));
            return now;
        }

        /// <summary>Marks a quest done, or clears it back to NotStarted when it is repeatable
        /// so it can be taken again.</summary>
        public static void Complete(long playerId, string questId, bool repeatable)
        {
            var entry = Get(playerId, questId);
            if (entry == null) return;

            entry.TimesCompleted++;
            entry.Counters = new List<int>();
            entry.UpdatedUtc = DateTime.UtcNow;
            entry.CompletedUtcTicks = DateTime.UtcNow.Ticks;

            // A repeatable quest goes back on offer, but the record is kept rather than
            // deleted so its completion still counts towards anything chained off it.
            entry.Status = repeatable ? QuestStatus.NotStarted : QuestStatus.Completed;
            Write(quests => quests.Update(entry));
        }

        /// <summary>True once this player has handed the quest in at least once. Accepts a
        /// legacy record that only has Status=Completed, from before completions were
        /// counted.</summary>
        public static bool HasEverCompleted(long playerId, string questId)
        {
            var entry = Get(playerId, questId);
            return entry != null && (entry.TimesCompleted > 0 || entry.Status == QuestStatus.Completed);
        }

        /// <summary>Which of a quest's prerequisites are still unmet -- earlier quests this
        /// player has not finished, and world progress that has not happened yet.</summary>
        public static List<string> MissingPrerequisites(long playerId, QuestDefinition quest)
        {
            var missing = new List<string>();
            if (quest == null) return missing;

            foreach (var requiredId in quest.RequiresQuests ?? new List<string>())
            {
                if (string.IsNullOrEmpty(requiredId) || HasEverCompleted(playerId, requiredId)) continue;
                var required = QuestStore.Get(requiredId);
                missing.Add(required != null ? required.Name : requiredId);
            }

            foreach (var key in quest.RequiresGlobalKeys ?? new List<string>())
            {
                if (string.IsNullOrEmpty(key)) continue;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(key)) continue;
                missing.Add(DescribeGlobalKey(key));
            }

            return missing;
        }

        /// <summary>Turns a global key into something a player would recognise. Unknown keys
        /// are shown as they are rather than hidden, so a typo in a quest yaml is visible
        /// instead of silently locking the quest forever.</summary>
        private static string DescribeGlobalKey(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "defeated_eikthyr": return "derrotar Eikthyr";
                case "defeated_gdking": return "derrotar o Ancião";
                case "defeated_bonemass": return "derrotar Bonemass";
                case "defeated_dragon": return "derrotar Moder";
                case "defeated_goblinking": return "derrotar Yagluth";
                case "defeated_queen": return "derrotar a Rainha";
                case "defeated_fader": return "derrotar Fader";
                default: return key;
            }
        }

        public static void Abandon(long playerId, string questId)
        {
            var entry = Get(playerId, questId);
            if (entry != null && entry.Status == QuestStatus.Active)
                Write(quests => quests.Delete(entry.Key));
        }
    }
}

