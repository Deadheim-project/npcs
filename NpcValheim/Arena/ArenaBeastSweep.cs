using System;
using System.Collections.Generic;
using HarmonyLib;

namespace NpcValheim.Arena
{
    /// <summary>
    /// Server: one pass over every object of the world, a few thousand a frame, for what the
    /// Bestiário leaves behind -- test creatures whose five minutes are up (npcv_beastUntil) and
    /// lacaios whose creature is gone (npcv_beastOwner no longer exists: it died, its match
    /// ended, the server restarted). Asked for at the start of the session, when a test creature
    /// goes and when a challenge's creature dies or is taken away; a request during a pass makes
    /// another one follow it.
    /// </summary>
    internal static class ArenaBeastSweep
    {
        /// <summary>Objects looked at per frame: well under a millisecond.</summary>
        private const int Budget = 4000;

        // private List<ZDO>[] m_objectsBySector (assembly_valheim 1.0): every object by zone.
        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> Sectors =
            AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        private static ZDOMan _man;
        private static int _sector = -1;
        private static bool _again;
        private static int _removed;
        private static readonly List<ZDOID> _found = new List<ZDOID>();

        internal static void Request()
        {
            if (_sector < 0) _sector = 0;
            else _again = true;
        }

        internal static void Step()
        {
            var man = ZDOMan.instance;
            if (man == null) return;
            if (!ReferenceEquals(man, _man))
            {
                _man = man;
                _sector = -1;
                _again = false;
                _removed = 0;
            }
            if (_sector < 0) return;

            var sectors = Sectors(man);
            if (sectors == null) return;
            long now = DateTime.UtcNow.Ticks;
            int seen = 0;
            _found.Clear();
            // A zone is looked at whole, so the removals below never shift a list mid-walk.
            while (_sector < sectors.Length && seen < Budget)
            {
                var list = sectors[_sector++];
                if (list == null) continue;
                seen += list.Count;
                foreach (var zdo in list)
                    if (Expired(zdo, now)) _found.Add(zdo.m_uid);
            }
            foreach (var id in _found) ArenaChallengeCreature.Despawn(id);
            _removed += _found.Count;
            if (_sector < sectors.Length) return;

            if (_removed > 0) Plugin.Log.LogInfo($"NpcValheim Arena: {_removed} criatura(s) de teste ou lacaio(s) do bestiario removido(s)");
            _removed = 0;
            _sector = _again ? 0 : -1;
            _again = false;
        }

        private static bool Expired(ZDO zdo, long nowUtcTicks)
        {
            long until = zdo.GetLong(ArenaBeastBody.ZdoUntil, 0L);
            if (until > 0L && until <= nowUtcTicks) return true;
            var owner = zdo.GetZDOID(ArenaBeastBrain.ZdoMinionOwner);
            return !owner.IsNone() && _man.GetZDO(owner) == null;
        }
    }
}
