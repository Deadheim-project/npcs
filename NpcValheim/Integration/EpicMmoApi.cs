using System;
using System.Linq;
using System.Reflection;

namespace NpcValheim.Integration
{
    /// <summary>
    /// Soft integration with WackyMole's EpicMMOSystem (levels, XP, attributes).
    ///
    /// Bound entirely through reflection and resolved lazily, so NpcValheim has no compile-
    /// or load-time dependency on it: with EpicMMO installed the quest rewards and level
    /// gates work, without it every call here is a no-op and IsAvailable is false. That
    /// mirrors how EpicMMO itself ships its API (a small reflection shim other mods copy)
    /// rather than a referenced assembly.
    /// </summary>
    internal static class EpicMmoApi
    {
        private const string PluginGuid = "WackyMole.EpicMMOSystem";

        private static bool _resolved;
        private static MethodInfo _addExp;
        private static MethodInfo _getLevel;

        public static bool IsAvailable
        {
            get
            {
                Resolve();
                return _addExp != null || _getLevel != null;
            }
        }

        /// <summary>Player's EpicMMO level, or 0 when the mod isn't present -- callers should
        /// treat 0 as "no level system", not as "level zero".</summary>
        public static int GetLevel()
        {
            Resolve();
            if (_getLevel == null) return 0;
            try
            {
                return (int)_getLevel.Invoke(null, Array.Empty<object>());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: EpicMMO GetLevel failed: {e.Message}");
                return 0;
            }
        }

        /// <summary>Grants XP. Silently does nothing when EpicMMO isn't installed, so quest
        /// code can hand out XP rewards unconditionally.</summary>
        public static void AddExp(int amount)
        {
            Resolve();
            if (_addExp == null || amount <= 0) return;
            try
            {
                _addExp.Invoke(null, new object[] { amount });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: EpicMMO AddExp failed: {e.Message}");
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "EpicMMOSystem");
                if (assembly == null)
                {
                    Plugin.Log.LogInfo("NpcValheim: EpicMMOSystem not installed -- level gates and XP rewards are disabled");
                    return;
                }

                // The mod exposes a static facade intended exactly for this.
                var apiType = assembly.GetType("API.EpicMMOSystem_API");
                if (apiType == null)
                {
                    Plugin.Log.LogWarning("NpcValheim: EpicMMOSystem found but its API type is missing; skipping integration");
                    return;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                apiType.GetMethod("Init", flags)?.Invoke(null, Array.Empty<object>());

                _addExp = apiType.GetMethod("AddExp", flags, null, new[] { typeof(int) }, null);
                _getLevel = apiType.GetMethod("GetLevel", flags, null, Type.EmptyTypes, null);

                Plugin.Log.LogInfo($"NpcValheim: EpicMMO integration ready (AddExp={_addExp != null}, GetLevel={_getLevel != null}) [{PluginGuid}]");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim: could not bind the EpicMMO API: {e.Message}");
            }
        }
    }
}
