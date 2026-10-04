using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The Deadheim PvP module, read by reflection the way it reads Guilds and Groups: no
    /// compile-time reference, and nothing breaks without it.
    ///
    /// The arena reuses Deadheim's ArenaZones instead of drawing its own: inside one, PvP is
    /// always on, nobody loses skills, nobody turns PK, immunity does not apply and a combat
    /// logout is not punished. A map whose starting points fall outside every arena zone is
    /// refused, because a match there would be fought under the open-world rules.
    /// </summary>
    internal static class ArenaDeadheim
    {
        private static bool _resolved;
        private static MethodInfo _isArena;
        private static MethodInfo _arenaName;
        private static BepInEx.Configuration.ConfigEntryBase _zonesEntry;

        internal static bool Installed
        {
            get
            {
                Resolve();
                return _isArena != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var zones = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Deadheim.Pvp.PvpZones", false))
                    .FirstOrDefault(t => t != null);
                if (zones == null)
                {
                    Plugin.Log.LogInfo("NpcValheim Arena: Deadheim PvP nao encontrado; arenas sem ArenaZones.");
                    return;
                }
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                _isArena = zones.GetMethod("IsArena", any, null, new[] { typeof(Vector3) }, null);
                _arenaName = zones.GetMethod("ArenaName", any, null, new[] { typeof(Vector3) }, null);
                _zonesEntry = zones.Assembly.GetType("Deadheim.Pvp.PvpConfig", false)?
                    .GetField("ArenaZones", any)?.GetValue(null) as BepInEx.Configuration.ConfigEntryBase;
                Plugin.Log.LogInfo($"NpcValheim Arena: Deadheim PvP integrado (IsArena={_isArena != null}, ArenaZones={_zonesEntry != null}).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: nao foi possivel ler as zonas do Deadheim: " + e.Message);
            }
        }

        internal static bool IsArena(Vector3 point)
        {
            Resolve();
            if (_isArena == null) return false;
            try { return (bool)_isArena.Invoke(null, new object[] { point }); }
            catch { return false; }
        }

        internal static string ArenaName(Vector3 point)
        {
            Resolve();
            if (_arenaName == null) return null;
            try { return _arenaName.Invoke(null, new object[] { point }) as string; }
            catch { return null; }
        }

        /// <summary>The ArenaZones text as this side has it (the server's, synced), or "" without Deadheim.</summary>
        internal static string ZonesText
        {
            get
            {
                Resolve();
                try { return _zonesEntry?.BoxedValue as string ?? ""; }
                catch { return ""; }
            }
        }

        internal static List<ArenaZone> Zones() => ArenaSettingsParser.ParseZones(ZonesText);

        /// <summary>Writes ArenaZones on the server. Deadheim saves its cfg, drops its zone cache
        /// and ServerSync sends the new value to every client, all from the setting changing.</summary>
        internal static bool TryWriteZones(string text, out string why)
        {
            why = null;
            Resolve();
            if (_zonesEntry == null)
            {
                why = Installed ? "esta versao do Deadheim nao expoe ArenaZones" : "Deadheim nao instalado";
                return false;
            }
            try
            {
                _zonesEntry.BoxedValue = text ?? "";
                return true;
            }
            catch (Exception e)
            {
                why = e.Message;
                return false;
            }
        }

        /// <summary>Both starting points inside a Deadheim arena zone. Without Deadheim there
        /// is nothing to check against, and the arena stands on its own.</summary>
        internal static bool CoversMap(ArenaMapDef map, out string why)
        {
            why = null;
            if (!Installed) return true;
            bool gold = IsArena(map.Gold), green = IsArena(map.Green);
            if (gold && green) return true;
            why = "o inicio " + (!gold ? "Ouro" : "Verde") + " esta fora de todas as ArenaZones do Deadheim " +
                  "(marque a area na aba Admin do Mestre da Arena)";
            return false;
        }
    }

    /// <summary>
    /// The party, from the Groups mod (blaxxun), by reflection. It only knows the local
    /// player's own group, so this runs on clients: the leader lists the members to queue,
    /// and each member's client answers whether it really is in that leader's group.
    /// </summary>
    internal static class ArenaGroups
    {
        private static bool _resolved;
        private static MethodInfo _groupPlayers;
        private static MethodInfo _getLeader;
        private static MethodInfo _findMember;
        private static FieldInfo _peerId;
        private static FieldInfo _name;

        internal static bool Installed
        {
            get
            {
                Resolve();
                return _groupPlayers != null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Groups");
                var api = assembly?.GetType("Groups.API");
                var reference = assembly?.GetType("Groups.PlayerReference");
                if (api == null || reference == null) return;
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Static;
                _groupPlayers = api.GetMethod("GroupPlayers", pub, null, Type.EmptyTypes, null);
                _getLeader = api.GetMethod("GetLeader", pub, null, Type.EmptyTypes, null);
                _findMember = api.GetMethod("FindGroupMemberByPlayerId", pub, null, new[] { typeof(long) }, null);
                _peerId = reference.GetField("peerId");
                _name = reference.GetField("name");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: nao foi possivel ligar a API do Groups: " + e.Message);
            }
        }

        /// <summary>The local player's group: (peer id, name) of everyone in it, self included.
        /// Empty when not grouped or without the mod.</summary>
        internal static List<(long Peer, string Name)> Members()
        {
            var result = new List<(long, string)>();
            Resolve();
            if (_groupPlayers == null || _peerId == null) return result;
            try
            {
                if (_groupPlayers.Invoke(null, null) is IEnumerable list)
                    foreach (var member in list)
                        result.Add(((long)_peerId.GetValue(member), _name?.GetValue(member) as string ?? ""));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: Groups.API.GroupPlayers falhou: " + e.Message);
            }
            return result;
        }

        /// <summary>Whether the local player leads their group (true when alone).</summary>
        internal static bool IsLeader()
        {
            Resolve();
            if (_getLeader == null || _peerId == null) return true;
            try
            {
                object leader = _getLeader.Invoke(null, null);
                if (leader == null) return true;
                return (long)_peerId.GetValue(leader) == ZDOMan.GetSessionID();
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Is <paramref name="playerId"/> in the local player's group?</summary>
        internal static bool InMyGroup(long playerId)
        {
            Resolve();
            if (_findMember == null || playerId == 0L) return false;
            try { return _findMember.Invoke(null, new object[] { playerId }) != null; }
            catch { return false; }
        }
    }
}
