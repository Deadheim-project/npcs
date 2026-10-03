using NpcValheim.Arena;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The arena's NPCs hold no state of their own: teams, queues, matches and points are the
    /// server's (Arena/ArenaEngine). An NPC is the place a request has to be made from, and
    /// the server is told which one it came through -- a charter is bought at the Organizer,
    /// the queue is joined at the Battlemaster, points are spent at the vendor.
    /// </summary>
    public abstract class ArenaNpcBase : NpcBase
    {
        internal abstract string Where { get; }

        internal bool RequestArena(string action, string payload = "") => InvokeServiceAction(action, payload);

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            if (string.IsNullOrEmpty(action) || action.StartsWith("RPC_", System.StringComparison.Ordinal))
                return base.DispatchServiceAction(sender, action, payload);
            ArenaServer.Handle(sender, Where, action, payload ?? "");
            return true;
        }
    }

    /// <summary>Arena Organizer: sells the team charter and registers the team once it is signed.</summary>
    public class ArenaOrganizerNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Organizador de Arena";
        internal override string Where => ArenaEngine.AtOrganizer;
    }

    /// <summary>Arena Battlemaster: the queue, rated and skirmish, and the ladder.</summary>
    public class ArenaBattlemasterNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Mestre da Arena";
        internal override string Where => ArenaEngine.AtBattlemaster;
    }

    /// <summary>Arena vendor: spends Arena Points; the goods go by mail.</summary>
    public class ArenaVendorNpc : ArenaNpcBase
    {
        protected override string DefaultNpcName => "Intendente da Arena";
        internal override string Where => ArenaEngine.AtVendor;
    }
}
