using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NpcValheim.Arena;
using UnityEngine;

/// <summary>
/// The arena engine, outside the game. Every expected number below was computed separately
/// (float32, as TrinityCore computes it) from the 3.3.5 formulas, not read back from the code
/// under test.
/// </summary>
class Program
{
    static int failed, passed;

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { passed++; System.Console.WriteLine("  PASS  " + what); }
        else { failed++; System.Console.WriteLine("  FAIL  " + what + (detail.Length > 0 ? "  -- " + detail : "")); }
    }

    static void Section(string name) => System.Console.WriteLine("\n== " + name + " ==");

    // -------------------------------------------------------------------- fake server

    sealed class FakeHost : IArenaHost
    {
        public double T = 1000;
        public DateTime Utc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        public readonly Dictionary<long, string> Online = new Dictionary<long, string>();
        public readonly Dictionary<long, Vector3> Positions = new Dictionary<long, Vector3>();
        public readonly List<(long To, string Kind, string Payload)> Sent = new List<(long, string, string)>();
        public readonly List<(long To, string Prefab, int Amount)> Items = new List<(long, string, int)>();
        public readonly List<(long To, string Body)> Letters = new List<(long, string)>();
        public readonly List<string> Logs = new List<string>();

        public double Now => T;
        public DateTime UtcNow => Utc;
        public bool IsOnline(long playerId) => Online.ContainsKey(playerId);
        public string OnlineName(long playerId) => Online.TryGetValue(playerId, out var n) ? n : null;
        public long FindOnline(string name) => Online.FirstOrDefault(kv => string.Equals(kv.Value, name, StringComparison.OrdinalIgnoreCase)).Key;
        // In the fake, a peer id is the player id plus a million.
        public long PlayerOfPeer(long peerId) => Online.ContainsKey(peerId - 1000000) ? peerId - 1000000 : 0L;

        public bool TryGetPosition(long playerId, out Vector3 position)
        {
            if (!Positions.TryGetValue(playerId, out position)) position = new Vector3(5000, 30, 5000);
            return Online.ContainsKey(playerId);
        }

        public void Send(long playerId, string kind, string payload)
        {
            Sent.Add((playerId, kind, payload));
            var f = payload.Split('\t');
            // The client's side of a teleport, landing at once.
            if (kind == ArenaWire.Enter) Positions[playerId] = V(f, 1);
            else if (kind == ArenaWire.Move) Positions[playerId] = V(f, 0);
            else if (kind == ArenaWire.Return && f.Length >= 3) Positions[playerId] = V(f, 0);
        }

        static Vector3 V(string[] f, int i) => new Vector3(float.Parse(f[i], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(f[i + 1], System.Globalization.CultureInfo.InvariantCulture),
            float.Parse(f[i + 2], System.Globalization.CultureInfo.InvariantCulture));

        public void MailItem(long playerId, string subject, string prefab, int amount) => Items.Add((playerId, prefab, amount));
        public void MailLetter(long playerId, string subject, string body) => Letters.Add((playerId, body));
        public void Log(string text) => Logs.Add(text);
        public void Warn(string text) => Logs.Add("WARN " + text);

        public string Last(long to, string kind) =>
            Sent.LastOrDefault(s => s.To == to && s.Kind == kind).Payload;

        public bool Got(long to, string kind, string contains = null) =>
            Sent.Any(s => s.To == to && s.Kind == kind && (contains == null || s.Payload.Contains(contains)));
    }

    const long A = 1, B = 2, C = 3, D = 4, E = 5, F = 6, G = 7, H = 8;
    static readonly Dictionary<long, string> Names = new Dictionary<long, string>
    {
        { A, "Alfa" }, { B, "Bravo" }, { C, "Charlie" }, { D, "Delta" },
        { E, "Echo" }, { F, "Foxtrot" }, { G, "Golf" }, { H, "Hotel" },
    };

    static ArenaTuning Tuning()
    {
        var t = new ArenaTuning();
        t.Maps.Add(new ArenaMapDef
        {
            Name = "Anel", Gold = new Vector3(100, 30, 100), GoldYaw = 90, Green = new Vector3(140, 30, 100), GreenYaw = 270,
        });
        t.Offers.Add(new ArenaOffer { Prefab = "ArmorKit1", Amount = 1, Points = 100, Rating = 0 });
        t.Offers.Add(new ArenaOffer { Prefab = "WeaponKit4", Amount = 1, Points = 200, Rating = 40, Bracket = 2 });
        return t;
    }

    static (ArenaEngine Engine, FakeHost Host, string Db) NewEngine(ArenaTuning tuning = null, string db = null)
    {
        db ??= Path.Combine(Path.GetTempPath(), "arenacheck-" + Guid.NewGuid().ToString("N") + ".db");
        var host = new FakeHost();
        foreach (var kv in Names) host.Online[kv.Key] = kv.Value;
        var engine = new ArenaEngine(host, new ArenaDatabase(db));
        engine.SetTuning(tuning ?? Tuning());
        return (engine, host, db);
    }

    static void Do(ArenaEngine e, long who, string where, string action, string payload = "", bool admin = false) =>
        e.Handle(who, Names.TryGetValue(who, out var n) ? n : "P" + who, where, admin, action, payload);

    static void Advance(ArenaEngine e, FakeHost h, double seconds, double step = 0.5)
    {
        for (double t = 0; t < seconds; t += step)
        {
            h.T += step;
            e.Tick();
        }
    }

    /// <summary>A two-member 2v2 team, made the WoW way: charter, signature, turn-in.</summary>
    static ArenaTeamRecord MakeTeam(ArenaEngine e, long captain, long signer, string name)
    {
        Do(e, captain, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, $"2\n{name}\n80");
        Do(e, captain, "", ArenaWire.ActCharterOffer, $"2\n{Names[signer]}");
        Do(e, signer, "", ArenaWire.ActCharterSign, $"{captain}\n2\n1");
        Do(e, captain, ArenaEngine.AtOrganizer, ArenaWire.ActCharterTurnIn, "2");
        return e.TeamOf(captain, 2);
    }

    /// <summary>The leader queues the group; the member's client confirms it.</summary>
    static void QueueRated(ArenaEngine e, long leader, long member)
    {
        Do(e, leader, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{member + 1000000}");
        var pending = e.PendingOf(leader);
        if (pending != null) Do(e, member, "", ArenaWire.ActGroupConfirm, $"{pending.Ticket}\n1");
    }

    static void EnterAll(ArenaEngine e, ArenaMatch match)
    {
        foreach (var seat in match.Seats.ToList()) Do(e, seat.PlayerId, "", ArenaWire.ActEnter);
    }

    static int SideOf(ArenaMatch m, long player) => m.Seat(player).Side;

    static void Main()
    {
        try
        {
            Rules();
            Parsers();
            Combat();
            Charters();
            RatedMatch();
            SoloArena();
            InviteAndDesertion();
            DrawAndOffline();
            QueueRules();
            Skirmish();
            WeeklyPoints();
            Vendor();
            TeamManagement();
            Persistence();
        }
        catch (Exception ex)
        {
            failed++;
            System.Console.WriteLine("  FAIL  the check itself threw: " + ex);
        }

        System.Console.WriteLine();
        System.Console.WriteLine(failed == 0 ? $"ALL {passed} CHECKS PASSED" : $"{failed} FAILED, {passed} passed");
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    // ----------------------------------------------------------------------- rules

    static void Rules()
    {
        Section("formulas of TrinityCore 3.3.5 (ArenaTeam.cpp)");
        var t = new ArenaTuning();
        Check("even match: 50%", Math.Abs(ArenaRules.ChanceAgainst(1500, 1500) - 0.5f) < 1e-6f);
        Check("0 vs 1500 MMR: 0.49%", Math.Abs(ArenaRules.ChanceAgainst(0, 1500) - 0.0048998f) < 1e-5f);

        (int own, int opp, bool won, int expect)[] rating =
        {
            (1500, 1500, true, 12), (1500, 1500, false, -12), (0, 1500, true, 48), (0, 1500, false, 0),
            (1150, 1500, true, 28), (2000, 1800, false, -16), (2000, 1800, true, 8), (1488, 1512, false, -11),
        };
        foreach (var r in rating)
            Check($"rating mod {r.own} vs {r.opp} {(r.won ? "win" : "loss")} = {r.expect}",
                ArenaRules.RatingMod(r.own, r.opp, r.won, t) == r.expect, "got " + ArenaRules.RatingMod(r.own, r.opp, r.won, t));

        (int own, int opp, bool won, int expect)[] mmr =
        {
            (1500, 1500, true, 12), (1500, 1500, false, -12), (1600, 1400, true, 8), (1400, 1600, false, -7),
            (1488, 1512, false, -11),
        };
        foreach (var r in mmr)
            Check($"MMR mod {r.own} vs {r.opp} {(r.won ? "win" : "loss")} = {r.expect}",
                ArenaRules.MatchmakerRatingMod(r.own, r.opp, r.won, t) == r.expect,
                "got " + ArenaRules.MatchmakerRatingMod(r.own, r.opp, r.won, t));

        (int size, int team, int personal, int expect)[] points =
        {
            (2, 1500, 1500, 261), (5, 1500, 1500, 344), (3, 1800, 1800, 669), (3, 2000, 1700, 534),
            (3, 2000, 1900, 928), (5, 2400, 2400, 1395), (2, 0, 0, 261), (3, 1501, 1501, 303),
        };
        foreach (var p in points)
            Check($"points {p.size}v{p.size} team {p.team} personal {p.personal} = {p.expect}",
                ArenaRules.Points(p.size, p.team, p.personal, 1f) == p.expect, "got " + ArenaRules.Points(p.size, p.team, p.personal, 1f));

        Check("30% of 10 games is 3", ArenaRules.RequiredGames(10, 30) == 3);
        Check("30% of 11 games rounds up to 4", ArenaRules.RequiredGames(11, 30) == 4);
        Check("30% of 20 games is 6", ArenaRules.RequiredGames(20, 30) == 6);
        Check("a team with no games requires none", ArenaRules.RequiredGames(0, 30) == 0);
        Check("joining a team below 1000 starts at 0", ArenaRules.StartingPersonalRating(999, 0) == 0);
        Check("joining a 1000+ team starts at 1000", ArenaRules.StartingPersonalRating(1000, 0) == 1000);
        Check("a configured start wins", ArenaRules.StartingPersonalRating(500, 1200) == 1200);
        Check("2v2 charter needs 1 signature", ArenaRules.RequiredSignatures(2, -1) == 1);
        Check("5v5 charter needs 4", ArenaRules.RequiredSignatures(5, -1) == 4);
        Check("the server can lower it to 0", ArenaRules.RequiredSignatures(3, 0) == 0);
        Check("a team holds twice its size", ArenaRules.MaxMembers(3) == 6);
        Check("a 1v1 team is the player alone", ArenaRules.MaxMembers(1) == 1);
        Check("ratings stop at zero", ArenaRules.Apply(5, -16) == 0 && ArenaRules.Apply(100, -16) == 84);

        var thursday = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Check("next reset from Thursday is Tuesday 15:00 UTC",
            t.NextResetAfter(thursday) == new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc), t.NextResetAfter(thursday).ToString("o"));
        var resetMoment = new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc);
        Check("at the reset moment the next one is a week later",
            t.NextResetAfter(resetMoment) == new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc));
        Check("Tuesday morning resets the same day",
            t.NextResetAfter(new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc)) == resetMoment);
    }

    static void Parsers()
    {
        Section("cfg formats");
        var problems = new List<string>();
        var maps = ArenaSettingsParser.ParseMaps("Anel;100,30,100,90;140,30,100,270;120,40,100|Ruim;1,2;3,4,5,6|Longe;0,0,0,0;1000,0,0,0", problems);
        Check("a well-formed arena is read", maps.Count == 1 && maps[0].Name == "Anel" && maps[0].HasSpectator);
        Check("its starts keep their facing", maps.Count == 1 && Math.Abs(maps[0].GoldYaw - 90f) < 0.01f && Math.Abs(maps[0].GreenYaw - 270f) < 0.01f);
        Check("a broken arena and a 1 km wide one are refused and named", problems.Count == 2 && problems.Any(p => p.Contains("Ruim")) && problems.Any(p => p.Contains("Longe")),
            string.Join(" / ", problems));
        Check("a start without facing is accepted", ArenaSettingsParser.ParseMaps("X;1,2,3;4,5,6", null).Count == 1);

        problems.Clear();
        var offers = ArenaSettingsParser.ParseOffers("prefab=Wood;amount=5;points=10|prefab=Stone;points=0|prefab=Iron;points=abc|prefab=Wood;points=99|prefab=Kit;points=50;rating=1500;bracket=3", problems);
        Check("good offers survive", offers.Count == 2 && offers[0].Prefab == "Wood" && offers[1].Rating == 1500 && offers[1].Bracket == 3);
        Check("free, malformed and repeated offers are refused", problems.Count == 3, string.Join(" / ", problems));
        Check("the default vendor list parses whole", ArenaSettingsParser.ParseOffers(ArenaSettingsParser.DefaultOffers, null).Count == 10);

        Check("brackets parse, sort and dedupe", string.Join(",", ArenaSettingsParser.ParseBrackets("5, 2,3,2", null)) == "2,3,5");
        problems.Clear();
        Check("a bracket of 11 is refused", ArenaSettingsParser.ParseBrackets("2,11", problems).Count == 1 && problems.Count == 1);
        var costs = ArenaSettingsParser.ParseSizeMap("2:80,3:120,5:200", "x", null);
        Check("charter costs parse", costs[2] == 80 && costs[3] == 120 && costs[5] == 200);
        Check("days in Portuguese", ArenaSettingsParser.TryParseDay("quarta", out var d) && d == DayOfWeek.Wednesday);
        Check("days in English", ArenaSettingsParser.TryParseDay("Tuesday", out var d2) && d2 == DayOfWeek.Tuesday);
        Check("nonsense is not a day", !ArenaSettingsParser.TryParseDay("amanha", out _));

        Check("team names: accents and spaces are fine",
            ArenaEngine.NormalizeTeamName("  Lobos   do Norte ", 24, out _) == "Lobos do Norte");
        Check("team names: separators are refused", ArenaEngine.NormalizeTeamName("Lobos\tx", 24, out _) == null || ArenaEngine.NormalizeTeamName("Lo|bos", 24, out _) == null);
        Check("team names: 25 characters is too long", ArenaEngine.NormalizeTeamName(new string('a', 25), 24, out _) == null);
    }

    static void Combat()
    {
        Section("who may hurt whom (ArenaCombat.Forbidden)");
        const int live = 2, prep = 1, outp = 3;
        Check("two players outside any match: not the arena's business", !ArenaCombat.Forbidden(0, 0, 0, 0, 0, 0));
        Check("opponents in the same live match fight", !ArenaCombat.Forbidden(9, 1, live, 9, 2, live));
        Check("teammates do not", ArenaCombat.Forbidden(9, 1, live, 9, 1, live));
        Check("nobody fights in the preparation", ArenaCombat.Forbidden(9, 1, prep, 9, 2, prep));
        Check("a knocked-out player neither hits nor is hit", ArenaCombat.Forbidden(9, 1, outp, 9, 2, live) && ArenaCombat.Forbidden(9, 1, live, 9, 2, outp));
        Check("an outsider cannot touch a participant", ArenaCombat.Forbidden(0, 0, 0, 9, 1, live));
        Check("a participant cannot touch an outsider", ArenaCombat.Forbidden(9, 1, live, 0, 0, 0));
        Check("two matches do not mix", ArenaCombat.Forbidden(9, 1, live, 10, 2, live));
    }

    // --------------------------------------------------------------------- charters

    static void Charters()
    {
        Section("charter and team (Arena Organizer)");
        var (e, h, _) = NewEngine();

        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nLobos\n50");
        Check("underpaying refunds everything", e.Charter(A, 2) == null && h.Got(A, ArenaWire.Coins, "50\t"));

        Do(e, A, "", ArenaWire.ActCharterBuy, "2\nLobos\n80");
        Check("a charter cannot be bought away from the Organizer", e.Charter(A, 2) == null);

        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nLobos\n100");
        Check("buying with change keeps the cost", e.Charter(A, 2)?.Paid == 80 && h.Got(A, ArenaWire.Coins, "20\tTroco"));

        Do(e, C, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nlobos\n80");
        Check("the name is taken while the charter is open, case and all", e.Charter(C, 2) == null && h.Got(C, ArenaWire.Coins, "80\t"));

        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterTurnIn, "2");
        Check("no team without the signature", e.TeamOf(A, 2) == null);

        Do(e, A, "", ArenaWire.ActCharterOffer, "2\nAlfa");
        Check("you cannot sign your own charter", e.Charter(A, 2).Signatures.Count == 0);

        Do(e, A, "", ArenaWire.ActCharterOffer, "2\nBravo");
        Check("the signer is asked", h.Got(B, ArenaWire.State, "S\t1\t2\tLobos"));
        Do(e, B, "", ArenaWire.ActCharterSign, $"{A}\n2\n1");
        Check("and signs", e.Charter(A, 2).Signatures.Count == 1);

        Do(e, B, "", ArenaWire.ActCharterSign, $"{A}\n2\n1");
        Check("a second signature from the same player does nothing", e.Charter(A, 2).Signatures.Count == 1);

        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterTurnIn, "2");
        var team = e.TeamOf(A, 2);
        Check("turned in: the team exists", team != null && team.Name == "Lobos");
        Check("the buyer is captain", team?.CaptainId == A);
        Check("the signer is a founding member", team?.Member(B) != null);
        Check("a new team starts at 0 rating, members at 0 personal",
            team?.Rating == 0 && team.Members.All(m => m.PersonalRating == 0));
        Check("the charter is gone", e.Charter(A, 2) == null);

        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nOutro\n80");
        Check("one team per bracket: no second 2v2 charter", e.Charter(A, 2) == null && h.Got(A, ArenaWire.Coins, "80\t"));

        Do(e, C, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nUrsos\n80");
        Do(e, C, "", ArenaWire.ActCharterOffer, "2\nBravo");
        Check("someone already on a 2v2 team cannot be asked to sign another", !h.Got(B, ArenaWire.State, "Ursos"));

        Do(e, E, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "3\nTrio\n120");
        Do(e, E, "", ArenaWire.ActCharterOffer, "3\nFoxtrot");
        Do(e, F, "", ArenaWire.ActCharterSign, $"{E}\n3\n1");
        Do(e, E, ArenaEngine.AtOrganizer, ArenaWire.ActCharterTurnIn, "3");
        Check("3v3 needs two signatures", e.TeamOf(E, 3) == null);
        Do(e, E, ArenaEngine.AtOrganizer, ArenaWire.ActCharterAbandon, "3");
        Check("a charter can be torn up", e.Charter(E, 3) == null);
    }

    // ------------------------------------------------------------------- solo 1v1

    static void SoloArena()
    {
        Section("1v1 alone, with no team to make");
        var tuning = Tuning();
        Check("solo puts 1v1 in front of the cfg brackets", string.Join(",", tuning.ActiveBrackets) == "1,2,3,5" && tuning.HasBracket(1));
        var off = Tuning();
        off.Solo = false;
        Check("solo off: no 1v1 unless the cfg lists it", !off.HasBracket(1) && string.Join(",", off.ActiveBrackets) == "2,3,5");

        var (e, h, _) = NewEngine();
        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "1\nSozinho\n0");
        Check("no charter for 1v1", e.Charter(A, 1) == null && h.Got(A, ArenaWire.Alert, "não precisa de carta"));

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"1\n1\n{B + 1000000}");
        Check("rated 1v1 with a group is refused", e.QueueOf(A) == null && e.PendingOf(A) == null && e.TeamOf(A, 1) == null);

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n1\n");
        var mine = e.TeamOf(A, 1);
        Check("queueing rated alone makes the 1v1 team", mine != null && mine.Members.Count == 1 && mine.CaptainId == A && mine.Name == "Alfa");
        Check("and puts the player in the queue", e.QueueOf(A) != null && e.QueueOf(A).Rated && e.QueueOf(A).TeamId == mine?.Id);
        Check("the 1v1 team does not reserve the player's name for real teams", MakeTeam(e, C, D, "Alfa") != null);

        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n1\n");
        var match = e.Matches.SingleOrDefault();
        Check("two solo players: a rated 1v1", match != null && match.Rated && match.Size == 1 && match.Seats.Count == 2);
        Check("one on each side", match != null && SideOf(match, A) != SideOf(match, B));

        EnterAll(e, match);
        Advance(e, h, 61);
        Check("the gates open", match.Status == MatchStatus.InProgress);
        Do(e, B, "", ArenaWire.ActKo, A.ToString());
        Check("one knockout ends a 1v1", match.Status == MatchStatus.WaitLeave && match.Winner == SideOf(match, A));
        Check("the winner's 1v1 rating rises", e.TeamOf(A, 1).Rating == 48, "got " + e.TeamOf(A, 1).Rating);
        Check("the loser keeps a 1v1 team too", e.TeamOf(B, 1) != null && e.TeamOf(B, 1).WeekGames == 1);
        Advance(e, h, 130);

        Do(e, A, "", ArenaWire.ActTeamInvite, "1\nCharlie");
        Check("nobody can be invited into a 1v1 team", e.TeamOf(C, 1) == null && !h.Got(C, ArenaWire.Alert, "convidou você"));
        Do(e, A, "", ArenaWire.ActTeamDisband, "1");
        Do(e, A, "", ArenaWire.ActTeamLeave, "1");
        Check("the 1v1 team cannot be thrown away with its rating", e.TeamOf(A, 1)?.Rating == 48);

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n1\n");
        Check("queueing again reuses the same team", e.Teams.Count(t => t.Size == 1 && t.Member(A) != null) == 1 && e.QueueOf(A) != null);

        Do(e, E, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Check("1v1 skirmish needs no team either", e.QueueOf(E) != null && !e.QueueOf(E).Rated && e.TeamOf(E, 1) == null);
    }

    // ------------------------------------------------------------------- rated match

    static void RatedMatch()
    {
        Section("a rated 2v2 from queue to scoreboard");
        var (e, h, _) = NewEngine();
        var lobos = MakeTeam(e, A, B, "Lobos");
        var ursos = MakeTeam(e, C, D, "Ursos");
        Check("two teams", lobos != null && ursos != null);

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n1\n");
        Check("rated alone in 2v2 is refused", e.QueueOf(A) == null && e.PendingOf(A) == null);

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{B + 1000000}");
        var pending = e.PendingOf(A);
        Check("the group waits for the member's own client", pending != null && e.QueueOf(A) == null);
        Check("the member was asked to confirm", h.Got(B, ArenaWire.Confirm, pending?.Ticket ?? "?"));
        Do(e, B, "", ArenaWire.ActGroupConfirm, $"{pending.Ticket}\n1");
        Check("confirmed: in the queue", e.QueueOf(A) != null && e.QueueOf(B) == e.QueueOf(A));
        Check("the queue MMR is the members' average (1500)", e.QueueOf(A)?.Mmr == 1500);

        QueueRated(e, C, D);
        var match = e.Matches.SingleOrDefault();
        Check("two teams in range: a match", match != null && match.Rated && e.QueueOf(A) == null);
        Check("everyone is invited", match != null && match.Seats.Count == 4 && match.Seats.All(s => s.State == SeatState.Invited));
        Check("the invitation reaches the players", h.Got(A, ArenaWire.State, "\tinvited\t"));
        Check("a team is all on one side", SideOf(match, A) == SideOf(match, B) && SideOf(match, C) == SideOf(match, D) && SideOf(match, A) != SideOf(match, C));

        Do(e, A, "", ArenaWire.ActQueueLeave);
        Check("once called, the queue cannot be left", match.Seat(A).State == SeatState.Invited);

        h.Positions[A] = new Vector3(-500, 20, 800);
        EnterAll(e, match);
        Check("entering sends each player to their side's start", h.Got(A, ArenaWire.Enter) && h.Positions[A] == match.Map.SpawnOf(SideOf(match, A)));
        Check("where they came from is kept", e.PlayerRecord(A).HasReturn && Math.Abs(e.PlayerRecord(A).ReturnX + 500) < 0.01f);
        Check("the preparation starts with the first entry", match.Status == MatchStatus.WaitJoin && match.PrepEndsAt > 0);
        Check("'one minute' is announced", h.Got(A, ArenaWire.Alert, "Um minuto"));

        Advance(e, h, 31);
        Check("'thirty seconds' is announced", h.Got(C, ArenaWire.Alert, "Trinta segundos"));
        Advance(e, h, 30);
        Check("the gates open after 60 s", match.Status == MatchStatus.InProgress && h.Got(A, ArenaWire.Start));

        int loserSide = SideOf(match, C);
        Do(e, C, "", ArenaWire.ActKo, A.ToString());
        Check("a knockout takes the player out", !match.Seat(C).Alive && match.Seat(A).Kos == 1);
        Check("the knocked-out player is moved aside", h.Got(C, ArenaWire.Move));
        Check("one down is not the end", match.Status == MatchStatus.InProgress);
        Do(e, C, "", ArenaWire.ActKo, A.ToString());
        Check("a second report changes nothing", match.Seat(A).Kos == 1);
        Do(e, D, "", ArenaWire.ActKo, C.ToString());
        Check("a teammate does not get credit for a knockout", match.Seat(C).Kos == 0);
        Check("last one standing: the match is over", match.Status == MatchStatus.WaitLeave && match.Winner == ArenaSide.Other(loserSide));

        lobos = e.TeamOf(A, 2);
        ursos = e.TeamOf(C, 2);
        Check("winning team: 0 -> 48", lobos.Rating == 48, "got " + lobos.Rating);
        Check("losing team at 0 loses nothing (ceil of -0.12)", ursos.Rating == 0, "got " + ursos.Rating);
        Check("winners' personal 0 -> 48", lobos.Member(A).PersonalRating == 48 && lobos.Member(B).PersonalRating == 48);
        Check("winners' MMR 1500 -> 1512", e.PlayerRecord(A).MmrOf(2, 1500) == 1512, "got " + e.PlayerRecord(A).MmrOf(2, 1500));
        Check("losers' MMR 1500 -> 1488", e.PlayerRecord(C).MmrOf(2, 1500) == 1488, "got " + e.PlayerRecord(C).MmrOf(2, 1500));
        Check("week and season counted for both teams", lobos.WeekGames == 1 && lobos.WeekWins == 1 && ursos.WeekGames == 1 && ursos.WeekWins == 0 && lobos.SeasonGames == 1);
        Check("members' games counted", lobos.Member(A).WeekGames == 1 && lobos.Member(A).WeekWins == 1 && ursos.Member(D).WeekGames == 1);
        Check("previous opponents recorded", lobos.PreviousOpponent == ursos.Id && ursos.PreviousOpponent == lobos.Id);

        var result = ArenaResult.Parse(h.Last(A, ArenaWire.Result));
        Check("the scoreboard names the winner", result.Winner == match.Winner && result.Rated);
        var winnerSide = result.Sides.First(s => s.Side == match.Winner);
        Check("the scoreboard shows +48 and the MMR change", winnerSide.RatingChange == 48 && winnerSide.MmrChange == 12);
        Check("the scoreboard lists every player with knockouts",
            result.Players.Count == 4 && result.Players.First(p => p.Name == "Alfa").Kos == 1);

        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Check("no queueing while the scoreboard is up", e.QueueOf(A) == null);

        Advance(e, h, 121);
        Check("after 2 minutes everyone goes home", h.Got(B, ArenaWire.Return) && !e.Matches.Any());
        Check("A went back to where they were", Math.Abs(h.Positions[A].x + 500) < 0.01f);
        Do(e, A, "", ArenaWire.ActReturned);
        Check("the return point is cleared once home", !e.PlayerRecord(A).HasReturn);

        Section("the rematch, MMR now apart");
        QueueRated(e, A, B);
        QueueRated(e, C, D);
        Check("previous opponents are not paired again at once", !e.Matches.Any());
        // 2 minutes, plus one pass of the rated search (every 5 s).
        Advance(e, h, 126, 1);
        var rematch = e.Matches.SingleOrDefault();
        Check("after 2 minutes they are", rematch != null,
            "queue=" + string.Join(";", e.Queue.Select(q => string.Join(",", q.Players) + "@" + q.Mmr + " t" + q.JoinTime)) +
            " now=" + h.T + " logs: " + string.Join(" | ", h.Logs.Skip(Math.Max(0, h.Logs.Count - 6))) +
            " alerts: " + string.Join(" | ", h.Sent.Where(x => x.Kind == ArenaWire.Alert).Skip(Math.Max(0, h.Sent.Count(x => x.Kind == ArenaWire.Alert) - 4)).Select(x => x.To + ":" + x.Payload)));
        if (rematch == null) return;
        EnterAll(e, rematch);
        Advance(e, h, 61);
        Do(e, A, "", ArenaWire.ActKo, C.ToString());
        Do(e, B, "", ArenaWire.ActKo, D.ToString());
        lobos = e.TeamOf(A, 2);
        ursos = e.TeamOf(C, 2);
        // Ursos (team 0, MMR 1488) beat Lobos (team 48, MMR 1512).
        Check("Ursos 0 -> 48 (won below 1000 against 1512)", ursos.Rating == 48, "got " + ursos.Rating);
        Check("Lobos 48 -> 48 (lost: ceil of -0.13 against 1488)", lobos.Rating == 48, "got " + lobos.Rating);
        // MMR 1512 losing to 1488: chance 0.521, so -ceil(12.51) = -12; the winners +ceil(12.51) = +13.
        Check("Lobos members' MMR 1512 -> 1500", e.PlayerRecord(A).MmrOf(2, 1500) == 1500, "got " + e.PlayerRecord(A).MmrOf(2, 1500));
        Check("Ursos members' MMR 1488 -> 1501", e.PlayerRecord(C).MmrOf(2, 1500) == 1501, "got " + e.PlayerRecord(C).MmrOf(2, 1500));
    }

    // ----------------------------------------------------------- leaving and lapsing

    static void InviteAndDesertion()
    {
        Section("an invitation left to lapse, and a desertion");
        var (e, h, _) = NewEngine();
        MakeTeam(e, A, B, "Lobos");
        MakeTeam(e, C, D, "Ursos");
        QueueRated(e, A, B);
        QueueRated(e, C, D);
        var match = e.Matches.Single();
        Do(e, A, "", ArenaWire.ActEnter);
        Do(e, C, "", ArenaWire.ActEnter);
        Do(e, D, "", ArenaWire.ActEnter);
        Advance(e, h, 61);
        Check("B never entered: the invitation lapsed", match.Seat(B).State == SeatState.Expired);
        Check("in a rated match that is a loss for B (0 stays 0, MMR -12)",
            e.TeamOf(B, 2).Member(B).WeekGames == 1 && e.PlayerRecord(B).MmrOf(2, 1500) == 1488);
        Check("the match still starts, 1 against 2", match.Status == MatchStatus.InProgress);

        int aSide = SideOf(match, A);
        Do(e, A, "", ArenaWire.ActLeave);
        Check("A leaves a started rated match: loss on the spot", e.TeamOf(A, 2).Member(A).WeekGames == 1 &&
                                                                    e.PlayerRecord(A).MmrOf(2, 1500) == 1488);
        Check("and is sent home", h.Got(A, ArenaWire.Return));
        Check("nobody left on that side: the other side wins", match.Status == MatchStatus.WaitLeave && match.Winner == ArenaSide.Other(aSide));
        Check("the deserter is not charged twice at the end", e.TeamOf(A, 2).Member(A).WeekGames == 1);
        Check("the team still played and lost", e.TeamOf(A, 2).WeekGames == 1 && e.TeamOf(A, 2).WeekWins == 0);
        Check("the winners won", e.TeamOf(C, 2).Member(C).WeekWins == 1 && e.TeamOf(C, 2).Member(D).WeekWins == 1);

        Section("a match nobody shows up to");
        var (e2, h2, _) = NewEngine();
        MakeTeam(e2, A, B, "Lobos");
        MakeTeam(e2, C, D, "Ursos");
        QueueRated(e2, A, B);
        QueueRated(e2, C, D);
        Advance(e2, h2, 62);
        Check("it is closed when every invitation lapses", !e2.Matches.Any());
        Check("everyone lapsed is charged (MMR 1488)", new[] { A, B, C, D }.All(p => e2.PlayerRecord(p).MmrOf(2, 1500) == 1488));
        Check("but no team rating moved without a match", e2.TeamOf(A, 2).WeekGames == 0 && e2.TeamOf(C, 2).WeekGames == 0);
    }

    static void DrawAndOffline()
    {
        Section("time limit and disconnections");
        var t = Tuning();
        t.TimeLimitSeconds = 300;
        var (e, h, _) = NewEngine(t);
        var lobos = MakeTeam(e, A, B, "Lobos");
        var ursos = MakeTeam(e, C, D, "Ursos");
        lobos.Rating = 1600;
        ursos.Rating = 1600;
        QueueRated(e, A, B);
        QueueRated(e, C, D);
        var match = e.Matches.Single();
        EnterAll(e, match);
        Advance(e, h, 300);
        Check("after the limit the match ends with no winner", match.Status == MatchStatus.WaitLeave && match.Winner == ArenaSide.None);
        Check("both teams lose 16", e.TeamOf(A, 2).Rating == 1584 && e.TeamOf(C, 2).Rating == 1584,
            e.TeamOf(A, 2).Rating + "/" + e.TeamOf(C, 2).Rating);
        Check("everyone takes a personal loss, nobody's MMR moves",
            e.TeamOf(A, 2).Member(A).WeekGames == 1 && e.PlayerRecord(A).MmrOf(2, 1500) == 1500 && e.PlayerRecord(D).MmrOf(2, 1500) == 1500);

        Section("disconnecting during a match");
        var (e2, h2, _) = NewEngine();
        MakeTeam(e2, A, B, "Lobos");
        MakeTeam(e2, C, D, "Ursos");
        QueueRated(e2, A, B);
        QueueRated(e2, C, D);
        var m2 = e2.Matches.Single();
        EnterAll(e2, m2);
        Advance(e2, h2, 61);
        h2.Online.Remove(B);
        Advance(e2, h2, 2);
        Check("a disconnected player is down", m2.Seat(B).Offline && !m2.Seat(B).Alive);
        Do(e2, C, "", ArenaWire.ActKo, A.ToString());
        Do(e2, D, "", ArenaWire.ActKo, A.ToString());
        Check("the side with someone still standing wins", m2.Winner == SideOf(m2, A));
        Check("A, standing, won: personal 0 -> 48", e2.TeamOf(A, 2).Member(A).PersonalRating == 48);
        Check("B, disconnected, is charged a loss even though the team won (OfflineMemberLost)",
            e2.TeamOf(A, 2).Member(B).WeekWins == 0 && e2.TeamOf(A, 2).Member(B).WeekGames == 1);
        Check("but B's MMR moves with the winners' (+12)", e2.PlayerRecord(B).MmrOf(2, 1500) == 1512);

        h2.Online[B] = "Bravo";
        Do(e2, B, "", ArenaWire.ActHello);
        Check("coming back after the match, B is sent home", h2.Got(B, ArenaWire.Return));
    }

    // ------------------------------------------------------------------------ queue

    static void QueueRules()
    {
        Section("matchmaking (BattlegroundQueue)");
        var (e, h, _) = NewEngine();
        var lobos = MakeTeam(e, A, B, "Lobos");
        var ursos = MakeTeam(e, C, D, "Ursos");
        e.PlayerRecord(C).SetMmr(2, 1800);
        e.PlayerRecord(D).SetMmr(2, 1800);
        QueueRated(e, A, B);
        QueueRated(e, C, D);
        Check("1500 against 1800 MMR: no match yet", !e.Matches.Any());
        Advance(e, h, 300, 1);
        Check("still none after 5 minutes", !e.Matches.Any());
        Advance(e, h, 301, 1);
        Check("after 10 minutes the rating no longer matters", e.Matches.Count() == 1);

        Section("a group that is not a group");
        var (e2, h2, _) = NewEngine();
        MakeTeam(e2, A, B, "Lobos");
        Do(e2, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{B + 1000000}");
        var pending = e2.PendingOf(A);
        Do(e2, B, "", ArenaWire.ActGroupConfirm, $"{pending.Ticket}\n0");
        Check("a member whose client says no keeps the group out", e2.QueueOf(A) == null && e2.PendingOf(A) == null);
        Check("and the leader is told", h2.Got(A, ArenaWire.Alert, "não está no seu grupo"));

        Do(e2, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{B + 1000000}");
        Advance(e2, h2, 11);
        Check("a member who never answers: no queue after 10 s", e2.QueueOf(A) == null && e2.PendingOf(A) == null);

        Do(e2, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{C + 1000000}");
        Check("someone from another team cannot be queued rated", e2.PendingOf(A) == null && e2.QueueOf(A) == null);

        Do(e2, A, "", ArenaWire.ActQueueJoin, $"2\n0\n");
        Check("the queue is joined at the Battlemaster only", e2.QueueOf(A) == null);

        var noMaps = Tuning();
        noMaps.Maps.Clear();
        e2.SetTuning(noMaps);
        Do(e2, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Check("no arena configured: no queue, and it says so", e2.QueueOf(A) == null && h2.Got(A, ArenaWire.Alert, "Nenhuma arena"));

        Section("one arena, two matches waiting");
        var (e3, h3, _) = NewEngine();
        foreach (var p in new[] { A, B, C, D, E, F, G, H }) Do(e3, p, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Check("the one arena hosts one match; the rest wait", e3.Matches.Count() == 1 && e3.Queue.Count() == 4);
        var first = e3.Matches.Single();
        EnterAll(e3, first);
        Advance(e3, h3, 61);
        foreach (var seat in first.Seats.Where(s => s.Side == ArenaSide.Gold).ToList())
            Do(e3, seat.PlayerId, "", ArenaWire.ActKo, "0");
        Advance(e3, h3, 121, 1);
        Check("when it frees up the next match takes it", e3.Matches.Count() == 1 && e3.Matches.Single() != first && !e3.Queue.Any());
    }

    static void Skirmish()
    {
        Section("skirmish");
        var (e, h, _) = NewEngine();
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n0\n{B + 1000000}");
        var pending = e.PendingOf(A);
        Do(e, B, "", ArenaWire.ActGroupConfirm, $"{pending.Ticket}\n1");
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Check("three players are not a 2v2", !e.Matches.Any());
        Do(e, D, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        var m = e.Matches.SingleOrDefault();
        Check("the fourth makes the match", m != null && !m.Rated);
        Check("the group stays together, the solos form the other side",
            m != null && SideOf(m, A) == SideOf(m, B) && SideOf(m, C) == SideOf(m, D) && SideOf(m, A) != SideOf(m, C));
        Check("no team needed", e.TeamOf(A, 2) == null);
        EnterAll(e, m);
        Advance(e, h, 61);
        Do(e, C, "", ArenaWire.ActKo, A.ToString());
        Do(e, D, "", ArenaWire.ActKo, B.ToString());
        Check("a skirmish ends like any match", m.Status == MatchStatus.WaitLeave);
        Check("and touches no rating", e.PlayerRecord(A).MmrOf(2, 1500) == 1500 && e.Teams.Count() == 0);

        Do(e, E, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n0\n{F + 1000000},{G + 1000000}");
        Check("a group of 3 does not fit a 2v2 skirmish", e.PendingOf(E) == null && e.QueueOf(E) == null);
    }

    // ------------------------------------------------------------------- points

    static void WeeklyPoints()
    {
        Section("weekly Arena Points (ArenaTeamMgr::DistributeArenaPoints)");
        var (e, h, _) = NewEngine();
        var lobos = MakeTeam(e, A, B, "Lobos");
        var ursos = MakeTeam(e, C, D, "Ursos");
        Do(e, E, "", ArenaWire.ActData);

        // Lobos: 10 games; A played 10, B played 2 (below 30% = 3).
        lobos.WeekGames = 10;
        lobos.Rating = 1500;
        lobos.Member(A).WeekGames = 10;
        lobos.Member(B).WeekGames = 2;
        // Ursos: 9 games, not active.
        ursos.WeekGames = 9;
        ursos.Member(C).WeekGames = 9;
        // A is also on a 3v3 team at 1800 that played 10, which pays more.
        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "3\nTrio\n120");
        Do(e, A, "", ArenaWire.ActCharterOffer, "3\nEcho");
        Do(e, E, "", ArenaWire.ActCharterSign, $"{A}\n3\n1");
        Do(e, A, "", ArenaWire.ActCharterOffer, "3\nFoxtrot");
        Do(e, F, "", ArenaWire.ActCharterSign, $"{A}\n3\n1");
        Do(e, A, ArenaEngine.AtOrganizer, ArenaWire.ActCharterTurnIn, "3");
        var trio = e.TeamOf(A, 3);
        trio.Rating = 1800;
        trio.WeekGames = 10;
        foreach (var m in trio.Members) { m.WeekGames = 10; m.PersonalRating = 1800; }

        Do(e, G, "", ArenaWire.ActAdminDistribute, "");
        Check("only an admin can run the payout by hand", e.PlayerRecord(A).Points == 0);

        int paid = e.DistributePoints(manual: true);
        Check("A gets the better of their teams, not the sum: 3v3 at 1800 = 669", e.PlayerRecord(A).Points == 669, "got " + e.PlayerRecord(A).Points);
        Check("B played under 30% of the games: nothing", (e.PlayerRecord(B)?.Points ?? 0) == 0);
        Check("an inactive team (9 games) pays nothing", (e.PlayerRecord(C)?.Points ?? 0) == 0);
        Check("E and F get 669 from the 3v3", e.PlayerRecord(E).Points == 669 && e.PlayerRecord(F).Points == 669);
        Check("three players were paid", paid == 3);
        Check("each paid player gets a letter", h.Letters.Count(l => l.To == A) == 1 && h.Letters.Any(l => l.To == E));
        Check("every team's week starts over", e.TeamOf(A, 2).WeekGames == 0 && e.TeamOf(C, 2).WeekGames == 0 &&
                                               e.TeamOf(A, 2).Member(A).WeekGames == 0 && trio.WeekGames == 0);
        Check("the season stays", trio.SeasonGames == 0 && e.TeamOf(A, 2).Rating == 1500);

        var t = Tuning();
        t.MaxPoints = 1000;
        e.SetTuning(t);
        trio.WeekGames = 10;
        foreach (var m in trio.Members) m.WeekGames = 10;
        e.DistributePoints(manual: true);
        Check("the balance stops at the cap", e.PlayerRecord(A).Points == 1000);

        Section("the weekly clock");
        var (e2, h2, db2) = NewEngine();
        e2.Tick();
        Check("the next reset is scheduled", e2.State.NextResetUtcTicks == new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc).Ticks);
        var team = MakeTeam(e2, A, B, "Lobos");
        team.WeekGames = 10;
        team.Member(A).WeekGames = 10;
        h2.Utc = new DateTime(2026, 10, 6, 15, 0, 1, DateTimeKind.Utc);
        Advance(e2, h2, 31, 1);
        Check("Tuesday 15:00 UTC: paid automatically", e2.PlayerRecord(A).Points == 261, "got " + e2.PlayerRecord(A).Points);
        Check("and the next one is a week later", e2.State.NextResetUtcTicks == new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc).Ticks);
        Advance(e2, h2, 31, 1);
        Check("not paid twice", e2.PlayerRecord(A).Points == 261);

        var reopened = new ArenaEngine(new FakeHost(), new ArenaDatabase(db2));
        Check("a restart keeps the clock and the balance", reopened.State.NextResetUtcTicks == e2.State.NextResetUtcTicks &&
                                                           reopened.PlayerRecord(A).Points == 261);
    }

    static void Vendor()
    {
        Section("arena vendor");
        var (e, h, _) = NewEngine();
        var team = MakeTeam(e, A, B, "Lobos");
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "ArmorKit1\n100");
        Check("no points, no sale", h.Items.Count == 0);

        e.PlayerRecord(A).Points = 500;
        Do(e, A, "", ArenaWire.ActVendorBuy, "ArmorKit1\n100");
        Check("spent at the vendor only", e.PlayerRecord(A).Points == 500);
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "ArmorKit1\n90");
        Check("a price the buyer did not see is refused", e.PlayerRecord(A).Points == 500 && h.Items.Count == 0);
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "ArmorKit1\n100");
        Check("bought: points down, item in the mail", e.PlayerRecord(A).Points == 400 && h.Items.Single().Prefab == "ArmorKit1");
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "Coins\n1");
        Check("what is not on the list is not for sale", h.Items.Count == 1);

        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "WeaponKit4\n200");
        Check("rating requirement: 0 is below 40", h.Items.Count == 1 && h.Got(A, ArenaWire.Alert, "Requer rating 40"));
        team.Member(A).PersonalRating = 48;
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "WeaponKit4\n200");
        Check("the team's rating counts too: personal 48, team 0", h.Items.Count == 1);
        team.Rating = 45;
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "WeaponKit4\n200");
        Check("both at 40+: sold", h.Items.Count == 2 && e.PlayerRecord(A).Points == 200);
        Check("the requirement is min(personal, team), best team", e.BestPurchaseRating(A, 2) == 45);
    }

    static void TeamManagement()
    {
        Section("team management");
        var (e, h, _) = NewEngine();
        var team = MakeTeam(e, A, B, "Lobos");
        Do(e, B, "", ArenaWire.ActTeamInvite, "2\nCharlie");
        Check("only the captain invites", !h.Got(C, ArenaWire.State, "I\t"));
        Do(e, A, "", ArenaWire.ActTeamInvite, "2\nCharlie");
        Check("the invitee is told", h.Got(C, ArenaWire.State, "I\t" + team.Id));
        Do(e, C, "", ArenaWire.ActTeamAnswer, $"{team.Id}\n1");
        Check("accepted: a member", e.TeamOf(C, 2) == team && team.Member(C).PersonalRating == 0);
        Do(e, A, "", ArenaWire.ActTeamInvite, "2\nDelta");
        Do(e, D, "", ArenaWire.ActTeamAnswer, $"{team.Id}\n1");
        Do(e, A, "", ArenaWire.ActTeamInvite, "2\nEcho");
        Do(e, E, "", ArenaWire.ActTeamAnswer, $"{team.Id}\n1");
        Check("a 2v2 team stops at 4 members", team.Members.Count == 4 && e.TeamOf(E, 2) == null);

        Do(e, A, "", ArenaWire.ActTeamLeave, "2");
        Check("the captain cannot leave a team with members", e.TeamOf(A, 2) == team);
        Do(e, A, "", ArenaWire.ActTeamCaptain, $"2\n{B}");
        Check("captaincy passes", team.CaptainId == B);
        Do(e, A, "", ArenaWire.ActTeamLeave, "2");
        Check("then the old captain can leave", e.TeamOf(A, 2) == null && team.Members.Count == 3);
        Do(e, C, "", ArenaWire.ActTeamKick, $"2\n{D}");
        Check("a member cannot remove anyone", team.Member(D) != null);
        Do(e, B, "", ArenaWire.ActTeamKick, $"2\n{D}");
        Check("the captain can", team.Member(D) == null);

        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, $"2\n1\n{C + 1000000}");
        Do(e, C, "", ArenaWire.ActGroupConfirm, $"{e.PendingOf(B).Ticket}\n1");
        Do(e, B, "", ArenaWire.ActTeamKick, $"2\n{C}");
        Check("no roster change while queued", team.Member(C) != null);
        Do(e, B, "", ArenaWire.ActTeamDisband, "2");
        Check("no disband while queued either", e.TeamOf(B, 2) != null);
        Do(e, B, "", ArenaWire.ActQueueLeave);
        Do(e, B, "", ArenaWire.ActTeamDisband, "2");
        Check("out of the queue, the captain disbands", e.TeamOf(B, 2) == null && e.TeamOf(C, 2) == null && !e.Teams.Any());
    }

    static void Persistence()
    {
        Section("restart");
        var (e, h, db) = NewEngine();
        var team = MakeTeam(e, A, B, "Lobos");
        team.Rating = 1234;
        MakeTeam(e, C, D, "Ursos");
        QueueRated(e, A, B);
        QueueRated(e, C, D);
        var match = e.Matches.Single();
        EnterAll(e, match);
        Advance(e, h, 61);
        Do(e, C, "", ArenaWire.ActKo, A.ToString());
        Do(e, D, "", ArenaWire.ActKo, A.ToString());
        Do(e, E, ArenaEngine.AtOrganizer, ArenaWire.ActCharterBuy, "2\nAguias\n80");
        int rating = e.TeamOf(A, 2).Rating;

        var reopened = new ArenaEngine(new FakeHost(), new ArenaDatabase(db));
        var back = reopened.TeamOf(A, 2);
        Check("teams survive a restart with their rating", back != null && back.Rating == rating && back.Member(B) != null);
        Check("member numbers survive", back?.Member(A).WeekGames == 1);
        Check("hidden ratings survive", reopened.PlayerRecord(A).MmrOf(2, 1500) == e.PlayerRecord(A).MmrOf(2, 1500));
        Check("open charters survive", reopened.Charter(E, 2)?.Name == "Aguias");
        Check("the return point survives (logged out in the arena)", reopened.PlayerRecord(A).HasReturn);
    }
}
