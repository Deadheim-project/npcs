using System;
using System.Linq;
using NpcValheim.Arena;

namespace NpcValheim.Npc
{
    /// <summary>
    /// One arena NPC since 0.1.89: the Mestre da Arena has every tab, and it is the only one
    /// the Hammer places. The Organizer and the Intendente already standing in a world are not
    /// migrated -- replacing a saved NPC would lose the name, look and lines its admin gave it --
    /// they stay where they are and open the very same tabs.
    /// </summary>
    internal static class ArenaDesk
    {
        internal const string Prefab = "NpcValheim_ArenaBattlemaster";
        internal static readonly string[] LegacyPrefabs = { "NpcValheim_ArenaOrganizer", "NpcValheim_ArenaVendor" };

        /// <summary>The tabs of every arena NPC, in order.</summary>
        internal static readonly string[] Tabs = { "Arena", "Times", "Criar time", "Ranking", "Loja" };

        /// <summary>The extra tab an admin sees on any arena NPC (the F8 page "Arena").</summary>
        internal const string AdminTab = "Configurar";

        /// <summary>Whether the Hammer offers this NPC prefab.</summary>
        internal static bool OnHammer(string prefab) =>
            !LegacyPrefabs.Contains(prefab, StringComparer.Ordinal);

        /// <summary>The tabs an arena NPC opens: the same whichever of the three it is.</summary>
        internal static string[] TabsOf(string where) => ArenaEngine.IsDesk(where) ? Tabs : Array.Empty<string>();
    }

    /// <summary>
    /// The arena's NPCs hold no state of their own: teams, queues, matches and points are the
    /// server's (Arena/ArenaEngine). An NPC is the place a request has to be made from, and
    /// the server is told which one it came through -- a charter is bought at the Organizer,
    /// the queue is joined at the Battlemaster, points are spent at the vendor.
    /// </summary>
    public abstract class ArenaNpcBase : NpcBase
    {
        internal abstract string Where { get; }

        internal override string[] IdleEmotes => ArenaIdle;
        private static readonly string[] ArenaIdle = { "flex", "challenge", "cheer" };

        internal bool RequestArena(string action, string payload = "") => InvokeServiceAction(action, payload);

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            if (string.IsNullOrEmpty(action) || action.StartsWith("RPC_", System.StringComparison.Ordinal))
                return base.DispatchServiceAction(sender, action, payload);
            ArenaServer.Handle(sender, Where, action, payload ?? "");
            return true;
        }
    }

    /// <summary>Arena Organizer, from before 0.1.89: kept so the ones in a world still load;
    /// opens the Mestre da Arena's tabs.</summary>
    public class ArenaOrganizerNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Organizador de Arena";
        internal override string Where => ArenaEngine.AtOrganizer;
    }

    /// <summary>Mestre da Arena: the queue and the challenges, the teams and their charters,
    /// the ladder and the shop.</summary>
    public class ArenaBattlemasterNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Mestre da Arena";
        internal override string Where => ArenaEngine.AtBattlemaster;
    }

    /// <summary>Intendente da Arena, from before 0.1.89: kept so the ones in a world still
    /// load; opens the Mestre da Arena's tabs.</summary>
    public class ArenaVendorNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Intendente da Arena";
        internal override string Where => ArenaEngine.AtVendor;
    }
}
