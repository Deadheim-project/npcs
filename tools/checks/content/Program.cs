using System;
using System.IO;
using System.Linq;
using System.Reflection;
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
        System.Console.WriteLine(failed == 0 ? $"ALL {passed} CHECKS PASSED" : $"{failed} FAILED, {passed} passed");
        return failed == 0 ? 0 : 1;
    }
}
