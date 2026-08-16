using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Cities
{
    /// <summary>Where a city is, as a client knows it.</summary>
    internal sealed class CityMark
    {
        internal string Id;
        internal string Name;
        internal Vector3 Position;
    }

    /// <summary>
    /// Tells clients where the cities are, and pins them on the map.
    ///
    /// A client cannot work this out for itself: the registry is a LiteDB file next to the
    /// plugin on whoever is authoritative, and on a dedicated server that machine is not the
    /// player's. So the coordinates travel over the same routed-RPC pattern the mail HUD uses --
    /// the client asks once it is in the world, the server answers with the cities belonging to
    /// the loaded world, and nothing else is sent.
    ///
    /// The pin is the whole point of shipping this rather than leaving the towns to be stumbled
    /// upon. A city can sit 4 km from spawn; without a pin, "there are three cities" and "there
    /// are no cities" are the same experience for most players.
    /// </summary>
    internal sealed class CityDirectory : MonoBehaviour
    {
        private const string RpcRequest = "NpcValheim_CityRequest";
        private const string RpcData = "NpcValheim_CityData";

        private static CityDirectory _instance;
        private static bool _registered;

        internal static List<CityMark> Known { get; private set; } = new List<CityMark>();

        private readonly HashSet<string> _pinned = new HashSet<string>();
        private float _nextRequest;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_CityDirectory");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<CityDirectory>();
        }

        private void Update()
        {
            TryRegister();

            if (Player.m_localPlayer == null)
            {
                // A fresh world means a fresh set of cities; holding the old ones would pin
                // last session's towns onto this session's map.
                if (Known.Count > 0) Known = new List<CityMark>();
                _pinned.Clear();
                _nextRequest = 0f;
                return;
            }

            // Asked repeatedly until an answer arrives, because the first request can land
            // before the server has finished siting -- and a request that goes unanswered once
            // would otherwise mean no pins for the whole session.
            if (Known.Count == 0 && Time.time >= _nextRequest)
            {
                _nextRequest = Time.time + 10f;
                SendToServer(RpcRequest);
            }

            if (Plugin.CityMapPins.Value) Pin();
        }

        private void Pin()
        {
            if (Minimap.instance == null) return;

            foreach (var city in Known)
            {
                if (!_pinned.Add(city.Id)) continue;

                try
                {
                    Minimap.instance.AddPin(city.Position, Minimap.PinType.Icon3, city.Name, save: false, isChecked: false);
                }
                catch (Exception e)
                {
                    // Unpin so a later frame retries rather than leaving this city off the map
                    // forever because the minimap was mid-initialisation.
                    _pinned.Remove(city.Id);
                    Plugin.Log.LogWarning($"NpcValheim: could not pin the city '{city.Name}': {e.Message}");
                    return;
                }
            }
        }

        private static void TryRegister()
        {
            if (_registered || ZRoutedRpc.instance == null) return;
            _registered = true;
            ZRoutedRpc.instance.Register(RpcRequest, (Action<long>)OnRequest);
            ZRoutedRpc.instance.Register(RpcData, (Action<long, string>)OnData);
            Plugin.Log.LogInfo("NpcValheim: city directory RPCs registered");
        }

        /// <summary>From a client, Valheim addresses the host with 0 rather than with
        /// GetServerPeerID() -- the same asymmetry the mail HUD documents. The id itself goes
        /// through GameApi because calling it directly throws MethodAccessException on this
        /// install.</summary>
        private static long ServerRpcTarget() =>
            ZNet.instance != null && ZNet.instance.IsServer() ? Npc.GameApi.GetServerPeerId() : 0L;

        private static void SendToServer(string rpc, params object[] args)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerRpcTarget(), rpc, args ?? new object[0]);
        }

        private static void OnRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            string world;
            try { world = ZNet.instance.GetWorldName(); }
            catch { world = null; }

            var records = string.IsNullOrEmpty(world) ? CityDatabase.All() : CityDatabase.InWorld(world);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, new object[] { Pack(records) });
        }

        private static void OnData(long sender, string packed)
        {
            Known = Unpack(packed);
            Plugin.Log.LogInfo($"NpcValheim: the server reports {Known.Count} city/cities in this world");
        }

        internal static string Pack(List<CityRecord> records)
        {
            var sb = new StringBuilder();
            foreach (var record in records ?? new List<CityRecord>())
            {
                if (record == null) continue;
                sb.Append(record.Id).Append('|')
                  .Append(record.Name).Append('|')
                  .Append(record.X.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(record.Y.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                  .Append(record.Z.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            }
            return sb.ToString();
        }

        internal static List<CityMark> Unpack(string packed)
        {
            var result = new List<CityMark>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (line.Length == 0) continue;
                var f = line.Split('|');
                if (f.Length < 5) continue;

                if (!float.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    continue;

                result.Add(new CityMark { Id = f[0], Name = f[1], Position = new Vector3(x, y, z) });
            }
            return result;
        }
    }
}
