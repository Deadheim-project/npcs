using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Narrower, more reliable sibling of AutoStart: auto-fills/submits the password screen
    /// and auto-confirms character selection, for use together with a
    /// `+connect ip:port password` launch argument that already gets you past world
    /// selection on its own. `+connect` does NOT auto-fill the password prompt by itself --
    /// confirmed live, the game still stops there waiting for a click -- so this reflects in
    /// FejdStartup.ServerPassword (via its property setter) and calls JoinServer()
    /// ourselves before moving on to the character screen. Retries a few times at each step
    /// since screens can take a moment to finish initializing.
    /// </summary>
    public class AutoConfirmCharacter : MonoBehaviour
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void EnsureCreated(string joinPassword)
        {
            var go = new GameObject("NpcValheim_AutoConfirmCharacter");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoConfirmCharacter>().StartCoroutine(Run(joinPassword));
        }

        private static IEnumerator Run(string joinPassword)
        {
            Plugin.Log.LogInfo("SELFTEST: AutoConfirmCharacter waiting for FejdStartup");

            float timeout = Time.realtimeSinceStartup + 60f;
            while (FejdStartup.instance == null && Time.realtimeSinceStartup < timeout)
                yield return null;

            if (FejdStartup.instance == null)
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- FejdStartup.instance never appeared");
                yield break;
            }

            Type type = typeof(FejdStartup);
            object instance = FejdStartup.instance;

            var needPassword = type.GetMethod("NeedPassword", AnyInstance);
            var joinServer = type.GetMethod("JoinServer", AnyInstance);
            var onCharacterStart = type.GetMethod("OnCharacterStart", AnyInstance);
            var serverPassword = type.GetProperty("ServerPassword", AnyInstance);
            var serverPasswordField = type.GetField("<ServerPassword>k__BackingField", AnyInstance);
            var serverPasswordSetter = type.GetMethod("set_ServerPassword", AnyInstance, null, new[] { typeof(string) }, null);
            var passwordInputField = type.GetField("m_serverPassword", AnyInstance);

            if (needPassword == null || joinServer == null || onCharacterStart == null ||
                (serverPassword?.CanWrite != true && serverPasswordField == null &&
                 serverPasswordSetter == null && passwordInputField == null))
            {
                Plugin.Log.LogError($"SELFTEST FAIL: AutoConfirmCharacter -- FejdStartup members via reflection: " +
                    $"NeedPassword={needPassword != null} JoinServer={joinServer != null} " +
                    $"OnCharacterStart={onCharacterStart != null} " +
                    $"ServerPassword={serverPassword?.CanWrite == true || serverPasswordField != null || serverPasswordSetter != null} " +
                    $"PasswordInput={passwordInputField != null}");
                yield break;
            }

            // ---- Phase 1: password screen ----
            bool passwordSubmitted = string.IsNullOrEmpty(joinPassword);
            for (int attempt = 1; attempt <= 8 && !passwordSubmitted; attempt++)
            {
                yield return new WaitForSeconds(1.5f);

                if (FejdStartup.instance == null) { yield break; } // already past the menu entirely

                try
                {
                    bool need = (bool)needPassword.Invoke(instance, Array.Empty<object>());
                    if (!need)
                    {
                        passwordSubmitted = true; // nothing to do, already past this screen
                        break;
                    }
                    if (serverPassword?.CanWrite == true)
                        serverPassword.SetValue(instance, joinPassword, null);
                    else if (serverPasswordSetter != null)
                        serverPasswordSetter.Invoke(instance, new object[] { joinPassword });
                    else if (serverPasswordField != null)
                        serverPasswordField.SetValue(instance, joinPassword);
                    else
                    {
                        var input = passwordInputField.GetValue(instance);
                        if (input == null) throw new InvalidOperationException("password input is not initialized");
                        var textProperty = input.GetType().GetProperty("text", AnyInstance);
                        if (textProperty?.CanWrite != true) throw new MissingMemberException(input.GetType().FullName, "text");
                        textProperty.SetValue(input, joinPassword, null);
                    }
                    joinServer.Invoke(instance, Array.Empty<object>());
                    Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter submitted the server password (attempt {attempt})");
                    passwordSubmitted = true;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"SELFTEST: AutoConfirmCharacter password attempt {attempt} threw (will retry): {e.Message}");
                }
            }

            // ---- Phase 2: character selection screen ----
            for (int attempt = 1; attempt <= 6; attempt++)
            {
                yield return new WaitForSeconds(2f);

                // Once ZNet spins up we've left the menu scene entirely -- FejdStartup.instance
                // goes away, which is our success signal, stop retrying.
                if (FejdStartup.instance == null)
                {
                    Plugin.Log.LogInfo("SELFTEST: AutoConfirmCharacter -- menu scene gone, assuming success");
                    yield break;
                }

                try
                {
                    onCharacterStart.Invoke(instance, Array.Empty<object>());
                    Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter called OnCharacterStart (attempt {attempt})");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"SELFTEST: AutoConfirmCharacter character-start attempt {attempt} threw (will retry): {e.Message}");
                }
            }
        }
    }
}
