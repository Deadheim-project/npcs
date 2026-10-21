using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with Deadheim's transmog (Deadheim.TransmogApi), which the Alfaiate
    /// fronts. Deadheim owns the rules (VIP, what was unlocked, same slot and weight class, the
    /// blacklist), the per-world file and the drawing; the Alfaiate adds the NPC, the price in
    /// Coins and the arena rating requirement.
    ///
    /// Bound by reflection and resolved on first use, like <see cref="DeadheimTrainerApi"/>, so
    /// NpcValheim neither compiles nor loads against Deadheim. A version this file does not
    /// know is treated as not installed.
    /// </summary>
    internal static class DeadheimTransmogApi
    {
        private const string TypeName = "Deadheim.TransmogApi";
        private const int KnownVersion = 1;

        private static bool _resolved;
        private static bool _loggedMissing;
        private static float _nextLookup;
        private static Type _api;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        internal const string NotInstalled = "O Deadheim deste jogo não tem o transmog: atualize o Deadheim.";

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        // ---- client ----

        public static bool IsEnabled() => CallValue("IsEnabled", false);
        public static bool RequiresVip() => CallValue("RequiresVip", true);
        public static bool IsLocalVip() => CallValue("IsLocalVip", false);
        public static int MaxChoices() => CallValue("MaxChoices", 0);
        public static int StateRevision() => CallValue("StateRevision", 0);
        public static bool IsSuppressedHere() => CallValue("IsSuppressedHere", false);

        public static List<string> LocalUnlocked() =>
            (CallText("LocalUnlocked") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();

        public static Dictionary<string, string> LocalChosen()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in ParsePairs(CallText("LocalChosen"))) map[kv.Key] = kv.Value;
            return map;
        }

        public static string SlotOf(string prefab) => CallText("SlotOf", prefab) ?? "";

        public static string WhyIncompatible(string real, string app) =>
            IsAvailable ? CallText("WhyIncompatible", real, app) : NotInstalled;

        public static void SetPreview(string pairs) => CallText("SetPreview", pairs ?? "");
        public static void ClearPreview() => CallText("ClearPreview");

        // ---- server ----

        public static string ServerCheck(long peerId, string pairs) => Grant("ServerCheck", peerId, pairs ?? "");
        public static int ServerChangedCount(long peerId, string pairs) => CallValue("ServerChangedCount", 0, peerId, pairs ?? "");
        public static string ServerApply(long peerId, string pairs) => Grant("ServerApply", peerId, pairs ?? "");
        public static string ServerOff(long peerId, string real) => Grant("ServerOff", peerId, real ?? "");

        /// <summary>"real>app;real>app" in order.</summary>
        internal static List<KeyValuePair<string, string>> ParsePairs(string raw)
        {
            var list = new List<KeyValuePair<string, string>>();
            foreach (string p in (raw ?? "").Split(';'))
            {
                int i = p.IndexOf('>');
                if (i > 0 && i < p.Length - 1) list.Add(new KeyValuePair<string, string>(p.Substring(0, i), p.Substring(i + 1)));
            }
            return list;
        }

        internal static string EncodePairs(IEnumerable<KeyValuePair<string, string>> pairs) =>
            string.Join(";", pairs.Select(kv => kv.Key + ">" + kv.Value));

        // ---- plumbing (same as DeadheimTrainerApi) ----

        /// <summary>Null means only "it took effect": a missing or throwing method is a failure
        /// with a reason, or the buyer would pay Coins for a look that never changed.</summary>
        private static string Grant(string name, params object[] args)
        {
            var method = IsAvailable ? Method(name) : null;
            if (method == null) return NotInstalled;
            try
            {
                return method.Invoke(null, args) as string;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TransmogApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
                return "O Deadheim não conseguiu trocar a aparência.";
            }
        }

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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TransmogApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TransmogApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TransmogApi has no {name}");
            Methods[name] = method;
            return method;
        }

        private static void Resolve()
        {
            if (_resolved) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
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
                        Plugin.Log.LogInfo("NpcValheim: Deadheim TransmogApi not found -- the Alfaiate stays closed");
                    _loggedMissing = true;
                    return;
                }

                var version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v != KnownVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Deadheim TransmogApi version {version ?? "missing"} is not {KnownVersion}; skipping integration");
                    return;
                }

                _api = api;
                _resolved = true;
                _loggedMissing = false;
                Plugin.Log.LogInfo($"NpcValheim: Deadheim transmog integration ready (API v{v})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Deadheim TransmogApi: {e.Message}");
            }
        }
    }
}
