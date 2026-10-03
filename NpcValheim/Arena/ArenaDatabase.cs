using System;
using System.Collections.Generic;
using System.Linq;
using LiteDB;
using NpcValheim.Persistence;

namespace NpcValheim.Arena
{
    /// <summary>A member of an arena team, with the personal numbers the WoW team frame shows.</summary>
    public class ArenaMemberRecord
    {
        public long PlayerId { get; set; }
        public string Name { get; set; }
        public int PersonalRating { get; set; }
        public int WeekGames { get; set; }
        public int WeekWins { get; set; }
        public int SeasonGames { get; set; }
        public int SeasonWins { get; set; }
    }

    /// <summary>An arena team (ArenaTeam in TrinityCore): one bracket, a captain, a rating.</summary>
    public class ArenaTeamRecord
    {
        [BsonId(true)]
        public int Id { get; set; }
        public string Name { get; set; }
        public string NameKey { get; set; }
        public int Size { get; set; }
        public long CaptainId { get; set; }
        public int Rating { get; set; }
        public int WeekGames { get; set; }
        public int WeekWins { get; set; }
        public int SeasonGames { get; set; }
        public int SeasonWins { get; set; }
        /// <summary>The last team this one played, and when -- the queue will not pair them
        /// again for a while (Arena.PreviousOpponentsDiscardTimer).</summary>
        public int PreviousOpponent { get; set; }
        public long CreatedUtcTicks { get; set; }
        public List<ArenaMemberRecord> Members { get; set; } = new List<ArenaMemberRecord>();

        public ArenaMemberRecord Member(long playerId) => Members.FirstOrDefault(m => m.PlayerId == playerId);
    }

    /// <summary>
    /// Everything the arena keeps about one character, whether or not they are on a team:
    /// Arena Points, the hidden matchmaking rating per bracket (it survives leaving a team, as
    /// character_arena_stats does), and where to send them home if they logged out mid-match.
    /// </summary>
    public class ArenaPlayerRecord
    {
        [BsonId]
        public long PlayerId { get; set; }
        public string Name { get; set; }
        public int Points { get; set; }
        public Dictionary<string, int> Mmr { get; set; } = new Dictionary<string, int>();
        public bool HasReturn { get; set; }
        public float ReturnX { get; set; }
        public float ReturnY { get; set; }
        public float ReturnZ { get; set; }
        public float ReturnYaw { get; set; }

        public int MmrOf(int size, int start) => Mmr != null && Mmr.TryGetValue(size.ToString(), out int v) ? v : start;

        public void SetMmr(int size, int value)
        {
            Mmr ??= new Dictionary<string, int>();
            Mmr[size.ToString()] = Math.Max(0, value);
        }
    }

    public class ArenaSignatureRecord
    {
        public long PlayerId { get; set; }
        public string Name { get; set; }
    }

    /// <summary>An Arena Team Charter: bought, signed by (size - 1) other players, turned in.</summary>
    public class ArenaCharterRecord
    {
        /// <summary>"owner:size" -- one open charter per player per bracket.</summary>
        [BsonId]
        public string Id { get; set; }
        public long OwnerId { get; set; }
        public string OwnerName { get; set; }
        public int Size { get; set; }
        public string Name { get; set; }
        public string NameKey { get; set; }
        public int Paid { get; set; }
        public long CreatedUtcTicks { get; set; }
        public List<ArenaSignatureRecord> Signatures { get; set; } = new List<ArenaSignatureRecord>();

        public static string KeyOf(long ownerId, int size) => ownerId + ":" + size;
    }

    /// <summary>The weekly clock, persisted so a restart neither skips nor repeats a payout.</summary>
    public class ArenaStateRecord
    {
        [BsonId]
        public string Id { get; set; } = "state";
        public long NextResetUtcTicks { get; set; }
        public string ResetKey { get; set; }
        public long LastDistributionUtcTicks { get; set; }
        public int Week { get; set; }
    }

    /// <summary>One finished match, for the admin and for "why did I lose 12 rating".</summary>
    public class ArenaMatchRecord
    {
        [BsonId]
        public long Id { get; set; }
        public int Size { get; set; }
        public bool Rated { get; set; }
        public string Map { get; set; }
        public long EndedUtcTicks { get; set; }
        public int Winner { get; set; }
        public string Summary { get; set; }
    }

