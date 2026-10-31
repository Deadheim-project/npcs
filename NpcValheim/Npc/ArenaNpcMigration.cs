using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Organizer and the Intendente of before the single Mestre da Arena, taken out of the
    /// world once, on the server: one standing next to a Mestre da Arena is a spare and goes;
    /// one standing alone becomes a Mestre da Arena on the spot -- the same ZDO with its prefab
    /// swapped, so its name, look, lines and facing all stay. The classes stay registered for
    /// now, so a ZDO that comes back (a restored backup) still loads, and the migration takes
    /// it on the next start.
    /// </summary>
    internal static class ArenaNpcMigration
    {
        /// <summary>A spare is an old NPC within this of a Mestre da Arena.</summary>
        internal const float SpareRadius = 15f;
        private const float RetrySeconds = 60f;

        internal enum Step { Keep, Delete, Convert }

        internal struct Spot
        {
            public string Prefab;
            public Vector3 Position;
        }

        /// <summary>What to do with each NPC, in the same order. A Mestre da Arena is kept; an
        /// old one near a Mestre (one already there, or one an earlier conversion just made)
        /// is deleted, else converted -- so three old ones side by side end as one Mestre.</summary>
        internal static List<Step> Plan(IList<Spot> npcs, float radius = SpareRadius)
        {
            var result = new List<Step>();
            var masters = npcs.Where(n => n.Prefab == ArenaDesk.Prefab).Select(n => n.Position).ToList();
            foreach (var npc in npcs)
            {
                if (!ArenaDesk.LegacyPrefabs.Contains(npc.Prefab)) { result.Add(Step.Keep); continue; }
                if (masters.Any(m => Vector3.Distance(m, npc.Position) <= radius)) { result.Add(Step.Delete); continue; }
                result.Add(Step.Convert);
                masters.Add(npc.Position);
            }
            return result;
        }

        private static bool _done;
        private static float _next;

        /// <summary>From the server's tick: once the world is loaded, until no old NPC is left.
        /// One loaded right now (a player stands by it) waits for the next pass: replacing an
        /// object under a player's feet would leave their client drawing the old one.</summary>
        internal static void Tick()
        {
            if (_done || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + RetrySeconds;
            try { Run(); }
            catch (System.Exception e)
            {
                _done = true;
                Plugin.Log.LogError("NpcValheim: migracao dos NPCs da arena falhou: " + e);
            }
        }

        private static void Run()
        {
            if (ZDOMan.instance == null || ZNetScene.instance == null || ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (Arena.ArenaConfig.MigrateOldNpcs != null && !Arena.ArenaConfig.MigrateOldNpcs.Value)
            {
                _done = true;
                return;
            }

            var zdos = new List<ZDO>();
            foreach (var prefab in ArenaDesk.LegacyPrefabs.Concat(new[] { ArenaDesk.Prefab }))
            {
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, zdos, ref index)) { }
            }
            var legacy = zdos.Where(z => z.GetPrefab() != ArenaDesk.Prefab.GetStableHashCode()).ToList();
            if (legacy.Count == 0)
            {
                Plugin.Log.LogInfo("NpcValheim: migracao dos NPCs da arena: nenhum Organizador ou Intendente antigo no mundo");
                _done = true;
                return;
            }

            var names = new Dictionary<int, string>
            {
                { ArenaDesk.Prefab.GetStableHashCode(), ArenaDesk.Prefab },
            };
            foreach (var p in ArenaDesk.LegacyPrefabs) names[p.GetStableHashCode()] = p;
            var spots = zdos.Select(z => new Spot { Prefab = names.TryGetValue(z.GetPrefab(), out var n) ? n : "", Position = z.GetPosition() }).ToList();
            var plan = Plan(spots);

            int deleted = 0, converted = 0, waiting = 0;
            long me = ZDOMan.GetSessionID();
            for (int i = 0; i < zdos.Count; i++)
            {
                if (plan[i] == Step.Keep) continue;
                var zdo = zdos[i];
                if (ZNetScene.instance.FindInstance(zdo) != null) { waiting++; continue; }
                string name = zdo.GetString(ZdoKeys.Name, spots[i].Prefab);
                zdo.SetOwner(me);
                if (plan[i] == Step.Delete)
                {
                    ZDOMan.instance.DestroyZDO(zdo);
                    deleted++;
                    Plugin.Log.LogInfo($"NpcValheim: NPC antigo '{name}' ({spots[i].Prefab}) em {spots[i].Position} removido: ja ha um Mestre da Arena ao lado");
                }
                else
                {
                    zdo.SetPrefab(ArenaDesk.Prefab.GetStableHashCode());
                    converted++;
                    Plugin.Log.LogInfo($"NpcValheim: NPC antigo '{name}' ({spots[i].Prefab}) em {spots[i].Position} virou Mestre da Arena");
                }
            }
            Plugin.Log.LogInfo($"NpcValheim: migracao dos NPCs da arena: {converted} convertido(s), {deleted} removido(s)" +
                               (waiting > 0 ? $", {waiting} esperando o jogador sair de perto" : ""));
            if (waiting == 0) _done = true;
        }
    }
}
