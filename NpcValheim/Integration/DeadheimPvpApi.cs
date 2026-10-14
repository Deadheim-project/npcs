using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with Deadheim's PvP module (Deadheim.Pvp.PvpApi): the bounty board the
    /// Caçador de Recompensas runs, and the PvE/PvP switch the Juiz de Guerra runs.
    ///
    /// Bound by reflection and resolved on first use, like <see cref="MontariasApi"/>, so
    /// NpcValheim neither compiles nor loads against Deadheim. Without it (or with a Deadheim
    /// older than 7.5.9, which has no PvpApi) both NPCs say the service is closed; nothing
    /// else changes.
    ///
    /// Everything here runs on the client and only asks: Deadheim's own server decides, with
    /// the same rules the old /bounty and /pve commands had. Its Version constant guards the
    /// shape -- a version this file does not know is treated as not installed.
    /// </summary>
    internal static class DeadheimPvpApi
    {
        private const string TypeName = "Deadheim.Pvp.PvpApi";
        private const int KnownVersion = 1;

        private static bool _resolved;
        private static bool _loggedMissing;
        private static float _nextLookup;
        private static Type _api;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        internal const string NotInstalled = "O PvP do Deadheim não está instalado (ou está desatualizado: precisa do 7.5.9).";

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        /// <summary>PvP module on, in a world, with Deadheim's channel open.</summary>
        public static bool Active() => CallValue("Active", false);

        public static string FormatDuration(double seconds) =>
            Call<string>("FormatDuration", seconds) ?? $"{Math.Ceiling(seconds)}s";

        // ---- bounty ----

        public static bool BountyEnabled() => CallValue("BountyEnabled", false);
        public static int BountyMinimum() => CallValue("BountyMinimum", 0);
        public static float BountyKillerSharePercent() => CallValue("BountyKillerSharePercent", 0f);
        public static int BountyDailyCap() => CallValue("BountyDailyCap", 0);
        public static int BountyDelaySeconds() => CallValue("BountyDelaySeconds", 0);
        public static int LocalBountyPot() => CallValue("LocalBountyPot", 0);
        public static int LocalBuyoutCost() => CallValue("LocalBuyoutCost", 0);
        public static bool LocalHunted() => CallValue("LocalHunted", false);
        public static bool LocalHuntPending() => CallValue("LocalHuntPending", false);
        public static double LocalBountyCooldownSeconds() => CallValue("LocalBountyCooldownSeconds", 0d);
        public static bool RequestBountyBoard() => CallValue("RequestBountyBoard", false);
        public static int BountyBoardRevision() => CallValue("BountyBoardRevision", 0);

        /// <summary>{ target, payout, state, online "1"/"0", hunted "1"/"0" } per bounty, biggest first.</summary>
        public static string[][] BountyBoard() => Call<string[][]>("BountyBoard") ?? new string[0][];

        /// <summary>Null when the request went out, else why not.</summary>
        public static string PlaceBounty(string target, int amount) =>
            IsAvailable ? Call<string>("PlaceBounty", target, amount) : NotInstalled;

        public static string PayOwnBounty() => IsAvailable ? Call<string>("PayOwnBounty") : NotInstalled;

        public static string LastBountyMessage() => Call<string>("LastBountyMessage") ?? "";
        public static int BountyMessageRevision() => CallValue("BountyMessageRevision", 0);

        // ---- PvE / PvP ----

        public static bool PveEnabled() => CallValue("PveEnabled", false);
        public static bool LocalIsPve() => CallValue("LocalIsPve", false);
        public static string PveTitle() => Call<string>("PveTitle") ?? "PvE";
        public static float PveSwitchCooldownHours() => CallValue("PveSwitchCooldownHours", 0f);
        public static float PveSkillMultiplier() => CallValue("PveSkillMultiplier", 1f);
        public static float PvpSkillMultiplier() => CallValue("PvpSkillMultiplier", 1f);
        public static float PveResourceRate() => CallValue("PveResourceRate", 0f);
        public static float PvpResourceRate() => CallValue("PvpResourceRate", 0f);
        public static double LocalPveSwitchSeconds() => CallValue("LocalPveSwitchSeconds", 0d);

        /// <summary>Why switching would be refused right now, or null when it can be asked.</summary>
        public static string PveSwitchRefusal(bool toPve) =>
            IsAvailable ? Call<string>("PveSwitchRefusal", toPve) : NotInstalled;

        /// <summary>Null when the request went out, else why not.</summary>
        public static string RequestPveSwitch(bool toPve) =>
            IsAvailable ? Call<string>("RequestPveSwitch", toPve) : NotInstalled;

        public static string LastPveMessage() => Call<string>("LastPveMessage") ?? "";
        public static int PveMessageRevision() => CallValue("PveMessageRevision", 0);

        // ---- admin ----

        public static bool LocalIsAdmin() => CallValue("LocalIsAdmin", false);

        /// <summary>{ key, label, value (cfg text), kind "bool"/"number"/"text", description } per attribute.</summary>
        public static string[][] PveSettings() => Call<string[][]>("PveSettings") ?? new string[0][];

        /// <summary>Null when the request went out, else why not. The server answers in LastSettingMessage.</summary>
        public static string SetPveSetting(string key, string value) =>
            IsAvailable ? Call<string>("SetPveSetting", key, value) : NotInstalled;

        public static string LastSettingMessage() => Call<string>("LastSettingMessage") ?? "";
        public static int SettingMessageRevision() => CallValue("SettingMessageRevision", 0);

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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim PvpApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim PvpApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim PvpApi has no {name}");
            Methods[name] = method;
            return method;
        }

        /// <summary>Looks for Deadheim at most every few seconds until it is found, so an answer
        /// of "missing" given before every plugin finished loading does not stick.</summary>
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
                        Plugin.Log.LogInfo("NpcValheim: Deadheim PvpApi not found -- bounty and PvE/PvP NPCs are closed");
                    _loggedMissing = true;
                    return;
                }

                var version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v != KnownVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Deadheim PvpApi version {version ?? "missing"} is not {KnownVersion}; skipping integration");
                    return;
                }

                _api = api;
                _resolved = true;
                _loggedMissing = false;
                Plugin.Log.LogInfo($"NpcValheim: Deadheim PvP integration ready (API v{v})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Deadheim PvpApi: {e.Message}");
            }
        }
    }
}
