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
        public readonly HashSet<long> Vips = new HashSet<long>();
        public bool IsVip(long playerId) => Vips.Contains(playerId);
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

        public int LastQuality;
        public void MailItem(long playerId, string subject, string prefab, int amount, int quality = 1)
        {
            Items.Add((playerId, prefab, amount));
            LastQuality = quality;
        }
        public void MailLetter(long playerId, string subject, string body) => Letters.Add((playerId, body));
        public readonly Dictionary<long, int> Deadcoins = new Dictionary<long, int>();
        public bool LedgerBroken;
        public bool CreditDeadcoins(long playerId, int amount, string reason, out int balance, out string error)
        {
            error = LedgerBroken ? "falha ao gravar o saldo." : null;
            Deadcoins.TryGetValue(playerId, out balance);
            if (LedgerBroken) return false;
            balance += amount;
            Deadcoins[playerId] = balance;
            return true;
        }
        public void Log(string text) => Logs.Add(text);
        public void Warn(string text) => Logs.Add("WARN " + text);

        // The challenge creature: match id -> health from 0 to 1. Killing it is removing it.
        public readonly Dictionary<long, float> Creatures = new Dictionary<long, float>();
        public readonly List<(long Match, string Prefab, Vector3 At)> Spawned = new List<(long, string, Vector3)>();
        public readonly List<long> Despawned = new List<long>();
        public bool SpawnFails;
        public int Sweeps;

        public readonly List<int> SpawnedFor = new List<int>();

        public bool SpawnCreature(long matchId, ArenaChallengeDef def, Vector3 at, float yaw, int players)
        {
            if (SpawnFails) return false;
            Creatures[matchId] = 1f;
            Spawned.Add((matchId, def.Prefab, at));
            SpawnedFor.Add(players);
            return true;
        }

        public bool CreatureAlive(long matchId) => Creatures.ContainsKey(matchId);
        public float CreatureHealth(long matchId) => Creatures.TryGetValue(matchId, out var h) ? h : -1f;

        public void DespawnCreature(long matchId)
        {
            Creatures.Remove(matchId);
            Despawned.Add(matchId);
        }

        public void SweepCreatures(IEnumerable<string> prefabs, Func<long, bool> live) => Sweeps++;

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

    /// <summary>The engine with every WoW bracket on (2, 3, 5), so 3v3 and 5v5 stay covered
    /// even though the server default is only 1v1 and 2v2.</summary>
    static ArenaTuning Tuning()
    {
        var t = new ArenaTuning { Brackets = new List<int> { 2, 3, 5 } };
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
        Dbs[engine] = db;
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
            TimersPerArena();
            ArenaHealthChecks();
            OneArenaNpc();
            ChallengeArena();
            NpcFacingChecks();
            InviteAndDesertion();
            DrawAndOffline();
            QueueRules();
            Skirmish();
            WeeklyPoints();
            Vendor();
            TeamManagement();
            Persistence();
            Challenge();
            MapUsage();
            FreeForAll();
            MatchPointsChecks();
            Bestiary();
            BestiaryKit();
            BestiarySchedule();
            Version090();
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
        var daily = new ArenaTuning { ResetIntervalHours = 24 };
        Check("daily: Thursday morning resets at 15:00 the same day",
            daily.NextResetAfter(new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc));
        Check("daily: at the reset moment the next one is a day later",
            daily.NextResetAfter(new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 10, 2, 15, 0, 0, DateTimeKind.Utc));
        var twice = new ArenaTuning { ResetIntervalHours = 12 };
        Check("every 12 h: 15:00 and 03:00",
            twice.NextResetAfter(new DateTime(2026, 10, 1, 16, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 10, 2, 3, 0, 0, DateTimeKind.Utc));
        var fortnight = new ArenaTuning { ResetIntervalHours = 336 };
        var f1 = fortnight.NextResetAfter(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        Check("every 2 weeks: on a Tuesday, then 14 days later",
            f1.DayOfWeek == DayOfWeek.Tuesday && f1.Hour == 15 && fortnight.NextResetAfter(f1) == f1.AddDays(14), f1.ToString("o"));
        Check("the interval changes the schedule key", daily.ResetKey != t.ResetKey);
        Check("the interval reads as people say it", t.IntervalText == "semanal" && daily.IntervalText == "diária" && twice.IntervalText == "a cada 12 h");
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

        // What the admin pages write must read back as what they meant, and touch nothing else.
        const string cfgMaps = "Anel;100,30,100,90;140,30,100,270;120,40,100|quebrada;1,2|Poço;0,0,0,0;10,0,0,180";
        var poco = new ArenaMapDef { Name = "poço", Gold = new Vector3(5.5f, 1, 2), GoldYaw = 45, Green = new Vector3(20, 1, 2), GreenYaw = 225 };
        string mapsAfter = ArenaSettingsParser.SetMap(cfgMaps, poco);
        var reread = ArenaSettingsParser.ParseMaps(mapsAfter, null);
        Check("saving an arena replaces the one of the same name in place",
            mapsAfter.Split('|').Length == 3 && reread.Count == 2 && reread[1].Name == "poço" && Math.Abs(reread[1].Gold.x - 5.5f) < 0.01f && !reread[1].HasSpectator,
            mapsAfter);
        Check("an entry the mod cannot read is left as written", mapsAfter.Contains("|quebrada;1,2|"), mapsAfter);
        poco.Name = "Nova|;x";
        poco.HasSpectator = true;
        poco.Spectator = new Vector3(1, 2, 3);
        mapsAfter = ArenaSettingsParser.SetMap(cfgMaps, poco);
        reread = ArenaSettingsParser.ParseMaps(mapsAfter, null);
        Check("a new arena is appended, separators out of its name, spectator kept",
            reread.Count == 3 && reread[2].Name == "Novax" && reread[2].HasSpectator && Math.Abs(reread[2].Spectator.z - 3f) < 0.01f, mapsAfter);
        mapsAfter = ArenaSettingsParser.RemoveMap(cfgMaps, "ANEL", out bool mapRemoved);
        Check("removing an arena ignores case and keeps the rest", mapRemoved && mapsAfter == "quebrada;1,2|Poço;0,0,0,0;10,0,0,180", mapsAfter);
        ArenaSettingsParser.RemoveMap(cfgMaps, "Nenhuma", out mapRemoved);
        Check("removing an arena that is not there says so", !mapRemoved);

        var kit = new ArenaOffer { Prefab = "ArmorKit1", Amount = 2, Points = 150, Rating = 1300, Bracket = 3 };
        string offersAfter = ArenaSettingsParser.SetOffer(ArenaSettingsParser.DefaultOffers, kit);
        var reoffers = ArenaSettingsParser.ParseOffers(offersAfter, null);
        var kitBack = reoffers.FirstOrDefault(o => o.Prefab == "ArmorKit1");
        Check("saving a vendor item replaces it in place",
            reoffers.Count == 10 && reoffers[1].Prefab == "ArmorKit1" && kitBack.Amount == 2 && kitBack.Points == 150 && kitBack.Rating == 1300 && kitBack.Bracket == 3,
            offersAfter);
        offersAfter = ArenaSettingsParser.SetOffer(ArenaSettingsParser.DefaultOffers, new ArenaOffer { Prefab = "Wood", Amount = 50, Points = 10 });
        Check("a new vendor item is appended without a bracket", offersAfter.EndsWith("|prefab=Wood;amount=50;points=10;rating=0") &&
            ArenaSettingsParser.ParseOffers(offersAfter, null).Count == 11, offersAfter);
        offersAfter = ArenaSettingsParser.RemoveOffer(ArenaSettingsParser.DefaultOffers, "WeaponKit1", out bool offerRemoved);
        Check("removing a vendor item", offerRemoved && ArenaSettingsParser.ParseOffers(offersAfter, null).All(o => o.Prefab != "WeaponKit1") &&
            ArenaSettingsParser.ParseOffers(offersAfter, null).Count == 9);
        ArenaSettingsParser.RemoveOffer(ArenaSettingsParser.DefaultOffers, "weaponkit1", out offerRemoved);
        Check("vendor prefabs compare exactly, as purchases look them up", !offerRemoved);

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

    // ------------------------------------------------------- admin page and one arena NPC

    static ArenaDraft Draft(string name, Vector3? gold, Vector3? green)
    {
        var d = new ArenaDraft { Name = name, Gold = gold, Green = green };
        d.SavedName = name;
        return d;
    }

    static bool Has(List<ArenaCheck> checks, ArenaFix fix) => checks.Any(c => !c.Ok && c.Fix == fix);

    /// <summary>"O que falta para funcionar": the lines the F8 page shows, and the button each
    /// one offers.</summary>
    static void ArenaHealthChecks()
    {
        Section("arena health: what is missing, and its fix");
        var zones = new List<ArenaZone>();
        var blank = new ArenaDraft { Name = "Arena 1" };
        var checks = ArenaHealth.ForArena(blank, zones, new[] { blank });
        Check("new arena: both starts missing, each with its Marcar aqui", Has(checks, ArenaFix.MarkGold) && Has(checks, ArenaFix.MarkGreen));
        Check("new arena: no zone line before the starts are marked", !checks.Any(c => c.Fix == ArenaFix.MakeZone));
        Check("new arena: serves every kind of match", checks.Any(c => c.Ok && c.Text.Contains("todos os tipos")));

        var gold = new Vector3(100, 30, 100);
        var green = new Vector3(140, 30, 100);
        var d = Draft("Coliseu", gold, green);
        checks = ArenaHealth.ForArena(d, zones, new[] { d });
        Check("both marked but outside every PvP area: Criar área em volta", Has(checks, ArenaFix.MakeZone) && checks.Count(c => !c.Ok) == 1,
            string.Join(" / ", checks));
        Check("without Deadheim there is no area to ask for", ArenaHealth.Ready(d, null, new[] { d }));

        var zone = ArenaHealth.ZoneAround("Coliseu", gold, green);
        Check("the area made around holds both starts", zone.Contains(gold) && zone.Contains(green) && zone.Name == "Coliseu");
        Check("centred between the starts, 25 m past them", Math.Abs(zone.X - 120) < 0.01f && Math.Abs(zone.Z - 100) < 0.01f && Math.Abs(zone.Radius - 45) < 0.01f,
            $"{zone.X},{zone.Z} r{zone.Radius}");
        Check("never under 30 m", Math.Abs(ArenaHealth.ZoneAround("x", gold, gold + new Vector3(2, 0, 0)).Radius - 30) < 0.01f);
        Check("never over the 300 m the zones allow", ArenaHealth.ZoneAround("x", gold, gold + new Vector3(900, 0, 0)).Radius <= ArenaSettingsParser.MaxZoneRadius);
        zones.Add(zone);
        Check("inside the area: the arena is ready", ArenaHealth.Ready(d, zones, new[] { d }), string.Join(" / ", ArenaHealth.ForArena(d, zones, new[] { d })));

        var far = Draft("Longe", gold, gold + new Vector3(350, 0, 0));
        Check("starts over 300 m apart: remark the Green", Has(ArenaHealth.ForArena(far, null, new[] { far }), ArenaFix.MarkGreen));
        var stacked = Draft("Junto", gold, gold + new Vector3(1, 0, 0));
        Check("starts on the same spot (Green marked without walking): remark the Green",
            Has(ArenaHealth.ForArena(stacked, null, new[] { stacked }), ArenaFix.MarkGreen));
        var none = Draft("Nada", gold, green);
        for (int i = 0; i < none.Types.Length; i++) none.Types[i] = false;
        Check("no box marked: Marcar tudo", Has(ArenaHealth.ForArena(none, zones, new[] { none }), ArenaFix.AllTypes));
        var off = Draft("Desligada", gold, green);
        off.On = false;
        Check("switched off: Ligar", Has(ArenaHealth.ForArena(off, zones, new[] { off }), ArenaFix.TurnOn));
        var twin = Draft("coliseu", gold, green);
        Check("two arenas of the same name: change it", Has(ArenaHealth.ForArena(twin, zones, new[] { d, twin }), ArenaFix.Name));
        var nameless = Draft(" ", gold, green);
        Check("no name: Dar nome", Has(ArenaHealth.ForArena(nameless, zones, new[] { nameless }), ArenaFix.Name));

        // A draft as the cfg block reads, and back to the arena the server is sent.
        var fromCfg = ArenaDraft.FromSlot(2, true, " Fosso ", "400,30,100,90", "440,30,100,270", "", new[] { false, true, false, false, true });
        Check("a Local block reads into a draft", fromCfg.Name == "Fosso" && fromCfg.Gold == new Vector3(400, 30, 100) && fromCfg.GreenYaw == 270 &&
                                                fromCfg.Spectator == null && fromCfg.Serves(1) && fromCfg.ServesChallenge && !fromCfg.Serves(0));
        var map = fromCfg.ToMap();
        Check("two boxes: the arena takes 2v2 and challenges only",
            map.ServesPvp(2) && !map.ServesPvp(1) && !map.ServesPvp(ArenaRules.FfaSize) && map.ServesPve);
        var parsed = ArenaSettingsParser.ParseMaps(ArenaSettingsParser.FormatMap(map), null);
        Check("and survives the wire", parsed.Count == 1 && parsed[0].ServesPvp(2) && !parsed[0].ServesPvp(3) && parsed[0].ServesPve);
        Check("every box: no restriction at all", !d.ToMap().Restricted);
        Check("a start missing: nothing to send", blank.ToMap() == null);
        Check("a broken point in the cfg reads as not marked", ArenaDraft.FromSlot(0, true, "X", "abc", "1,2,3", "", null).Gold == null);

        Section("challenge health");
        var pveOnly = Draft("Fosso", new Vector3(400, 30, 100), new Vector3(440, 30, 100));
        for (int i = 0; i < 4; i++) pveOnly.Types[i] = false;
        var pvpOnly = Draft("Anel", gold, green);
        pvpOnly.Types[ArenaHealth.PveType] = false;
        var all = new List<ArenaDraft> { pvpOnly, pveOnly };
        var ready = new List<ArenaDraft>(all);
        Func<string, string> known = c => c == "bestiario:sumido" ? "\"sumido\" não está no Bestiário" : null;
        Func<string, bool> item = p => p != "Inventado";

        var ch = new ChallengeDraft { On = true, Name = "Troll", Creature = "Troll", Reward = "Coins", Amount = 300 };
        var chChecks = ArenaHealth.ForChallenge(ch, all, ready, known, item);
        Check("any arena: fine while one ready arena takes challenges", chChecks.All(c => c.Ok) && chChecks.Any(c => c.Text.Contains("Fosso")),
            string.Join(" / ", chChecks));
        ch.Map = "Anel";
        chChecks = ArenaHealth.ForChallenge(ch, all, ready, known, item);
        var serve = chChecks.FirstOrDefault(c => !c.Ok);
        Check("its arena does not take challenges: mark it there",
            serve != null && serve.Fix == ArenaFix.ServeType && serve.Target == "Anel" && serve.Type == ArenaHealth.PveType);
        ch.Map = "Sumida";
        Check("its arena is gone: pick another", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickArena));
        ch.Map = "Fosso";
        Check("its arena exists but does not work yet: open it",
            Has(ArenaHealth.ForChallenge(ch, all, new List<ArenaDraft> { pvpOnly }, known, item), ArenaFix.OpenArena));
        ch.Map = "";
        Check("no arena takes challenges: mark the first ready one",
            ArenaHealth.ForChallenge(ch, new List<ArenaDraft> { pvpOnly }, new List<ArenaDraft> { pvpOnly }, known, item)
                .Any(c => !c.Ok && c.Fix == ArenaFix.ServeType && c.Target == "Anel"));
        Check("no arena at all: Criar arena",
            Has(ArenaHealth.ForChallenge(ch, new List<ArenaDraft>(), new List<ArenaDraft>(), known, item), ArenaFix.NewArena));
        ch.Creature = "bestiario:sumido";
        Check("a creature the Bestiário lost: pick another", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickCreature));
        ch.Creature = "bestiario:troll_anciao";
        Check("a creature of the Bestiário it has: fine", !Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickCreature));
        ch.Creature = " ";
        Check("no creature: pick one", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickCreature));
        ch.Creature = "Troll";
        ch.Reward = "Inventado";
        Check("a reward the game does not have: change it", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickReward));
        ch.Reward = "Coins";
        ch.Extras = "TrophyTroll:abc";
        Check("a badly written extra: fix it", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickReward));
        ch.Extras = "TrophyTroll:1, Inventado";
        Check("an extra the game does not have: fix it", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.PickReward));
        ch.Extras = "TrophyTroll:1";
        ch.On = false;
        Check("switched off: Ligar", Has(ArenaHealth.ForChallenge(ch, all, ready, known, item), ArenaFix.TurnOn));

        Section("the server as a whole");
        Check("no arena: every queue refuses, Criar arena",
            Has(ArenaHealth.Overall(new List<ArenaDraft>(), new List<ArenaDraft>(), new[] { 1, 2 }, true, true), ArenaFix.NewArena));
        Check("arenas but none ready: open one",
            Has(ArenaHealth.Overall(all, new List<ArenaDraft>(), new[] { 1, 2 }, true, true), ArenaFix.OpenArena));
        var only1v1 = Draft("Duelo", gold, green);
        for (int i = 1; i < only1v1.Types.Length; i++) only1v1.Types[i] = false;
        var one = new List<ArenaDraft> { only1v1 };
        var overall = ArenaHealth.Overall(one, one, new[] { 1, 2 }, true, true);
        Check("a 1v1-only arena leaves 2v2, FFA and challenges without one",
            overall.Count(c => !c.Ok && c.Fix == ArenaFix.ServeType && c.Target == "Duelo") == 3 && overall.Any(c => c.Ok && c.Text.StartsWith("1v1")),
            string.Join(" / ", overall));
        Check("FFA and challenges off: not asked for", ArenaHealth.Overall(one, one, new[] { 1 }, false, false).All(c => c.Ok));
        Check("5v5 has no box: not asked for", ArenaHealth.Overall(one, one, new[] { 1, 5 }, false, false).All(c => c.Ok));
    }

    /// <summary>One arena NPC: the Organizer and the Intendente placed before 0.1.89 open the
    /// Mestre da Arena's tabs, and the server takes everything through any of the three.</summary>
    static void OneArenaNpc()
    {
        Section("one arena NPC; the old ones keep working");
        var tabs = NpcValheim.Npc.ArenaDesk.TabsOf(ArenaEngine.AtBattlemaster);
        Check("the Mestre da Arena has every tab", string.Join(",", tabs) == "Arena,Times,Criar time,Ranking,Loja");
        Check("the old Organizer opens the same tabs", NpcValheim.Npc.ArenaDesk.TabsOf(ArenaEngine.AtOrganizer).SequenceEqual(tabs));
        Check("the old Intendente opens the same tabs", NpcValheim.Npc.ArenaDesk.TabsOf(ArenaEngine.AtVendor).SequenceEqual(tabs));
        Check("no NPC (the H panel): no NPC tabs", NpcValheim.Npc.ArenaDesk.TabsOf("").Length == 0);
        Check("the Hammer only places the Mestre da Arena",
            NpcValheim.Npc.ArenaDesk.OnHammer(NpcValheim.Npc.ArenaDesk.Prefab) &&
            NpcValheim.Npc.ArenaDesk.LegacyPrefabs.All(p => !NpcValheim.Npc.ArenaDesk.OnHammer(p)));

        var (e, h, _) = NewEngine();
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActQueueJoin, "1\n0\n");
        Check("the queue through the old Intendente", e.QueueOf(A) != null);
        Do(e, B, ArenaEngine.AtOrganizer, ArenaWire.ActFfaJoin);
        Check("the FFA through the old Organizer", e.QueueOf(B) != null || e.MatchOf(B) != null);
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActCharterBuy, "2\nLeoes\n80");
        Check("a charter at the Mestre da Arena", e.Charter(C, 2) != null);
        Do(e, D, "", ArenaWire.ActCharterBuy, "2\nUrsos\n80");
        Check("still not from the H panel", e.Charter(D, 2) == null);
        e.PlayerRecord(A).Points = 500;
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActVendorBuy, "ArmorKit1\n100");
        Check("the shop at the Mestre da Arena", h.Items.Any(i => i.To == A && i.Prefab == "ArmorKit1"));
    }

    /// <summary>A challenge that names its arena only goes there; the admin's Testar agora
    /// calls it from anywhere.</summary>
    static void ChallengeArena()
    {
        Section("a challenge in its own arena");
        var t = ChallengeTuning();
        t.Maps.Add(new ArenaMapDef { Name = "Fosso", Gold = new Vector3(400, 30, 100), Green = new Vector3(440, 30, 100) });
        t.Challenges[0].Map = "Fosso";
        var (e, h, _) = NewEngine(t);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("it goes to its arena", e.MatchOf(A)?.Map.Name == "Fosso");
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("its arena taken: busy, even with another arena free", e.MatchOf(B) == null && h.Got(B, ArenaWire.Alert, "ocupadas"));

        t = ChallengeTuning();
        t.Challenges[0].Map = "Sumida";
        (e, h, _) = NewEngine(t);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("its arena does not exist: refused, naming it", e.MatchOf(A) == null && h.Got(A, ArenaWire.Alert, "Sumida"));

        (e, h, _) = NewEngine(ChallengeTuning());
        Do(e, A, "", ArenaWire.ActAdminChallengeTest, "Troll Furioso\n");
        Check("Testar agora: refused to a player", e.MatchOf(A) == null);
        Do(e, A, "", ArenaWire.ActAdminChallengeTest, "Troll Furioso\n", admin: true);
        Check("Testar agora: an admin, from the F8 page", e.MatchOf(A)?.IsChallenge == true);
    }

    /// <summary>Where an NPC faces, the head turn, the plate and the idle clock (NpcFacing),
    /// and the one-time removal of the old arena NPCs (ArenaNpcMigration.Plan).</summary>
    static void NpcFacingChecks()
    {
        Section("NPC facing and presence");
        Check("normalize: negative and past 360", Math.Abs(NpcValheim.Npc.NpcFacing.Normalize(-15) - 345) < 0.01f &&
                                                  Math.Abs(NpcValheim.Npc.NpcFacing.Normalize(375) - 15) < 0.01f &&
                                                  NpcValheim.Npc.NpcFacing.Normalize(360) == 0f && NpcValheim.Npc.NpcFacing.Normalize(float.NaN) == 0f);
        var npc = new Vector3(10, 0, 10);
        Check("toward: north is 0", Math.Abs(NpcValheim.Npc.NpcFacing.Toward(npc, new Vector3(10, 0, 20), 77)) < 0.01f);
        Check("toward: east is 90", Math.Abs(NpcValheim.Npc.NpcFacing.Toward(npc, new Vector3(20, 0, 10), 0) - 90) < 0.01f);
        Check("toward: west is 270", Math.Abs(NpcValheim.Npc.NpcFacing.Toward(npc, new Vector3(0, 5, 10), 0) - 270) < 0.01f);
        Check("toward: on the same spot keeps the facing", Math.Abs(NpcValheim.Npc.NpcFacing.Toward(npc, npc + new Vector3(0, 2, 0), 123) - 123) < 0.01f);
        Check("away: the opposite", Math.Abs(NpcValheim.Npc.NpcFacing.Away(npc, new Vector3(10, 0, 20), 0) - 180) < 0.01f);
        Check("delta: the short way round", Math.Abs(NpcValheim.Npc.NpcFacing.Delta(350, 10) - 20) < 0.01f &&
                                             Math.Abs(NpcValheim.Npc.NpcFacing.Delta(10, 350) + 20) < 0.01f);
        Check("steps of 15 from 350 wrap to 5", Math.Abs(NpcValheim.Npc.NpcFacing.Normalize(350 + 15) - 5) < 0.01f);

        var head = new Vector3(0, 1.7f, 0);
        var (yaw, pitch) = NpcValheim.Npc.NpcFacing.HeadTurn(head, 0, new Vector3(1, 1.7f, 1));
        Check("head: 45 degrees to the right, level", Math.Abs(yaw - 45) < 0.1f && Math.Abs(pitch) < 0.1f, $"{yaw} {pitch}");
        (yaw, _) = NpcValheim.Npc.NpcFacing.HeadTurn(head, 0, new Vector3(5, 1.7f, 0.5f));
        Check("head: no further than 65 degrees", Math.Abs(yaw - 65) < 0.1f, yaw.ToString());
        (yaw, pitch) = NpcValheim.Npc.NpcFacing.HeadTurn(head, 0, new Vector3(0, 1.7f, -3));
        Check("head: someone behind is not looked at", yaw == 0 && pitch == 0);
        (_, pitch) = NpcValheim.Npc.NpcFacing.HeadTurn(head, 0, new Vector3(0, 6, 1));
        Check("head: looks up, at most 25 degrees", Math.Abs(pitch - 25) < 0.1f, pitch.ToString());

        Check("idle: within 40 to 90 s", Math.Abs(NpcValheim.Npc.NpcFacing.NextIdle(100, 0) - 140) < 0.01f &&
                                         Math.Abs(NpcValheim.Npc.NpcFacing.NextIdle(100, 1) - 190) < 0.01f);
        Check("idle: due with someone near", NpcValheim.Npc.NpcFacing.IdleDue(200, 190, 12, false));
        Check("idle: not before its time", !NpcValheim.Npc.NpcFacing.IdleDue(180, 190, 12, false));
        Check("idle: not with nobody to see it", !NpcValheim.Npc.NpcFacing.IdleDue(200, 190, 25, false));
        Check("idle: not over its open window", !NpcValheim.Npc.NpcFacing.IdleDue(200, 190, 3, true));

        Check("plate: a named NPC shows what it does", NpcValheim.Npc.NpcFacing.Titled("Bjorn", "Mestre da Arena").StartsWith("Bjorn ") &&
                                                        NpcValheim.Npc.NpcFacing.Titled("Bjorn", "Mestre da Arena").Contains("Mestre da Arena"));
        Check("plate: still called by its role, no repeat", NpcValheim.Npc.NpcFacing.Titled("Mestre da Arena", "Mestre da Arena") == "Mestre da Arena");
        Check("plate: an unnamed NPC shows its role", NpcValheim.Npc.NpcFacing.Titled("NPC", "Barbeiro") == "Barbeiro");

        Section("the old arena NPCs leave the world");
        var M = NpcValheim.Npc.ArenaDesk.Prefab;
        string O = NpcValheim.Npc.ArenaDesk.LegacyPrefabs[0], V = NpcValheim.Npc.ArenaDesk.LegacyPrefabs[1];
        NpcValheim.Npc.ArenaNpcMigration.Spot S(string prefab, float x) => new NpcValheim.Npc.ArenaNpcMigration.Spot { Prefab = prefab, Position = new Vector3(x, 30, 0) };
        var steps = NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(O, 0), S(M, 4), S(V, 8) });
        Check("the three side by side: the two old ones are spares",
            steps.SequenceEqual(new[] { NpcValheim.Npc.ArenaNpcMigration.Step.Delete, NpcValheim.Npc.ArenaNpcMigration.Step.Keep, NpcValheim.Npc.ArenaNpcMigration.Step.Delete }),
            string.Join(",", steps));
        steps = NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(O, 0), S(V, 5) });
        Check("no Mestre: the first becomes one, the one beside it goes",
            steps.SequenceEqual(new[] { NpcValheim.Npc.ArenaNpcMigration.Step.Convert, NpcValheim.Npc.ArenaNpcMigration.Step.Delete }),
            string.Join(",", steps));
        steps = NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(M, 0), S(V, 200) });
        Check("an Intendente alone across town becomes a Mestre (nobody loses the shop)",
            steps.SequenceEqual(new[] { NpcValheim.Npc.ArenaNpcMigration.Step.Keep, NpcValheim.Npc.ArenaNpcMigration.Step.Convert }));
        Check("nothing old: nothing to do", NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(M, 0), S(M, 3) })
            .All(x => x == NpcValheim.Npc.ArenaNpcMigration.Step.Keep));
        Check("15 m is the edge", NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(M, 0), S(O, 15) })[1] == NpcValheim.Npc.ArenaNpcMigration.Step.Delete &&
                                  NpcValheim.Npc.ArenaNpcMigration.Plan(new[] { S(M, 0), S(O, 15.5f) })[1] == NpcValheim.Npc.ArenaNpcMigration.Step.Convert);
    }

    /// <summary>Two arenas, two matches made 20 s apart: each counts its own preparation from
    /// its own first entry, so the second opens 20 s after the first, not with it.</summary>
    static void TimersPerArena()
    {
        Section("each match keeps its own clock");
        var tuning = Tuning();
        tuning.Maps.Add(new ArenaMapDef
        {
            Name = "Fosso", Gold = new Vector3(400, 30, 100), GoldYaw = 90, Green = new Vector3(440, 30, 100), GreenYaw = 270,
        });
        var (e, h, _) = NewEngine(tuning);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        var first = e.MatchOf(A);
        EnterAll(e, first);
        Advance(e, h, 20);
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Do(e, D, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        var second = e.MatchOf(C);
        Check("two matches in two arenas", first != null && second != null && first != second && first.Map.Name != second.Map.Name);
        if (first == null || second == null) return;
        EnterAll(e, second);
        Advance(e, h, 41);
        Check("the first opens after its own preparation", first.Status == MatchStatus.InProgress);
        Check("the second is still preparing", second.Status == MatchStatus.WaitJoin, second.Status.ToString());
        Advance(e, h, 20);
        Check("the second opens 20 s later", second.Status == MatchStatus.InProgress);
        double gap = second.StartedAt - first.StartedAt;
        Check("start times 20 s apart", Math.Abs(gap - 20) <= 1.0, "gap " + gap);
    }

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
        Do(e, A, "", ArenaWire.ActEnter);
        Advance(e, h, 20);
        Check("one accepted, three still deciding: the countdown has not started", match.PrepEndsAt <= 0);
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
        Check("the countdown waited for B's answer: it starts only now", match.Status == MatchStatus.WaitJoin && match.PrepEndsAt > 0);
        Advance(e, h, 61);
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

    // ------------------------------------------------------------------- arena usage

    static ArenaMapDef UsageMap(string name, string usage, float x)
    {
        var map = new ArenaMapDef { Name = name, Gold = new Vector3(x, 30, 100), Green = new Vector3(x + 40, 30, 100), GreenYaw = 180 };
        ArenaSettingsParser.TryUsage(usage, map, out _);
        return map;
    }

    static void MapUsage()
    {
        Section("arena usage (uso=)");
        var problems = new List<string>();
        var maps = ArenaSettingsParser.ParseMaps(
            "Velha;1,2,3,0;10,2,3,180|Duelo;1,2,3,0;10,2,3,180;uso=1v1,x2|Covil;1,2,3,0;10,2,3,180;uso=pve;5,6,7|" +
            "Campo;1,2,3,0;10,2,3,180;4,5,6;uso=pvp,desafio|Tudo;1,2,3,0;10,2,3,180;uso=todos|Errada;1,2,3,0;10,2,3,180;uso=pev|" +
            "Torta;1,2,3,0;10,2,3,180;uso=2v3", problems);
        ArenaMapDef M(string n) => maps.FirstOrDefault(m => m.Name == n);
        Check("an arena without uso serves everything", M("Velha") != null && !M("Velha").Restricted && M("Velha").ServesPvp(5) && M("Velha").ServesPve);
        Check("uso=1v1,x2 serves 1v1 and 2v2 only, no PvE",
            M("Duelo") != null && M("Duelo").ServesPvp(1) && M("Duelo").ServesPvp(2) && !M("Duelo").ServesPvp(3) && !M("Duelo").ServesPve);
        Check("uso before the spectator spot still reads both", M("Covil") != null && M("Covil").Pve && !M("Covil").ServesPvp(2) && M("Covil").HasSpectator);
        Check("pvp = every size; desafio = pve", M("Campo") != null && M("Campo").ServesPvp(7) && M("Campo").ServesPve && M("Campo").HasSpectator);
        Check("uso=todos is the same as nothing", M("Tudo") != null && !M("Tudo").Restricted);
        Check("an unknown word or an uneven size refuses the arena, by name",
            M("Errada") == null && M("Torta") == null && problems.Count == 2 && problems.Any(p => p.Contains("Errada")) && problems.Any(p => p.Contains("Torta")),
            string.Join(" / ", problems));

        var round = ArenaSettingsParser.ParseMaps(ArenaSettingsParser.FormatMap(M("Duelo")) + "|" + ArenaSettingsParser.FormatMap(M("Velha")), null);
        Check("the usage is written back and read the same", round.Count == 2 && round[0].UsageText == "1v1,2v2" && !round[1].Restricted,
            ArenaSettingsParser.FormatMap(M("Duelo")));
        Check("an unrestricted arena writes no uso", !ArenaSettingsParser.FormatMap(M("Velha")).Contains("uso="));

        // The queues only use arenas of their usage.
        var t = ChallengeTuning();
        t.Maps.Clear();
        t.Maps.Add(UsageMap("Covil", "pve", 100));
        t.Maps.Add(UsageMap("Duelo", "1v1", 300));
        var (e, h, _) = NewEngine(t);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Check("2v2 with no arena for it is refused at the door", e.QueueOf(A) == null && h.Got(A, ArenaWire.Alert, "2v2"));
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        var duel = e.MatchOf(A);
        Check("a 1v1 goes to the 1v1 arena, not the PvE one", duel != null && duel.Map.Name == "Duelo");
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        var challenge = e.MatchOf(C);
        Check("a challenge goes to the PvE arena", challenge != null && challenge.IsChallenge && challenge.Map.Name == "Covil");
        Do(e, D, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Do(e, E, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Check("with the 1v1 arena busy, the next 1v1 waits instead of taking the free PvE arena",
            e.MatchOf(D) == null && e.QueueOf(D) != null);

        var noPve = Tuning();
        noPve.Maps.Clear();
        noPve.Maps.Add(UsageMap("Duelo", "1v1,2v2,3v3,5v5", 300));
        noPve.Challenges = ChallengeTuning().Challenges;
        noPve.FfaEnabled = false;
        var gaps = ArenaSettingsParser.UsageProblems(noPve);
        Check("the server names a usage gap: no PvE arena for the challenges", gaps.Count == 1 && gaps[0].Contains("pve"), string.Join(" / ", gaps));
        noPve.Maps[0] = UsageMap("Duelo", "1v1,4v4,pve", 300);
        gaps = ArenaSettingsParser.UsageProblems(noPve);
        Check("and brackets with no arena, and a size nobody plays",
            gaps.Count == 4 && gaps.Count(g => g.Contains("nenhuma arena com uso")) == 3 && gaps.Any(g => g.Contains("4v4")), string.Join(" / ", gaps));
    }

    // ------------------------------------------------------------------- free-for-all

    static ArenaTuning FfaTuning(params ArenaMapDef[] maps)
    {
        var t = Tuning();
        t.Maps.Clear();
        t.Maps.AddRange(maps.Length > 0 ? maps : new[] { UsageMap("Poço", "ffa", 100) });
        t.FfaMinPlayers = 3;
        t.FfaMaxPlayers = 4;
        t.FfaWaitSeconds = 60f;
        return t;
    }

    static void FreeForAll()
    {
        Section("FFA: everyone against everyone");
        Check("the server default is 1v1, 2v2 and 3v3", string.Join(",", new ArenaTuning().ActiveBrackets) == "1,2,3");
        Check("the FFA goes by its own name", ArenaRules.BracketName(ArenaRules.FfaSize) == "FFA");

        var poco = UsageMap("Poço", "ffa", 100);
        Check("sides 1 and 2 stand on the starts", poco.SpawnFor(1) == poco.Gold && poco.SpawnFor(2) == poco.Green);
        Check("odd sides share Gold, even ones Green, each a step aside inside the gate",
            Vector3.Distance(poco.SpawnFor(3), poco.Gold) > 2f && Vector3.Distance(poco.SpawnFor(3), poco.Gold) < 3f &&
            Vector3.Distance(poco.SpawnFor(4), poco.Green) > 2f && Vector3.Distance(poco.SpawnFor(5), poco.SpawnFor(3)) > 1f &&
            poco.YawOf(4) == poco.GreenYaw);

        var maps = ArenaSettingsParser.ParseMaps("A;1,2,3,0;10,2,3,180;uso=ffa,pve|B;1,2,3,0;10,2,3,180;uso=pvp|C;1,2,3,0;10,2,3,180;uso=2v2", null);
        Check("uso=ffa serves the FFA and nothing else PvP", maps[0].ServesPvp(ArenaRules.FfaSize) && !maps[0].ServesPvp(2) && maps[0].ServesPve);
        Check("pvp includes the FFA; 2v2 alone does not", maps[1].ServesPvp(ArenaRules.FfaSize) && !maps[2].ServesPvp(ArenaRules.FfaSize));
        Check("ffa is written back", ArenaSettingsParser.FormatMap(maps[0]).EndsWith(";uso=ffa,pve"), ArenaSettingsParser.FormatMap(maps[0]));

        var (e, h, _) = NewEngine(FfaTuning(UsageMap("Duelo", "1v1", 300)));
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Check("no arena with uso ffa: refused at the door", e.QueueOf(A) == null && h.Got(A, ArenaWire.Alert, "FFA"));

        (e, h, _) = NewEngine(FfaTuning());
        Do(e, A, "", ArenaWire.ActFfaJoin);
        Check("only at the Battlemaster", e.QueueOf(A) == null);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Check("two in line wait for the minimum", e.QueueOf(A) != null && e.QueueOf(A).Size == ArenaRules.FfaSize && !e.Matches.Any());
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Advance(e, h, 30);
        Check("three, before WaitSeconds: still waiting for more", !e.Matches.Any());
        Advance(e, h, 31);
        var m = e.Matches.SingleOrDefault();
        Check("three after WaitSeconds: the match is made", m != null && m.IsFfa && !m.Rated && m.Seats.Count == 3);
        Check("each one is a side of their own", m != null && m.Seats.Select(s => s.Side).Distinct().Count() == 3 && m.Seats.All(s => s.Side >= 1 && s.Side <= 3));

        EnterAll(e, m);
        Check("they come in at their own spots", h.Sent.Count(s => s.Kind == ArenaWire.Enter) == 3);
        Advance(e, h, 61);
        Check("the gates open", m.Status == MatchStatus.InProgress);
        Do(e, A, "", ArenaWire.ActKo, B.ToString());
        Check("one down, two standing: still on", m.Status == MatchStatus.InProgress && m.Seat(B).Kos == 1);
        Do(e, C, "", ArenaWire.ActKo, B.ToString());
        Check("the last one standing wins", m.Status == MatchStatus.WaitLeave && m.Winner == m.Seat(B).Side);
        Check("the winner hears Vitória, the others Derrota",
            h.Got(B, ArenaWire.Alert, "Vitória") && h.Got(A, ArenaWire.Alert, "Derrota") && h.Got(C, ArenaWire.Alert, "Derrota"));
        Check("no rating, no team", e.Teams.Count() == 0);

        (e, h, _) = NewEngine(FfaTuning());
        foreach (var p in new[] { A, B, C, D, E }) Do(e, p, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        m = e.Matches.SingleOrDefault();
        Check("MaxPlayers in line: the match is made at once, with the first four", m != null && m.Seats.Count == 4 && m.Seat(E) == null && e.QueueOf(E) != null);
        Do(e, F, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Do(e, G, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Advance(e, h, 30);
        Check("with the only FFA arena busy, the next three wait", e.Matches.Count() == 1 && e.QueueOf(G) != null);

        var off = FfaTuning();
        off.FfaEnabled = false;
        (e, h, _) = NewEngine(off);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Check("FFA off: refused", e.QueueOf(A) == null && h.Got(A, ArenaWire.Alert, "desligado"));
        Check("the server names an FFA with no arena", ArenaSettingsParser.UsageProblems(FfaTuning(UsageMap("Duelo", "1v1,2v2,3v3,5v5", 0))).Any(g => g.Contains("ffa")));
    }

    // ------------------------------------------------------------------- points per match

    static void MatchPointsChecks()
    {
        Section("Arena Points per match (PorPartida)");
        var problems = new List<string>();
        var parsed = ArenaSettingsParser.ParseMatchPoints("x1=15/5|2v2=20|FFA=25/5|desafio=10/0|3v4=1/1|pve=abc|ffa=-1/0", problems);
        Check("modes read in any spelling; a bare number pays both", parsed.Count == 4 && parsed["1v1"] == (15, 5) && parsed["2v2"] == (20, 20) &&
                                                                      parsed["ffa"] == (25, 5) && parsed["pve"] == (10, 0));
        Check("bad entries are named and left out", problems.Count == 3, string.Join(" / ", problems));
        Check("written back in the usual order",
            ArenaSettingsParser.FormatMatchPoints(parsed) == "1v1=15/5|2v2=20/20|ffa=25/5|pve=10/0", ArenaSettingsParser.FormatMatchPoints(parsed));
        Check("the default line reads whole", ArenaSettingsParser.ParseMatchPoints(ArenaSettingsParser.DefaultMatchPoints, null).Count == 5);

        // 1v1 skirmish: the winner gets the win, the loser the loss.
        var t = Tuning();
        t.MatchPoints = ArenaSettingsParser.ParseMatchPoints("1v1=15/5", null);
        t.MatchPointsDailyCap = 25;
        var (e, h, _) = NewEngine(t);
        ArenaMatch Duel()
        {
            Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
            Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
            var duel = e.MatchOf(A);
            EnterAll(e, duel);
            Advance(e, h, 61);
            Do(e, B, "", ArenaWire.ActKo, A.ToString());
            Advance(e, h, 125);
            return duel;
        }
        Duel();
        Check("winner +15, loser +5", e.PlayerRecord(A).Points == 15 && e.PlayerRecord(B).Points == 5,
            $"A={e.PlayerRecord(A).Points} B={e.PlayerRecord(B).Points}");
        Duel();
        Check("the daily cap stops the win at 25", e.PlayerRecord(A).Points == 25 && e.PlayerRecord(B).Points == 10,
            $"A={e.PlayerRecord(A).Points} B={e.PlayerRecord(B).Points}");
        h.Utc = h.Utc.AddDays(1);
        Duel();
        Check("a new UTC day, a new cap", e.PlayerRecord(A).Points == 40, $"A={e.PlayerRecord(A).Points}");

        // A walk-over (the other side leaves, nobody knocked out) pays the winner the loss.
        h.Utc = h.Utc.AddDays(1);
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "1\n0\n");
        EnterAll(e, e.MatchOf(A));
        Advance(e, h, 61);
        Do(e, B, "", ArenaWire.ActLeave);
        Advance(e, h, 125);
        Check("a win with nobody knocked out pays the loss (+5), the one who left 0",
            e.PlayerRecord(A).Points == 45 && e.PlayerRecord(B).Points == 15, $"A={e.PlayerRecord(A).Points} B={e.PlayerRecord(B).Points}");

        // Someone who walks out before the end gets nothing; a mode left out pays nothing.
        t = FfaTuning();
        t.MatchPoints = ArenaSettingsParser.ParseMatchPoints("ffa=25/5", null);
        (e, h, _) = NewEngine(t);
        foreach (var p in new[] { A, B, C }) Do(e, p, ArenaEngine.AtBattlemaster, ArenaWire.ActFfaJoin);
        Advance(e, h, 61);
        var m = e.Matches.Single();
        EnterAll(e, m);
        Advance(e, h, 61);
        Do(e, C, "", ArenaWire.ActLeave);
        Do(e, A, "", ArenaWire.ActKo, B.ToString());
        Check("FFA: winner 25, the knocked-out who stayed 5, the one who left 0",
            e.PlayerRecord(B).Points == 25 && e.PlayerRecord(A).Points == 5 && e.PlayerRecord(C).Points == 0,
            $"A={e.PlayerRecord(A).Points} B={e.PlayerRecord(B).Points} C={e.PlayerRecord(C).Points}");
    }

    // ------------------------------------------------------------------- points

    static void WeeklyPoints()
    {
        Section("no periodic payout: points come only from matches");
        var (e, h, _) = NewEngine();
        var lobos = MakeTeam(e, A, B, "Lobos");
        lobos.WeekGames = 10;
        lobos.Rating = 1500;
        lobos.Member(A).WeekGames = 10;
        lobos.Member(B).WeekGames = 2;

        e.ResetWeek();
        Check("the weekly reset pays nothing", (e.PlayerRecord(A)?.Points ?? 0) == 0 && (e.PlayerRecord(B)?.Points ?? 0) == 0);
        Check("and sends no letter", h.Letters.Count == 0);
        Check("every team's week starts over", lobos.WeekGames == 0 && lobos.Member(A).WeekGames == 0);
        Check("the season stays", lobos.Rating == 1500);

        Section("the weekly clock");
        var (e2, h2, db2) = NewEngine();
        e2.Tick();
        Check("the next reset is scheduled", e2.State.NextResetUtcTicks == new DateTime(2026, 10, 6, 15, 0, 0, DateTimeKind.Utc).Ticks);
        var team = MakeTeam(e2, A, B, "Lobos");
        team.WeekGames = 10;
        team.Member(A).WeekGames = 10;
        h2.Utc = new DateTime(2026, 10, 6, 15, 0, 1, DateTimeKind.Utc);
        Advance(e2, h2, 31, 1);
        Check("Tuesday 15:00 UTC: the week starts over, nothing paid", team.WeekGames == 0 && (e2.PlayerRecord(A)?.Points ?? 0) == 0);
        Check("and the next one is a week later", e2.State.NextResetUtcTicks == new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Utc).Ticks);

        var reopened = new ArenaEngine(new FakeHost(), new ArenaDatabase(db2));
        Check("a restart keeps the clock", reopened.State.NextResetUtcTicks == e2.State.NextResetUtcTicks);
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

        Section("Deadcoins for Arena Points");
        var t = Tuning();
        t.Offers = ArenaSettingsParser.ParseOffers("prefab=deadcoins;amount=10;points=150", null);
        e.SetTuning(t);
        Check("prefab=Deadcoins (any case) is a Deadcoins offer", t.Offers.Single().IsDeadcoins);
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "Deadcoins\n150");
        Check("bought: 150 points become 10 Deadcoins, nothing in the mail",
            e.PlayerRecord(A).Points == 50 && h.Deadcoins[A] == 10 && h.Items.Count == 2, $"points {e.PlayerRecord(A).Points}");
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "Deadcoins\n150");
        Check("not enough points: no Deadcoins", e.PlayerRecord(A).Points == 50 && h.Deadcoins[A] == 10);
        e.PlayerRecord(A).Points = 500;
        h.LedgerBroken = true;
        Do(e, A, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "Deadcoins\n150");
        Check("a ledger that refuses takes no points", e.PlayerRecord(A).Points == 500 && h.Deadcoins[A] == 10);
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

    static ArenaTuning ChallengeTuning()
    {
        var t = Tuning();
        var problems = new List<string>();
        t.Challenges = new List<ArenaChallengeDef>
        {
            ArenaSettingsParser.MakeChallenge("Desafio 1", "Troll Furioso", "Troll", 3, 2f, "Coins", 300, "TrophyFrostTroll:1", problems),
        };
        t.ChallengeMaxPlayers = 3;
        t.ChallengeTimeLimitSeconds = 600f;
        t.ChallengeCooldownSeconds = 3600f;
        return t;
    }

    /// <summary>The leader calls the challenge with the group; each member's client confirms.</summary>
    static void CallChallenge(ArenaEngine e, long leader, params long[] members)
    {
        Do(e, leader, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin,
            "Troll Furioso\n" + string.Join(",", members.Select(m => (m + 1000000).ToString())));
        var pending = e.PendingOf(leader);
        if (pending != null)
            foreach (var m in members) Do(e, m, "", ArenaWire.ActGroupConfirm, $"{pending.Ticket}\n1");
    }

    static ArenaMatch StartChallenge(ArenaEngine e, FakeHost h, long leader, params long[] members)
    {
        CallChallenge(e, leader, members);
        var match = e.MatchOf(leader);
        if (match == null) return null;
        EnterAll(e, match);
        Advance(e, h, 61);
        return match;
    }

    static void Challenge()
    {
        Section("challenge: a group against one creature");

        var problems = new List<string>();
        var full = ArenaSettingsParser.MakeChallenge("Desafio 1", " Troll Furioso ", "Troll", 3, 2f, "Coins", 300, "TrophyTroll:1, MeadHealthMajor:5", problems);
        Check("fields: name, level, health", full != null && full.Name == "Troll Furioso" && full.Level == 3 && Math.Abs(full.HealthMultiplier - 2f) < 1e-6f);
        Check("fields: reward plus the extras", full != null && full.Rewards.Count == 3 && full.Rewards[0].Key == "Coins" && full.Rewards[0].Value == 300 &&
                                                full.Rewards[2].Key == "MeadHealthMajor" && full.Rewards[2].Value == 5);
        var bare = ArenaSettingsParser.MakeChallenge("Desafio 2", "", "Wolf", 0, 500f, "", 10, "", problems);
        Check("fields: no name takes the creature's, out-of-range numbers clamp, no reward", bare != null && bare.Name == "Wolf" &&
                                                                                             bare.Level == 1 && Math.Abs(bare.HealthMultiplier - 100f) < 1e-6f && bare.Rewards.Count == 0);
        Check("fields: amount 0 drops the reward", ArenaSettingsParser.MakeChallenge("Desafio 3", "X", "Troll", 1, 1f, "Coins", 0, "", problems).Rewards.Count == 0);
        Check("fields: no problems so far", problems.Count == 0, string.Join(" / ", problems));
        Check("fields: a bad extra refuses the challenge", ArenaSettingsParser.MakeChallenge("Desafio 4", "X", "Troll", 1, 1f, "Coins", 1, "Trophy:abc", problems) == null &&
                                                           problems.Count == 1 && problems[0].StartsWith("Desafio 4"));
        Check("fields: no creature refuses it", ArenaSettingsParser.MakeChallenge("Desafio 5", "X", " ", 1, 1f, "", 0, "", problems) == null && problems.Count == 2);
        Check("the menu's creature list has no duplicate and no boss", ArenaSettingsParser.ChallengeCreatures.Distinct().Count() == ArenaSettingsParser.ChallengeCreatures.Length &&
                                                                       !ArenaSettingsParser.ChallengeCreatures.Intersect(new[] { "Eikthyr", "gd_king", "Bonemass", "Dragon", "GoblinKing", "SeekerQueen", "Fader" }).Any());

        // Alone: the match is made at once, no queue.
        var (e, h, _) = NewEngine(ChallengeTuning());
        Do(e, A, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        var solo = e.MatchOf(A);
        Check("alone: invited at once", solo != null && solo.IsChallenge && solo.Seats.Count == 1 && solo.Seats[0].Side == ArenaSide.Gold);
        Check("alone: not in any queue", e.QueueOf(A) == null);
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("the only arena is taken: refused", e.MatchOf(B) == null && h.Got(B, ArenaWire.Alert, "ocupadas"));
        Do(e, B, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Do(e, C, ArenaEngine.AtBattlemaster, ArenaWire.ActQueueJoin, "2\n0\n");
        Advance(e, h, 2);
        Check("a skirmish waits for the arena the challenge holds", e.MatchOf(B) == null && e.QueueOf(B) != null);
        Do(e, D, "", ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("only at the Battlemaster", e.MatchOf(D) == null);
        Do(e, D, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Hidra\n");
        Check("unknown challenge refused", e.MatchOf(D) == null && h.Got(D, ArenaWire.Alert, "não existe"));

        // A group of four is one too many.
        (e, h, _) = NewEngine(ChallengeTuning());
        CallChallenge(e, A, B, C, D);
        Check("four players: refused", e.MatchOf(A) == null && e.PendingOf(A) == null && h.Got(A, ArenaWire.Alert, "até 3"));

        // Three: confirmed by the members' own clients, then invited together.
        CallChallenge(e, A, B, C);
        var match = e.MatchOf(A);
        Check("three players: one match, all on Gold", match != null && match.Seats.Count == 3 && match.Seats.All(s => s.Side == ArenaSide.Gold));
        Check("nobody is rated", match != null && !match.Rated);
        EnterAll(e, match);
        Advance(e, h, 30);
        Check("no creature during the preparation", h.Spawned.Count == 0 && match.Status == MatchStatus.WaitJoin);
        Advance(e, h, 31);
        Check("gates open: live", match.Status == MatchStatus.InProgress);
        Check("the creature comes in at the Green start", h.Spawned.Count == 1 && h.Spawned[0].Prefab == "Troll" &&
                                                          h.Spawned[0].At == new Vector3(140, 30, 100));
        Check("the spawn is told how many came in (health per extra player)", h.SpawnedFor.Count == 1 && h.SpawnedFor[0] == 3);
        Check("the old creatures were swept once", h.Sweeps == 1);

        h.Creatures[match.Id] = 0.5f;
        Advance(e, h, 1.5);
        Check("its health shows on the scoreboard", h.Last(A, ArenaWire.State)?.Contains("Troll Furioso (50%)") == true, h.Last(A, ArenaWire.State));

        Do(e, B, "", ArenaWire.ActKo, "0");
        Check("a knockout by the creature", h.Got(A, ArenaWire.Feed, "Bravo foi derrotado por Troll Furioso") && match.Status == MatchStatus.InProgress);

        h.Creatures.Remove(match.Id);
        Advance(e, h, 1.5);
        Check("the creature falls: the group wins", match.Winner == ArenaSide.Gold && match.Status == MatchStatus.WaitLeave);
        Check("everyone still there is paid, knocked out or not",
            new[] { A, B, C }.All(p => h.Items.Count(i => i.To == p) == 2 && h.Items.Any(i => i.To == p && i.Prefab == "Coins" && i.Amount == 300)));
        Check("a dead creature is not despawned again", !h.Despawned.Contains(match.Id));
        Check("the result names the challenge", h.Last(A, ArenaWire.Result)?.Contains("Troll Furioso") == true);
        Advance(e, h, 121);
        Check("then everyone goes back", e.MatchOf(A) == null && h.Got(A, ArenaWire.Return));

        // The same challenge again within the cooldown: played, not paid.
        int paid = h.Items.Count;
        var again = StartChallenge(e, h, A, B, C);
        h.Creatures.Remove(again.Id);
        Advance(e, h, 1.5);
        Check("won again within the cooldown: no second reward", again.Winner == ArenaSide.Gold && h.Items.Count == paid &&
                                                                h.Got(A, ArenaWire.Notice, "já pagou"));
        Advance(e, h, 121);

        // Everyone down: the creature wins and leaves.
        var lost = StartChallenge(e, h, A, B);
        Do(e, A, "", ArenaWire.ActKo, "0");
        Do(e, B, "", ArenaWire.ActKo, "0");
        Check("all knocked out: the creature wins", lost.Winner == ArenaSide.Green);
        Check("and is taken out of the world", h.Despawned.Contains(lost.Id) && !h.Creatures.ContainsKey(lost.Id));
        Check("a loss pays nothing", h.Items.Count == paid);
        Advance(e, h, 121);

        // The clock.
        var slow = StartChallenge(e, h, C);
        Advance(e, h, 600);
        Check("time up: no winner, creature gone", slow.Winner == ArenaSide.None && h.Despawned.Contains(slow.Id));
        Advance(e, h, 121);

        // Running away leaves the creature to the others; the last one out ends it.
        var fled = StartChallenge(e, h, A, B);
        Do(e, A, "", ArenaWire.ActLeave);
        Check("one leaves, the other fights on", fled.Status == MatchStatus.InProgress);
        Do(e, B, "", ArenaWire.ActLeave);
        Check("the last leaves: lost, creature gone", fled.Winner == ArenaSide.Green && h.Despawned.Contains(fled.Id));
        Advance(e, h, 121);

        // The creature cannot be made: the challenge ends with nobody hurt.
        h.SpawnFails = true;
        var broken = StartChallenge(e, h, D);
        Check("spawn failed: cancelled", broken.Winner == ArenaSide.None && h.Got(D, ArenaWire.Alert, "não pôde"));
        h.SpawnFails = false;
        Advance(e, h, 121);

        // Off in the cfg.
        var off = ChallengeTuning();
        off.ChallengeEnabled = false;
        e.SetTuning(off);
        Do(e, E, ArenaEngine.AtBattlemaster, ArenaWire.ActChallengeJoin, "Troll Furioso\n");
        Check("disabled: refused", e.MatchOf(E) == null && h.Got(E, ArenaWire.Alert, "desligados"));
    }

    // ------------------------------------------------- 0.1.90: layout, no clock, edge, PvE

    static ArenaTuning PveTuning(float limit)
    {
        var t = ChallengeTuning();
        t.Challenges[0].Id = "troll_furioso";
        t.Challenges[0].Difficulty = 1500;
        t.ChallengeTimeLimitSeconds = limit;
        t.ChallengeCooldownSeconds = 0f;
        return t;
    }

    static void Version090()
    {
        Section("0.1.90: PvE rating (Elo 650 against the challenge's difficulty)");
        // 1000 against 1500: chance 1/(1+10^(500/650)) = 0.14537; K 32.
        Check("1000 beats a 1500 challenge: +28", ArenaRules.PveRatingMod(1000, 1500, true, 32f) == 28);
        Check("1000 loses to it: -4", ArenaRules.PveRatingMod(1000, 1500, false, 32f) == -4);
        Check("even: +16 / -16", ArenaRules.PveRatingMod(1500, 1500, true, 32f) == 16 && ArenaRules.PveRatingMod(1500, 1500, false, 32f) == -16);
        Check("the PvE ladder reads as PvE", ArenaRules.BracketName(ArenaRules.PveLadder) == "PvE");

        Section("0.1.90: the arenas and challenges file");
        var maps = Enumerable.Range(1, 12).Select(i => new ArenaLayoutMap
        {
            On = i % 2 == 0, Name = "Arena " + i, Gold = $"{i},30,0,90", Green = $"{i + 40},30,0,270",
            Types = new[] { true, false, true, true, i == 12 },
        }).ToList();
        var taken = new List<string>();
        var challenges = new List<ArenaLayoutChallenge>();
        for (int i = 1; i <= 9; i++)
        {
            var c = new ArenaLayoutChallenge
            {
                On = true, Name = i <= 2 ? "Troll Furioso" : "Lobo " + i, Creature = "Troll", Level = 2, Health = 1.5f,
                Reward = "Coins", Amount = 10 * i, Extras = "TrophyTroll:1", Map = "Arena 3", Difficulty = 1000 + 100 * i,
            };
            c.Id = ArenaLayout.NewId(c.Name, taken);
            taken.Add(c.Id);
            challenges.Add(c);
        }
        Check("ids: from the name, accents out, a clash gets _2", taken[0] == "troll_furioso" && taken[1] == "troll_furioso_2" &&
                                                              ArenaLayout.NewId("Ação Épica!", null) == "acao_epica");
        Check("ids are safe on the wire", taken.All(ArenaLayout.ValidId) && !ArenaLayout.ValidId("a\tb") && !ArenaLayout.ValidId(""));
        string json = ArenaLayout.ToJson(maps, challenges);
        var problems = new List<string>();
        bool ok = ArenaLayout.Parse(json, problems, out var maps2, out var challenges2);
        Check("12 arenas and 9 challenges: written and read back", ok && problems.Count == 0 && maps2.Count == 12 && challenges2.Count == 9,
            string.Join(" / ", problems));
        Check("nothing changes on the way", ok && ArenaLayout.ToJson(maps2, challenges2) == json);
        Check("fields kept: types, difficulty, id", ok && maps2[11].Types[4] && !maps2[0].Types[1] && !maps2[0].On &&
                                                    challenges2[8].Difficulty == 1900 && challenges2[1].Id == "troll_furioso_2");
        Check("one line on the wire", json.IndexOf('\n') < 0 && json.IndexOf('\t') < 0);

        // The cfg blocks of 0.1.89, captured as orphans, become the file.
        var orphans = new Dictionary<BepInEx.Configuration.ConfigDefinition, string>
        {
            { new BepInEx.Configuration.ConfigDefinition("Arena - Local 1", "Ativo"), "true" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Local 1", "Nome"), "Coliseu" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Local 1", "Ouro"), "1,2,3,90" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Local 1", "Verde"), "41,2,3,270" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Local 1", "TipoFFA"), "false" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Desafio 3", "Ativo"), "true" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Desafio 3", "Nome"), "Lobo Alfa" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Desafio 3", "Criatura"), "Wolf" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Desafio 3", "Nivel"), "3" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Desafio 3", "Quantidade"), "50" },
            { new BepInEx.Configuration.ConfigDefinition("Arena - Outra", "Nome"), "x" },
        };
        string file = Path.Combine(Path.GetTempPath(), "arenacheck-layout-" + Guid.NewGuid().ToString("N") + ".json");
        ArenaLayout.CaptureLegacy(orphans);
        ArenaLayout.LoadOrSeed(file, problems, out var migratedMaps, out var migratedChallenges);
        Check("migration: the old blocks went to the file", File.Exists(file) && migratedMaps.Count == 1 && migratedMaps[0].Name == "Coliseu" &&
                                                            !migratedMaps[0].Types[3] && migratedMaps[0].Types[0] &&
                                                            migratedChallenges.Count == 1 && migratedChallenges[0].Id == "lobo_alfa" &&
                                                            migratedChallenges[0].Level == 3 && migratedChallenges[0].Amount == 50);
        ArenaLayout.LoadOrSeed(file, problems, out var again, out var againChallenges);
        Check("migration: the second start reads the file, not the cfg", again.Count == 1 && againChallenges.Count == 1);
        File.Delete(file);
        ArenaLayout.LoadOrSeed(file, null, out var fresh, out var seeded);
        Check("no file and no old blocks: the five example challenges", fresh.Count == 0 && seeded.Count == 5 && seeded.All(c => ArenaLayout.ValidId(c.Id)));
        File.Delete(file);

        Section("0.1.90: the shop sells by level and by PvE rating");
        var offer = new ArenaOffer { Prefab = "SwordIron", Amount = 1, Points = 300, Quality = 3, PveRating = 1200 };
        var parsed = ArenaSettingsParser.ParseOffers(ArenaSettingsParser.FormatOffer(offer), null).SingleOrDefault();
        Check("quality= and pve= survive the cfg", parsed != null && parsed.Quality == 3 && parsed.PveRating == 1200);

        Section("0.1.90: no time limit");
        var (e, h, _) = NewEngine(PveTuning(0f));
        var open = StartChallenge(e, h, A);
        Advance(e, h, 1800, 5);
        Check("limit 0: still going after 30 min", open.Status == MatchStatus.InProgress);
        Check("the client is told there is no clock (-1)", h.Last(A, ArenaWire.State)?.Split('\n').Any(l => l.StartsWith("X\t") && l.Split('\t')[7] == "-1") == true,
            h.Last(A, ArenaWire.State));
        Do(e, A, "", ArenaWire.ActLeave);
        Advance(e, h, 121);
        (e, h, _) = NewEngine(PveTuning(300f));
        var timed = StartChallenge(e, h, A);
        Advance(e, h, 300);
        Check("limit 5 min: ends with no winner", timed.Winner == ArenaSide.None && h.Got(A, ArenaWire.Alert, "Tempo esgotado"));
        Check("a time-out moves the PvE rating like a loss", e.PlayerRecord(A).PveGames == 1 && e.PlayerRecord(A).PveRating < 1000);

        Section("0.1.90: knocked out -> the arena's edge, watching");
        Check("edge: towards where they fell, 3 m inside", ArenaEngine.EdgeSpot(new Vector3(0, 5, 0), 30f, new Vector3(-10, 0, 0)) == new Vector3(-27, 5, 0));
        Check("edge: straight north when they fell on the centre", ArenaEngine.EdgeSpot(Vector3.zero, 30f, Vector3.zero) == new Vector3(0, 0, 27));
        (e, h, _) = NewEngine(PveTuning(0f));
        var watched = StartChallenge(e, h, A, B);
        Do(e, B, "", ArenaWire.ActKo, "0");
        var move = h.Last(B, ArenaWire.Move)?.Split('\t');
        // Tuning(): Gold (100,30,100), Green (140,30,100) -> centre (120,30,100), radius 50; B fell at the Gold start.
        Check("the Move says: watch, beaten by the creature, centre and radius", move != null && move.Length == 9 && move[4] == "1" &&
                                                                              move[5] == "Troll Furioso" && move[6] == "120" && move[7] == "100" && move[8] == "50",
            string.Join("|", move ?? new string[0]));
        Check("to the edge on the Gold side, facing the middle", move != null && move[0] == "73" && move[2] == "100" && Math.Abs(float.Parse(move[3], System.Globalization.CultureInfo.InvariantCulture) - 90f) < 0.01f);
        Advance(e, h, 60, 1);
        Check("watching from the edge is not running away", watched.Seat(B).State == SeatState.Entered && watched.Status == MatchStatus.InProgress);
        int returns = h.Sent.Count(s => s.To == B && s.Kind == ArenaWire.Return);
        Do(e, B, "", ArenaWire.ActLeaveSpectate);
        Check("Sair agora: home at once", h.Sent.Count(s => s.To == B && s.Kind == ArenaWire.Return) == returns + 1 && watched.Seat(B).WentHome);
        Do(e, A, "", ArenaWire.ActLeaveSpectate);
        Check("someone still standing cannot", !watched.Seat(A).WentHome);
        Advance(e, h, 30, 1);
        Check("gone home is not counted as fleeing", watched.Seat(B).State == SeatState.Entered && watched.Status == MatchStatus.InProgress);

        Section("0.1.90: PvE rating and best times");
        h.Creatures.Remove(watched.Id);
        Advance(e, h, 1.5);
        Check("the group wins: both +28 (1000 against 1500)", watched.Winner == ArenaSide.Gold &&
                                                             e.PlayerRecord(A).PveRating == 1028 && e.PlayerRecord(B).PveRating == 1028 &&
                                                             e.PlayerRecord(A).PveGames == 1 && e.PlayerRecord(A).PveWins == 1);
        var result = h.Last(A, ArenaWire.Result) ?? "";
        Check("the result: first time is a record, PvE before and change", result.Split('\n')[0].Split('\t').Last() == "1" &&
                                                                         result.Contains("Bravo\t0\tderrotado\t1000\t28"), result);
        Check("the best time is kept", e.BestTimes("troll_furioso").Count == 1 && e.BestTimes("troll_furioso")[0].Names == "Alfa, Bravo");
        Advance(e, h, 121);
        Check("the one who left early is not sent home twice", h.Sent.Count(s => s.To == B && s.Kind == ArenaWire.Return) == returns + 1);

        var slower = StartChallenge(e, h, A);
        Advance(e, h, 200, 2);
        h.Creatures.Remove(slower.Id);
        Advance(e, h, 1.5);
        Check("a slower win: no record, second place", (h.Last(A, ArenaWire.Result) ?? "").Split('\n')[0].EndsWith("\t0") &&
                                                       e.BestTimes("troll_furioso").Count == 2 &&
                                                       e.BestTimes("troll_furioso")[0].Seconds < e.BestTimes("troll_furioso")[1].Seconds);
        Advance(e, h, 121);

        int before = e.PlayerRecord(A).PveRating;
        var lost = StartChallenge(e, h, A);
        int movesBefore = h.Sent.Count(s => s.To == A && s.Kind == ArenaWire.Move);
        Do(e, A, "", ArenaWire.ActKo, "0");
        Check("a knockout that ends the match sends nobody to the edge",
            h.Sent.Count(s => s.To == A && s.Kind == ArenaWire.Move) == movesBefore && lost.Status == MatchStatus.WaitLeave);
        Check("a loss takes PvE rating, keeps the wins", lost.Winner == ArenaSide.Green && e.PlayerRecord(A).PveRating < before &&
                                                        e.PlayerRecord(A).PveWins == 2 && e.PlayerRecord(A).PveGames == 3);
        Advance(e, h, 121);
        Check("the PvP ratings are untouched", e.PlayerRecord(A).MmrOf(2, 1500) == 1500 && !e.Teams.Any());

        Do(e, A, "", ArenaWire.ActLadder, ArenaRules.PveLadder.ToString());
        var ladder = h.Last(A, ArenaWire.Ladder) ?? "";
        Check("the PvE ladder: players by rating, then the best times", ladder.StartsWith("L\t-1") &&
                                                                       ladder.Contains("P\t1\tAlfa\t") && ladder.Contains("P\t2\tBravo\t1028\t1\t1\t0") &&ladder.Contains("B\tTroll Furioso\t1\t"), ladder);
        Check("the panel gets the PvE rating", h.Last(A, ArenaWire.State)?.Split('\n')[0].Split('\t').Length >= 6);

        // A server restart keeps the records.
        var (e2, h2, _) = NewEngine(PveTuning(0f), e == null ? null : FindDb(e));
        Check("best times survive a restart", e2.BestTimes("troll_furioso").Count == 2);

        Section("0.1.90: the shop's PvE requirement");
        var shop = PveTuning(0f);
        shop.Offers = new List<ArenaOffer>
        {
            new ArenaOffer { Prefab = "SwordIron", Amount = 1, Points = 100, Quality = 3, PveRating = 1100 },
            new ArenaOffer { Prefab = "ArmorKit1", Amount = 1, Points = 100, PveRating = 1000 },
        };
        e2.SetTuning(shop);
        e2.PlayerRecord(B).Points = 500;
        Do(e2, B, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "SwordIron\n100");
        Check("below the PvE requirement: refused", h2.Items.Count == 0 && h2.Got(B, ArenaWire.Alert, "rating PvE 1100"));
        Do(e2, B, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "ArmorKit1\n100");
        Check("at or above it: sold", h2.Items.Count == 1 && e2.PlayerRecord(B).Points == 400);
        e2.PlayerRecord(B).PveRating = 1200;
        Do(e2, B, ArenaEngine.AtVendor, ArenaWire.ActVendorBuy, "SwordIron\n100");
        Check("the item goes at its level", h2.Items.Count == 2 && h2.LastQuality == 3);
    }

    static readonly Dictionary<ArenaEngine, string> Dbs = new Dictionary<ArenaEngine, string>();
    static string FindDb(ArenaEngine e) => Dbs.TryGetValue(e, out var db) ? db : null;

    // ----------------------------------------------------------------------- bestiary

    static void Bestiary()
    {
        Section("bestiary: the file");
        var problems = new List<string>();
        var seeds = ArenaBestiary.Parse(ArenaBestiary.SeedJson, problems);
        Check("the five examples parse with no problem", seeds != null && seeds.Count == 5 && problems.Count == 0, string.Join(" / ", problems));
        Check("their ids", seeds != null && string.Join(",", seeds.Select(b => b.Id)) ==
                           "troll_anciao,senhor_draugr,lobo_alfa,abominacao_ancestral,golem_runico");
        var troll = seeds?.FirstOrDefault(b => b.Id == "troll_anciao");
        Check("Troll Ancião: base, name, title, bar", troll != null && troll.Base == "Troll" && troll.Name == "Troll Ancião" &&
                                                      troll.Title == "Guardião da Floresta Negra" && troll.BossBar);
        Check("Troll Ancião: visual", troll != null && Math.Abs(troll.Scale - 1.5f) < 1e-6f && troll.Tint.SequenceEqual(new[] { 95, 122, 74 }) &&
                                      Math.Abs(troll.TintStrength - 0.45f) < 1e-6f && troll.Effect == "Nenhum");
        Check("Troll Ancião: attributes", troll != null && troll.Level == 3 && Math.Abs(troll.HealthMultiplier - 2.5f) < 1e-6f &&
                                          Math.Abs(troll.HealthPerExtraPlayer - 0.6f) < 1e-6f && Math.Abs(troll.Turn - 0.8f) < 1e-6f &&
                                          Math.Abs(troll.PushResist - 0.7f) < 1e-6f && !troll.FleeLowHealth);
        Check("Troll Ancião: every damage type has a resistance", troll != null && troll.Resistances.Count == 8 &&
                                                                 troll.Resistances["pierce"] == "Muito resistente" && troll.Resistances["frost"] == "Normal");
        Check("attacks and abilities are read", troll != null && troll.Attacks.Count == 2 && troll.Abilities.Count == 3 &&
                                                troll.Abilities[0].Kind == "stomp");
        var golem = seeds?.FirstOrDefault(b => b.Id == "golem_runico");
        Check("Golem Rúnico is immune to pierce (0%) and weak to blunt (150%)", golem != null &&
              ArenaBestiary.ModifierFactor(golem.Resistances["pierce"]) == 0f && ArenaBestiary.ModifierFactor(golem.Resistances["blunt"]) == 1.5f);

        string json = ArenaBestiary.ToJson(seeds);
        var again = ArenaBestiary.Parse(json, problems);
        Check("written and read back, nothing changes", again != null && problems.Count == 0 && ArenaBestiary.ToJson(again) == json);
        Check("one line on the wire (no tab, no line break)", json.IndexOf('\n') < 0 && json.IndexOf('\t') < 0);
        Check("numbers are written short (1.35, not 1.35000002)", json.Contains("1.35") && !json.Contains("1.3500"));
        Check("a bare list is read too", ArenaBestiary.Parse("[" + ArenaBestiary.ToJson(troll) + "]", null)?.Count == 1);

        Section("bestiary: what is refused");
        ArenaBeastDef One(string edit, out List<string> why)
        {
            why = new List<string>();
            var doc = ArenaBestiary.ToBson(troll);
            var patch = LiteDB.JsonSerializer.Deserialize(edit).AsDocument;
            foreach (var key in patch.Keys)
            {
                if (patch[key].IsDocument && doc[key].IsDocument)
                    foreach (var inner in patch[key].AsDocument.Keys) doc[key].AsDocument[inner] = patch[key].AsDocument[inner];
                else doc[key] = patch[key];
            }
            return ArenaBestiary.ParseOne(LiteDB.JsonSerializer.Serialize(doc), why);
        }
        bool Refused(string what, string edit, string expect)
        {
            var b = One(edit, out var why);
            bool ok = b == null && why.Any(w => w.Contains(expect));
            Check("refused: " + what, ok, string.Join(" / ", why));
            return ok;
        }
        Check("the unchanged creature passes", One("{}", out var none) != null && none.Count == 0, string.Join(" / ", none));
        Refused("id with capitals", "{\"id\":\"Troll Velho\"}", "id precisa");
        Refused("a boss as base", "{\"base\":\"Eikthyr\"}", "não está na lista");
        Refused("size 5x", "{\"visual\":{\"escala\":5}}", "tamanho");
        Refused("colour above 255", "{\"visual\":{\"tingimento\":[300,0,0]}}", "tingimento");
        Refused("an effect that does not exist", "{\"visual\":{\"efeito\":\"Arco-íris\"}}", "efeito");
        Refused("level 4", "{\"atributos\":{\"nivel\":4}}", "nível");
        Refused("level 2.5", "{\"atributos\":{\"nivel\":2.5}}", "inteiro");
        Refused("health x0", "{\"atributos\":{\"vidaMult\":0}}", "multiplicador de vida");
        Refused("speed as text", "{\"atributos\":{\"velocidade\":\"rápido\"}}", "precisa ser um número");
        Refused("a resistance that does not exist", "{\"resistencias\":{\"fire\":\"Super\"}}", "fire");
        Refused("a damage type that does not exist", "{\"resistencias\":{\"chop\":\"Imune\"}}", "chop");
        Refused("no name", "{\"nome\":\"\"}", "nome");
        Check("the base is matched without case and written as the game names it", One("{\"base\":\"troll\"}", out _)?.Base == "Troll");
        Check("tabs and line breaks leave the name", One("{\"nome\":\"Troll\\tAnci\\u00e3o\\n\"}", out _)?.Name == "Troll Ancião");
        Check("not JSON at all: nothing, and why", ArenaBestiary.Parse("{ isto não é json", problems = new List<string>()) == null && problems.Count == 1);
        var mixed = ArenaBestiary.Parse("{\"criaturas\":[" + ArenaBestiary.ToJson(troll) + ",{\"id\":\"x\",\"base\":\"Dragon\",\"nome\":\"X\"}," +
                                        ArenaBestiary.ToJson(troll) + "]}", problems = new List<string>());
        Check("a bad creature is left out, the good ones still load", mixed?.Count == 1 && problems.Count == 2, string.Join(" / ", problems));
        Check("ids out of names", ArenaBestiary.IdFrom("Troll Ancião!") == "troll_anciao" && ArenaBestiary.IdFrom("  Golem   Rúnico ") == "golem_runico");

        Section("bestiary: health");
        // Troll: 600 base. Nível 3 x vida 2.5 = 4500 solo; +60% per extra player.
        Check("solo: base x level x multiplier", Math.Abs(ArenaBestiary.MaxHealth(600f, troll, 1) - 4500f) < 0.01f);
        Check("pair: 1.6x the solo", Math.Abs(ArenaBestiary.MaxHealth(600f, troll, 2) - 7200f) < 0.01f);
        Check("trio: 2.2x the solo", Math.Abs(ArenaBestiary.MaxHealth(600f, troll, 3) - 9900f) < 0.01f);
        Check("no players counts as one", Math.Abs(ArenaBestiary.MaxHealth(600f, troll, 0) - 4500f) < 0.01f);
        Check("the factor on the ZDO leaves level out (the game multiplies it)", Math.Abs(ArenaBestiary.HealthFactor(troll, 3) - 2.5f * 2.2f) < 1e-4f);
        var lone = troll.Clone();
        lone.HealthPerExtraPlayer = 0f;
        Check("0% per extra player: the group faces the solo health", Math.Abs(ArenaBestiary.MaxHealth(600f, lone, 3) - 4500f) < 0.01f);
        Check("a clone is a copy, not the same lists", !ReferenceEquals(lone.Attacks, troll.Attacks) && Math.Abs(troll.HealthPerExtraPlayer - 0.6f) < 1e-6f);

        Section("bestiary: in a challenge");
        ArenaBestiary.Replace(seeds);
        problems = new List<string>();
        var def = ArenaSettingsParser.MakeChallenge("Desafio 1", "", "bestiario:troll_anciao", 1, 1f, "Coins", 100, "", problems);
        Check("bestiario:id spawns the base, with the mark", def != null && def.Prefab == "Troll" && def.BeastId == "troll_anciao", string.Join(" / ", problems));
        Check("no name: the creature's", def?.Name == "Troll Ancião");
        Check("the prefix ignores case", ArenaSettingsParser.MakeChallenge("Desafio 1", "X", "Bestiario:Lobo_Alfa", 1, 1f, "", 0, "", problems)?.Prefab == "Wolf");
        Check("a creature of the game has no mark", ArenaSettingsParser.MakeChallenge("Desafio 1", "X", "Troll", 1, 1f, "", 0, "", problems)?.BeastId == null);
        Check("an id the bestiary lacks refuses the challenge",
            ArenaSettingsParser.MakeChallenge("Desafio 2", "X", "bestiario:hidra", 1, 1f, "", 0, "", problems) == null &&
            problems.Count == 1 && problems[0].Contains("hidra"));
        Check("the challenge value", ArenaBestiary.ChallengeValue(troll) == "bestiario:troll_anciao" &&
                                     ArenaBestiary.BeastIdOf(" bestiario:troll_anciao ") == "troll_anciao" && ArenaBestiary.BeastIdOf("Troll") == null);
        Check("every base is a challenge creature (no boss)", seeds.All(b => ArenaSettingsParser.ChallengeCreatures.Contains(b.Base)));
        ArenaBestiary.Replace(new List<ArenaBeastDef>());
    }

    static ArenaBeastDef SeedBeast(string id) => ArenaBestiary.Parse(ArenaBestiary.SeedJson, null).First(b => b.Id == id);

    /// <summary>The troll of the seed with "ataques" or "habilidades" replaced by <paramref name="json"/>.</summary>
    static ArenaBeastDef WithList(string key, string json, out List<string> problems, out List<string> warnings)
    {
        problems = new List<string>();
        warnings = new List<string>();
        var doc = ArenaBestiary.ToBson(SeedBeast("troll_anciao"));
        doc[key] = LiteDB.JsonSerializer.Deserialize(json);
        return ArenaBestiary.ParseOne(doc, problems, null, warnings);
    }

    static void BestiaryKit()
    {
        Section("bestiary: attacks");
        var troll = SeedBeast("troll_anciao");
        Check("the seed's attacks name the base's items", string.Join(",", troll.Attacks.Select(a => a.Item)) == "troll_punch,troll_throw");
        var punch = troll.Attacks[0];
        Check("an attack's numbers", punch.Damage[0] == 85f && punch.Damage.Skip(1).All(d => d == 0f) && Math.Abs(punch.Range - 3.5f) < 1e-6f &&
                                     punch.Interval == 2f && punch.Force == 120f && punch.HitEffect == "Nenhum");
        var draugr = SeedBeast("senhor_draugr");
        Check("mixed damage and a hit effect", draugr.Attacks[0].Damage[1] == 45f && draugr.Attacks[0].Damage[3] == 40f &&
                                               draugr.Attacks[0].HitEffect == "Queimando");
        Check("hit effects map to the game's status effects", ArenaBeastKit.StatusEffectOf("Queimando") == "Burning" &&
              ArenaBeastKit.StatusEffectOf("Congelado (lento)") == "Frost" && ArenaBeastKit.StatusEffectOf("Nenhum") == "");
        Check("there is no bleeding in the game", ArenaBeastKit.CanonicalHitEffect("Sangrando") == null);

        var old = WithList("ataques", "[{\"nome\":\"Tapa\",\"dmg\":{\"blunt\":85},\"alcance\":3.5,\"status\":\"Nenhum\"}," +
                                      "{\"item\":\"troll_punch\",\"dano\":{\"blunt\":50}}]", out var problems, out var warnings);
        Check("an attack of the first format (no item) is dropped with a warning", old != null && old.Attacks.Count == 1 &&
              old.Attacks[0].Item == "troll_punch" && warnings.Count == 1 && warnings[0].Contains("Tapa"), string.Join(" / ", problems.Concat(warnings)));
        var bleed = WithList("ataques", "[{\"item\":\"troll_punch\",\"efeito\":\"Sangrando\"}]", out problems, out warnings);
        Check("an effect the game lacks becomes Nenhum, with a warning", bleed != null && bleed.Attacks[0].HitEffect == "Nenhum" &&
              warnings.Count == 1 && warnings[0].Contains("Sangrando"), string.Join(" / ", problems.Concat(warnings)));
        void RefusedAttack(string what, string json, string expect)
        {
            var b = WithList("ataques", json, out var why, out _);
            Check("refused attack: " + what, b == null && why.Any(w => w.Contains(expect)), string.Join(" / ", why));
        }
        RefusedAttack("damage 2001", "[{\"item\":\"troll_punch\",\"dano\":{\"blunt\":2001}}]", "dano de blunt");
        RefusedAttack("a damage type that does not exist", "[{\"item\":\"troll_punch\",\"dano\":{\"chop\":10}}]", "chop");
        RefusedAttack("range 0", "[{\"item\":\"troll_punch\",\"alcance\":0}]", "alcance");
        RefusedAttack("interval 61 s", "[{\"item\":\"troll_punch\",\"intervalo\":61}]", "intervalo");
        RefusedAttack("push 1001", "[{\"item\":\"troll_punch\",\"empurrao\":1001}]", "empurrão");
        RefusedAttack("the same item twice", "[{\"item\":\"troll_punch\"},{\"item\":\"Troll_Punch\"}]", "repetido");
        RefusedAttack("an item that is no prefab name", "[{\"item\":\"troll punch!\"}]", "prefab");
        var round = ArenaBestiary.ToJson(draugr);
        Check("attacks written and read back", ArenaBestiary.ToJson(ArenaBestiary.ParseOne(round, null)) == round);

        Section("bestiary: abilities");
        Check("the seed's abilities", string.Join(",", troll.Abilities.Select(a => a.Kind)) == "stomp,summon,enrage");
        var stomp = troll.Abilities[0];
        Check("stomp: trigger and numbers", stomp.On && stomp.Trigger == "every" && stomp.Value == 14f && stomp.N("dano") == 60f &&
                                            stomp.N("raio") == 6f && stomp.N("empurrao") == 80f && Math.Abs(stomp.N("preparo") - 1.2f) < 1e-6f);
        var summon = troll.Abilities[1];
        Check("summon: prefab and counts", summon.Prefab == "Greydwarf_Elite" && summon.N("qtd") == 2f && summon.N("maxVivos") == 4f);
        var zone = SeedBeast("senhor_draugr").Abilities.First(a => a.Kind == "zone");
        Check("zone: the damage type", zone.DamageType == "fire" && zone.N("dps") == 15f);
        var golem = SeedBeast("golem_runico");
        Check("phase 3 kinds load but do not act", golem.Abilities.Count == 4 && golem.Abilities.Where(a => a.Kind != "timerage").All(a => !a.Acts) &&
                                                   golem.Abilities.Single(a => a.Kind == "timerage").Acts);
        var off = WithList("habilidades", "[{\"tipo\":\"stomp\",\"ligada\":false}]", out problems, out _);
        Check("switched off: kept, does not act", off != null && off.Abilities.Count == 1 && !off.Abilities[0].Acts);
        var bare = WithList("habilidades", "[{\"tipo\":\"zone\"}]", out problems, out _);
        Check("missing numbers take the prototype's", bare != null && bare.Abilities[0].Trigger == "every" && bare.Abilities[0].Value == 12f &&
                                                      bare.Abilities[0].DamageType == "fire" && bare.Abilities[0].N("aviso") == 1.5f, string.Join(" / ", problems));
        var extra = WithList("habilidades", "[{\"tipo\":\"enrage\",\"cor\":\"vermelha\"}]", out problems, out warnings);
        Check("an unknown parameter is only a warning", extra != null && warnings.Count == 1 && warnings[0].Contains("cor"));
        void RefusedAbility(string what, string json, string expect)
        {
            var b = WithList("habilidades", json, out var why, out _);
            Check("refused ability: " + what, b == null && why.Any(w => w.Contains(expect)), string.Join(" / ", why));
        }
        RefusedAbility("a kind that does not exist", "[{\"tipo\":\"meteoro\"}]", "meteoro");
        RefusedAbility("radius 25 m", "[{\"tipo\":\"stomp\",\"raio\":25}]", "raio");
        RefusedAbility("radius 0.5 m", "[{\"tipo\":\"stomp\",\"raio\":0.5}]", "raio");
        RefusedAbility("damage 2500", "[{\"tipo\":\"stomp\",\"dano\":2500}]", "dano");
        RefusedAbility("9 lacaios", "[{\"tipo\":\"summon\",\"qtd\":9,\"maxVivos\":8}]", "quantidade");
        RefusedAbility("2.5 lacaios", "[{\"tipo\":\"summon\",\"qtd\":2.5}]", "inteiro");
        RefusedAbility("more at once than may live", "[{\"tipo\":\"summon\",\"qtd\":5,\"maxVivos\":4}]", "máximo de vivos");
        RefusedAbility("health trigger 0%", "[{\"tipo\":\"enrage\",\"gatilho\":\"hp\",\"valor\":0}]", "vida do gatilho");
        RefusedAbility("health trigger 100%", "[{\"tipo\":\"enrage\",\"gatilho\":\"hp\",\"valor\":100}]", "vida do gatilho");
        RefusedAbility("cooldown 2 s", "[{\"tipo\":\"stomp\",\"gatilho\":\"every\",\"valor\":2}]", "segundos");
        RefusedAbility("cooldown 601 s", "[{\"tipo\":\"stomp\",\"gatilho\":\"every\",\"valor\":601}]", "segundos");
        RefusedAbility("a trigger that does not exist", "[{\"tipo\":\"stomp\",\"gatilho\":\"sempre\"}]", "gatilho");
        RefusedAbility("a damage type that does not exist", "[{\"tipo\":\"zone\",\"tipoDano\":\"acid\"}]", "acid");
        RefusedAbility("a summon of no prefab", "[{\"tipo\":\"summon\",\"prefab\":\"\"}]", "prefab");
        RefusedAbility("seven abilities", "[" + string.Join(",", Enumerable.Repeat("{\"tipo\":\"stomp\"}", 7)) + "]", "no máximo 6");
        var start = WithList("habilidades", "[{\"tipo\":\"stomp\",\"gatilho\":\"start\",\"valor\":999}]", out problems, out _);
        Check("start takes no value", start != null && start.Abilities[0].Value == 0f, string.Join(" / ", problems));
        round = ArenaBestiary.ToJson(SeedBeast("lobo_alfa"));
        Check("abilities written and read back", ArenaBestiary.ToJson(ArenaBestiary.ParseOne(round, null)) == round);
        Check("every new ability is valid as it comes", ArenaBeastKit.Abilities.All(s =>
        {
            var b = SeedBeast("troll_anciao");
            b.Abilities = new List<ArenaBeastAbility> { ArenaBeastKit.NewAbility(s.Key) };
            return ArenaBestiary.ParseOne(ArenaBestiary.ToJson(b), null) != null;
        }));
        Check("the phase line", ArenaBeastKit.PhaseLine(troll.Abilities) == "60% ᛗ Invocar Lacaios → 30% ᛏ Fúria", ArenaBeastKit.PhaseLine(troll.Abilities));
        Check("when", ArenaBeastKit.When(stomp) == "a cada 14 s" && ArenaBeastKit.When(summon) == "vida ≤ 60%");

        Section("bestiary: sync and the client's first look");
        var seeds = ArenaBestiary.Parse(ArenaBestiary.SeedJson, null);
        string json = ArenaBestiary.ToJson(seeds);
        Check("the hash is stable and follows the text", ArenaBestiary.HashOf(json) == ArenaBestiary.HashOf(json) &&
              ArenaBestiary.HashOf(json) != ArenaBestiary.HashOf(json + " ") && ArenaBestiary.HashOf(json).Length == 16);
        ArenaBestiary.Replace(seeds);
        Check("the server's hash is the hash of what it sends", ArenaBestiary.Hash == ArenaBestiary.HashOf(ArenaBestiary.ToJson(ArenaBestiary.All)));
        ArenaBestiary.Forget();
        problems = new List<string>();
        Check("before the bestiary arrives, bestiario:id is quietly left out",
              ArenaSettingsParser.MakeChallenge("Desafio 1", "", "bestiario:troll_anciao", 1, 1f, "", 0, "", problems) == null && problems.Count == 0 &&
              !ArenaBestiary.Loaded && ArenaBestiary.Hash == "");
        ArenaBestiary.Replace(new List<ArenaBeastDef>());
        Check("once it is there, a missing one is named",
              ArenaSettingsParser.MakeChallenge("Desafio 1", "", "bestiario:troll_anciao", 1, 1f, "", 0, "", problems) == null && problems.Count == 1);
    }

    static void BestiarySchedule()
    {
        Section("bestiary: triggers");
        ArenaBeastAbility A(string kind, string trigger, float value, bool on = true)
        {
            var a = ArenaBeastKit.NewAbility(kind);
            a.Trigger = trigger;
            a.Value = value;
            a.On = on;
            return a;
        }
        double half() => 0.5;
        const long T0 = 1_000_000L;

        var hpOnce = new List<ArenaBeastAbility> { A("summon", "hp", 60) };
        var s = new ArenaBeastScheduleState();
        Check("hp: nothing above the value", ArenaBeastSchedule.Decide(hpOnce, s, T0, 100f, half) == -1 && s.StartMs == T0);
        Check("hp: goes off at the value", ArenaBeastSchedule.Decide(hpOnce, s, T0 + 1000, 60f, half) == 0);
        Check("hp: only once", ArenaBeastSchedule.Decide(hpOnce, s, T0 + 9000, 40f, half) == -1 && s.Used(0));

        var every = new List<ArenaBeastAbility> { A("stomp", "every", 10) };
        s = new ArenaBeastScheduleState();
        Check("every: not at the start", ArenaBeastSchedule.Decide(every, s, T0, 100f, half) == -1);
        Check("every: not before its cooldown", ArenaBeastSchedule.Decide(every, s, T0 + 9999, 100f, half) == -1);
        Check("every: after one cooldown", ArenaBeastSchedule.Decide(every, s, T0 + 10000, 100f, half) == 0);
        Check("every: the next one a cooldown later", s.NextMs[0] == T0 + 20000 &&
              ArenaBeastSchedule.Decide(every, s, T0 + 15000, 100f, half) == -1 && ArenaBeastSchedule.Decide(every, s, T0 + 20000, 100f, half) == 0);
        var low = new ArenaBeastScheduleState();
        ArenaBeastSchedule.Decide(every, low, T0, 100f, () => 0.0);
        var high = new ArenaBeastScheduleState();
        ArenaBeastSchedule.Decide(every, high, T0, 100f, () => 0.999999);
        Check("every: ±20% of variation", low.NextMs[0] == T0 + 8000 && Math.Abs(high.NextMs[0] - (T0 + 12000)) <= 1, $"{low.NextMs[0] - T0} / {high.NextMs[0] - T0}");

        var time = new List<ArenaBeastAbility> { A("timerage", "time", 300) };
        s = new ArenaBeastScheduleState { StartMs = T0 };
        Check("time: counts from the start", ArenaBeastSchedule.Decide(time, s, T0 + 299_999, 100f, half) == -1 &&
                                             ArenaBeastSchedule.Decide(time, s, T0 + 300_000, 100f, half) == 0 &&
                                             ArenaBeastSchedule.Decide(time, s, T0 + 400_000, 100f, half) == -1);

        var startOne = new List<ArenaBeastAbility> { A("stomp", "start", 0) };
        s = new ArenaBeastScheduleState();
        Check("start: at once, then never", ArenaBeastSchedule.Decide(startOne, s, T0, 100f, half) == 0 &&
                                           ArenaBeastSchedule.Decide(startOne, s, T0 + 60000, 100f, half) == -1);

        var both = new List<ArenaBeastAbility> { A("stomp", "every", 10), A("enrage", "hp", 30) };
        s = new ArenaBeastScheduleState();
        ArenaBeastSchedule.Decide(both, s, T0, 100f, half);
        Check("a phase of health goes before a cooldown due at the same time", ArenaBeastSchedule.Decide(both, s, T0 + 10000, 20f, half) == 1);
        ArenaBeastSchedule.Busy(s, T0 + 10000, 1.5f);
        Check("while one is under way, the next waits", ArenaBeastSchedule.Decide(both, s, T0 + 11000, 20f, half) == -1);
        Check("and goes when it ends", ArenaBeastSchedule.Decide(both, s, T0 + 11500, 20f, half) == 0);

        var quiet = new List<ArenaBeastAbility> { A("stomp", "start", 0, on: false), A("shield", "start", 0) };
        s = new ArenaBeastScheduleState();
        Check("switched off and phase 3: nothing goes off", ArenaBeastSchedule.Decide(quiet, s, T0, 10f, half) == -1 &&
                                                            ArenaBeastSchedule.Decide(quiet, s, T0 + 600_000, 1f, half) == -1);

        // The owner changes in the middle: the new one reads what the old one wrote on the ZDO.
        var fight = new List<ArenaBeastAbility> { A("summon", "hp", 60), A("stomp", "every", 14), A("timerage", "time", 120) };
        var first = new ArenaBeastScheduleState();
        var zdo = new Dictionary<string, long>();
        var fired = new List<(long, int)>();
        for (long t = 0; t <= 60_000; t += 250)
        {
            int i = ArenaBeastSchedule.Decide(fight, first, T0 + t, 50f, half);
            if (i >= 0) { fired.Add((t, i)); ArenaBeastSchedule.Busy(first, T0 + t, 1f); }
        }
        first.Write((k, v) => zdo[k] = v);
        var second = ArenaBeastScheduleState.Read(k => zdo.TryGetValue(k, out var v) ? v : 0L);
        var after = new List<(long, int)>();
        for (long t = 60_250; t <= 130_000; t += 250)
        {
            int i = ArenaBeastSchedule.Decide(fight, second, T0 + t, 40f, half);
            if (i >= 0) { after.Add((t, i)); ArenaBeastSchedule.Busy(second, T0 + t, 1f); }
        }
        Check("owner change: the start is kept", second.StartMs == T0);
        Check("owner change: a phase already used does not come back", fired.Count(f => f.Item2 == 0) == 1 && after.All(f => f.Item2 != 0),
              string.Join(" ", fired.Concat(after)));
        Check("owner change: the cooldown goes on where it was", after.Where(f => f.Item2 == 1).Select(f => f.Item1).FirstOrDefault() ==
              fired.Where(f => f.Item2 == 1).Max(f => f.Item1) + 14_000, string.Join(" ", fired.Concat(after)));
        Check("owner change: the fight's time goes on (time 120 s)", after.Count(f => f.Item2 == 2) == 1 &&
              after.First(f => f.Item2 == 2).Item1 >= 120_000, string.Join(" ", after));

        Section("bestiary: Fúria do Tempo");
        Check("nothing before it starts", ArenaBeastSchedule.TimeRageBonus(10f, 0L, T0) == 0f && ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 - 1) == 0f);
        Check("+10% at once", ArenaBeastSchedule.TimeRageBonus(10f, T0, T0) == 10f && ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 + 9999) == 10f);
        Check("+10% more every 10 s", ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 + 10_000) == 20f &&
                                      ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 + 45_000) == 50f);
        Check("stops at +200%", ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 + 190_000) == 200f &&
                                ArenaBeastSchedule.TimeRageBonus(10f, T0, T0 + 3_600_000) == 200f &&
                                ArenaBeastSchedule.TimeRageBonus(30f, T0, T0 + 60_000) == 200f);
        Check("the hit's multiplier: Fúria × Fúria do Tempo", Math.Abs(ArenaBeastSchedule.DamageMultiplier(1.25f, 20f) - 1.5f) < 1e-5f &&
              ArenaBeastSchedule.DamageMultiplier(0f, 0f) == 1f && Math.Abs(ArenaBeastSchedule.DamageMultiplier(0f, 200f) - 3f) < 1e-5f);
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
