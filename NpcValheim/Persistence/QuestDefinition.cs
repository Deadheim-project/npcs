using System.Collections.Generic;

namespace NpcValheim.Persistence
{
    public enum QuestObjectiveKind
    {
        /// <summary>Kill N creatures whose prefab name matches Target.</summary>
        Kill,

        /// <summary>Hand over N of the item prefab named in Target. Checked against the bag at
        /// hand-in, so buying the items is as valid as finding them.</summary>
        Collect,

        /// <summary>
        /// Pick up N of the item prefab named in Target, counted as they are picked up.
        ///
        /// Different from Collect on purpose: this one asks the player to actually go and
        /// gather, and the counter only moves for items that pass through their hands. Buying
        /// a stack from the merchant satisfies a Collect and does nothing for a Gather.
        /// </summary>
        Gather,

        /// <summary>Speak to the NPC whose name is in Target. The errand-running objective:
        /// "take this word to the smith".</summary>
        Talk,

        /// <summary>
        /// Reach a place. Target is "x,z" and Amount is the radius in metres the player has to
        /// come within -- so a vague "explore the swamp" becomes something the game can check.
        /// </summary>
        Explore,
    }

    /// <summary>
    /// One line of a quest's to-do list: "kill 10 boars", "hand over 5 leather scraps".
    ///
    /// A quest carries a list of these rather than a single one because that is what a quest
    /// actually is in every game that has them -- "bring me two deer hides and five neck
    /// tails" is one errand, not two, and splitting it into two entries in the journal makes
    /// the player do bookkeeping the game should be doing.
    /// </summary>
    public class QuestObjective
    {
        public QuestObjectiveKind Kind { get; set; } = QuestObjectiveKind.Collect;

        /// <summary>Prefab name of the creature to kill or the item to hand over; for Explore,
        /// the "x,z" of the place to reach.</summary>
        public string Target { get; set; } = "";
        public int Amount { get; set; } = 1;
    }

    /// <summary>
    /// A quest as authored by an admin, in YAML under
    /// `BepInEx/plugins/NpcValheim/npcs/quests/&lt;id&gt;.yaml`. Definitions are content, not
    /// state -- what a given player has done with a quest lives in QuestDatabase.
    /// </summary>
    public class QuestDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";

        /// <summary>
        /// Everything this quest asks for. Left empty by a quest written the old way, or by
        /// the in-game quest maker, which both use the single Objective/Target/Amount below --
        /// Steps() folds the two shapes into one so nothing downstream has to care which was
        /// used.
        /// </summary>
        public List<QuestObjective> Objectives { get; set; } = new List<QuestObjective>();

        public QuestObjectiveKind Objective { get; set; } = QuestObjectiveKind.Collect;
        /// <summary>Prefab name of the creature to kill or the item to hand over.</summary>
        public string Target { get; set; } = "";
        public int Amount { get; set; } = 1;

        /// <summary>
        /// The objectives this quest really has, as one list.
        ///
        /// Deliberately a method and not a property: both YamlDotNet and LiteDB map every
        /// public property they find, so a computed property here would be written back to
        /// disk as a duplicate of the data it was derived from.
        /// </summary>
        public List<QuestObjective> Steps()
        {
            if (Objectives != null && Objectives.Count > 0) return Objectives;
            return new List<QuestObjective>
            {
                new QuestObjective { Kind = Objective, Target = Target, Amount = Amount },
            };
        }

        /// <summary>Minimum EpicMMO level. Ignored when EpicMMO isn't installed, so a quest
        /// pack written for it still works on a server without it.</summary>
        public int RequiredLevel { get; set; } = 0;
        public bool Repeatable { get; set; } = false;

        /// <summary>Cooldown: hours before a finished quest is offered again. 24 makes it a
        /// daily, 168 a weekly, 0 leaves it one-and-done. Measured from the moment it was
        /// handed in, not from midnight, so it behaves the same on every server's clock and
        /// timezone.</summary>
        public int ResetHours { get; set; } = 0;

        /// <summary>
        /// World progress this quest waits for, as Valheim's own global keys -- most usefully
        /// the boss defeats: `defeated_eikthyr`, `defeated_gdking`, `defeated_bonemass`,
        /// `defeated_dragon`, `defeated_goblinking`, `defeated_queen`, `defeated_fader`.
        ///
        /// Separate from RequiresQuests because it asks a different question: that one is
        /// "have you done this errand", this is "has the world moved on". A quest that only
        /// makes sense after the Elder falls should not be visible before he does, no matter
        /// what else the player has finished.
        /// </summary>
        public List<string> RequiresGlobalKeys { get; set; } = new List<string>();

        /// <summary>Ids of quests that must be finished before this one is offered. This is
        /// what turns a folder of quests into a storyline: the second chapter simply lists
        /// the first. A repeatable quest counts as completed once it has been handed in at
        /// least once, otherwise a chain built on one could never advance.</summary>
        public List<string> RequiresQuests { get; set; } = new List<string>();

        public QuestRewards Rewards { get; set; } = new QuestRewards();
    }

    public class QuestRewards
    {
        public int Coins { get; set; } = 0;
        /// <summary>EpicMMO experience. Silently skipped when that mod isn't present.</summary>
        public int Experience { get; set; } = 0;
        public List<QuestItemReward> Items { get; set; } = new List<QuestItemReward>();
    }

    public class QuestItemReward
    {
        public string ItemName { get; set; } = "";
        public int Amount { get; set; } = 1;
        public int Quality { get; set; } = 1;
    }
}
