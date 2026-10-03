using System;
using System.IO;
using System.Linq;
using System.Reflection;
using LiteDB;
using NpcValheim.Persistence;

class Program
{
    static int failed = 0, passed = 0;
    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { passed++; System.Console.WriteLine("  PASS  " + what); }
        else { failed++; System.Console.WriteLine("  FAIL  " + what + (detail.Length > 0 ? "  -- " + detail : "")); }
    }

    /// <summary>Points BepInEx's Paths at a throwaway folder and gives Plugin.Log somewhere
    /// to write, so the real ContentSeeder/QuestStore/NpcConfigStore run untouched.</summary>
    static void Bootstrap(string pluginRoot)
    {
        var paths = typeof(BepInEx.Paths);
        var prop = paths.GetProperty("PluginPath", BindingFlags.Public | BindingFlags.Static);
        var setter = prop.GetSetMethod(true);
        if (setter != null) setter.Invoke(null, new object[] { pluginRoot });
        else paths.GetField("<PluginPath>k__BackingField",
                 BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, pluginRoot);

        var plugin = Type.GetType("NpcValheim.Plugin, NpcValheim");
        plugin.GetField("Log", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)
              .SetValue(null, new BepInEx.Logging.ManualLogSource("contentcheck"));
    }

    /// <summary>
    /// The pass ledger against a real file, including the two edits an admin makes by hand
    /// while the server runs: taking a pass away and giving one.
    /// </summary>
    static void CheckBossPassLedger()
    {
        System.Console.WriteLine();
        System.Console.WriteLine("== boss pass ledger (file) ==");

        var ledger = Type.GetType("NpcValheim.Npc.BossPassLedger, NpcValheim");
        const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        string path = (string)ledger.GetProperty("FilePath", anyStatic).GetValue(null);
        bool Has(long id, string boss) => (bool)ledger.GetMethod("Has", anyStatic).Invoke(null, new object[] { id, boss });
        bool Grant(long id, string name, string boss, string how) =>
            (bool)ledger.GetMethod("Grant", anyStatic).Invoke(null, new object[] { id, name, boss, how });
        string Kind(long id, string boss)
        {
            var kinds = (System.Collections.Generic.IDictionary<string, string>)ledger.GetMethod("KindsOf", anyStatic)
                .Invoke(null, new object[] { id });
            return kinds.TryGetValue(boss, out var kind) ? kind : null;
        }

        if (File.Exists(path)) File.Delete(path);
        Check("nobody holds a pass before the file exists", !Has(5001, "gd_king"));
        Check("a kill writes a pass", Grant(5001, "Ragnar", "gd_king", "kill"));
        Check("the file is created with its explanation", File.Exists(path) &&
              File.ReadAllText(path).StartsWith("# Passes de loja por boss"));
        Check("the pass is held by that character, for that boss only",
              Has(5001, "gd_king") && !Has(5001, "Bonemass") && !Has(5002, "gd_king"));
        Check("a second grant is no grant", !Grant(5001, "Ragnar", "gd_king", "gold:1000"));
        Check("and writes no second line",
              File.ReadAllLines(path).Count(l => l.StartsWith("5001;gd_king;")) == 1);

        // An admin deletes the line with the server running.
        File.WriteAllLines(path, File.ReadAllLines(path).Where(l => !l.StartsWith("5001;")).ToArray());
        Check("deleting the line takes the pass away", !Has(5001, "gd_king"));

        // And gives one by hand, in the short form.
        File.AppendAllText(path, "5002;Bonemass" + Environment.NewLine);
        Check("a hand-written line gives a pass", Has(5002, "Bonemass"));
        Check("the character can win it again after losing it", Grant(5001, "Ragnar", "gd_king", "kill") &&
              Has(5001, "gd_king") && Has(5002, "Bonemass"));

        // What the boss pass NPC shows next to each boss.
        Check("a pass won in a fight says so", Kind(5001, "gd_king") == "kill");
        Check("a hand-written pass is the admin's", Kind(5002, "Bonemass") == "admin");
        Check("a bought pass says what paid for it", Grant(5001, "Ragnar", "Dragon", "gold:5000") &&
              Kind(5001, "Dragon") == "gold");
        File.AppendAllText(path, "5002;SeekerQueen;deadcoins:10000;2026-10-03 09:00:00;Bravo" + Environment.NewLine);
        Check("and so does one an admin copied in", Kind(5002, "SeekerQueen") == "deadcoins");
        Check("a pass nobody holds has no kind", Kind(5002, "Dragon") == null);
    }

    /// <summary>
    /// Exercises the real LiteDB-backed market/mail implementations against throwaway files.
    /// These are integration checks, not mocks: listing transactions, the durable outbox and
    /// the claim state machine all run through the same public methods used by the mod.
    /// </summary>
    static void CheckEconomyAndMail(string root)
    {
        string economyRoot = Path.Combine(root, "economy");
        Directory.CreateDirectory(economyRoot);
        string marketPath = Path.Combine(economyRoot, "market.db");
        string mailPath = Path.Combine(economyRoot, "mail.db");

        MailDatabase.Init(mailPath);
        MarketDatabase.Init(marketPath);
        try
        {
            const long sellerId = 41001;
            const long buyerId = 41002;
            const string boardId = "integration-board";

            System.Console.WriteLine();
            System.Console.WriteLine("== economy transaction and outbox ==");

            var listing = MarketDatabase.AddListing(
                boardId, sellerId, "Seller", "Wood", quality: 2,
                amount: 10, pricePerUnit: 5, duration: TimeSpan.FromHours(1));
            Check("a valid integration listing is persisted", listing != null);

            bool bought = MarketDatabase.Buy(
                listing.Id, boardId, buyerId, amount: 4, taxPercent: 10, paid: 25,
                out var boughtFrom, out var refund, out var buyError);
            Check("a purchase commits", bought, buyError ?? "");
            // paid 25 for 20: a listing's price never changes, so only a client that says it
            // paid more than it did ever gets here -- and change is what it was after.
            Check("a purchase gives no change on a claimed overpayment",
                  bought && refund == 0, "refund=" + refund);
            Check("a partial purchase leaves the remaining stock",
                  boughtFrom != null && boughtFrom.Amount == 6 &&
                  MarketDatabase.GetListings(boardId).Single().Amount == 6);

            var sellerMail = MailDatabase.GetMail(sellerId);
            var buyerMail = MailDatabase.GetMail(buyerId);
            Check("a purchase creates the seller coin delivery",
                  sellerMail.Count == 1 && sellerMail[0].Coins == 18 &&
                  string.IsNullOrEmpty(sellerMail[0].ItemName),
                  sellerMail.Count == 0 ? "missing" : "coins=" + sellerMail[0].Coins);
            Check("a purchase creates the buyer item delivery",
                  buyerMail.Count == 1 && buyerMail[0].ItemName == "Wood" &&
                  buyerMail[0].Quality == 2 && buyerMail[0].Amount == 4,
                  buyerMail.Count == 0 ? "missing" : buyerMail[0].ItemName + " x" + buyerMail[0].Amount);
            Check("a purchase produces exactly two deliveries",
                  sellerMail.Count + buyerMail.Count == 2,
                  "got " + (sellerMail.Count + buyerMail.Count));

            int mailCountAfterBuy = MailDatabase.CountMail(sellerId) + MailDatabase.CountMail(buyerId);
            Check("flushing an empty outbox is idempotent",
                  MarketDatabase.FlushOutbox() == 0 && MarketDatabase.FlushOutbox() == 0 &&
                  MailDatabase.CountMail(sellerId) + MailDatabase.CountMail(buyerId) == mailCountAfterBuy);

            // Simulate the precise crash window the outbox is designed for: mail insertion
            // committed, but the matching outbox row was not deleted before shutdown.
            const long replayPlayerId = 41003;
            const string replayId = "integration-outbox-replay";
            MailDatabase.SendItem(replayPlayerId, "Replay", "Stone", 1, 7, replayId);
            using (var marketDb = new LiteDatabase(marketPath))
            {
                marketDb.GetCollection<EconomyDelivery>("delivery_outbox").Insert(
                    new EconomyDelivery
                    {
                        Id = replayId,
                        PlayerId = replayPlayerId,
                        Subject = "Replay",
                        ItemName = "Stone",
                        Quality = 1,
                        Amount = 7,
                        CreatedUtcTicks = DateTime.UtcNow.Ticks,
                    });
            }

            int firstReplayFlush = MarketDatabase.FlushOutbox();
            int secondReplayFlush = MarketDatabase.FlushOutbox();
            var replayMail = MailDatabase.GetMail(replayPlayerId)
                .Where(entry => entry.Id == replayId).ToList();
            Check("a committed-mail outbox replay is consumed",
                  firstReplayFlush == 1 && secondReplayFlush == 0,
                  firstReplayFlush + "/" + secondReplayFlush);
            Check("outbox replay cannot duplicate a parcel",
                  replayMail.Count == 1 && replayMail[0].Amount == 7,
                  "got " + replayMail.Count);

            const long cancelOwnerId = 41004;
            var cancelledListing = MarketDatabase.AddListing(
                boardId, cancelOwnerId, "Owner", "FineWood", quality: 3,
                amount: 6, pricePerUnit: 9, duration: TimeSpan.FromHours(1));
            int cancelledAmount = MarketDatabase.CancelListing(
                cancelledListing.Id, boardId, cancelOwnerId);
            var cancellationMail = MailDatabase.GetMail(cancelOwnerId);
            Check("cancelling removes the listing",
                  cancelledAmount == 6 &&
                  MarketDatabase.GetListings(boardId).All(x => x.Id != cancelledListing.Id));
            Check("cancelling returns the complete item stack",
                  cancellationMail.Count == 1 &&
                  cancellationMail[0].ItemName == "FineWood" &&
                  cancellationMail[0].Quality == 3 && cancellationMail[0].Amount == 6,
                  cancellationMail.Count == 0 ? "missing" : cancellationMail[0].Amount.ToString());

            CheckAuctionRefunds(boardId, marketPath);

            System.Console.WriteLine();
            System.Console.WriteLine("== durable mail claims ==");

            const long recipientId = 42001;
            const long intruderId = 42002;
            var parcel = MailDatabase.SendItem(
                recipientId, "Claim state", "Iron", quality: 2, amount: 3);

            Check("another user cannot begin a claim",
                  MailDatabase.BeginClaim(parcel.Id, intruderId, "intruder") == null &&
                  MailDatabase.CountMail(recipientId) == 1);

            var begun = MailDatabase.BeginClaim(parcel.Id, recipientId, "attempt-1");
            Check("the recipient can begin a claim without consuming it",
                  begun != null && MailDatabase.CountMail(recipientId) == 1);
            Check("a competing token cannot take an in-flight claim",
                  MailDatabase.BeginClaim(parcel.Id, recipientId, "attempt-2") == null);

            MailDatabase.ReleaseClaim(parcel.Id, intruderId, "attempt-1");
            Check("another user cannot release the claim",
                  MailDatabase.BeginClaim(parcel.Id, recipientId, "attempt-2") == null);

            MailDatabase.ReleaseClaim(parcel.Id, recipientId, "attempt-1");
            var retried = MailDatabase.BeginClaim(parcel.Id, recipientId, "attempt-2");
            Check("release preserves the parcel for a retry", retried != null &&
                  retried.ItemName == "Iron" && retried.Amount == 3 &&
                  MailDatabase.CountMail(recipientId) == 1);

            Check("another user cannot complete a claim",
                  !MailDatabase.CompleteClaim(parcel.Id, intruderId, "attempt-2") &&
                  MailDatabase.CountMail(recipientId) == 1);
            Check("the wrong token cannot complete a claim",
                  !MailDatabase.CompleteClaim(parcel.Id, recipientId, "wrong-token") &&
                  MailDatabase.CountMail(recipientId) == 1);
            Check("the matching recipient and token consume the parcel once",
                  MailDatabase.CompleteClaim(parcel.Id, recipientId, "attempt-2") &&
                  MailDatabase.CountMail(recipientId) == 0 &&
                  !MailDatabase.CompleteClaim(parcel.Id, recipientId, "attempt-2"));

            var guardedParcel = MailDatabase.SendCoins(recipientId, "Guarded", 11);
            Check("another user cannot use the legacy direct claim either",
                  MailDatabase.Claim(guardedParcel.Id, intruderId) == null &&
                  MailDatabase.GetMail(recipientId).Any(x => x.Id == guardedParcel.Id));
        }
        finally
        {
            MarketDatabase.Shutdown();
            MailDatabase.Shutdown();
        }
    }

    /// <summary>
    /// The buyer's `paid` is the client's own word: it pays before asking, and the server
    /// cannot look inside a remote bag. These prove a refund is worked out from the listing
    /// the server recorded and never from that word -- a modified client used to claim two
    /// billion and receive two billion.
    /// </summary>
    static void CheckAuctionRefunds(string boardId, string marketPath)
    {
        System.Console.WriteLine();
        System.Console.WriteLine("== auction refunds come from the listing, not the claim ==");

        const long sellerId = 43001;
        const long racerA = 43002, racerB = 43003, racerC = 43004;
        const string otherBoard = "integration-other-board";
        const int claim = 2000000000;

        var cheap = MarketDatabase.AddListing(boardId, sellerId, "Seller", "Wood", 1,
            amount: 3, pricePerUnit: 5, duration: TimeSpan.FromHours(1));

        Check("a 2e9 claim on a 5-coin unit buys it and gets no change",
              MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, claim, out _, out int change, out _) &&
              change == 0, "refund=" + change);

        // Refusals an honest client cannot produce: its UI only offers listings on this board
        // that are not its own, one unit at a time, at the price the listing carries.
        Check("a listing the server never had refunds nothing",
              !MarketDatabase.Buy("never-existed", boardId, racerA, 1, 0, claim, out _, out int ghost, out _) &&
              ghost == 0, "refund=" + ghost);
        Check("one coin short on a listing still there refunds nothing",
              !MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, 4, out _, out int shortPaid, out _) &&
              shortPaid == 0, "refund=" + shortPaid);
        Check("asking for more than the stock refunds nothing",
              !MarketDatabase.Buy(cheap.Id, boardId, racerA, 100, 0, claim, out _, out int tooMany, out _) &&
              tooMany == 0, "refund=" + tooMany);
        Check("the seller buying their own listing gets nothing back",
              !MarketDatabase.Buy(cheap.Id, boardId, sellerId, 1, 0, claim, out _, out int own, out _) &&
              own == 0, "refund=" + own);
        Check("a listing bought through another board refunds nothing",
              !MarketDatabase.Buy(cheap.Id, otherBoard, racerA, 1, 0, claim, out _, out int foreign, out _) &&
              foreign == 0, "refund=" + foreign);

        // The honest race: the listing leaves the board between the buyer's screen and the
        // request landing, after the coins already left the bag.
        Check("the last two units sell",
              MarketDatabase.Buy(cheap.Id, boardId, racerB, 2, 0, 10, out _, out _, out _));
        MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, claim, out _, out int raced, out var racedError);
        Check("a buyer who raced a sold-out listing gets its price back, not the claim",
              raced == 5, "refund=" + raced + " (" + racedError + ")");
        // "Comprar 1" pressed again and again on a board that went stale: each press paid.
        MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, 5, out _, out int secondPress, out _);
        MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, 5, out _, out int thirdPress, out _);
        MarketDatabase.Buy(cheap.Id, boardId, racerA, 1, 0, 5, out _, out int fourthPress, out _);
        Check("repeated presses come back up to the stock it was listed with (3), then stop",
              secondPress == 5 && thirdPress == 5 && fourthPress == 0,
              secondPress + "/" + thirdPress + "/" + fourthPress);
        MarketDatabase.Buy(cheap.Id, boardId, racerC, 50, 0, claim, out _, out int wide, out _);
        Check("one huge claim gets no more than the listing's whole stock (3 x 5)", wide == 15, "refund=" + wide);
        MarketDatabase.Buy(cheap.Id, boardId, racerC, 1, 0, claim, out _, out int wideAgain, out _);
        Check("and nothing once that is spent", wideAgain == 0, "refund=" + wideAgain);
        MarketDatabase.Buy(cheap.Id, boardId, sellerId, 1, 0, 5, out _, out int ownGone, out _);
        Check("the seller gets nothing back from their own closed listing", ownGone == 0, "refund=" + ownGone);
        MarketDatabase.Buy(cheap.Id, otherBoard, racerB, 1, 0, 5, out _, out int foreignGone, out _);
        Check("a closed listing refunds nothing through another board", foreignGone == 0, "refund=" + foreignGone);

        var cancelled = MarketDatabase.AddListing(boardId, sellerId, "Seller", "Stone", 1,
            amount: 4, pricePerUnit: 9, duration: TimeSpan.FromHours(1));
        MarketDatabase.CancelListing(cancelled.Id, boardId, sellerId);
        MarketDatabase.Buy(cancelled.Id, boardId, racerA, 1, 0, claim, out _, out int cancelRace, out _);
        Check("a buyer who raced a cancellation gets the price back", cancelRace == 9, "refund=" + cancelRace);
        MarketDatabase.Buy(cancelled.Id, boardId, racerB, 1, 0, 3, out _, out int underClaim, out _);
        Check("a claim below the price lowers the refund, never raises it", underClaim == 3, "refund=" + underClaim);

        var lapsed = MarketDatabase.AddListing(boardId, sellerId, "Seller", "Resin", 1,
            amount: 2, pricePerUnit: 7, duration: TimeSpan.FromHours(-1));
        MarketDatabase.Buy(lapsed.Id, boardId, racerA, 1, 0, claim, out _, out int lapsedRace, out var lapsedError);
        Check("an expired listing refunds its price, not the claim",
              lapsedRace == 7 && lapsedError == "Anúncio expirado", "refund=" + lapsedRace + " (" + lapsedError + ")");
        MarketDatabase.Buy(lapsed.Id, boardId, racerA, 1, 0, claim, out _, out int lapsedSecond, out _);
        Check("a second press before the sweep gets the second unit back", lapsedSecond == 7, "refund=" + lapsedSecond);
        MarketDatabase.ReturnExpiredListings();
        MarketDatabase.Buy(lapsed.Id, boardId, racerA, 1, 0, 7, out _, out int afterSweep, out _);
        Check("the sweep does not start the count over", afterSweep == 0, "refund=" + afterSweep);

        // A row from before OriginalAmount existed reads it as 0: the stock it had when it
        // went is the best record left, and the refund must not fall back to the claim.
        using (var raw = new LiteDatabase(marketPath))
        {
            raw.GetCollection("listings").Insert(new BsonDocument
            {
                ["_id"] = "legacy-listing",
                ["NpcId"] = boardId,
                ["OwnerId"] = sellerId,
                ["OwnerName"] = "Seller",
                ["ItemName"] = "Flint",
                ["Quality"] = 1,
                ["Amount"] = 4,
                ["PricePerUnit"] = 6,
                ["ExpiresUtcTicks"] = DateTime.UtcNow.AddHours(1).Ticks,
            });
        }
        Check("a listing written before OriginalAmount still sells",
              MarketDatabase.Buy("legacy-listing", boardId, racerB, 1, 0, 6, out _, out _, out _));
        MarketDatabase.CancelListing("legacy-listing", boardId, sellerId);
        MarketDatabase.Buy("legacy-listing", boardId, racerA, 10, 0, claim, out _, out int legacyRace, out _);
        Check("and refunds no more than the stock it had when it went (3 x 6)", legacyRace == 18, "refund=" + legacyRace);

        int sellerCoins = MailDatabase.GetMail(sellerId).Where(m => m.Coins > 0).Sum(m => m.Coins);
        Check("only the three real sales credited the seller (5 + 10 + 6)", sellerCoins == 21, "coins=" + sellerCoins);
    }

    static int Main(string[] args)
    {
        string root = Path.Combine(Path.GetTempPath(), "npcv-contentcheck");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        // Lay out the folder exactly as the mod ships: <plugins>/NpcValheim/Content/...
        string shipped = Path.Combine(root, "NpcValheim", "Content");
        // Where the yaml that the mod ships actually lives. Passed in by run.ps1;
        // defaults to the repo checkout this harness was built inside.
        string shippedSource = args.Length > 0 ? args[0]
            : Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                  "..", "..", "..", "..", "..", "NpcValheim", "Content"));
        foreach (var folder in new[] { "quests", "templates" })
        {
            Directory.CreateDirectory(Path.Combine(shipped, folder));
            foreach (var f in Directory.GetFiles(Path.Combine(shippedSource, folder), "*.yaml"))
                File.Copy(f, Path.Combine(shipped, folder, Path.GetFileName(f)));
        }
        Bootstrap(root);

        System.Console.WriteLine("== seeding ==");
        ContentSeeder.Run();
        string liveQuests = Path.Combine(root, "NpcValheim", "npcs", "quests");
        string liveTemplates = Path.Combine(root, "NpcValheim", "npcs", "templates");
        Check("quests were seeded", Directory.GetFiles(liveQuests, "*.yaml").Length == 248,
              Directory.GetFiles(liveQuests, "*.yaml").Length.ToString());
        Check("templates were seeded", Directory.GetFiles(liveTemplates, "*.yaml").Length == 115,
              Directory.GetFiles(liveTemplates, "*.yaml").Length.ToString());

        // The rule that matters most: an admin's edit must survive the next startup.
        string victim = Path.Combine(liveQuests, "totem.yaml");
        File.WriteAllText(victim, "name: Editado pelo admin\nobjective: Kill\ntarget: Boar\namount: 1\n");
        ContentSeeder.Run();
        Check("re-seeding never overwrites an edited file",
              File.ReadAllText(victim).Contains("Editado pelo admin"));

        System.Console.WriteLine();
        System.Console.WriteLine("== quest loading ==");
        var validatorFixture = new QuestDefinition
        {
            Id = "validator-fixture",
            Name = "Validator fixture",
            Objectives = new System.Collections.Generic.List<QuestObjective>
            {
                new QuestObjective { Kind = QuestObjectiveKind.Kill, Target = "Boar", Amount = 1 }
            },
        };
        Check("the quest validator admits a standard objective",
              QuestProgressRules.Validate(validatorFixture, out var validationError), validationError ?? "");
        var exploreFixture = new QuestObjective
        {
            Kind = QuestObjectiveKind.Explore,
            Target = "-346,-118",
            Amount = 30,
        };
        Check("an explore radius is not treated as thirty required arrivals",
              QuestProgressRules.Goal(exploreFixture) == 1 &&
              QuestProgressRules.ExploreRadius(exploreFixture) == 30);
        Check("valid explore coordinates parse with invariant decimals",
              QuestProgressRules.TryParseExploreTarget("-346.5, 118.25", out var explorePlace) &&
              Math.Abs(explorePlace.x - (-346.5f)) < 0.01f &&
              Math.Abs(explorePlace.y - 118.25f) < 0.01f);
        Check("malformed or non-finite explore coordinates are rejected",
              !QuestProgressRules.TryParseExploreTarget("onde fica", out _) &&
              !QuestProgressRules.TryParseExploreTarget("NaN,10", out _) &&
              !QuestProgressRules.TryParseExploreTarget("1000001,10", out _));
        QuestStore.Reload();
        var all = QuestStore.All;
        Check("every seeded quest loads", all.Count == 248, "got " + all.Count);

        var multi = all.Where(q => q.Steps().Count > 1).ToList();
        Check("multi-objective quests survived the yaml", multi.Count >= 30, "got " + multi.Count);

        var courolegs = QuestStore.Get("courolegs");
        Check("a two-objective quest reads back whole",
              courolegs != null && courolegs.Steps().Count == 2 &&
              courolegs.Steps()[0].Target == "Deer" && courolegs.Steps()[0].Amount == 2 &&
              courolegs.Steps()[1].Target == "Neck" && courolegs.Steps()[1].Amount == 5);
        Check("its prerequisite came across",
              courolegs != null && courolegs.RequiresQuests.Contains("courohelmet"));
        Check("its item reward kept its quality",
              courolegs != null && courolegs.Rewards.Items.Count == 1 &&
              courolegs.Rewards.Items[0].Quality == 2);

        var daily = QuestStore.Get("dly-meadowst-01");
        Check("a daily carries its cooldown", daily != null && daily.ResetHours == 30,
              daily == null ? "missing" : daily.ResetHours.ToString());
        Check("a daily carries its level gate", daily != null && daily.RequiredLevel == 1);
        Check("accents survived the whole pipeline",
              QuestStore.Get("totem-de-protecao") == null &&
              all.Any(q => q.Name.Contains("ç") || q.Name.Contains("õ") || q.Name.Contains("ã")));

        Check("no quest chain points at a quest that does not exist",
              all.All(q => q.RequiresQuests.All(r => QuestStore.Get(r) != null)));

        System.Console.WriteLine();
        System.Console.WriteLine("== templates ==");
        var forShop = NpcConfigStore.ListTemplatesFor("Marketplace");
        var forTp = NpcConfigStore.ListTemplatesFor("Teleporter");
        var forQg = NpcConfigStore.ListTemplatesFor("QuestGiver");
        Check("merchant templates are offered to merchants", forShop.Count == 54, "got " + forShop.Count);
        Check("teleporter templates are offered to teleporters", forTp.Count == 2, "got " + forTp.Count);
        Check("quest boards are offered to quest givers", forQg.Count == 59, "got " + forQg.Count);
        Check("a merchant is not offered a travel network",
              !forShop.Any(n => n.StartsWith("kg-tp-")));
        Check("a teleporter is not offered a price list",
              !forTp.Any(n => n.StartsWith("kg-quests-")) && forTp.All(n => n.StartsWith("kg-tp-")));

        var pescador = NpcConfigStore.LoadTemplate("kg-pescador");
        Check("a merchant template carries both sides of the counter",
              pescador != null && pescador.Marketplace.Sells.Count == 4 &&
              pescador.Marketplace.Buys.Count == 12,
              pescador == null ? "missing" : pescador.Marketplace.Sells.Count + "/" + pescador.Marketplace.Buys.Count);
        Check("applying it would not rename the npc",
              pescador != null && string.IsNullOrWhiteSpace(pescador.Name));

        var barqueiro = NpcConfigStore.LoadTemplate("kg-tp-barqueiro");
        Check("a travel network keeps its coordinates",
              barqueiro != null && barqueiro.Teleporter.Destinations.Count == 7 &&
              Math.Abs(barqueiro.Teleporter.Destinations[0].X - (-346)) < 0.01f);

        var board = NpcConfigStore.LoadTemplate("kg-quests-dlymeadowstorril");
        Check("a quest board lists quests that actually exist",
              board != null && board.QuestGiver.Quests.Count == 4 &&
              board.QuestGiver.Quests.All(id => QuestStore.Get(id) != null));

        // The boss lock rides the template's patch rule: written = applied, omitted = untouched.
        var application = typeof(NpcProfile).GetMethod("TypeSpecificApplication",
            BindingFlags.NonPublic | BindingFlags.Instance);
        File.WriteAllText(Path.Combine(liveTemplates, "trava-pantano.yaml"),
            "forType: Marketplace\nmarketplace:\n  requiredBoss: gd_king\n");
        File.WriteAllText(Path.Combine(liveTemplates, "destrava.yaml"),
            "forType: Marketplace\nmarketplace:\n  requiredBoss: ''\n");
        var lockTemplate = NpcConfigStore.LoadTemplate("trava-pantano");
        var unlockTemplate = NpcConfigStore.LoadTemplate("destrava");
        var lockApplied = (NpcProfile)application.Invoke(lockTemplate, new object[] { null });
        var unlockApplied = (NpcProfile)application.Invoke(unlockTemplate, new object[] { null });
        var pescadorApplied = (NpcProfile)application.Invoke(pescador, new object[] { null });
        Check("a template can lock a counter behind a boss",
              lockApplied?.Marketplace?.RequiredBoss == "gd_king", lockApplied?.Marketplace?.RequiredBoss ?? "null");
        Check("a template can open a locked counter", unlockApplied?.Marketplace?.RequiredBoss == "");
        Check("a template that says nothing about the lock leaves it alone",
              pescadorApplied?.Marketplace != null && pescadorApplied.Marketplace.RequiredBoss == null);
        Check("and still applies its price list",
              pescadorApplied?.Marketplace?.Sells != null && pescadorApplied.Marketplace.Sells.Count == 4);

        CheckBossPassLedger();

        System.Console.WriteLine();
        System.Console.WriteLine("== player directory identity ==");
        using (var directoryDb = new LiteDatabase(Path.Combine(root, "directory.db")))
        {
            typeof(PlayerDirectory).GetMethod(
                    "Attach", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { directoryDb });

            PlayerDirectory.Remember(1001, "Mesmo Nome");
            PlayerDirectory.Remember(2002, "Mesmo Nome");

            var stored = directoryDb.GetCollection<KnownPlayer>("players").FindAll().ToList();
            Check("equal display names remain separate accounts", stored.Count == 2,
                  "got " + stored.Count);
            Check("an ambiguous display name does not select an account",
                  PlayerDirectory.FindByName("Mesmo Nome") == null);

            var firstIds = PlayerDirectory.IdsFor(1001, "Mesmo Nome");
            Check("a name hint cannot pull in another account",
                  firstIds.Contains(1001) && !firstIds.Contains(2002),
                  string.Join(",", firstIds));

            PlayerDirectory.Remember(1001, "Primeiro", new long[] { 3003 });
            var authenticatedAliases = PlayerDirectory.IdsFor(3003, "Mesmo Nome");
            Check("authenticated id overlap still joins aliases",
                  authenticatedAliases.Contains(1001) && authenticatedAliases.Contains(3003) &&
                  !authenticatedAliases.Contains(2002),
                  string.Join(",", authenticatedAliases));
            Check("remembering aliases does not delete historic rows",
                  directoryDb.GetCollection<KnownPlayer>("players").Count() == 2);
        }

        CheckEconomyAndMail(root);

        System.Console.WriteLine();
        System.Console.WriteLine(failed == 0 ? $"ALL {passed} CHECKS PASSED" : $"{failed} FAILED, {passed} passed");
        return failed == 0 ? 0 : 1;
    }
}
