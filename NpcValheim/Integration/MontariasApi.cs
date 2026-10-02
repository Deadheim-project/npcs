using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with the Montarias mod (ValheimMontarias): the mounts and the riding
    /// skill that the Mestre das Montarias sells.
    ///
    /// Bound by reflection and resolved on first use, like <see cref="EpicMmoApi"/>, so
    /// NpcValheim neither compiles nor loads against it. Without Montarias the trainer has
    /// nothing to sell and says so; nothing else changes.
    ///
    /// The other side is ValheimMontarias.MontariasApi, which keeps to primitive types for
    /// exactly this. Its Version constant guards the shape: a version this file does not know
    /// is treated as not installed rather than called into blind.
    /// </summary>
    internal static class MontariasApi
    {
        private const string AssemblyName = "ValheimMontarias";
        private const string TypeName = "ValheimMontarias.MontariasApi";
        private const int KnownVersion = 1;

        private static bool _resolved;
        private static bool _loggedMissing;
        private static float _nextLookup;
        private static Type _api;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        // ---- catalog ----

        public static string[] MountIds() => Call<string[]>("MountIds") ?? new string[0];

        public static string MountName(string id) => Call<string>("MountName", id) ?? id ?? "";

        public static string MountIconPath(string id) => Call<string>("MountIconPath", id) ?? "";

        public static int MountRequiredRank(string id) => CallValue("MountRequiredRank", 1, id);

        public static int RankCount() => CallValue("RankCount", 0);

        public static string RankName(int rank) => Call<string>("RankName", rank) ?? $"Nível {rank}";

        public static float RankSpeed(int rank) => CallValue("RankSpeed", 1f, rank);

        // ---- local player ----

        public static bool LocalKnown() => CallValue("LocalKnown", false);

        public static int LocalRank() => CallValue("LocalRank", 0);

        public static bool LocalOwns(string id) => CallValue("LocalOwns", false, id);

        public static int LocalRevision() => CallValue("LocalRevision", 0);

        public static void RequestLocalState() => Call<object>("RequestLocalState");

        // ---- server ----

        /// <summary>The sender's riding level, or -1 when it cannot be read (no Montarias, or an
        /// account the server cannot resolve).</summary>
        public static int ServerRank(long sender) => CallValue("ServerRank", -1, sender);

        public static bool ServerOwns(long sender, string mountId) => CallValue("ServerOwns", false, sender, mountId);

        /// <summary>Null on success, else the message for the player.</summary>
        public static string ServerGrantRank(long sender, int rank, string why) =>
            IsAvailable ? Call<string>("ServerGrantRank", sender, rank, why) : NotInstalled;

        public static string ServerGrantMount(long sender, string mountId, string why) =>
            IsAvailable ? Call<string>("ServerGrantMount", sender, mountId, why) : NotInstalled;

        internal const string NotInstalled = "As montarias não estão instaladas no servidor.";

        // ---- plumbing ----

        private static T Call<T>(string name, params object[] args) where T : class
        {
            var method = Method(name);
            if (method == null) return null;
            try
            {
                return method.Invoke(null, args) as T;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Montarias {name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Montarias {name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Montarias API has no {name}");
            Methods[name] = method;
            return method;
        }

        /// <summary>Looks for the mod at most every few seconds until it is found. Not once and
        /// for all: the panel asks every frame, and an answer of "missing" given before every
        /// plugin finished loading must not stick for the whole session.</summary>
        private static void Resolve()
        {
            if (_resolved) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextLookup) return;
            _nextLookup = now + 10f;

            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == AssemblyName);
                if (assembly == null)
                {
                    if (!_loggedMissing)
                        Plugin.Log.LogInfo("NpcValheim: Montarias not installed -- the Mestre das Montarias has nothing to sell");
                    _loggedMissing = true;
                    return;
                }

                _resolved = true;
                var api = assembly.GetType(TypeName);
                var version = api?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v != KnownVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Montarias API version {version ?? "missing"} is not {KnownVersion}; skipping integration");
                    return;
                }

                _api = api;
                Plugin.Log.LogInfo($"NpcValheim: Montarias integration ready (API v{v})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Montarias API: {e.Message}");
            }
        }
    }
}
