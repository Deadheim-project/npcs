using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NpcValheim.Npc;
using NpcValheim.Persistence;

class Program
{
    static int failed = 0, passed = 0;

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { passed++; System.Console.WriteLine("  PASS  " + what); }
        else { failed++; System.Console.WriteLine("  FAIL  " + what + (detail.Length > 0 ? "  -- " + detail : "")); }
    }

    static readonly Type Giver = typeof(QuestGiverNpc);

    static MethodInfo Priv(string name) =>
        Giver.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);

    static string PackObjectives(List<QuestObjective> steps, QuestProgress progress) =>
        (string)Priv("PackObjectives").Invoke(null, new object[] { steps, progress });

    static int FieldCount =>
        (int)Giver.GetField("FieldCount", BindingFlags.NonPublic | BindingFlags.Static)
                  .GetRawConstantValue();

    static bool IdentityMatches(long senderId, long playerId, long zdoUserId,
        bool hasPeer, bool hasPeerCharacter, bool exactPeerCharacter,
        bool exactNetworkOwner = false)
    {
        var method = typeof(GameApi).GetMethod(
            "IdentityMatches", BindingFlags.NonPublic | BindingFlags.Static);
        return method != null && (bool)method.Invoke(null, new object[]
        {
            senderId, playerId, zdoUserId,
            hasPeer, hasPeerCharacter, exactPeerCharacter, exactNetworkOwner,
        });
    }

    /// <summary>Hands the mod the logger BepInEx would have given it in-game.
    /// Unpack now runs the EpicMMO level gate, and that integration logs "not installed"
    /// on its first call -- with Plugin.Log still null, the logging is what throws, and the
    /// check dies on the very line it exists to verify.</summary>
    static void GiveTheModALogger()
    {
        typeof(NpcValheim.Plugin)
            .GetField("Log", BindingFlags.NonPublic | BindingFlags.Static)
            ?.SetValue(null, new BepInEx.Logging.ManualLogSource("wirecheck"));
    }

    static readonly Type Teleporter = typeof(TeleporterNpc);

    static string PackRoutes(List<TeleportDestination> routes) =>
        (string)Teleporter.GetMethod("Pack", BindingFlags.NonPublic | BindingFlags.Static)
                          .Invoke(null, new object[] { routes });

    static List<TeleportDestination> ParseRoutes(string packed) =>
        (List<TeleportDestination>)Teleporter
            .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { packed });

    static TeleportDestination Route(string id, string name, int cost, string costItem) =>
        new TeleportDestination
        {
            Id = id,
            Name = name,
            Position = new UnityEngine.Vector3(120.5f, 31.25f, -640.75f),
            Yaw = 90f,
            Cost = cost,
            CostItem = costItem,
        };

    static readonly Type Catalog = typeof(QuestGiverNpc).Assembly.GetType("NpcValheim.Npc.DeadcoinCatalog");
    static readonly Type Ledger = typeof(QuestGiverNpc).Assembly.GetType("NpcValheim.Npc.DeadcoinLedger");

    static string DefaultDeadcoinItems =>
        (string)Catalog.GetField("DefaultItems", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();

    static List<DeadcoinOffer> ParseOffers(string raw, List<string> problems) =>
        (List<DeadcoinOffer>)Catalog.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)
                                    .Invoke(null, new object[] { raw, problems });

    static string SetDeadcoinOffer(string raw, DeadcoinOffer offer) =>
        (string)Catalog.GetMethod("SetOffer", BindingFlags.NonPublic | BindingFlags.Static)
                       .Invoke(null, new object[] { raw, offer });

    static string RemoveDeadcoinOffer(string raw, string prefab, out bool removed)
    {
        var args = new object[] { raw, prefab, false };
        var result = (string)Catalog.GetMethod("RemoveOffer", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args);
        removed = (bool)args[2];
        return result;
    }

    static bool OnSaleOn(DeadcoinOffer offer, System.DateTime day) =>
        (bool)Catalog.GetMethod("OnSaleOn", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { offer, day });

    static readonly Type MountCatalogType = typeof(QuestGiverNpc).Assembly.GetType("NpcValheim.Npc.MountCatalog");

    static string DefaultMountOffers =>
        (string)MountCatalogType.GetField("DefaultOffers", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();

    /// <summary>MountOffer is internal, so its rows come back as objects and are read by name.</summary>
    static System.Collections.IList ParseMountOffers(string raw, List<string> problems) =>
        (System.Collections.IList)MountCatalogType.GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)
                                                  .Invoke(null, new object[] { raw, problems });

    static string ResolveMountId(string id, params string[] mounts) =>
        (string)MountCatalogType.GetMethod("ResolveMountId", BindingFlags.NonPublic | BindingFlags.Static)
                                .Invoke(null, new object[] { id, new HashSet<string>(mounts, StringComparer.Ordinal) });

    static string OfferField(object offer, string name) => offer.GetType().GetField(name).GetValue(offer)?.ToString();

    static string CanonicalAccountId(string hostName) =>
        (string)Ledger.GetMethod("CanonicalAccountId", BindingFlags.NonPublic | BindingFlags.Static)
                      .Invoke(null, new object[] { hostName });

    static string FileNameFor(string playerName, string accountId) =>
        (string)Ledger.GetMethod("FileNameFor", BindingFlags.NonPublic | BindingFlags.Static)
                      .Invoke(null, new object[] { playerName, accountId });

    // ---- BossPass: everything below is internal to the mod, so it is reached by name ----

    const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static Type ModType(string name) => typeof(QuestGiverNpc).Assembly.GetType("NpcValheim.Npc." + name);

    static readonly Type PassCatalog = ModType("BossPassCatalog");
    static readonly Type PassLedger = ModType("BossPassLedger");
    static readonly Type KillMatcher = ModType("BossKillMatcher");
    static readonly Type Death = ModType("BossDeath");
    static readonly Type Witness = ModType("BossWitness");
    static readonly Type Witnesses = ModType("BossWitnesses");

    static List<BossPassEntry> ParseBosses(string raw, List<string> problems) =>
        (List<BossPassEntry>)PassCatalog.GetMethod("Parse", AnyStatic).Invoke(null, new object[] { raw, problems });

    static string DefaultBosses => (string)PassCatalog.GetField("DefaultBosses", AnyStatic).GetRawConstantValue();

    static Dictionary<long, HashSet<string>> ParseLedger(string[] lines, List<string> problems) =>
        (Dictionary<long, HashSet<string>>)PassLedger.GetMethod("Parse", AnyStatic)
            .Invoke(null, new object[] { lines, problems });

    static Dictionary<long, Dictionary<string, string>> ParseLedgerSources(string[] lines) =>
        (Dictionary<long, Dictionary<string, string>>)PassLedger.GetMethod("ParseSources", AnyStatic)
            .Invoke(null, new object[] { lines, null });

    static string KindOf(string source) =>
        (string)PassLedger.GetMethod("KindOf", AnyStatic).Invoke(null, new object[] { source });

    static string ChosenBuyPayload(string boss, string method, int price) =>
        (string)ModType("BossPass").GetMethod("ChosenBuyPayload", AnyStatic).Invoke(null, new object[] { boss, method, price });

    static string LedgerLine(long playerId, string boss, string source, string name) =>
        (string)PassLedger.GetMethod("FormatLine", AnyStatic)
            .Invoke(null, new object[] { playerId, boss, source, new DateTime(2026, 10, 1, 12, 0, 0), name });

    static object NewMatcher(float window) =>
        Activator.CreateInstance(KillMatcher, AnyMember, null, new object[] { window }, null);

    static object NewDeath(string boss, string defeatKey, float time)
    {
        var death = Activator.CreateInstance(Death, true);
        Death.GetField("Entry").SetValue(death, new BossPassEntry { Boss = boss, Name = boss });
        Death.GetField("DefeatKey").SetValue(death, defeatKey);
        Death.GetField("Time").SetValue(death, time);
        return death;
    }

    static bool Gone(object matcher, object death, float now) =>
        (bool)KillMatcher.GetMethod("OnGone", AnyMember).Invoke(matcher, new[] { death, (object)now });

    static object Key(object matcher, string key, float now) =>
        KillMatcher.GetMethod("OnKey", AnyMember).Invoke(matcher, new object[] { key, now });

    static object NewWitness(long playerId, float x, float y, float z, bool attacker, bool hasPosition = true)
    {
        object witness = Activator.CreateInstance(Witness);
        Witness.GetField("PlayerId").SetValue(witness, playerId);
        Witness.GetField("Name").SetValue(witness, "p" + playerId);
        Witness.GetField("HasPosition").SetValue(witness, hasPosition);
        Witness.GetField("Position").SetValue(witness, new UnityEngine.Vector3(x, y, z));
        Witness.GetField("Attacker").SetValue(witness, attacker);
        return witness;
    }

    /// <summary>The character ids SelectWitnesses keeps, in order.</summary>
    static List<long> SelectWitnesses(float radius, params object[] candidates)
    {
        var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Witness));
        foreach (var c in candidates) list.Add(c);
        var kept = (System.Collections.IList)Witnesses.GetMethod("Select", AnyStatic)
            .Invoke(null, new object[] { new UnityEngine.Vector3(0f, 0f, 0f), radius, list });
        var ids = new List<long>();
        foreach (var w in kept) ids.Add((long)Witness.GetField("PlayerId").GetValue(w));
        return ids;
    }

    static List<string> FilesNamed(List<string> fileNames, string playerName) =>
        (List<string>)Ledger.GetMethod("FilesNamed", BindingFlags.NonPublic | BindingFlags.Static)
                            .Invoke(null, new object[] { fileNames, playerName });

    static readonly Type Merchant = typeof(MarketplaceNpc);

    static int ClosingPrice(int paid, int amount, int currentPrice, params int[] shown) =>
        (int)Merchant.GetMethod("ClosingPrice", BindingFlags.NonPublic | BindingFlags.Static)
                     .Invoke(null, new object[] { paid, amount, currentPrice, shown });

    static readonly Type ShownPricesType = typeof(MarketplaceNpc).Assembly.GetType("NpcValheim.Npc.ShownPrices");

    /// <summary>
    /// Whether a method's own body calls `methodName` or loads the string `literal`, read
    /// straight from its IL. Not a disassembler: every byte is tried as the start of a call or
    /// ldstr, and a token only counts when it resolves to exactly the thing asked for -- so it
    /// can miss nothing that is there, and four random bytes resolving to precisely that
    /// method or string is not a coincidence worth planning for.
    /// </summary>
    static bool BodyReaches(Type owner, string method, string methodName, string literal)
    {
        var info = owner.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        var il = info?.GetMethodBody()?.GetILAsByteArray();
        if (il == null) throw new InvalidOperationException(owner.Name + "." + method + " has no body to read");

        var module = info.Module;
        for (int i = 0; i + 4 < il.Length; i++)
        {
            int token = BitConverter.ToInt32(il, i + 1);
            try
            {
                if ((il[i] == 0x28 || il[i] == 0x6F) && module.ResolveMethod(token).Name == methodName) return true;
                if (il[i] == 0x72 && module.ResolveString(token) == literal) return true;
            }
            // A token that does not resolve here -- not a method, or a member of a game
            // assembly this harness does not carry -- is not the call being looked for. The
            // positive controls in Main prove real ones are still found.
            catch (Exception e) when (!(e is OutOfMemoryException)) { }
        }
        return false;
    }

    // ---- Treinador: the catalog, the ledger and the grant are internal, reached by name ----

    static readonly Type TrainerCatalogType = ModType("TrainerCatalog");
    static readonly Type TrainerLedgerType = ModType("TrainerLedger");
    static readonly Type TrainerType = ModType("Trainer");
    static readonly Type TrainerLessonsType = ModType("TrainerLessons");

    static string TrainerConst(string name) =>
        (string)TrainerCatalogType.GetField(name, AnyStatic).GetRawConstantValue();

    static TrainerPrices ParseTrainerPrices(string raw, List<string> problems) =>
        (TrainerPrices)TrainerCatalogType.GetMethod("ParsePrices", AnyStatic).Invoke(null, new object[] { raw, problems });

    static List<TrainerCap> ParseTrainerCaps(string raw, List<string> problems) =>
        (List<TrainerCap>)TrainerCatalogType.GetMethod("ParseCaps", AnyStatic).Invoke(null, new object[] { raw, problems });

    static int CapFor(List<TrainerCap> caps, int maxLevel, out string from, params string[] passes)
    {
        var args = new object[] { caps, new HashSet<string>(passes, StringComparer.Ordinal), maxLevel, null };
        int cap = (int)TrainerCatalogType.GetMethod("CapFor", AnyStatic).Invoke(null, args);
        from = (string)args[3];
        return cap;
    }

    static string LessonRefusal(TrainerPrices prices, int level, int cap, int used, int perWeek, out int target,
        out TrainerLessonBand band)
    {
        var args = new object[] { prices, level, cap, used, perWeek, 0, null };
        var refusal = (string)TrainerCatalogType.GetMethod("LessonRefusal", AnyStatic).Invoke(null, args);
        target = (int)args[5];
        band = (TrainerLessonBand)args[6];
        return refusal;
    }

    static string WeekOf(DateTime day) =>
        (string)TrainerLedgerType.GetMethod("WeekOf", AnyStatic).Invoke(null, new object[] { day });

    static string LessonLine(long playerId, string week, string skill, int target, string name, string token) =>
        (string)TrainerLedgerType.GetMethod("FormatLine", AnyStatic).Invoke(null, new object[]
        {
            playerId, week, skill, target, "deadcoins:15", new DateTime(2026, 10, 14, 12, 0, 0), name, token,
        });

    static List<TrainerLessonRecord> ParseLessons(string[] lines, List<string> problems) =>
        (List<TrainerLessonRecord>)TrainerLedgerType.GetMethod("Parse", AnyStatic).Invoke(null, new object[] { lines, problems });

    static int CountWeek(List<TrainerLessonRecord> lessons, long playerId, string week) =>
        (int)TrainerLedgerType.GetMethod("CountWeek", AnyStatic, null,
                new[] { typeof(IEnumerable<TrainerLessonRecord>), typeof(long), typeof(string) }, null)
            .Invoke(null, new object[] { lessons, playerId, week });

    static string PackGrant(TrainerGrant grant) =>
        (string)typeof(TrainerGrant).GetMethod("Pack", AnyMember).Invoke(grant, null);

    static TrainerGrant UnpackGrant(string payload)
    {
        var args = new object[] { payload, null };
        return (bool)typeof(TrainerGrant).GetMethod("TryUnpack", AnyStatic).Invoke(null, args) ? (TrainerGrant)args[1] : null;
    }

    static string TrainerBuyPayload(string item, string method, int price, string skill, int level) =>
        (string)TrainerType.GetMethod("BuyPayload", AnyStatic).Invoke(null, new object[] { item, method, price, skill, level });

    static bool TryParseSkill(string name) =>
        (bool)TrainerLessonsType.GetMethod("TryParseSkill", AnyStatic).Invoke(null, new object[] { name, null });

    /// <summary>Whether this NPC type answers server-decided requests itself, rather than
    /// inheriting NpcBase's refusal.</summary>
    static bool HandlesOnServer(Type npc, string dispatcher) =>
        npc.GetMethod(dispatcher, BindingFlags.NonPublic | BindingFlags.Instance)
           ?.DeclaringType == npc;

    static int Main()
    {
        GiveTheModALogger();

        System.Console.WriteLine("== objective encoding ==");

        var steps = new List<QuestObjective>
        {
            new QuestObjective { Kind = QuestObjectiveKind.Kill,    Target = "Boar",  Amount = 2 },
            new QuestObjective { Kind = QuestObjectiveKind.Collect, Target = "Neck",  Amount = 3 },
            new QuestObjective { Kind = QuestObjectiveKind.Explore, Target = "-346,-118", Amount = 30 },
        };
        var progress = new QuestProgress();
        progress.SetCounterAt(0, 1);
        progress.SetCounterAt(2, 7);

        string packed = PackObjectives(steps, progress);
        System.Console.WriteLine("  packed: " + packed);

        var view = new QuestView();
        var unpack = Giver.GetMethod("UnpackObjectives", BindingFlags.NonPublic | BindingFlags.Static);
        var back = (List<QuestObjectiveView>)unpack.Invoke(null, new object[] { packed });

        Check("round-trips every objective", back.Count == 3, "got " + back.Count);
        Check("kinds survive", back[0].Kind == QuestObjectiveKind.Kill &&
                               back[1].Kind == QuestObjectiveKind.Collect &&
                               back[2].Kind == QuestObjectiveKind.Explore);
        Check("goals survive", back[0].Goal == 2 && back[1].Goal == 3 && back[2].Goal == 30);
        Check("counters land on the right objective",
              back[0].Counter == 1 && back[1].Counter == 0 && back[2].Counter == 7,
              string.Join(",", back.Select(b => b.Counter)));

        // The one that actually bites: Explore stores a place as "x,z", and the reward-item
        // encoding right next to it uses ',' as its separator.
        Check("an Explore target keeps its comma", back[2].Target == "-346,-118", back[2].Target);

        System.Console.WriteLine();
        System.Console.WriteLine("== hostile input ==");

        var nasty = new List<QuestObjective>
        {
            new QuestObjective { Kind = QuestObjectiveKind.Talk, Target = "Rei|Eldgar*o;Grande", Amount = 1 },
        };
        var nastyBack = (List<QuestObjectiveView>)unpack.Invoke(
            null, new object[] { PackObjectives(nasty, null) });
        Check("separators in a target cannot split the record", nastyBack.Count == 1,
              "got " + nastyBack.Count);
        Check("no separator survives into the target",
              nastyBack.Count == 1 && !nastyBack[0].Target.Contains("|") &&
              !nastyBack[0].Target.Contains("*") && !nastyBack[0].Target.Contains(";"),
              nastyBack.Count == 1 ? nastyBack[0].Target : "n/a");

        var empty = (List<QuestObjectiveView>)unpack.Invoke(null, new object[] { "" });
        Check("an empty field yields no objectives", empty.Count == 0);

        var junk = (List<QuestObjectiveView>)unpack.Invoke(null, new object[] { "not*a*record" });
        Check("a malformed record is dropped, not thrown on", junk.Count == 0);

        System.Console.WriteLine();
        System.Console.WriteLine("== full line ==");
        System.Console.WriteLine("  FieldCount = " + FieldCount);

        // A whole quest line built to the documented field order, parsed by the real Unpack.
        string line = string.Join(";", new[]
        {
            "totem", "Totem", "desc", "objtext", "1", "2", "1", "0", "0", "0",
            "recompensa", "25", "250", "Coins*25", "0", "Boar", "0", "", "1",
        }) + ";" + packed;

        Check("the built line has exactly FieldCount fields",
              line.Split(';').Length == FieldCount, line.Split(';').Length.ToString());

        var quests = QuestGiverNpc.UnpackPublic(line);
        Check("the line parses", quests.Count == 1, "got " + quests.Count);
        if (quests.Count == 1)
        {
            var q = quests[0];
            Check("id survives", q.Id == "totem", q.Id);
            Check("objectives reach the client", q.Objectives.Count == 3, "got " + q.Objectives.Count);
            Check("reward items still parse alongside", q.RewardItems.Count == 1);
            Check("repeats flag survives", q.Repeats);
            Check("first objective mirrors the legacy fields",
                  q.Objective == QuestObjectiveKind.Kill && q.Target == "Boar");
        }

        // A line one field short is what a client sees when talking to an older server.
        var stale = QuestGiverNpc.UnpackPublic(string.Join(";", line.Split(';').Take(FieldCount - 1)));
        Check("a short line is rejected rather than half-read", stale.Count == 0, "got " + stale.Count);

        System.Console.WriteLine();
        System.Console.WriteLine("== progress counters ==");
        var p2 = new QuestProgress();
        Check("an unset counter reads zero", p2.CounterAt(0) == 0 && p2.CounterAt(9) == 0);
        p2.SetCounterAt(3, 5);
        Check("setting a far index fills the gap", p2.Counters.Count == 4 && p2.CounterAt(3) == 5);
        Check("Counter still means objective zero", p2.Counter == p2.CounterAt(0));
        p2.SetCounterAt(-1, 9);
        Check("a negative index is ignored", p2.Counters.Count == 4);

        System.Console.WriteLine();
        System.Console.WriteLine("== authenticated player resolution ==");
        Check("a peer resolves only through its exact character ZDO",
              IdentityMatches(41, 99, 41, true, true, true));
        Check("a peer resolves through exact server-owned Player ZDO",
              IdentityMatches(41, 99, 88, true, false, false, true));
        Check("a peer cannot fall back to a coincident Player id",
              !IdentityMatches(41, 41, 88, true, true, false));
        Check("a peer without a character ZDO fails closed",
              !IdentityMatches(41, 41, 41, true, false, false));
        Check("a peer cannot use another Player's network-owned ZDO",
              !IdentityMatches(41, 99, 88, true, false, false, false));
        Check("the explicit local path accepts an exact Player id",
              IdentityMatches(41, 41, 88, false, false, false));
        Check("the explicit local path accepts an exact ZDO user id",
              IdentityMatches(41, 99, 41, false, false, false));
        Check("zero is never an authenticated sender",
              !IdentityMatches(0, 0, 0, false, false, false));

        var playerPatch = typeof(GameApi).Assembly.GetType("NpcValheim.Patches.PlayerNpcPatch");
        var awakePostfix = playerPatch?.GetMethod(
            "Awake", BindingFlags.NonPublic | BindingFlags.Static);
        Check("Player.Awake has an NPC registry cleanup postfix",
              awakePostfix != null && awakePostfix.GetCustomAttributes(false)
                  .Any(a => a.GetType().FullName == "HarmonyLib.HarmonyPostfix"));

        System.Console.WriteLine();
        System.Console.WriteLine("== teleporter routes ==");

        var packedRoutes = PackRoutes(new List<TeleportDestination>
        {
            Route("aaaa1111", "Praca", 3, "Coins"),
            Route("bbbb2222", "Mina", 0, ""),
        });
        var readBack = ParseRoutes(packedRoutes);
        Check("a packed route list reads back whole", readBack.Count == 2, packedRoutes);
        Check("a route keeps its own price and item",
              readBack[0].Cost == 3 && readBack[0].CostItem == "Coins");
        Check("a route with no item of its own reads back empty, not null",
              readBack[1].Cost == 0 && readBack[1].CostItem == "");
        Check("a route keeps its position through the round trip",
              UnityEngine.Vector3.Distance(readBack[0].Position,
                  new UnityEngine.Vector3(120.5f, 31.25f, -640.75f)) < 0.01f);

        // The row written before routes could name an item. It has to keep reading, or every
        // travel network built so far goes blank on update.
        var legacy = ParseRoutes("cccc3333;Porto;10;20;30;180;7");
        Check("a seven-field route still reads",
              legacy.Count == 1 && legacy[0].Cost == 7 && legacy[0].CostItem == "");
        Check("a six-field row is refused", ParseRoutes("dddd4444;Porto;10;20;30;180").Count == 0);
        Check("a row with a non-numeric price is refused",
              ParseRoutes("eeee5555;Porto;10;20;30;180;caro").Count == 0);
        Check("a row with no name is refused", ParseRoutes("ffff6666;;10;20;30;180;1").Count == 0);

        System.Console.WriteLine();
        System.Console.WriteLine("== requests reach the server ==");

        // This is what was actually broken: a ZNetView RPC runs on whoever owns the ZDO, and
        // a Character's ZDO belongs to the nearest player -- at a teleporter, the person
        // using it. Both dispatchers below are the server-addressed channels that replace it.
        Check("the teleporter handles admin edits on the server channel",
              HandlesOnServer(Teleporter, "DispatchAdminMutation"));
        Check("the teleporter handles travel requests on the server channel",
              HandlesOnServer(Teleporter, "DispatchServiceAction"));
        Check("the quest giver still shares that same request channel",
              HandlesOnServer(Giver, "DispatchServiceAction"));
        Check("the Deadcoins counter decides purchases on the server channel",
              HandlesOnServer(typeof(DeadcoinShopNpc), "DispatchServiceAction"));
        Check("the merchant sells boss passes on the server channel",
              HandlesOnServer(typeof(MarketplaceNpc), "DispatchServiceAction"));
        Check("the merchant takes its boss lock on the admin channel",
              HandlesOnServer(typeof(MarketplaceNpc), "DispatchAdminMutation"));
        Check("the boss pass NPC sells passes on the server channel",
              HandlesOnServer(typeof(BossPassNpc), "DispatchServiceAction"));
        Check("the Mestre das Montarias decides purchases on the server channel",
              HandlesOnServer(typeof(MountTrainerNpc), "DispatchServiceAction"));
        Check("the Treinador decides purchases on the server channel",
              HandlesOnServer(typeof(TrainerNpc), "DispatchServiceAction"));

        System.Console.WriteLine();
        System.Console.WriteLine("== Deadcoins counter ==");

        // The DonationShop default, first entry exactly as it shipped. No reader can make
        // sense of it, and it must cost the other fifteen nothing.
        const string oldDefault =
            "prefabs=Blueberriesamount=50;price=150|prefab=Raspberry;amount=50;price=150|prefab=Thistle;amount=50;price=200|prefab=Cloudberry;amount=50;price=100|prefab=Wood;amount=50;price=100|prefab=Stone;amount=50;price=100|prefab=RoundLog;amount=50;price=100|prefab=FineWood;amount=50;price=150|prefab=IronNails;amount=10;price=110|prefab=IronOre;amount=50;price=700|prefab=SilverOre;amount=50;price=1000|prefab=GreydwarfEye;amount=500;price=250|prefab=SurtlingCore;amount=100;price=100|prefab=PortalToken;amount=1;price=750|prefab=ResetToken;amount=1;price=250|prefab=Coins;amount=1000;price=500";
        var problems = new List<string>();
        var old = ParseOffers(oldDefault, problems);
        Check("the old default loses only its malformed entry", old.Count == 15, "got " + old.Count);
        Check("and says which one it dropped",
              problems.Count == 1 && problems[0].Contains("Blueberriesamount"), string.Join(" / ", problems));
        Check("an entry keeps its amount and price",
              old.Count == 15 && old[0].Prefab == "Raspberry" && old[0].Amount == 50 && old[0].Price == 150);

        var repaired = ParseOffers(DefaultDeadcoinItems, null);
        Check("the new default is whole", repaired.Count == 10 && repaired[0].Prefab == "PortalToken",
              "got " + repaired.Count);

        // What the old server believed from the client, now refused in the list itself.
        Check("a free item is refused", ParseOffers("prefab=Wood;amount=50;price=0", null).Count == 0);
        Check("a negative price is refused", ParseOffers("prefab=Wood;amount=50;price=-100", null).Count == 0);
        Check("a non-positive amount is refused",
              ParseOffers("prefab=Wood;amount=0;price=10|prefab=Stone;amount=-5;price=10", null).Count == 0);
        Check("a non-numeric price is refused", ParseOffers("prefab=Wood;amount=5;price=caro", null).Count == 0);

        var twice = ParseOffers("prefab=Wood;amount=5;price=10|prefab=Wood;amount=500;price=1", null);
        Check("a repeated item keeps its first price",
              twice.Count == 1 && twice[0].Amount == 5 && twice[0].Price == 10);
        var loose = ParseOffers(" Prefab = Wood ; AMOUNT = 5 ; price = 10 |", null);
        Check("spacing and key case do not matter",
              loose.Count == 1 && loose[0].Prefab == "Wood" && loose[0].Price == 10);
        Check("an empty list is empty, not an error", ParseOffers("", null).Count == 0);

        // What the counter's admin page writes, read back the way the counter reads it.
        var changed = SetDeadcoinOffer(DefaultDeadcoinItems, new DeadcoinOffer { Prefab = "Pet_galinha", Amount = 1, Price = 90 });
        var rereadCounter = ParseOffers(changed, null);
        Check("saving an item changes it in place",
              rereadCounter.Count == 10 && rereadCounter[2].Prefab == "Pet_galinha" && rereadCounter[2].Amount == 1 && rereadCounter[2].Price == 90,
              changed);
        changed = SetDeadcoinOffer(" Prefab = Wood ; AMOUNT = 5 ; price = 10 |broken", new DeadcoinOffer { Prefab = "Wood", Amount = 6, Price = 11 });
        Check("a hand-written entry is matched, and an unreadable one is left alone",
              changed == "prefab=Wood;amount=6;price=11|broken", changed);
        changed = SetDeadcoinOffer("", new DeadcoinOffer { Prefab = "DeadToken", Amount = 1, Price = 750 });
        Check("an item is added to an empty list", changed == "prefab=DeadToken;amount=1;price=750", changed);
        changed = RemoveDeadcoinOffer(DefaultDeadcoinItems, "FichaBatizar", out bool counterRemoved);
        Check("removing an item keeps the others",
              counterRemoved && ParseOffers(changed, null).Count == 9 && ParseOffers(changed, null).TrueForAll(o => o.Prefab != "FichaBatizar"));
        RemoveDeadcoinOffer(DefaultDeadcoinItems, "fichabatizar", out counterRemoved);
        Check("prefabs compare exactly, as purchases look them up", !counterRemoved);

        System.Console.WriteLine();
        System.Console.WriteLine("== Deadcoins counter: sale windows (A1) ==");

        var windowed = ParseOffers("prefab=FichaHalloween;amount=1;price=400;from=2026-10-24;to=2026-11-07", null);
        string FormatOffer(DeadcoinOffer o) => (string)Catalog.GetMethod("Format", AnyStatic).Invoke(null, new object[] { o });
        Check("a window parses into From/To",
              windowed.Count == 1 && FormatOffer(windowed[0]) == "prefab=FichaHalloween;amount=1;price=400;from=2026-10-24;to=2026-11-07",
              windowed.Count > 0 ? FormatOffer(windowed[0]) : "none");
        // FichaHalloween left the shipped list in 0.1.74; a window on the server's own cfg is
        // what the parse above covers.
        Check("from after to is refused",
              ParseOffers("prefab=Wood;amount=1;price=10;from=2026-11-07;to=2026-10-24", null).Count == 0);
        Check("a malformed date is refused",
              ParseOffers("prefab=Wood;amount=1;price=10;from=24-10-2026", null).Count == 0);
        Check("no window at all is always on sale",
              OnSaleOn(ParseOffers("prefab=Wood;amount=1;price=10", null)[0], new System.DateTime(2026, 1, 1)));
        var fromTo = ParseOffers("prefab=Wood;amount=1;price=10;from=2026-10-24;to=2026-11-07", null)[0];
        Check("before the window is not on sale", !OnSaleOn(fromTo, new System.DateTime(2026, 10, 23)));
        Check("the first day of the window is on sale", OnSaleOn(fromTo, new System.DateTime(2026, 10, 24)));
        Check("the last day of the window is on sale", OnSaleOn(fromTo, new System.DateTime(2026, 11, 7)));
        Check("after the window is not on sale", !OnSaleOn(fromTo, new System.DateTime(2026, 11, 8)));
        Check("Format round-trips a window", Catalog.GetMethod("Format", AnyStatic)
                  .Invoke(null, new object[] { fromTo }).ToString() == "prefab=Wood;amount=1;price=10;from=2026-10-24;to=2026-11-07");

        System.Console.WriteLine();
        System.Console.WriteLine("== Mestre das Montarias ==");

        var mountProblems = new List<string>();
        var shipped = ParseMountOffers(DefaultMountOffers, mountProblems);
        Check("the default offers are whole", shipped.Count == 10 && mountProblems.Count == 0,
              shipped.Count + " / " + string.Join(" / ", mountProblems));
        Check("the default sells the first level for Coins and the capybara for Deadcoins",
              shipped.Count == 10 &&
              OfferField(shipped[0], "Kind") == "Skill" && OfferField(shipped[0], "Rank") == "1" &&
              OfferField(shipped[0], "Gold") == "500" && OfferField(shipped[0], "Deadcoins") == "0" &&
              OfferField(shipped[4], "Kind") == "Mount" && OfferField(shipped[4], "Id") == "capivara" &&
              OfferField(shipped[4], "Gold") == "0" && OfferField(shipped[4], "Deadcoins") == "300");
        Check("A4: Mestre and Voadora sell for both currencies, Aprendiz and Experiente stay Coins-only",
              OfferField(shipped[2], "Id") == "3" && OfferField(shipped[2], "Gold") == "10000" && OfferField(shipped[2], "Deadcoins") == "500" &&
              OfferField(shipped[3], "Id") == "4" && OfferField(shipped[3], "Gold") == "20000" && OfferField(shipped[3], "Deadcoins") == "1000" &&
              OfferField(shipped[1], "Deadcoins") == "0");
        var boar = shipped.Cast<object>().FirstOrDefault(o => OfferField(o, "Id") == "boar");
        var moderOffer = shipped.Cast<object>().FirstOrDefault(o => OfferField(o, "Id") == "moder");
        Check("A4: the Boar sells for both currencies, the Moder too",
              boar != null && OfferField(boar, "Gold") == "1000" && OfferField(boar, "Deadcoins") == "300" &&
              moderOffer != null && OfferField(moderOffer, "Gold") == "10000" && OfferField(moderOffer, "Deadcoins") == "600");
        Check("the previous javali setting resolves to the current capivara id",
              ResolveMountId("javali", "capivara", "neck") == "capivara");
        Check("current mount ids remain unchanged",
              ResolveMountId("neck", "capivara", "neck") == "neck");
        Check("unknown mount ids remain unavailable",
              ResolveMountId("javali", "neck") == null && ResolveMountId("unknown", "capivara") == null);

        // ---- NPC dialogue: one packed ZDO string, written by the server, read by every client ----
        var dialogueType = ModType("NpcDialogue");
        Func<DialogueSettings, string> dlgPack = d =>
            (string)dialogueType.GetMethod("Pack", AnyStatic).Invoke(null, new object[] { d });
        Func<string, DialogueSettings> dlgUnpack = s =>
            (DialogueSettings)dialogueType.GetMethod("Unpack", AnyStatic).Invoke(null, new object[] { s });
        var dlgSpoken = new DialogueSettings
        {
            Greet = new List<string> { "Olá, {jogador}!", "  ", "Bem-vindo" },
            Talk = new List<string> { "linha\ncom quebra" },
            Bye = new List<string>(),
            GreetEmote = "wave",
            TalkEmote = "dance",
            ByeEmote = "bow",
        };
        var dlgBack = dlgUnpack(dlgPack(dlgSpoken));
        Check("dialogue survives the ZDO round trip",
              dlgBack.Greet.SequenceEqual(new[] { "Olá, {jogador}!", "Bem-vindo" }) && dlgBack.Bye.Count == 0 &&
              dlgBack.GreetEmote == "wave" && dlgBack.ByeEmote == "bow",
              string.Join(" | ", dlgBack.Greet));
        Check("a phrase cannot break the packing", dlgBack.Talk.Count == 1 && dlgBack.Talk[0] == "linha com quebra",
              string.Join(" | ", dlgBack.Talk));
        Check("a looping or unknown emote is dropped", dlgBack.TalkEmote == "", dlgBack.TalkEmote);
        Check("an empty or foreign dialogue string reads as silence",
              dlgUnpack("").Greet.Count == 0 && dlgUnpack("9\u001fx").GreetEmote == "");
        var dlgMany = new DialogueSettings { Greet = Enumerable.Range(0, 40).Select(i => new string('a', 400)).ToList() };
        var dlgCapped = dlgUnpack(dlgPack(dlgMany));
        Check("phrases are capped in number and length",
              dlgCapped.Greet.Count == 12 && dlgCapped.Greet.All(p => p.Length == 160), dlgCapped.Greet.Count.ToString());

        Check("a free level is refused", ParseMountOffers("skill=1;price=0", null).Count == 0);
        Check("a negative price is refused", ParseMountOffers("mount=javali;price=-5;currency=deadcoins", null).Count == 0);
        Check("level 0 is refused", ParseMountOffers("skill=0;price=10", null).Count == 0);
        Check("a level and a mount in one entry is refused",
              ParseMountOffers("skill=1;mount=javali;price=10", null).Count == 0);
        Check("an entry with neither is refused", ParseMountOffers("price=10;currency=coins", null).Count == 0);
        Check("an unknown currency is refused, not read as Coins",
              ParseMountOffers("skill=1;price=10;currency=ouro", null).Count == 0);

        var noCurrency = ParseMountOffers("skill=1;price=10", null);
        Check("no currency means Coins",
              noCurrency.Count == 1 && OfferField(noCurrency[0], "Gold") == "10" && OfferField(noCurrency[0], "Deadcoins") == "0");
        var anyCase = ParseMountOffers(" Mount = javali ; PRICE = 5 ; currency = DeadCoins |", null);
        Check("spacing and case do not matter",
              anyCase.Count == 1 && OfferField(anyCase[0], "Id") == "javali" &&
              OfferField(anyCase[0], "Deadcoins") == "5" && OfferField(anyCase[0], "Gold") == "0");

        // A4: two entries for the same id in different currencies are one offer with both
        // prices, not a duplicate -- same rule a boss pass has always had for Gold/Deadcoins.
        var merged = ParseMountOffers("mount=javali;price=5;currency=deadcoins|mount=javali;price=1000;currency=coins", null);
        Check("A4: a mount priced in both currencies merges into one offer",
              merged.Count == 1 && OfferField(merged[0], "Deadcoins") == "5" && OfferField(merged[0], "Gold") == "1000");
        var repeatedOffer = ParseMountOffers("mount=javali;price=5;currency=deadcoins|mount=javali;price=1;currency=deadcoins", null);
        Check("a repeated offer in the same currency keeps its first price",
              repeatedOffer.Count == 1 && OfferField(repeatedOffer[0], "Deadcoins") == "5");

        System.Console.WriteLine();
        System.Console.WriteLine("== Deadcoins balance files ==");

        // The server builds these from the connection; the old mod took the id from the client.
        Check("a Steam socket id gets the Steam_ prefix the old files used",
              CanonicalAccountId("76561198000000001") == "Steam_76561198000000001");
        Check("a crossplay id is already in that form",
              CanonicalAccountId("Steam_76561198000000001") == "Steam_76561198000000001" &&
              CanonicalAccountId("Xbox_2535400000000000") == "Xbox_2535400000000000");
        Check("no id is no account", CanonicalAccountId("") == "" && CanonicalAccountId(null) == "");
        Check("the file name is the one DonationShop wrote",
              FileNameFor("Ragnar", "Steam_76561198000000001") == "Ragnar-Steam_76561198000000001.json");
        Check("a name cannot leave the folder",
              FileNameFor("../Ragnar", "Steam_1") == null && FileNameFor("..\\Ragnar", "Steam_1") == null);
        Check("an account cannot leave the folder", FileNameFor("Ragnar", "Steam_1/../x") == null);
        Check("a missing part is refused", FileNameFor("", "Steam_1") == null && FileNameFor("Ragnar", "") == null);

        // An admin credits an offline player by name; the file has to be theirs and only theirs.
        var ledgerFiles = new List<string>
        {
            "Ragnar-Steam_76561198000000001.json", "ragnar2-Steam_2.json", "Ana-Maria-Steam_3.json",
            "Bjorn-Steam_4.json", "Bjorn-Xbox_5.json", "notes.txt",
        };
        Check("a name finds its file, case ignored",
              FilesNamed(ledgerFiles, "RAGNAR").Count == 1 && FilesNamed(ledgerFiles, "RAGNAR")[0] == ledgerFiles[0]);
        Check("a name with a dash still matches", FilesNamed(ledgerFiles, "Ana-Maria").Count == 1);
        Check("a prefix of a name is not the name", FilesNamed(ledgerFiles, "Ana").Count == 0 &&
                                                    FilesNamed(ledgerFiles, "Rag").Count == 0);
        Check("two accounts with one name are both reported", FilesNamed(ledgerFiles, "Bjorn").Count == 2);
        Check("no name matches nothing", FilesNamed(ledgerFiles, " ").Count == 0);

        System.Console.WriteLine();
        System.Console.WriteLine("== boss pass prices ==");

        var bossProblems = new List<string>();
        var bosses = ParseBosses(DefaultBosses, bossProblems);
        Check("the default lists the six bosses the server owner priced, Eikthyr left out",
              string.Join(",", bosses.Select(b => b.Boss)) == "gd_king,Bonemass,Dragon,GoblinKing,SeekerQueen,Fader",
              string.Join(",", bosses.Select(b => b.Boss)));
        Check("and parses without a complaint", bossProblems.Count == 0, string.Join(" / ", bossProblems));
        // A2 (14/10/2026): every default is gold=0 -- a pass comes only from killing the boss
        // or from Deadcoins now, never from Coins.
        Check("the swamp pass is Deadcoins-only at 500 (R$5 at 100 per real)",
              bosses.Count > 0 && bosses[0].Gold == 0 && bosses[0].Deadcoins == 500 &&
              bosses[0].Shop == "Loja do Pântano");
        Check("the Deep North pass is Deadcoins-only at 20000 (R$200)",
              bosses.Count == 6 && bosses[5].Gold == 0 && bosses[5].Deadcoins == 20000);
        Check("the price ladder matches the table, boss by boss, gold=0 throughout",
              string.Join(",", bosses.Select(b => b.Gold + "/" + b.Deadcoins)) ==
              "0/500,0/1000,0/2500,0/5000,0/10000,0/20000");

        Check("a negative price is refused", ParseBosses("boss=Bonemass;gold=-1;deadcoins=10", null).Count == 0);
        Check("a non-numeric price is refused", ParseBosses("boss=Bonemass;gold=muito", null).Count == 0);
        Check("an entry without a boss is refused", ParseBosses("name=Bonemass;gold=10", null).Count == 0);
        Check("a boss id that would break the wire is refused",
              ParseBosses("boss=gd king;gold=10|boss=a,b;gold=10", null).Count == 0);
        var unpriced = new List<string>();
        var killOnly = ParseBosses("boss=Eikthyr", unpriced);
        Check("a boss with no price is kept (kill only) and said so",
              killOnly.Count == 1 && killOnly[0].Gold == 0 && killOnly[0].Deadcoins == 0 && unpriced.Count == 1);
        Check("a boss with no name shows its prefab", killOnly.Count == 1 && killOnly[0].Name == "Eikthyr");
        var repeated = ParseBosses("boss=Dragon;gold=5|boss=Dragon;gold=1", null);
        Check("a repeated boss keeps its first price", repeated.Count == 1 && repeated[0].Gold == 5);
        Check("an empty list is empty, not an error", ParseBosses("", null).Count == 0);

        System.Console.WriteLine();
        System.Console.WriteLine("== boss pass ledger ==");

        string passLine = LedgerLine(76561, "gd_king", "kill", "Rag;nar");
        Check("a pass line starts with the character and the boss", passLine.StartsWith("76561;gd_king;kill;"), passLine);
        Check("a name cannot add a field to the line", passLine.Split(';').Length == 5, passLine);
        var ledgerProblems = new List<string>();
        var held = ParseLedger(new[]
        {
            "# comentario",
            "",
            passLine,
            "76561;Bonemass;gold:3000;2026-10-01 12:00:00;Ragnar",
            "99;Dragon",
            "abc;Dragon;x",
            "0;Dragon;x",
            "77;gd king;x",
        }, ledgerProblems);
        Check("a written pass reads back", held.ContainsKey(76561) && held[76561].Contains("gd_king"));
        Check("one character holds several passes", held.ContainsKey(76561) && held[76561].Count == 2);
        Check("a hand-added two-field line is a pass", held.ContainsKey(99) && held[99].Contains("Dragon"));
        Check("lines that are not passes are skipped and named",
              held.Count == 2 && ledgerProblems.Count == 3, string.Join(" / ", ledgerProblems));

        // How a pass was won, which the boss pass NPC shows next to each boss.
        Check("a kill reads as a kill", KindOf("kill") == "kill");
        Check("a Coins purchase reads as Coins, whatever the price", KindOf("gold:3000") == "gold");
        Check("a Deadcoins purchase reads as Deadcoins", KindOf(" Deadcoins:500 ") == "deadcoins");
        Check("anything an admin typed reads as the admin's", KindOf("presente do Werner") == "admin" &&
              KindOf("") == "admin" && KindOf(null) == "admin" && KindOf("killer") == "admin");
        var sources = ParseLedgerSources(new[]
        {
            "5;Dragon;kill;2026-10-01 12:00:00;Ragnar",
            "5;Dragon;gold:5000;2026-10-02 12:00:00;Ragnar",
            "5;Bonemass",
        });
        Check("a pass listed twice keeps how it was first won",
              sources.ContainsKey(5) && sources[5]["Dragon"] == "kill");
        Check("a two-field line has no source, so it is the admin's",
              sources.ContainsKey(5) && sources[5]["Bonemass"] == "" && KindOf(sources[5]["Bonemass"]) == "admin");
        Check("the NPC's request names the pass before the counter's request",
              ChosenBuyPayload("Dragon", "deadcoins", 2500) == "Dragon\ndeadcoins\n2500");

        System.Console.WriteLine();
        System.Console.WriteLine("== boss deaths ==");

        // The order a kill arrives in: the defeat key, then the ZDO.
        var matcher = NewMatcher(20f);
        Check("a defeat key alone is not a kill", Key(matcher, "defeated_gdking", 100f) == null);
        Check("the boss vanishing right after it is", Gone(matcher, NewDeath("gd_king", "defeated_gdking", 101f), 101f));

        // And the other order, should the network deliver it so.
        var gone = NewDeath("Bonemass", "defeated_bonemass", 200f);
        Check("a boss vanishing alone is not a kill", !Gone(matcher, gone, 200f));
        Check("its defeat key a moment later confirms it", ReferenceEquals(Key(matcher, "defeated_bonemass", 203f), gone));

        Check("a key does not confirm a different boss",
              !Gone(matcher, NewDeath("Dragon", "defeated_dragon", 300f), 300f) &&
              Key(matcher, "defeated_gdking", 301f) == null);
        Check("a key from too long ago does not count",
              Key(matcher, "defeated_goblinking", 400f) == null &&
              !Gone(matcher, NewDeath("GoblinKing", "defeated_goblinking", 425f), 425f));
        Check("one key confirms one death, not two",
              Key(matcher, "defeated_queen", 500f) == null &&
              Gone(matcher, NewDeath("SeekerQueen", "defeated_queen", 501f), 501f) &&
              !Gone(matcher, NewDeath("SeekerQueen", "defeated_queen", 502f), 502f));
        Check("a boss with no defeat key counts on its own", Gone(matcher, NewDeath("Modded", "", 600f), 600f));
        Check("keys match regardless of case",
              Key(matcher, "Defeated_Fader", 700f) == null &&
              Gone(matcher, NewDeath("Fader", "defeated_fader", 701f), 701f));

        System.Console.WriteLine();
        System.Console.WriteLine("== who was at the fight ==");

        Check("someone standing within the radius counts", SelectWitnesses(60f, NewWitness(1, 40f, 0f, 30f, false)).Count == 1);
        Check("height does not count against them (Moder dies in the air)",
              SelectWitnesses(60f, NewWitness(1, 10f, 500f, 0f, false)).Count == 1);
        Check("someone outside it does not", SelectWitnesses(60f, NewWitness(1, 61f, 0f, 0f, false)).Count == 0);
        Check("someone who hit the boss counts from anywhere",
              SelectWitnesses(60f, NewWitness(1, 900f, 0f, 0f, true)).Count == 1);
        Check("an attacker the server cannot place still counts",
              SelectWitnesses(60f, NewWitness(1, 0f, 0f, 0f, true, hasPosition: false)).Count == 1);
        Check("a character the server cannot place, who did not hit it, does not",
              SelectWitnesses(60f, NewWitness(1, 0f, 0f, 0f, false, hasPosition: false)).Count == 0);
        Check("an unresolved character is never given a pass",
              SelectWitnesses(60f, NewWitness(0, 0f, 0f, 0f, true)).Count == 0);
        var crowd = SelectWitnesses(60f, NewWitness(1, 1f, 0f, 0f, false), NewWitness(2, 500f, 0f, 0f, false),
            NewWitness(3, 2f, 0f, 0f, true), NewWitness(1, 3f, 0f, 0f, true));
        Check("a party counts once each, the far bystander left out",
              string.Join(",", crowd) == "1,3", string.Join(",", crowd));
        Check("a broken radius counts only the attackers",
              string.Join(",", SelectWitnesses(float.NaN, NewWitness(1, 1f, 0f, 0f, false), NewWitness(2, 1f, 0f, 0f, true))) == "2");

        System.Console.WriteLine();
        System.Console.WriteLine("== merchant counter: what the client says it paid ==");

        // The live hole: "amount;paid" is the client's own word, and the counter used to pay
        // it back -- in full for an item not for sale, as change above the price otherwise.
        Check("a 2e9 claim for a 1-coin item buys the item at 1",
              ClosingPrice(2000000000, 1, 1) == 1);
        Check("a 2e9 claim for an item that is not for sale buys nothing",
              ClosingPrice(2000000000, 1, 0) == 0);
        // What capping the refund at the price would have left open: 2000 units at the
        // 1,000,000 ceiling cost 2e9, and one coin short used to come back almost whole.
        Check("one coin short of an expensive stack buys nothing",
              ClosingPrice(1999999999, 2000, 1000000) == 0);
        Check("an honest payment at the posted price buys",
              ClosingPrice(50, 10, 5) == 5);

        // The race the old refund existed for: the admin changes the price between the
        // client reading it and the request landing. The buyer gets the goods at the price
        // they paid, instead of coins.
        Check("a price raised a moment ago still sells at the one the buyer saw",
              ClosingPrice(10, 10, 2, 1) == 1);
        Check("an item removed a moment ago still sells at the price the buyer saw",
              ClosingPrice(50, 5, 0, 10) == 10);
        Check("a recent price counts only for exactly what the amount costs at it",
              ClosingPrice(49, 5, 0, 10) == 0 && ClosingPrice(51, 5, 0, 10) == 0);
        Check("a claim that matches no price the counter showed buys nothing",
              ClosingPrice(7, 10, 2, 1, 3) == 0);
        Check("a free or negative claim never buys a removed item",
              ClosingPrice(0, 1, 0, 0) == 0 && ClosingPrice(-5, 1, 0, -5) == 0);
        // 50000 x 100000 wraps to +705,032,704 in int; PayoutFor refuses it on both paths.
        Check("an overflowing stack is no price, current or recent",
              ClosingPrice(705032704, 50000, 100000, 100000) == 0);

        var shownPrices = Activator.CreateInstance(ShownPricesType, nonPublic: true);
        var remember = ShownPricesType.GetMethod("Remember");
        var pricesFor = ShownPricesType.GetMethod("For");
        remember.Invoke(shownPrices, new object[] { new Dictionary<string, int> { { "Wood", 3 }, { "Stone", 0 } }, 60f });
        remember.Invoke(shownPrices, new object[] { new Dictionary<string, int> { { "Wood", 5 } }, 90f });
        var woodAt30 = (List<int>)pricesFor.Invoke(shownPrices, new object[] { "Wood", 30f });
        var woodAt75 = (List<int>)pricesFor.Invoke(shownPrices, new object[] { "Wood", 75f });
        var woodAt91 = (List<int>)pricesFor.Invoke(shownPrices, new object[] { "Wood", 91f });
        Check("every price a counter showed is honoured inside its window",
              woodAt30.Count == 2 && woodAt30.Contains(3) && woodAt30.Contains(5), string.Join(",", woodAt30));
        Check("and each one stops being honoured when its window ends",
              woodAt75.Count == 1 && woodAt75[0] == 5 && woodAt91.Count == 0, string.Join(",", woodAt75));
        Check("a price of zero is never remembered as one somebody paid",
              ((List<int>)pricesFor.Invoke(shownPrices, new object[] { "Stone", 0f })).Count == 0);

        // Lots and per-item boss passes ride extra ';' fields; a table without them must stay
        // byte-for-byte what it was, and a field that does not parse must drop the entry
        // rather than sell it per unit or unlocked.
        var sells = (System.Collections.IDictionary)Merchant.GetMethod("ParseSellTable", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { "Wood;10;0;0;50;Eikthyr\nStone;2;0;0\nResin;5;3;20;1;gd_king\nBad;5;0;0;0\nBad2;5;0;0;x;Eikthyr" });
        SellOffer O(string k) => sells.Contains(k) ? (SellOffer)sells[k] : null;
        Check("a sell line carries its lot and its boss",
              O("Wood")?.Lot == 50 && O("Wood")?.Boss == "Eikthyr" && O("Resin")?.Lot == 1 && O("Resin")?.Boss == "gd_king");
        Check("an old four-field sell line is one unit, open to anyone",
              O("Stone")?.Lot == 1 && O("Stone")?.Boss == "");
        Check("a sell line with a bad lot is dropped", O("Bad") == null && O("Bad2") == null,
              string.Join(",", sells.Keys.Cast<string>()));
        string formatted = (string)Merchant.GetMethod("FormatSellTable", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { sells });
        Check("the sell table writes back what it read",
              formatted == "Wood;10;0;0;50;Eikthyr\nStone;2;0;0\nResin;5;3;20;1;gd_king", formatted.Replace("\n", "|"));

        const string buyTable = "Wood;1;50;Eikthyr\nStone;2\nFlint;3;10\nBad;4;0";
        var buyPrices = (Dictionary<string, int>)Merchant.GetMethod("ParsePriceTable", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { buyTable });
        var buyLots = (Dictionary<string, int>)Merchant.GetMethod("ParseLotTable", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { buyTable });
        var buyBosses = (Dictionary<string, string>)Merchant.GetMethod("ParseBuyBossTable", BindingFlags.NonPublic | BindingFlags.Static)
            .Invoke(null, new object[] { buyTable });
        Check("a buy line carries price, lot and boss",
              buyPrices.Count == 3 && buyPrices["Wood"] == 1 && buyLots["Wood"] == 50 && buyBosses["Wood"] == "Eikthyr" &&
              buyLots["Flint"] == 10 && !buyBosses.ContainsKey("Flint") && !buyLots.ContainsKey("Stone") && !buyPrices.ContainsKey("Bad"),
              string.Join(",", buyPrices.Keys));

        // Structural: the purchase path has no coin payout left in it at all. The two
        // positive controls make sure the IL reader is actually finding calls and strings.
        Check("the reader sees the auction's refund call (control)",
              BodyReaches(Merchant, "RPC_Buy", "Refund", null));
        Check("the reader sees the payout string in a sale to the NPC (control)",
              BodyReaches(Merchant, "RPC_SellToNpc", null, "paid"));
        Check("buying from the counter can no longer pay coins back",
              !BodyReaches(Merchant, "RPC_BuyFromNpc", "Refund", "paid") &&
              !BodyReaches(Merchant, "RPC_BuyFromNpc", "GiveCoins", null));

        // A ZNetView RPC runs on whichever peer owns the NPC, normally the nearest player, so
        // no trade may be registered there any more: each one goes through the server channel.
        Check("the merchant decides every trade on the server channel",
              new[] { "RPC_BuyFromNpc", "RPC_SellToNpc", "RPC_Buy", "RPC_Sell", "RPC_CancelListing", "RPC_RequestMarketData" }
                  .All(rpc => !BodyReaches(Merchant, "RegisterRpc", null, rpc)) &&
              BodyReaches(Merchant, "RegisterRpc", null, "RPC_MarketData"));

        System.Console.WriteLine();
        System.Console.WriteLine("== Correio ==");

        Check("the mailbox decides everything on the server channel",
              HandlesOnServer(typeof(MailboxNpc), "DispatchServiceAction"));
        Check("the mailbox registers no ZNetView RPC of its own",
              typeof(MailboxNpc).GetMethod("RegisterRpc", BindingFlags.NonPublic | BindingFlags.Instance)?.DeclaringType != typeof(MailboxNpc));

        var mailWire = typeof(MailboxNpc).Assembly.GetType("NpcValheim.Persistence.MailWire");
        var letter = new NpcValheim.Persistence.MailEntry
        {
            Id = "abc", Subject = "Oi; tudo", ItemName = "", Quality = 1, Amount = 0, Coins = 0,
            SenderName = "Ragnar", Body = "linha 1\nlinha; 2", Read = true,
        };
        var packedMail = (string)mailWire.GetMethod("Pack", AnyStatic)
            .Invoke(null, new object[] { new List<NpcValheim.Persistence.MailEntry> { letter } });
        var unpackedMail = (List<NpcValheim.Persistence.MailEntry>)mailWire.GetMethod("Unpack", AnyStatic)
            .Invoke(null, new object[] { packedMail });
        Check("a letter crosses the wire whole",
              unpackedMail.Count == 1 && unpackedMail[0].Id == "abc" && unpackedMail[0].SenderName == "Ragnar" &&
              unpackedMail[0].Body == "linha 1\nlinha, 2" && unpackedMail[0].Read && unpackedMail[0].IsMessage,
              packedMail);

        var unpackSend = typeof(MailboxNpc).GetMethod("TryUnpackSend", AnyStatic);
        var packSend = typeof(MailboxNpc).GetMethod("PackSend", AnyStatic);
        object[] SendArgs(string request) => new object[] { request, null, null, null, null, null, null };
        var parcelArgs = SendArgs((string)packSend.Invoke(null, new object[] { "Ragnar", "Pacote", "toma;\nisso", "Wood", 1, 50 }));
        Check("a parcel request carries recipient and stack",
              (bool)unpackSend.Invoke(null, parcelArgs) && (string)parcelArgs[1] == "Ragnar" &&
              (string)parcelArgs[3] == "toma,\nisso" && (string)parcelArgs[4] == "Wood" && (int)parcelArgs[6] == 50);
        var letterArgs = SendArgs((string)packSend.Invoke(null, new object[] { "Ragnar", "Oi", "", "", 1, 0 }));
        Check("a plain letter has no attachment",
              (bool)unpackSend.Invoke(null, letterArgs) && (string)letterArgs[4] == "" && (int)letterArgs[6] == 0);
        Check("a request in the old house format is refused",
              !(bool)unpackSend.Invoke(null, SendArgs("house;Clã;Oi;corpo;;1;0")));
        Check("a negative attachment is refused",
              !(bool)unpackSend.Invoke(null, SendArgs("Ragnar;Oi;corpo;Wood;1;-5")));
        Check("houses are gone from the mod",
              typeof(MailboxNpc).Assembly.GetType("NpcValheim.Persistence.HouseDatabase") == null &&
              typeof(NpcValheim.Persistence.MailEntry).GetProperty("HouseName") == null);

        System.Console.WriteLine();
        System.Console.WriteLine("== Treinador: config ==");

        var settingsNow = (TrainerSettings)typeof(TrainerSettings).GetMethod("Current", AnyStatic).Invoke(null, null);
        Check("without a cfg the numbers are the approved ones (+50% 60 min below 50, -50% PvE only, cap 50, 5 a week)",
              settingsNow.StudyBonus == 0.5f && settingsNow.StudyMinutes == 60 && settingsNow.StudyMaxSkill == 50 &&
              settingsNow.ProtectionFactor == 0.5f && settingsNow.ProtectionPveOnly && settingsNow.LessonMaxLevel == 50 &&
              settingsNow.LessonsPerWeek == 5);
        Check("the default caps are the approved ladder",
              TrainerConst("DefaultCaps") == "none:15|gd_king:25|Bonemass:35|Dragon:45|GoblinKing:50", TrainerConst("DefaultCaps"));

        var trainerProblems = new List<string>();
        var trainerPrices = ParseTrainerPrices(TrainerConst("DefaultPrices"), trainerProblems);
        Check("the default price list parses without a complaint", trainerProblems.Count == 0, string.Join(" / ", trainerProblems));
        Check("study and protection are sold in both currencies",
              trainerPrices.Study != null && trainerPrices.Study.Gold > 0 && trainerPrices.Study.Deadcoins > 0 &&
              trainerPrices.Protection != null && trainerPrices.Protection.Gold > 0 && trainerPrices.Protection.Deadcoins > 0);
        bool contiguous = trainerPrices.Lessons.Count > 0 && trainerPrices.Lessons[0].From == 1 &&
                          trainerPrices.Lessons[trainerPrices.Lessons.Count - 1].To == 50;
        for (int i = 1; i < trainerPrices.Lessons.Count; i++)
            contiguous &= trainerPrices.Lessons[i].From == trainerPrices.Lessons[i - 1].To + 1;
        Check("the default lessons price every target level from 1 to 50, no gap",
              contiguous, string.Join(",", trainerPrices.Lessons.Select(b => b.From + "-" + b.To)));
        Check("the bands follow the pass ladder (15, 25, 35, 45, 50)",
              string.Join(",", trainerPrices.Lessons.Select(b => b.To)) == "15,25,35,45,50");
        Check("a lesson costs more the higher it goes",
              trainerPrices.Lessons.Zip(trainerPrices.Lessons.Skip(1), (a, b) => b.Gold > a.Gold && b.Deadcoins > a.Deadcoins).All(x => x));
        Check("a lesson is priced by its target level",
              trainerPrices.LessonTo(15)?.To == 15 && trainerPrices.LessonTo(16)?.From == 16 && trainerPrices.LessonTo(51) == null &&
              trainerPrices.LessonTo(0) == null);

        var badPrices = new List<string>();
        var parsedBad = ParseTrainerPrices(
            "item=study;gold=-1;deadcoins=5|item=protection;gold=0;deadcoins=0|item=potion;gold=5|" +
            "item=lesson;levels=10-5;gold=5|item=lesson;levels=0-3;gold=5|item=lesson;gold=5|item=lesson;levels=1-101;gold=5|" +
            "item=lesson;levels=7;gold=70;deadcoins=0|item=lesson;levels=5-8;gold=1|item=study;gold=9;deadcoins=9|item=study;gold=1",
            badPrices);
        Check("a negative price is refused", parsedBad.Study == null || parsedBad.Study.Gold == 9);
        Check("a thing with no price is not sold", parsedBad.Protection == null);
        Check("an unknown item, a backwards band and a band outside 1-100 are refused",
              parsedBad.Lessons.Count == 1, string.Join(",", parsedBad.Lessons.Select(b => b.From + "-" + b.To)));
        Check("a single level is a band of one, sold only in the currency with a price",
              parsedBad.Lessons.Count == 1 && parsedBad.Lessons[0].From == 7 && parsedBad.Lessons[0].To == 7 &&
              parsedBad.Lessons[0].Gold == 70 && parsedBad.Lessons[0].Deadcoins == 0);
        Check("an overlapping band loses to the first", parsedBad.LessonTo(8) == null);
        Check("a repeated study keeps its first good price", parsedBad.Study != null && parsedBad.Study.Gold == 9);
        Check("every bad entry is named in the log", badPrices.Count == 9, string.Join(" / ", badPrices));
        Check("an empty price list sells nothing",
              ParseTrainerPrices("", null).Study == null && ParseTrainerPrices("", null).Lessons.Count == 0);

        System.Console.WriteLine();
        System.Console.WriteLine("== Treinador: teto da aula pelo passe ==");

        var capProblems = new List<string>();
        var caps = ParseTrainerCaps(TrainerConst("DefaultCaps"), capProblems);
        Check("the default caps parse whole", caps.Count == 5 && capProblems.Count == 0, string.Join(" / ", capProblems));
        Check("without a pass lessons go to 15", CapFor(caps, 50, out string capFrom) == 15 && capFrom == null);
        Check("the Elder's pass opens 25", CapFor(caps, 50, out capFrom, "gd_king") == 25 && capFrom == "gd_king");
        Check("a bought Bonemass pass opens 35 without the Elder's", CapFor(caps, 50, out capFrom, "Bonemass") == 35 && capFrom == "Bonemass");
        Check("the best pass wins", CapFor(caps, 50, out capFrom, "gd_king", "GoblinKing", "Dragon") == 50 && capFrom == "GoblinKing");
        Check("a pass with no step of its own changes nothing", CapFor(caps, 50, out capFrom, "SeekerQueen") == 15);
        Check("LessonMaxLevel is the ceiling whatever the pass", CapFor(caps, 40, out capFrom, "GoblinKing") == 40);
        Check("without a none step, no pass means no lessons", CapFor(ParseTrainerCaps("gd_king:25", null), 50, out capFrom) == 0);
        var badCaps = new List<string>();
        var parsedCaps = ParseTrainerCaps("none:abc|gd king:20|Dragon:101|NONE:10|none:30|Bonemass:-1|:5|Dragon:44", badCaps);
        Check("bad steps are refused and named, the first none wins",
              parsedCaps.Count == 2 && parsedCaps[0].Boss == null && parsedCaps[0].Level == 10 && parsedCaps[1].Level == 44 &&
              badCaps.Count == 6, string.Join(" / ", badCaps));

        Check("a lesson inside the cap is sold, priced by its target",
              LessonRefusal(trainerPrices, 14, 15, 0, 5, out int lessonTarget, out var lessonBand) == null &&
              lessonTarget == 15 && lessonBand != null && lessonBand.To == 15);
        Check("a lesson past the cap is refused", LessonRefusal(trainerPrices, 15, 15, 0, 5, out _, out lessonBand) != null && lessonBand == null);
        Check("the fifth lesson of the week is the last",
              LessonRefusal(trainerPrices, 3, 15, 4, 5, out _, out _) == null &&
              LessonRefusal(trainerPrices, 3, 15, 5, 5, out _, out _) != null);
        Check("LessonsPerWeek 0 is no limit", LessonRefusal(trainerPrices, 3, 15, 999, 0, out _, out _) == null);
        Check("a target with no price is refused", LessonRefusal(trainerPrices, 50, 60, 0, 5, out _, out _) != null);
        Check("a level the game cannot have is refused",
              LessonRefusal(trainerPrices, -1, 15, 0, 5, out _, out _) != null &&
              LessonRefusal(trainerPrices, 100, 100, 0, 5, out _, out _) != null);
        Check("a cap of 0 says a boss pass opens the lessons",
              (LessonRefusal(trainerPrices, 0, 0, 0, 5, out _, out _) ?? "").Contains("passe"));

        System.Console.WriteLine();
        System.Console.WriteLine("== Treinador: aulas da semana ==");

        Check("a week opens on Monday", WeekOf(new DateTime(2026, 10, 14, 23, 59, 0)) == "2026-10-12" &&
                                        WeekOf(new DateTime(2026, 10, 12, 0, 0, 1)) == "2026-10-12");
        Check("Sunday is still the same week", WeekOf(new DateTime(2026, 10, 18, 23, 59, 59)) == "2026-10-12");
        Check("the next Monday is a new week", WeekOf(new DateTime(2026, 10, 19, 0, 0, 0)) == "2026-10-19");
        Check("a week can start in the previous year", WeekOf(new DateTime(2027, 1, 1)) == "2026-12-28");

        string lessonLine = LessonLine(76561, "2026-10-12", "Swords", 15, "Rag;nar", "abc123");
        Check("a lesson line has its eight fields, whatever the name", lessonLine.Split(';').Length == 8, lessonLine);
        var lessonProblems = new List<string>();
        var lessons = ParseLessons(new[]
        {
            "# comentario",
            lessonLine,
            LessonLine(76561, "2026-10-12", "Axes", 7, "Ragnar", "def456"),
            LessonLine(76561, "2026-10-05", "Axes", 6, "Ragnar", "old"),
            LessonLine(42, "2026-10-12", "Bows", 3, "Bjorn", "bjorn1"),
            "76561;2026-10-12",
            "abc;2026-10-12;Swords",
            "0;2026-10-12",
            "77;",
        }, lessonProblems);
        Check("a written lesson reads back", lessons.Any(l => l.Token == "abc123" && l.Skill == "Swords" && l.Target == 15));
        Check("the week counts this character's lessons of this week only",
              CountWeek(lessons, 76561, "2026-10-12") == 3 && CountWeek(lessons, 42, "2026-10-12") == 1 &&
              CountWeek(lessons, 76561, "2026-10-19") == 0);
        Check("a hand-added two-field line counts", lessons.Count(l => l.PlayerId == 76561 && l.Token == "") == 1);
        Check("lines that are not lessons are skipped and named", lessonProblems.Count == 3, string.Join(" / ", lessonProblems));

        System.Console.WriteLine();
        System.Console.WriteLine("== Treinador: o que viaja ==");

        Check("a lesson request names the skill and the level shown",
              TrainerBuyPayload("lesson", "deadcoins", 15, "Swords", 14) == "lesson\ndeadcoins\n15\nSwords\n14");
        Check("a buff request has the same five fields",
              TrainerBuyPayload("study", "gold", 1000, null, 0) == "study\ngold\n1000\n\n");
        var grant = new TrainerGrant
        {
            Token = "tok", Item = "protection", Method = "gold", Price = 1000, Balance = -1, Skill = "", Target = 0,
            Bonus = 0.5f, Minutes = 60, MaxSkill = 50, Factor = 0.35f, PveOnly = false,
        };
        string packedGrant = PackGrant(grant);
        var grantBack = UnpackGrant(packedGrant);
        Check("a grant reads back whole",
              grantBack != null && grantBack.Token == "tok" && grantBack.Item == "protection" && grantBack.Method == "gold" &&
              grantBack.Price == 1000 && grantBack.Balance == -1 && grantBack.Bonus == 0.5f && grantBack.Minutes == 60 &&
              grantBack.MaxSkill == 50 && grantBack.Factor == 0.35f && !grantBack.PveOnly, packedGrant.Replace('\n', '|'));
        var lessonGrant = UnpackGrant(PackGrant(new TrainerGrant { Token = "t2", Item = "lesson", Method = "deadcoins", Price = 15, Balance = 85, Skill = "Swords", Target = 15 }));
        Check("a lesson grant carries the skill, the target and the balance",
              lessonGrant != null && lessonGrant.Skill == "Swords" && lessonGrant.Target == 15 && lessonGrant.Balance == 85);
        var parts = packedGrant.Split('\n');
        Check("a grant with a field missing is refused", UnpackGrant(string.Join("\n", parts.Take(11))) == null);
        Check("a grant with a broken number is refused",
              UnpackGrant(packedGrant.Replace("\n1000\n", "\nmil\n")) == null);
        Check("a grant without its purchase token is refused", UnpackGrant("\n" + string.Join("\n", parts.Skip(1))) == null);
        Check("a grant whose PvE flag is not 0 or 1 is refused",
              UnpackGrant(string.Join("\n", parts.Take(11)) + "\n2") == null);

        Check("a skill is named exactly as the game spells it", TryParseSkill("Swords") && TryParseSkill("WoodCutting"));
        Check("no other spelling is a skill",
              !TryParseSkill("swords") && !TryParseSkill("1") && !TryParseSkill("Swords, Knives") && !TryParseSkill("") &&
              !TryParseSkill(null) && !TryParseSkill(" Swords"));
        Check("None and All are not skills to teach", !TryParseSkill("None") && !TryParseSkill("All"));

        // The rule the release check enforces by compiling against the game's own assembly:
        // Skills.GetSkill is private there, so the lesson reaches it by reflection only.
        // Control on the same game type: a Player member does not resolve outside the game
        // (its fields need Unity modules this harness does not carry), a Skills one does.
        Check("the reader sees a call to a Skills method, GetSkillList in RawLevel (control)",
              BodyReaches(TrainerLessonsType, "RawLevel", "GetSkillList", null));
        Check("the lesson never calls the private Skills.GetSkill directly",
              !BodyReaches(TrainerLessonsType, "Apply", "GetSkill", null));
        Check("a refused undo cannot refund: the server only pays back what it has a purchase for",
              BodyReaches(TrainerType, "RPC_Undo", "TryGetValue", null));

        // ---- Admin saves: the "Preços" tab and the Mercador's per-item pass ----
        var priceEditor = typeof(QuestGiverNpc).Assembly.GetType("NpcValheim.Npc.PriceEditor");
        string ReplacePrice(string raw, string seen, string key, int value, out string error)
        {
            var args = new object[] { raw, seen, key, value, null };
            var result = (string)priceEditor.GetMethod("ReplacePrice", BindingFlags.NonPublic | BindingFlags.Static)
                                            .Invoke(null, args);
            error = (string)args[4];
            return result;
        }
        const string passes = "boss=gd_king;name=Ancião;shop=Loja do Pântano;gold=0;deadcoins=500|" +
                              "boss=Bonemass;name=Bonemass;shop=Loja da Montanha;gold=0;deadcoins=1000";
        string seenElder = "boss=gd_king;name=Ancião;shop=Loja do Pântano;gold=0;deadcoins=500";
        string afterGold = ReplacePrice(passes, seenElder, "gold", 300, out string err1);
        Check("a pass price saves (control)",
              afterGold == passes.Replace("gold=0;deadcoins=500", "gold=300;deadcoins=500"), err1 ?? afterGold);
        // The reported bug: the Deadcoins row still holds the entry with gold=0.
        string afterBoth = ReplacePrice(afterGold, seenElder, "deadcoins", 50, out string err2);
        Check("the second price of the same pass saves after the first changed the entry",
              afterBoth == passes.Replace("gold=0;deadcoins=500", "gold=300;deadcoins=50"), err2 ?? afterBoth);
        Check("the other pass is left exactly as it was",
              afterBoth != null && afterBoth.EndsWith("|boss=Bonemass;name=Bonemass;shop=Loja da Montanha;gold=0;deadcoins=1000"));
        const string mounts = "skill=3;price=10000;currency=coins|skill=3;price=500;currency=deadcoins";
        string afterMount = ReplacePrice(mounts, "skill=3;price=400;currency=deadcoins", "price", 450, out string err3);
        Check("an entry is told apart by everything but its price (currency included)",
              afterMount == "skill=3;price=10000;currency=coins|skill=3;price=450;currency=deadcoins", err3 ?? afterMount);
        Check("an entry that is gone is refused",
              ReplacePrice(passes, "boss=Dragon;name=Moder;shop=x;gold=0;deadcoins=1", "gold", 1, out string err4) == null && err4 != null);
        Check("two entries with the same name are refused, not guessed",
              ReplacePrice("boss=a;gold=1|boss=a;gold=2", "boss=a;gold=9", "gold", 3, out string err5) == null && err5 != null);
        Check("the server's price save goes through the tolerant match",
              BodyReaches(priceEditor, "SetInList", "ReplacePrice", null));

        var keepBoss = typeof(MarketplaceNpc).GetMethod("ItemBossAfterPriceEdit", BindingFlags.NonPublic | BindingFlags.Static);
        string BossAfter(bool given, string requested, string existing) =>
            (string)keepBoss.Invoke(null, new object[] { given, requested, existing });
        Check("a Mercador price saved with no boss keeps the item's pass", BossAfter(false, "", "gd_king") == "gd_king");
        Check("a Mercador price saved with a boss sets that pass (control)", BossAfter(true, "Bonemass", "gd_king") == "Bonemass");
        Check("RPC_SetPrice decides the pass through that rule",
              BodyReaches(typeof(MarketplaceNpc), "RPC_SetPrice", "ItemBossAfterPriceEdit", null));

        System.Console.WriteLine();
        System.Console.WriteLine(failed == 0 ? $"ALL {passed} CHECKS PASSED" : $"{failed} FAILED, {passed} passed");
        return failed == 0 ? 0 : 1;
    }
}
