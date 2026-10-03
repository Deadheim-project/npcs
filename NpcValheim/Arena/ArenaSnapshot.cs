using System.Collections.Generic;
using System.Linq;
using static NpcValheim.Arena.ArenaWire;

namespace NpcValheim.Arena
{
    internal sealed class ArenaTeamView
    {
        public int Id;
        public int Size;
        public string Name;
        public long CaptainId;
        public int Rating;
        public int Rank;
        public int WeekGames;
        public int WeekWins;
        public int SeasonGames;
        public int SeasonWins;
        public int Mmr;
        public readonly List<ArenaMemberView> Members = new List<ArenaMemberView>();
    }

    internal sealed class ArenaMemberView
    {
        public long PlayerId;
        public string Name;
        public int Personal;
        public int WeekGames;
        public int WeekWins;
        public int SeasonGames;
        public int SeasonWins;
        public bool Online;
    }

    internal sealed class ArenaCharterView
    {
        public int Size;
        public string Name;
        public int Signatures;
        public int Required;
        public string Signers;
    }

    internal sealed class ArenaSignView
    {
        public long Owner;
        public int Size;
        public string TeamName;
        public string OwnerName;
    }

    internal sealed class ArenaInviteView
    {
        public int TeamId;
        public int Size;
        public string TeamName;
        public string From;
    }

    internal sealed class ArenaQueueView
    {
        public int Size;
        public bool Rated;
        public int Waited;
        public string Status;
    }

    internal sealed class ArenaMatchView
    {
        public long Id;
        public int Size;
        public bool Rated;
        public string Map;
        public int Side;
        /// <summary>invited, prep, live, ended.</summary>
        public string Phase;
        public int SecondsLeft;
        /// <summary>invited, alive, ko.</summary>
        public string Mine;
        public int Winner;
        public int GoldAlive;
        public int GoldTotal;
        public int GreenAlive;
        public int GreenTotal;
        public string GoldName;
        public string GreenName;
    }

    /// <summary>One player's whole arena state, as the server last described it.</summary>
    internal sealed class ArenaSnapshot
    {
        public long PlayerId;
        public int Points;
        public readonly List<ArenaTeamView> Teams = new List<ArenaTeamView>();
        public readonly List<ArenaCharterView> Charters = new List<ArenaCharterView>();
        public readonly List<ArenaSignView> SignRequests = new List<ArenaSignView>();
        public ArenaInviteView Invite;
        public ArenaQueueView Queue;
        public ArenaMatchView Match;

        public ArenaTeamView TeamOf(int size) => Teams.FirstOrDefault(t => t.Size == size);
        public ArenaCharterView CharterOf(int size) => Charters.FirstOrDefault(c => c.Size == size);

