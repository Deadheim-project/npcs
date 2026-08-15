using System.Collections.Generic;

namespace NpcValheim.Persistence
{
    public enum QuestObjectiveKind
    {
        /// <summary>Kill N creatures whose prefab name matches Target.</summary>
        Kill,
        /// <summary>Hand over N of the item prefab named in Target.</summary>
        Collect,
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

        public QuestObjectiveKind Objective { get; set; } = QuestObjectiveKind.Collect;
        /// <summary>Prefab name of the creature to kill or the item to hand over.</summary>
        public string Target { get; set; } = "";
        public int Amount { get; set; } = 1;

        /// <summary>Minimum EpicMMO level. Ignored when EpicMMO isn't installed, so a quest
        /// pack written for it still works on a server without it.</summary>
        public int RequiredLevel { get; set; } = 0;
        public bool Repeatable { get; set; } = false;

        /// <summary>Hours before a finished quest is offered again. 24 makes it a daily, 168
        /// a weekly, 0 leaves it one-and-done. Measured from the moment it was handed in, not
        /// from midnight, so it behaves the same on every server's clock and timezone.</summary>
        public int ResetHours { get; set; } = 0;

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
