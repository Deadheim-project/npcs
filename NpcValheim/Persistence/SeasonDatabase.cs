using System;
using System.Collections.Generic;
using LiteDB;

namespace NpcValheim.Persistence
{
    /// <summary>What one player has done in one season. Keyed by player id and season id, so a
    /// new SeasonId starts everybody from zero without touching the old rows.</summary>
    public class SeasonRecord
    {
        [BsonId]
        public string Key { get; set; }
        public long PlayerId { get; set; }
        public string SeasonId { get; set; }

        public int Points { get; set; }

        /// <summary>Whether the paid track was bought.</summary>
        public bool Paid { get; set; }

        /// <summary>Levels whose reward the registry has already received.</summary>
        public List<int> Claimed { get; set; } = new List<int>();

        /// <summary>Week of the season the counters below belong to (-1 = none yet).</summary>
        public int Week { get; set; } = -1;

        /// <summary>Progress on the week's challenges, in the order the week lists them.</summary>
        public List<int> Counters { get; set; } = new List<int>();

        /// <summary>Bit i set = challenge i of this week already paid its points.</summary>
        public int DoneMask { get; set; }

        public static string MakeKey(long playerId, string seasonId) => playerId + "|" + seasonId;

        public int CounterAt(int index) =>
            Counters != null && index >= 0 && index < Counters.Count ? Counters[index] : 0;

        public void SetCounterAt(int index, int value)
        {
            if (index < 0) return;
            if (Counters == null) Counters = new List<int>();
            while (Counters.Count <= index) Counters.Add(0);
            Counters[index] = value;
        }
    }

    /// <summary>Season progress, server-side (season.db next to the quest database).</summary>
    public static class SeasonDatabase
    {
        private static LiteDbFile _file;

        public static void Init(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db => db.GetCollection<SeasonRecord>("season").EnsureIndex(x => x.PlayerId));
        }

        public static void Shutdown() => _file = null;

        /// <summary>The record, or a fresh unsaved one when the player has none yet.</summary>
        public static SeasonRecord Get(long playerId, string seasonId)
        {
            var key = SeasonRecord.MakeKey(playerId, seasonId);
            var found = _file.Read(db => db.GetCollection<SeasonRecord>("season").FindById(key));
            return found ?? new SeasonRecord { Key = key, PlayerId = playerId, SeasonId = seasonId };
        }

        public static void Save(SeasonRecord record) =>
            _file.Write(db => db.GetCollection<SeasonRecord>("season").Upsert(record));
    }
}
