using System;
using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The arenas and challenges (ArenaLayout) over the arena's channels, the way the Bestiário
    /// goes: the server owns the file; on the hello it sends only the hash, and a client whose
    /// copy differs asks for the whole JSON; after every change the whole list goes to everyone.
    /// </summary>
    internal static class ArenaLayoutNet
    {
        private static ZNet _session;
        private static float _nextAsk;

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // ------------------------------------------------------------------ server

        /// <summary>Reads the file once per session (writing it from the old cfg blocks, or the
        /// example challenges, when there is none).</summary>
        internal static void EnsureLoaded()
        {
            if (!IsServer || (ReferenceEquals(_session, ZNet.instance) && ArenaLayout.Loaded)) return;
            _session = ZNet.instance;
            var problems = new List<string>();
            try
            {
                ArenaLayout.LoadOrSeed(ArenaLayout.FilePath, problems, out var maps, out var challenges);
                ArenaLayout.Replace(maps, challenges);
                Plugin.Log.LogInfo($"NpcValheim Arena: {maps.Count} arena(s) e {challenges.Count} desafio(s) de {ArenaLayout.FilePath}");
            }
            catch (Exception e)
            {
                problems.Add(e.Message);
                ArenaLayout.Replace(new List<ArenaLayoutMap>(), new List<ArenaLayoutChallenge>());
            }
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: arenas: " + p);
            ArenaConfig.Invalidate();
        }

        internal static void SendTo(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.Layout, ArenaLayout.ToJson(ArenaLayout.Maps, ArenaLayout.Challenges));
        }

        internal static void SendHash(long peer)
        {
            EnsureLoaded();
            ArenaNet.Send(peer, ArenaWire.LayoutHash, ArenaLayout.Hash);
        }

        /// <summary>After an admin changed the lists in place: the file, the hash, everyone's copy.
        /// A file that cannot be written puts the lists back as the file has them.</summary>
        internal static bool Commit(long sender)
        {
            try { ArenaLayout.Save(ArenaLayout.FilePath, ArenaLayout.Maps, ArenaLayout.Challenges); }
            catch (Exception e)
            {
                Plugin.Log.LogError("NpcValheim Arena: arenas nao gravadas: " + e);
                ArenaNet.Send(sender, ArenaWire.Alert, "Não foi possível gravar as arenas: " + e.Message);
                _session = null;
                EnsureLoaded();
                return false;
            }
            ArenaLayout.Replace(ArenaLayout.Maps, ArenaLayout.Challenges);
            ArenaConfig.Invalidate();
            ArenaNet.Send(ZRoutedRpc.Everybody, ArenaWire.Layout, ArenaLayout.ToJson(ArenaLayout.Maps, ArenaLayout.Challenges));
            return true;
        }

        // ------------------------------------------------------------------ client

        internal static void OnHash(string hash)
        {
            if (IsServer) return;
            if (ArenaLayout.Loaded && string.Equals((hash ?? "").Trim(), ArenaLayout.Hash, StringComparison.Ordinal)) return;
            Ask();
        }

        /// <summary>A page that needs the arenas while none came yet asks again, at most every few seconds.</summary>
        internal static void AskIfMissing()
        {
            if (IsServer || ArenaLayout.Loaded) return;
            Ask();
        }

        private static void Ask()
        {
            if (Time.realtimeSinceStartup < _nextAsk) return;
            _nextAsk = Time.realtimeSinceStartup + 5f;
            ArenaNet.Request(ArenaWire.ActLayoutGet);
        }

        internal static void Receive(string json)
        {
            if (IsServer) return;
            var problems = new List<string>();
            bool ok = ArenaLayout.Parse(json, problems, out var maps, out var challenges);
            foreach (var p in problems) Plugin.Log.LogWarning("NpcValheim Arena: arenas recebidas: " + p);
            if (!ok) return;
            ArenaLayout.Replace(maps, challenges, ArenaBestiary.HashOf(json));
            ArenaConfig.Invalidate();
        }
    }
}
