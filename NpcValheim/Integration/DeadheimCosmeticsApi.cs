using System;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with Deadheim's cosmetics registry (Deadheim.CosmeticosApi): the
    /// season pass hands out its rewards as registry keys. Bound by reflection and resolved on
    /// first use, like <see cref="DeadheimTrainerApi"/>, so NpcValheim neither compiles nor
    /// loads against Deadheim.
    /// </summary>
    internal static class DeadheimCosmeticsApi
    {
        private const string TypeName = "Deadheim.CosmeticosApi";
        private const int KnownVersion = 1;

        private static bool _resolved;
        private static float _nextLookup;
        private static MethodInfo _grant;

        /// <summary>Whether the registry is there to receive a grant.</summary>
        internal static bool IsAvailable
        {
            get
            {
                Resolve();
                return _grant != null;
            }
        }

        /// <summary>
        /// Grants a key on the server. Returns false when the registry cannot be reached (the
        /// caller must try again later); otherwise true, and <paramref name="newlyGranted"/>
        /// says whether the player lacked the key until now.
        /// </summary>
        internal static bool TryGrant(long playerId, string key, out bool newlyGranted)
        {
            newlyGranted = false;
            Resolve();
            if (_grant == null) return false;
            try
            {
                newlyGranted = (bool)_grant.Invoke(null, new object[] { playerId, key });
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: Deadheim CosmeticosApi.Grant failed: {e.InnerException?.Message ?? e.Message}");
                return false;
            }
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
                if (api == null) return;

                var version = api.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (!(version is int v) || v != KnownVersion)
                {
                    Plugin.Log.LogWarning($"NpcValheim: Deadheim CosmeticosApi version {version ?? "missing"} is not {KnownVersion}; season rewards stay pending");
                    return;
                }

                _grant = api.GetMethod("Grant", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(long), typeof(string) }, null);
                _resolved = _grant != null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the Deadheim CosmeticosApi: {e.Message}");
            }
        }
    }
}
