using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with Deadheim's Totem da Cidade (Deadheim.TotemCidadeApi). The totem is a
    /// one-way trip from a player's own base to the city; the Teleportador is both ends of the
    /// city side of it: an admin marks the arrival at a teleporter, and a teleporter can offer
    /// "Voltar para casa", which takes each player back to the totem *they* built -- the server
    /// looks the totem up by its creator, so nobody can be sent into somebody else's base.
    ///
    /// Bound by reflection and resolved on first use, like <see cref="DeadheimTransmogApi"/>, so
    /// NpcValheim neither compiles nor loads against Deadheim. The API only ever grows, so the
    /// check is a minimum version, never equality.
    /// </summary>
    internal static class DeadheimTotemApi
    {
        private const string TypeName = "Deadheim.TotemCidadeApi";
        private const int MinVersion = 1;

        private static bool _resolved;
        private static bool _loggedMissing;
        private static float _nextLookup;
        private static Type _api;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        internal const string NotInstalled = "O Deadheim deste jogo não tem o Totem da Cidade: atualize o Deadheim.";

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        // ---- client and server ----

        public static bool IsEnabled() => CallValue("IsEnabled", false);

        /// <summary>The local player's totems as the server last counted them; 999 = no answer yet.</summary>
        public static int LocalCount() => CallValue("LocalCount", 999);

        /// <summary>Where the totem lands, or false when no arrival is set.</summary>
        public static bool TryArrival(out Vector3 arrival)
        {
            arrival = Vector3.zero;
            var points = ParsePoints(CallText("Arrival"));
            if (points.Count == 0) return false;
            arrival = points[0];
            return true;
        }

        // ---- server ----

        /// <summary>Null means the arrival was written; otherwise the reason it was not.</summary>
        public static string SetArrival(Vector3 position)
        {
            var method = IsAvailable ? Method("SetArrival") : null;
            if (method == null) return NotInstalled;
            try
            {
                return method.Invoke(null, new object[] { position.x, position.y, position.z }) as string;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TotemCidadeApi.SetArrival failed: {e.InnerException?.Message ?? e.Message}");
                return "O Deadheim não conseguiu gravar a chegada.";
            }
        }

        /// <summary>Where this player arrives at each totem they built, in a stable order.</summary>
        public static List<Vector3> HomesOf(long playerId) => ParsePoints(CallText("HomesOf", playerId));

        /// <summary>"x;y;z|x;y;z" with invariant decimals, as Deadheim writes them.</summary>
        internal static List<Vector3> ParsePoints(string raw)
        {
            var points = new List<Vector3>();
            foreach (string entry in (raw ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = entry.Split(';');
                if (p.Length != 3) continue;
                if (float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    points.Add(new Vector3(x, y, z));
            }
            return points;
        }

        // ---- plumbing (same as DeadheimTransmogApi) ----

        private static string CallText(string name, params object[] args)
        {
            var method = Method(name);
            if (method == null) return null;
            try
            {
                return method.Invoke(null, args) as string;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TotemCidadeApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
                return null;
            }
        }

        private static T CallValue<T>(string name, T fallback, params object[] args) where T : struct
        {
            var method = Method(name);
            if (method == null) return fallback;
            try
            {
                return method.Invoke(null, args) is T value ? value : fallback;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TotemCidadeApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
                return fallback;
            }
        }

        private static MethodInfo Method(string name)
        {
            Resolve();
            if (_api == null) return null;
            if (Methods.TryGetValue(name, out var cached)) return cached;
            var method = _api.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
            if (method == null)
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TotemCidadeApi has no {name}");
            Methods[name] = method;
            return method;
        }

        private static void Resolve()
        {
            if (_resolved) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextLookup) return;
            _nextLookup = now + 10f;

            try
            {
                var api = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(TypeName, false))
                    .FirstOrDefault(t => t != null);
                if (api == null)
                {
                    if (!_loggedMissing)
                        Plugin.Log.LogInfo("NpcValheim: Deadheim TotemCidadeApi not found -- the teleporter has no totem options");
                    _loggedMissing = true;
                    return;
                }

                var version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v < MinVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Deadheim TotemCidadeApi version {version ?? "missing"} is below {MinVersion}; skipping integration");
                    return;
                }

                _api = api;
                _resolved = true;
                _loggedMissing = false;
                Plugin.Log.LogInfo($"NpcValheim: Deadheim totem integration ready (API v{v})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Deadheim TotemCidadeApi: {e.Message}");
            }
        }
    }
}
