using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The Bestiário over the arena's channels (ArenaNet). The server owns the file. On the hello
    /// (login, respawn) it sends only the hash of the list; a client whose own list has another
    /// hash asks for the whole JSON. After each change the whole list goes to everyone. Saving,
    /// removing and summoning are the admin's, checked here against the real sender.
    /// </summary>
    internal static class ArenaBestiaryNet
    {
        /// <summary>How long a creature summoned with "Invocar perto de mim" stays.</summary>
        internal const float TestSeconds = 300f;
        private const int MaxTests = 10;
        /// <summary>The admin's client says where in front of them; the server only believes
        /// it this close to where it sees them.</summary>
        private const float MaxSpawnDistance = 15f;

        private static ZNet _session;
        private static readonly List<(ZDOID Id, long Until)> _tests = new List<(ZDOID, long)>();
        private static float _nextTestCheck;
        private static bool _swept;
        private static float _nextAsk;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // ------------------------------------------------------------------ server

        /// <summary>Reads the file once per session (writing the examples when there is none).
        /// Before the challenges are built: a "bestiario:id" needs it to resolve.</summary>
        internal static void EnsureLoaded()
        {
            if (!IsServer || (ReferenceEquals(_session, ZNet.instance) && ArenaBestiary.Loaded)) return;
            if (!ReferenceEquals(_session, ZNet.instance))
            {
                _tests.Clear();
                _swept = false;
            }
            _session = ZNet.instance;
            var problems = new List<string>();
            try
            {
                var beasts = ArenaBestiary.LoadOrSeed(ArenaBestiary.FilePath, problems);
                foreach (var beast in beasts) DropForeignAttacks(beast, problems);
                ArenaBestiary.Replace(beasts);
                Plugin.Log.LogInfo($"NpcValheim Arena: bestiario com {beasts.Count} criatura(s) de {ArenaBestiary.FilePath}");
            }
            catch (Exception e)
            {
                problems.Add(e.Message);
                ArenaBestiary.Replace(new List<ArenaBeastDef>());
            }
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: bestiario: " + p);
            ArenaConfig.Invalidate();
        }

        internal static bool IsBeastAction(string action) =>
            action == ArenaWire.ActAdminBeastSet || action == ArenaWire.ActAdminBeastRemove || action == ArenaWire.ActAdminBeastSpawn;

        internal static void SendTo(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.Bestiary, ArenaBestiary.ToJson(ArenaBestiary.All));
        }

        internal static void SendHash(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.BestiaryHash, ArenaBestiary.Hash);
        }

        /// <summary>The attacks name items of the base. One the base does not carry (another base
        /// was chosen, or the item is not what the game calls it) would never be used: it goes,
        /// and the reason is said. Nothing is dropped while the game's prefabs are not known.</summary>
        internal static void DropForeignAttacks(ArenaBeastDef beast, List<string> said)
        {
            var items = ArenaBeastBody.AttackItems(beast.Base);
            if (items == null) return;
            beast.Attacks.RemoveAll(a =>
            {
                if (items.Contains(a.Item)) return false;
                said?.Add($"\"{beast.Id}\": {beast.Base} não tem o ataque \"{a.Item}\" (tem: {string.Join(", ", items)}); descartado");
                return true;
            });
        }

        /// <summary>What a summon may call: a creature of this game, not a boss nor a player.</summary>
        private static bool MinionUsable(ArenaBeastDef beast, out string why)
        {
            why = null;
            foreach (var ability in beast.Abilities)
            {
                if (ability.Kind != "summon" || BaseUsable(ability.Prefab, out string reason)) continue;
                why = "lacaio: " + reason;
                return false;
            }
            return true;
        }

        private static void Broadcast() =>
            ArenaNet.Send(ZRoutedRpc.Everybody, ArenaWire.Bestiary, ArenaBestiary.ToJson(ArenaBestiary.All));

        /// <summary>One admin request. <paramref name="position"/> is where the server sees the
        /// sender standing, or null when it does not know.</summary>
        internal static void Handle(long sender, string name, bool admin, string action, string payload, Vector3? position)
        {
            if (!admin)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Só admin.");
                return;
            }
            EnsureLoaded();
            switch (action)
            {
                case ArenaWire.ActAdminBeastSet: Save(sender, name, payload); break;
                case ArenaWire.ActAdminBeastRemove: Remove(sender, name, payload.Trim()); break;
                case ArenaWire.ActAdminBeastSpawn: Spawn(sender, name, payload, position); break;
            }
        }

        private static void Save(long sender, string name, string json)
        {
            var problems = new List<string>();
            var warnings = new List<string>();
            var beast = ArenaBestiary.ParseOne(json, problems, warnings);
            if (beast == null)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Não salvei: " + (problems.FirstOrDefault() ?? "criatura inválida") + ".");
                return;
            }
            if (!BaseUsable(beast.Base, out string why) || !MinionUsable(beast, out why))
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Não salvei: " + why + ".");
                return;
            }
            DropForeignAttacks(beast, warnings);

            var list = ArenaBestiary.All.ToList();
            int at = list.FindIndex(b => string.Equals(b.Id, beast.Id, StringComparison.OrdinalIgnoreCase));
            if (at < 0 && list.Count >= ArenaBestiary.MaxCreatures)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, $"Não salvei: o bestiário já tem {ArenaBestiary.MaxCreatures} criaturas.");
                return;
            }
            if (at >= 0) list[at] = beast;
            else list.Add(beast);

            if (!Write(sender, list)) return;
            Plugin.Log.LogInfo($"NpcValheim Arena: {name} salvou a criatura '{beast.Id}' do bestiario: {ArenaBestiary.ToJson(beast)}");
            foreach (var w in warnings) Plugin.Log.LogWarning("NpcValheim Arena: bestiario: " + w);
            ArenaNet.Send(sender, ArenaWire.Notice, $"Salvo: {beast.DisplayName}. Vale a partir da próxima criatura criada." +
                (warnings.Count > 0 ? " Aviso: " + warnings[0] + (warnings.Count > 1 ? $" (+{warnings.Count - 1})" : "") + "." : ""));
        }

        private static void Remove(long sender, string name, string id)
        {
            var beast = ArenaBestiary.Find(id);
            if (beast == null)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, $"Não há \"{id}\" no bestiário.");
                return;
            }
            // A challenge pointing at a creature that is gone would just stop being offered;
            // better to say so now than to find it missing at the Battlemaster.
            string value = ArenaBestiary.ChallengeValue(beast);
            for (int i = 0; i < ArenaConfig.ChallengeSlots.Count; i++)
                if (string.Equals(ArenaConfig.ChallengeSlots[i].Creature.Value.Trim(), value, StringComparison.OrdinalIgnoreCase))
                {
                    ArenaNet.Send(sender, ArenaWire.Notice, $"{beast.DisplayName} está no Desafio {i + 1}: troque a criatura de lá antes de excluir.");
                    return;
                }

            var list = ArenaBestiary.All.Where(b => !ReferenceEquals(b, beast)).ToList();
            if (!Write(sender, list)) return;
            Plugin.Log.LogInfo($"NpcValheim Arena: {name} excluiu a criatura '{beast.Id}' do bestiario");
            ArenaNet.Send(sender, ArenaWire.Notice, $"Excluída: {beast.DisplayName}.");
        }

        private static bool Write(long sender, List<ArenaBeastDef> list)
        {
            try { ArenaBestiary.Save(ArenaBestiary.FilePath, list); }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim Arena: bestiario nao gravado: " + e);
                ArenaNet.Send(sender, ArenaWire.Alert, "Não foi possível gravar o bestiário: " + e.Message);
                return false;
            }
            ArenaBestiary.Replace(list);
            ArenaConfig.Invalidate();
            Broadcast();
            return true;
        }

        /// <summary>"id\nx\ny\nz\nyaw": in front of the admin, outside any match.</summary>
        private static void Spawn(long sender, string name, string payload, Vector3? position)
        {
            var f = ArenaWire.Fields(payload);
            var beast = ArenaBestiary.Find(f[0].Trim());
            if (beast == null)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Salve a criatura antes de invocar.");
                return;
            }
            var at = new Vector3(ArenaWire.Float(f, 1, float.NaN), ArenaWire.Float(f, 2, float.NaN), ArenaWire.Float(f, 3, float.NaN));
            if (float.IsNaN(at.x) || float.IsNaN(at.y) || float.IsNaN(at.z))
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Pedido de invocação malformado.");
                return;
            }
            if (position == null || Vector3.Distance(position.Value, at) > MaxSpawnDistance)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "O servidor não confirmou onde você está; tente de novo.");
                return;
            }
            PruneTests();
            if (_tests.Count >= MaxTests)
            {
                ArenaNet.Send(sender, ArenaWire.Notice, $"Já há {MaxTests} criaturas de teste no mundo; espere alguma sumir.");
                return;
            }
            if (!BaseUsable(beast.Base, out string why))
            {
                ArenaNet.Send(sender, ArenaWire.Notice, "Não invoquei: " + why + ".");
                return;
            }

            long until = DateTime.UtcNow.AddSeconds(TestSeconds).Ticks;
            var id = ArenaChallengeCreature.SpawnTest(beast, at, ArenaWire.Float(f, 4, 0f), until);
            if (id.IsNone())
            {
                ArenaNet.Send(sender, ArenaWire.Notice, $"Não foi possível criar {beast.Base}.");
                return;
            }
            _tests.Add((id, until));
            Plugin.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                "NpcValheim Arena: {0} invocou '{1}' para teste em {2:0},{3:0},{4:0}", name, beast.Id, at.x, at.y, at.z));
            ArenaNet.Send(sender, ArenaWire.Notice, $"{beast.DisplayName} invocado à sua frente para teste (some em 5 min, fora de partida).");
        }

        /// <summary>The base must be a creature of the running game and no boss: killing a boss
        /// writes a global key and counts for Deadheim's boss pass.</summary>
        private static bool BaseUsable(string prefabName, out string why)
        {
            why = null;
            if (ZNetScene.instance == null) return true;
            var prefab = ZNetScene.instance.GetPrefab(prefabName);
            var character = prefab != null ? prefab.GetComponent<Character>() : null;
            if (character == null || character is Player || prefab.GetComponent<BaseAI>() == null)
            {
                why = $"'{prefabName}' não é uma criatura deste jogo";
                return false;
            }
            if (ArenaChallengeCreature.IsBoss(character))
            {
                why = $"'{prefabName}' é chefe";
                return false;
            }
            return true;
        }

        private static void PruneTests() =>
            _tests.RemoveAll(t => ZDOMan.instance == null || ZDOMan.instance.GetZDO(t.Id) == null);

        /// <summary>Server tick: test creatures whose five minutes are up go. The first tick also
        /// starts a pass over the world for the ones a restart left behind (ArenaBeastSweep, a
        /// few thousand objects a frame); every test creature that goes starts one for its lacaios.</summary>
        internal static void Tick()
        {
            if (!IsServer) return;
            ArenaBeastSweep.Step();
            if (Time.realtimeSinceStartup < _nextTestCheck) return;
            _nextTestCheck = Time.realtimeSinceStartup + 2f;
            EnsureLoaded();
            if (!_swept)
            {
                _swept = true;
                ArenaBeastSweep.Request();
            }
            if (_tests.Count == 0) return;
            long now = DateTime.UtcNow.Ticks;
            for (int i = _tests.Count - 1; i >= 0; i--)
            {
                if (_tests[i].Until > now) continue;
                ArenaChallengeCreature.Despawn(_tests[i].Id);
                _tests.RemoveAt(i);
                ArenaBeastSweep.Request();
            }
        }

        // ------------------------------------------------------------------ client

        /// <summary>The server's hash on the hello: the list is asked for only when this one differs
        /// (or there is none yet), and not more than once every few seconds.</summary>
        internal static void OnHash(string hash)
        {
            if (IsServer) return;
            if (ArenaBestiary.Loaded && string.Equals((hash ?? "").Trim(), ArenaBestiary.Hash, StringComparison.Ordinal)) return;
            if (Time.realtimeSinceStartup < _nextAsk) return;
            _nextAsk = Time.realtimeSinceStartup + 5f;
            ArenaNet.Request(ArenaWire.ActBestiaryGet);
        }

        /// <summary>The admin page while nothing came yet: asks again, not more than once every few
        /// seconds, so a hello the server could not answer does not leave the page empty.</summary>
        internal static void AskIfMissing()
        {
            if (IsServer || ArenaBestiary.Loaded || Time.realtimeSinceStartup < _nextAsk) return;
            _nextAsk = Time.realtimeSinceStartup + 5f;
            ArenaNet.Request(ArenaWire.ActBestiaryGet);
        }

        /// <summary>What the server sent. On the server itself its own list already is the file.</summary>
        internal static void Receive(string json)
        {
            if (IsServer) return;
            var problems = new List<string>();
            var beasts = ArenaBestiary.Parse(json, problems);
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: bestiario recebido: " + p);
            if (beasts == null) return;
            // The hash of the text as it came: the one the server works out of the same text.
            ArenaBestiary.Replace(beasts, ArenaBestiary.HashOf(json));
            ArenaConfig.Invalidate();
            ArenaBeastBody.ApplyToLoaded();
        }
    }
}
