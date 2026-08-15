using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Dev-only (gated by Testing.EnableSelfTest): auto-selects the first world in the list
    /// and starts it, then auto-continues past character selection, so the self-test can run
    /// fully hands-off from a cold launch, entirely in local/singleplayer -- no dedicated
    /// server, no password screen, none of that complexity. Only makes sense together with
    /// SelfTestRunner -- there's no reason to skip the menu otherwise.
    ///
    /// Uses System.Reflection with BindingFlags.NonPublic rather than calling FejdStartup
    /// members directly. Several of its members throw FieldAccessException /
    /// MethodAccessException at runtime despite compiling fine against the publicized
    /// reference assembly we build against, so whatever tool publicized that DLL didn't
    /// cover this type. Reflection with NonPublic binding flags looks up and invokes members
    /// by name at runtime instead of emitting a direct IL member-access instruction, which
    /// sidesteps that CLR-level accessibility check entirely.
    ///
    /// SetSelectedWorld(0, true) alone previously NRE'd inside the game's own code -- it
    /// assumes the on-screen world-list UI elements already exist, which only happens after
    /// UpdateWorldList() builds them. Calling that first fixes it.
    /// </summary>
    public class AutoStart : MonoBehaviour
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_AutoStart");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoStart>().StartCoroutine(Run());
        }

        private static IEnumerator Run()
        {
            Plugin.Log.LogInfo("SELFTEST: AutoStart waiting for main menu");

            float timeout = Time.realtimeSinceStartup + 60f;
            while (FejdStartup.instance == null && Time.realtimeSinceStartup < timeout)
                yield return null;

            if (FejdStartup.instance == null)
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoStart -- FejdStartup.instance never appeared");
                yield break;
            }

            yield return new WaitForSeconds(0.5f); // let the menu scene settle

            object startup = FejdStartup.instance;
            Type type = typeof(FejdStartup);

            // World list UI elements don't exist until this runs -- SetSelectedWorld
            // indexes into them and NREs if called first.
            if (!TryInvoke(type, startup, "UpdateWorldList", new object[] { true }))
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoStart -- could not build world list (see error above)");
                yield break;
            }

            yield return new WaitForSeconds(0.3f);

            if (!TryInvoke(type, startup, "SetSelectedWorld", new object[] { PickWorld(type, startup), true }))
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoStart -- could not select a world (see error above)");
                yield break;
            }

            yield return new WaitForSeconds(0.3f);

            if (!TryInvoke(type, startup, "OnWorldStart", Array.Empty<object>()))
                yield break;

            Plugin.Log.LogInfo("SELFTEST: AutoStart called OnWorldStart, waiting for character screen");
            yield return new WaitForSeconds(1f);

            // OnCharacterStart is what a click on the character-selection "Start" button
            // invokes; if there's no character yet a fresh one is auto-created by the game
            // itself before this point (same as vanilla first-time flow). Retry a few times
            // in case the screen is still initializing.
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                if (FejdStartup.instance == null)
                {
                    Plugin.Log.LogInfo("SELFTEST: AutoStart -- menu scene gone, assuming success");
                    yield break;
                }

                if (TryInvoke(type, startup, "OnCharacterStart", Array.Empty<object>()))
                    yield break;

                yield return new WaitForSeconds(1f);
            }
        }

        /// <summary>
        /// Picks which world to load. Index 0 used to be taken blindly, and that turned a
        /// 30-second test cycle into a five-minute one whenever the top entry happened to be
        /// a world with no .db yet -- the game silently regenerates the whole map, terrain
        /// and locations included, before the run can even start.
        ///
        /// So: honour Testing.WorldName if set, otherwise take the first world that is
        /// already generated. Falls back to 0 only when nothing else is knowable.
        /// </summary>
        private static int PickWorld(Type type, object startup)
        {
            try
            {
                var worlds = type.GetField("m_worlds", AnyInstance)?.GetValue(startup) as System.Collections.IList;
                if (worlds == null || worlds.Count == 0) return 0;

                string wanted = Plugin.WorldName?.Value ?? "";
                int firstGenerated = -1;

                for (int i = 0; i < worlds.Count; i++)
                {
                    var world = worlds[i];
                    if (world == null) continue;
                    var worldType = world.GetType();
                    string name = worldType.GetField("m_name", AnyInstance)?.GetValue(world) as string ?? "";

                    if (!string.IsNullOrEmpty(wanted) && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        Plugin.Log.LogInfo($"SELFTEST: AutoStart selected world '{name}' (index {i}, by name)");
                        return i;
                    }

                    if (firstGenerated < 0 && HasSavedMap(worldType, world))
                        firstGenerated = i;
                }

                if (!string.IsNullOrEmpty(wanted))
                    Plugin.Log.LogWarning($"SELFTEST: AutoStart -- world '{wanted}' not in the list, falling back");

                if (firstGenerated >= 0)
                {
                    Plugin.Log.LogInfo($"SELFTEST: AutoStart selected world index {firstGenerated} (already generated)");
                    return firstGenerated;
                }

                Plugin.Log.LogWarning("SELFTEST: AutoStart -- no already-generated world found; index 0 will be generated from scratch, which is slow");
                return 0;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"SELFTEST: AutoStart -- could not inspect the world list ({e.Message}); using index 0");
                return 0;
            }
        }

        /// <summary>True if this world's map is already on disk, i.e. loading it will not
        /// trigger a full world generation.</summary>
        private static bool HasSavedMap(Type worldType, object world)
        {
            var check = worldType.GetMethod("CheckDbFile", AnyInstance);
            return check != null && check.GetParameters().Length == 0 && (bool)check.Invoke(world, Array.Empty<object>());
        }

        private static bool TryInvoke(Type type, object instance, string methodName, object[] args)
        {
            try
            {
                var method = type.GetMethod(methodName, AnyInstance);
                if (method == null)
                {
                    Plugin.Log.LogError($"SELFTEST FAIL: AutoStart -- method '{methodName}' not found via reflection");
                    return false;
                }
                method.Invoke(instance, args);
                Plugin.Log.LogInfo($"SELFTEST: AutoStart called {methodName} (via reflection)");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"SELFTEST FAIL: AutoStart -- {methodName} threw: {e}");
                return false;
            }
        }
    }
}
