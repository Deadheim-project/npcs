using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>A character the server could see at the moment a boss died.</summary>
    internal struct BossWitness
    {
        /// <summary>Whom to tell: the routed-RPC id of their connection.</summary>
        public long Peer;
        public long PlayerId;
        public string Name;
        public bool HasPosition;
        public Vector3 Position;
        /// <summary>The boss's own record says this character hit it.</summary>
        public bool Attacker;
    }

    /// <summary>A boss whose ZDO the server watched disappear, and who was there.</summary>
    internal sealed class BossDeath
    {
        public BossPassEntry Entry;
        public string DefeatKey;
        public Vector3 Position;
        public float Time;
        public List<BossWitness> Witnesses = new List<BossWitness>();
    }

    /// <summary>
    /// Pairs the two things the server sees when a boss dies, which arrive as separate
    /// messages: the boss's defeat key (Character.OnDeath sends ZoneSystem.SetGlobalKey) and
    /// the boss's ZDO being destroyed (ZNetScene.Destroy, a frame later).
    ///
    /// Either alone is not a kill. A ZDO can disappear without its creature dying -- an admin
    /// command, or a mod that despawns idle bosses -- and a defeat key is a global key that
    /// `setkey` or a modified client can send at any time. Both together, for the same boss,
    /// within a few seconds, is what a death looks like. A boss with no defeat key at all (a
    /// modded one) has only the first signal, and is taken on it.
    ///
    /// No Unity calls, so the wire check exercises it outside the game.
    /// </summary>
    internal sealed class BossKillMatcher
    {
        private readonly float _window;
        private readonly List<KeyValuePair<string, float>> _keys = new List<KeyValuePair<string, float>>();
        private readonly List<BossDeath> _deaths = new List<BossDeath>();

        internal BossKillMatcher(float windowSeconds) => _window = windowSeconds;

        internal int Pending => _keys.Count + _deaths.Count;

        internal void Clear()
        {
            _keys.Clear();
            _deaths.Clear();
        }

        /// <summary>A boss's ZDO is gone. True when that is a confirmed kill now; otherwise it
        /// waits for its key.</summary>
        internal bool OnGone(BossDeath death, float now)
        {
            Prune(now);
            if (string.IsNullOrEmpty(death.DefeatKey)) return true;

            int key = _keys.FindIndex(k => SameKey(k.Key, death.DefeatKey));
            if (key >= 0)
            {
                _keys.RemoveAt(key);
                return true;
            }
            _deaths.Add(death);
            return false;
        }

        /// <summary>A defeat key arrived. The death it confirms, or null.</summary>
        internal BossDeath OnKey(string defeatKey, float now)
        {
            Prune(now);
            int index = _deaths.FindIndex(d => SameKey(d.DefeatKey, defeatKey));
            if (index >= 0)
            {
                var death = _deaths[index];
                _deaths.RemoveAt(index);
                return death;
            }
            _keys.Add(new KeyValuePair<string, float>(defeatKey, now));
            return null;
        }

        private void Prune(float now)
        {
            _keys.RemoveAll(k => now - k.Value > _window);
            _deaths.RemoveAll(d => now - d.Time > _window);
        }

        private static bool SameKey(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static class BossWitnesses
    {
        /// <summary>
        /// Who counts as having been at the fight: anyone who hit the boss, wherever they are
        /// now, and anyone standing within `radius` of it, measured on the ground. Height is
        /// left out because Moder dies in the air and the Queen in a pit.
        ///
        /// The radius is what the server owner chose ("quem estava perto"); the hit list is
        /// Valheim's own record on the boss, and it is there so an archer at the edge of the
        /// arena, or someone who died mid-fight and is running back from the bed, is not
        /// left out. One entry per character.
        /// </summary>
        internal static List<BossWitness> Select(Vector3 bossPosition, float radius, IEnumerable<BossWitness> candidates)
        {
            var result = new List<BossWitness>();
            var seen = new HashSet<long>();
            if (candidates == null) return result;
            if (float.IsNaN(radius) || radius < 0f) radius = 0f;

            foreach (var candidate in candidates)
            {
                if (candidate.PlayerId == 0L || seen.Contains(candidate.PlayerId)) continue;
                bool near = candidate.HasPosition && GroundDistance(bossPosition, candidate.Position) <= radius;
                if (!near && !candidate.Attacker) continue;

                seen.Add(candidate.PlayerId);
                result.Add(candidate);
            }
            return result;
        }

        internal static float GroundDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }

    /// <summary>
    /// Watches, on the server, for the bosses in BossPass.Bosses to die, and gives the pass to
    /// everyone who was there. Nothing here listens to a client: both signals are the
    /// server's own view of the world (see BossKillMatcher), and who was there comes from the
    /// server's copy of each character and of the boss.
    /// </summary>
    internal static class BossKillWatcher
    {
        /// <summary>The key goes out in the same frame as the destroy; this only has to cover
        /// network jitter between two messages from the same peer.</summary>
        private const float MatchWindow = 20f;

        private static readonly BossKillMatcher Matcher = new BossKillMatcher(MatchWindow);

        private static string _indexedRaw;
        private static bool _indexedWithScene;
        private static Dictionary<int, BossPassEntry> _byPrefab = new Dictionary<int, BossPassEntry>();
        private static Dictionary<string, string> _keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
        private static HashSet<string> _defeatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>Called once per connection. Half a kill from the previous session is not
        /// half a kill in this one. On the server it also resolves the watch list straight
        /// away, so the boot log says which bosses are watched and with which key, instead of
        /// leaving that to the first fight.</summary>
        internal static void Reset()
        {
            Matcher.Clear();
            if (!IsServer) return;
            try { EnsureIndex(); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: BossPass could not read its boss list: {e.Message}"); }
        }

        /// <summary>
        /// Which ZDO prefabs are watched bosses, and the defeat key each one sends. Rebuilt when
        /// the config changes, so a boss added to the cfg is watched from the next death.
        ///
        /// The key is read off the game's own prefab (Character.m_defeatSetGlobalKey) instead of
        /// being written in the config: it is a fact about the creature, and a hand-copied
        /// "defeated_gdking" is one typo away from a boss whose kills never count.
        /// </summary>
        private static void EnsureIndex()
        {
            string raw = Plugin.BossPassBosses?.Value ?? "";
            bool withScene = ZNetScene.instance != null;
            if (string.Equals(raw, _indexedRaw, StringComparison.Ordinal) && withScene == _indexedWithScene) return;

            var byPrefab = new Dictionary<int, BossPassEntry>();
            var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var defeatKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var described = new List<string>();

            foreach (var entry in BossPassCatalog.Current())
            {
                string key = "";
                if (withScene)
                {
                    var prefab = ZNetScene.instance.GetPrefab(entry.Boss);
                    var creature = prefab != null ? prefab.GetComponent<Character>() : null;
                    if (prefab == null)
                        Plugin.Log.LogWarning($"NpcValheim: BossPass.Bosses -- '{entry.Boss}' is not a prefab this game knows; its deaths cannot be seen");
                    else if (creature == null)
                        Plugin.Log.LogWarning($"NpcValheim: BossPass.Bosses -- '{entry.Boss}' is not a creature");
                    key = creature != null ? creature.m_defeatSetGlobalKey ?? "" : "";
                }

                byPrefab[entry.Boss.GetStableHashCode()] = entry;
                keyOf[entry.Boss] = key;
                if (key.Length > 0) defeatKeys.Add(key);
                described.Add(key.Length > 0 ? $"{entry.Boss} ({key})" : $"{entry.Boss} (no defeat key)");
            }

            _byPrefab = byPrefab;
            _keyOf = keyOf;
            _defeatKeys = defeatKeys;
            _indexedRaw = raw;
            _indexedWithScene = withScene;

            if (IsServer && withScene)
                Plugin.Log.LogInfo("NpcValheim: BossPass watches " +
                                   (described.Count > 0 ? string.Join(", ", described) : "no boss"));
        }

        /// <summary>Every ZDO the server destroys passes through here, so the miss has to be
        /// cheap: one dictionary lookup.</summary>
        internal static void OnZdoDestroyed(ZDOID uid)
        {
            if (!IsServer) return;
            try
            {
                var zdo = ZDOMan.instance?.GetZDO(uid);
                if (zdo == null) return;

                EnsureIndex();
                if (!_byPrefab.TryGetValue(zdo.GetPrefab(), out var entry)) return;

                float radius = Mathf.Clamp(Plugin.BossPassKillRadius?.Value ?? 60f, 0f, 1000f);
                var death = new BossDeath
                {
                    Entry = entry,
                    DefeatKey = _keyOf.TryGetValue(entry.Boss, out var key) ? key : "",
                    Position = zdo.GetPosition(),
                    Time = Time.realtimeSinceStartup,
                };

                // Read now: the ZDO, and with it the boss's list of who hit it, is released as
                // soon as this returns.
                var online = GameApi.ListOnlineCharacters();
                var candidates = online.Select(c => new BossWitness
                {
                    Peer = c.Peer,
                    PlayerId = c.PlayerId,
                    Name = c.Name,
                    HasPosition = c.HasPosition,
                    Position = c.Position,
                    Attacker = !string.IsNullOrEmpty(c.Name) && zdo.GetBool(ZDOVars.s_attackers + c.Name),
                }).ToList();
                death.Witnesses = BossWitnesses.Select(death.Position, radius, candidates);

                // Everyone online with their distance, so "I was there and got nothing" can be
                // settled from the log, and the radius tuned from real fights.
                string roll = string.Join(", ", candidates.Select(c =>
                    $"{c.Name} {(c.HasPosition ? BossWitnesses.GroundDistance(death.Position, c.Position).ToString("0") + "m" : "?m")}" +
                    (c.Attacker ? " hit" : "")));
                Plugin.Log.LogInfo(
                    $"NpcValheim: boss {entry.Boss} gone at ({death.Position.x:0}, {death.Position.z:0}); radius {radius:0}m; " +
                    $"online: [{roll}]; counted: [{string.Join(", ", death.Witnesses.Select(w => w.Name))}]");

                if (Matcher.OnGone(death, death.Time)) Award(death);
                else
                    Plugin.Log.LogInfo($"NpcValheim: waiting for '{death.DefeatKey}' to count the {entry.Boss} as killed");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: BossPass could not inspect a destroyed object: {e.Message}");
            }
        }

        internal static void OnGlobalKey(long sender, string name)
        {
            if (!IsServer || string.IsNullOrEmpty(name)) return;
            try
            {
                EnsureIndex();
                if (!_defeatKeys.Contains(name)) return;

                Plugin.Log.LogInfo($"NpcValheim: defeat key '{name}' from peer {sender}");
                var death = Matcher.OnKey(name, Time.realtimeSinceStartup);
                if (death != null) Award(death);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: BossPass could not read a global key: {e.Message}");
            }
        }

        private static void Award(BossDeath death)
        {
            var granted = new List<string>();
            var kept = new List<string>();
            foreach (var witness in death.Witnesses)
                (BossPass.GrantForKill(witness.Peer, witness.PlayerId, witness.Name, death.Entry) ? granted : kept)
                    .Add($"{witness.Name} ({witness.PlayerId})");

            Plugin.Log.LogInfo(
                $"NpcValheim: {death.Entry.Name} ({death.Entry.Boss}) killed: pass granted to [{string.Join(", ", granted)}]" +
                (kept.Count > 0 ? $"; already had it or not written: [{string.Join(", ", kept)}]" : ""));
        }
    }

    /// <summary>The server's view of every destroyed ZDO, before it is released. A prefix on
    /// the private handler rather than ZDOMan.m_onZDODestroyed, because that is a single
    /// public delegate field that any other mod may assign over.</summary>
    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    internal static class ZDOMan_HandleDestroyedZDO_BossPass_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(ZDOID uid) => BossKillWatcher.OnZdoDestroyed(uid);
    }

    /// <summary>Registered on the server only, which is the only place it is wanted.</summary>
    [HarmonyPatch(typeof(ZoneSystem), "RPC_SetGlobalKey")]
    internal static class ZoneSystem_RPC_SetGlobalKey_BossPass_Patch
    {
        [HarmonyPrefix]
        private static void Prefix(long sender, string name) => BossKillWatcher.OnGlobalKey(sender, name);
    }
}