    /// <summary>
    /// arena.db beside the other NpcValheim databases. Server only, single writer, one
    /// connection per operation (see LiteDbFile). The engine keeps its own copy of teams,
    /// players and charters in memory and writes each one back when it changes.
    /// </summary>
    internal sealed class ArenaDatabase
    {
        private readonly LiteDbFile _file;

        internal ArenaDatabase(string path)
        {
            _file = new LiteDbFile(path);
            _file.Write(db =>
            {
                db.GetCollection<ArenaTeamRecord>("teams").EnsureIndex(x => x.NameKey, true);
                db.GetCollection<ArenaCharterRecord>("charters").EnsureIndex(x => x.OwnerId);
                db.GetCollection<ArenaMatchRecord>("matches").EnsureIndex(x => x.EndedUtcTicks);
            });
        }

        internal string Path => _file.Path;

        internal List<ArenaTeamRecord> LoadTeams() =>
            _file.Read(db => db.GetCollection<ArenaTeamRecord>("teams").FindAll().ToList());

        internal List<ArenaPlayerRecord> LoadPlayers() =>
            _file.Read(db => db.GetCollection<ArenaPlayerRecord>("players").FindAll().ToList());

        internal List<ArenaCharterRecord> LoadCharters() =>
            _file.Read(db => db.GetCollection<ArenaCharterRecord>("charters").FindAll().ToList());

        internal ArenaStateRecord LoadState() =>
            _file.Read(db => db.GetCollection<ArenaStateRecord>("state").FindById("state")) ?? new ArenaStateRecord();

        internal long LastMatchId() =>
            _file.Read(db =>
            {
                var matches = db.GetCollection<ArenaMatchRecord>("matches");
                return matches.Count() == 0 ? 0L : matches.Max(x => x.Id);
            });

        /// <summary>Inserts a new team and returns it with its id.</summary>
        internal ArenaTeamRecord InsertTeam(ArenaTeamRecord team)
        {
            _file.Write(db => db.GetCollection<ArenaTeamRecord>("teams").Insert(team));
            return team;
        }

        internal void SaveTeams(IEnumerable<ArenaTeamRecord> teams)
        {
            var list = teams.Where(t => t != null).ToList();
            if (list.Count == 0) return;
            _file.Write(db =>
            {
                var col = db.GetCollection<ArenaTeamRecord>("teams");
                foreach (var t in list) col.Upsert(t);
            });
        }

        internal void DeleteTeam(int id) =>
            _file.Write(db => db.GetCollection<ArenaTeamRecord>("teams").Delete(id));

        internal void SavePlayers(IEnumerable<ArenaPlayerRecord> players)
        {
            var list = players.Where(p => p != null).ToList();
            if (list.Count == 0) return;
            _file.Write(db =>
            {
                var col = db.GetCollection<ArenaPlayerRecord>("players");
                foreach (var p in list) col.Upsert(p);
            });
        }

        internal void SaveCharter(ArenaCharterRecord charter) =>
            _file.Write(db => db.GetCollection<ArenaCharterRecord>("charters").Upsert(charter));

        internal void DeleteCharter(string id) =>
            _file.Write(db => db.GetCollection<ArenaCharterRecord>("charters").Delete(id));

        internal void SaveState(ArenaStateRecord state) =>
            _file.Write(db => db.GetCollection<ArenaStateRecord>("state").Upsert(state));

        internal void SaveMatch(ArenaMatchRecord match) =>
            _file.Write(db => db.GetCollection<ArenaMatchRecord>("matches").Upsert(match));

        /// <summary>
        /// Teams, players and weekly state written in one transaction. The weekly payout
        /// touches all three, and a payout half-written before a crash would either pay a week
        /// twice or wipe the week's games without paying it.
        /// </summary>
        internal void SaveWeek(IEnumerable<ArenaTeamRecord> teams, IEnumerable<ArenaPlayerRecord> players, ArenaStateRecord state)
        {
            var teamList = teams.ToList();
            var playerList = players.ToList();
            _file.Write(db =>
            {
                db.BeginTrans();
                try
                {
                    var t = db.GetCollection<ArenaTeamRecord>("teams");
                    foreach (var team in teamList) t.Upsert(team);
                    var p = db.GetCollection<ArenaPlayerRecord>("players");
                    foreach (var player in playerList) p.Upsert(player);
                    db.GetCollection<ArenaStateRecord>("state").Upsert(state);
                    db.Commit();
                }
                catch
                {
                    db.Rollback();
                    throw;
                }
            });
        }
    }
}