        internal static ArenaSnapshot Parse(string payload)
        {
            var s = new ArenaSnapshot();
            foreach (var f in ArenaWire.Parse(payload))
            {
                switch (f[0])
                {
                    case "P":
                        s.Points = Int(f, 1);
                        s.PlayerId = Long(f, 2);
                        break;
                    case "T":
                        s.Teams.Add(new ArenaTeamView
                        {
                            Id = Int(f, 1), Size = Int(f, 2), Name = Str(f, 3), CaptainId = Long(f, 4), Rating = Int(f, 5),
                            Rank = Int(f, 6), WeekGames = Int(f, 7), WeekWins = Int(f, 8), SeasonGames = Int(f, 9),
                            SeasonWins = Int(f, 10), Mmr = Int(f, 11),
                        });
                        break;
                    case "M":
                        var team = s.Teams.FirstOrDefault(t => t.Id == Int(f, 1));
                        team?.Members.Add(new ArenaMemberView
                        {
                            PlayerId = Long(f, 2), Name = Str(f, 3), Personal = Int(f, 4), WeekGames = Int(f, 5),
                            WeekWins = Int(f, 6), SeasonGames = Int(f, 7), SeasonWins = Int(f, 8), Online = Str(f, 9) == "1",
                        });
                        break;
                    case "C":
                        s.Charters.Add(new ArenaCharterView
                        {
                            Size = Int(f, 1), Name = Str(f, 2), Signatures = Int(f, 3), Required = Int(f, 4), Signers = Str(f, 5),
                        });
                        break;
                    case "S":
                        s.SignRequests.Add(new ArenaSignView { Owner = Long(f, 1), Size = Int(f, 2), TeamName = Str(f, 3), OwnerName = Str(f, 4) });
                        break;
                    case "I":
                        s.Invite = new ArenaInviteView { TeamId = Int(f, 1), Size = Int(f, 2), TeamName = Str(f, 3), From = Str(f, 4) };
                        break;
                    case "Q":
                        s.Queue = new ArenaQueueView { Size = Int(f, 1), Rated = Str(f, 2) == "1", Waited = Int(f, 3), Status = Str(f, 4) };
                        break;
                    case "X":
                        s.Match = new ArenaMatchView
                        {
                            Id = Long(f, 1), Size = Int(f, 2), Rated = Str(f, 3) == "1", Map = Str(f, 4), Side = Int(f, 5),
                            Phase = Str(f, 6), SecondsLeft = Int(f, 7), Mine = Str(f, 8), Winner = Int(f, 9, -1),
                        };
                        break;
                    case "A":
                        if (s.Match == null) break;
                        s.Match.GoldAlive = Int(f, 1);
                        s.Match.GoldTotal = Int(f, 2);
                        s.Match.GreenAlive = Int(f, 3);
                        s.Match.GreenTotal = Int(f, 4);
                        s.Match.GoldName = Str(f, 5);
                        s.Match.GreenName = Str(f, 6);
                        break;
                }
            }
            return s;
        }
    }

    internal sealed class ArenaResultSide
    {
        public int Side;
        public string TeamName;
        public int RatingBefore;
        public int RatingChange;
        public int Mmr;
        public int MmrChange;
    }

    internal sealed class ArenaResultPlayer
    {
        public int Side;
        public string Name;
        public int Kos;
        public string Status;
        public int PersonalBefore;
        public int PersonalChange;
    }

    /// <summary>The scoreboard at the end of a match.</summary>
    internal sealed class ArenaResult
    {
        public long MatchId;
        public int Size;
        public bool Rated;
        public int Winner;
        public string Map;
        public int Duration;
        public readonly List<ArenaResultSide> Sides = new List<ArenaResultSide>();
        public readonly List<ArenaResultPlayer> Players = new List<ArenaResultPlayer>();

        internal static ArenaResult Parse(string payload)
        {
            var r = new ArenaResult();
            foreach (var f in ArenaWire.Parse(payload))
            {
                switch (f[0])
                {
                    case "R":
                        r.MatchId = Long(f, 1);
                        r.Size = Int(f, 2);
                        r.Rated = Str(f, 3) == "1";
                        r.Winner = Int(f, 4);
                        r.Map = Str(f, 5);
                        r.Duration = Int(f, 6);
                        break;
                    case "S":
                        r.Sides.Add(new ArenaResultSide
                        {
                            Side = Int(f, 1), TeamName = Str(f, 2), RatingBefore = Int(f, 3), RatingChange = Int(f, 4),
                            Mmr = Int(f, 5), MmrChange = Int(f, 6),
                        });
                        break;
                    case "U":
                        r.Players.Add(new ArenaResultPlayer
                        {
                            Side = Int(f, 1), Name = Str(f, 2), Kos = Int(f, 3), Status = Str(f, 4),
                            PersonalBefore = Int(f, 5, -1), PersonalChange = Int(f, 6),
                        });
                        break;
                }
            }
            return r;
        }
    }

    internal sealed class ArenaLadderRow
    {
        public int Rank;
        public string Name;
        public int Rating;
        public int Games;
        public int Wins;
        public int TeamId;
        public bool Mine;
    }
}
