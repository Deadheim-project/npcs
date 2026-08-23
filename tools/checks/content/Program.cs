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
            Check("a purchase returns only the overpayment",
                  bought && refund == 5, "refund=" + refund);
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
