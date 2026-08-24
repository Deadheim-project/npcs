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

    static void Main()
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
        System.Console.WriteLine(failed == 0 ? $"ALL {passed} CHECKS PASSED" : $"{failed} FAILED, {passed} passed");
    }
}
