using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with Deadheim's Treinador buffs (Deadheim.TreinadorApi): the study (more
    /// skill XP for a while) and the protection (less skill lost on a death), which the Treinador
    /// sells. Deadheim owns both because it owns the skill math they change -- its RaiseSkill
    /// already applies the server's SkillMultiplier and its death patch the PvP penalties.
    ///
    /// Bound by reflection and resolved on first use, like <see cref="DeadheimPvpApi"/>, so
    /// NpcValheim neither compiles nor loads against Deadheim. Without it (or with a Deadheim
    /// that predates the API) the Treinador still gives lessons but does not sell the buffs.
    /// Its Version constant guards the shape -- a version this file does not know is treated
    /// as not installed.
    /// </summary>
    internal static class DeadheimTrainerApi
    {
        private const string TypeName = "Deadheim.TreinadorApi";
        private const int KnownVersion = 1;

        private static bool _resolved;
        private static bool _loggedMissing;
        private static float _nextLookup;
        private static Type _api;
        private static readonly Dictionary<string, MethodInfo> Methods = new Dictionary<string, MethodInfo>();

        internal const string NotInstalled = "O Deadheim deste jogo não tem os buffs do Treinador: atualize o Deadheim.";

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _api != null;
            }
        }

        /// <summary>Null when the study was applied, else why not.</summary>
        public static string GrantStudy(float bonus, int minutes, int maxSkill) =>
            Grant("GrantStudy", bonus, (float)minutes, (float)maxSkill);

        public static double LocalStudySeconds() => CallValue("LocalStudySeconds", 0d);
        public static float LocalStudyBonus() => CallValue("LocalStudyBonus", 0f);

        /// <summary>Null when the protection was applied, else why not.</summary>
        public static string GrantProtection(float factor, bool pveOnly) => Grant("GrantProtection", factor, pveOnly);

        public static int LocalProtectionCharges() => CallValue("LocalProtectionCharges", 0);

        /// <summary>The local player's skill gain before the study, as a fraction of vanilla.</summary>
        public static float LocalSkillGain() => CallValue("LocalSkillGain", 1f);

        // ---- plumbing (same as DeadheimPvpApi) ----

        /// <summary>
        /// A grant answers null when it took effect. Here null must mean only that: a method
        /// that is missing or throws is a failure with a reason, or the buyer would pay Coins
        /// for a buff that never started.
        /// </summary>
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TreinadorApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
                return "O Deadheim não conseguiu aplicar o buff.";
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TreinadorApi.{name} failed: {e.InnerException?.Message ?? e.Message}");
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
                Plugin.Log.LogWarning($"NpcValheim: Deadheim TreinadorApi has no {name}");
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
                        Plugin.Log.LogInfo("NpcValheim: Deadheim TreinadorApi not found -- the Treinador sells lessons only");
                    _loggedMissing = true;
                    return;
                }

                var version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v != KnownVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Deadheim TreinadorApi version {version ?? "missing"} is not {KnownVersion}; skipping integration");
                    return;
                }

                _api = api;
                _resolved = true;
                _loggedMissing = false;
                Plugin.Log.LogInfo($"NpcValheim: Deadheim Treinador integration ready (API v{v})");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Deadheim TreinadorApi: {e.Message}");
            }
        }
    }
}
