using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Testing
{
    /// <summary>
    /// Dev-only (gated by Testing.AutoConfirmCharacterOnJoin): joins a dedicated server from
    /// a cold launch with zero clicks -- queues the join, answers the password prompt and
    /// confirms character selection.
    ///
    /// It no longer depends on a `+connect ip:port` launch argument. All `+connect` does is
    /// call ZSteamMatchmaking.QueueServerJoin(addr) from FejdStartup.HandleStartupJoin, and
    /// we can call exactly that ourselves from Testing.AutoJoinServer -- which means a test
    /// run needs one config file instead of a config file plus the right Steam launch options.
    ///
    /// The password is NOT typed into any field. ZNet.RPC_ClientHandshake reads the static
    /// FejdStartup.ServerPassword and, when it is non-null, answers the server challenge with
    /// it instead of opening the dialog. Setting that property once, up front, is the whole
    /// mechanism. (The previous version asked FejdStartup.NeedPassword() first, which is about
    /// *hosting* -- it reports whether the public/open toggles on the start-a-server panel
    /// require a password -- so it answered false on every join and the password was never
    /// set. It also looked the property up with instance binding flags, and the property is
    /// static, so every fallback silently resolved to null.)
    ///
    /// Uses reflection on FejdStartup for the same reason AutoStart does: several of its
    /// members throw FieldAccessException at runtime despite compiling against the publicized
    /// reference assembly. ZSteamMatchmaking and ZNet are genuinely public, so those are
    /// called directly.
    /// </summary>
    public class AutoConfirmCharacter : MonoBehaviour
    {
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static void EnsureCreated(string joinPassword, string joinServer)
        {
            var go = new GameObject("NpcValheim_AutoConfirmCharacter");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoConfirmCharacter>().StartCoroutine(Run(joinPassword, joinServer));
        }

        private static IEnumerator Run(string joinPassword, string joinServer)
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

            var onCharacterStart = type.GetMethod("OnCharacterStart", AnyInstance);
            var serverPassword = type.GetProperty("ServerPassword", AnyStatic);
            var profilesField = type.GetField("m_profiles", AnyInstance);
            var profileIndexField = type.GetField("m_profileIndex", AnyInstance);
            var characterScreenField = type.GetField("m_characterSelectScreen", AnyInstance);

            if (onCharacterStart == null || profilesField == null || profileIndexField == null)
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- FejdStartup members via reflection: " +
                    $"OnCharacterStart={onCharacterStart != null} m_profiles={profilesField != null} " +
                    $"m_profileIndex={profileIndexField != null} ServerPassword={serverPassword?.CanWrite == true}");
                yield break;
            }

            // ---- Password: answered by ZNet during the handshake, not typed anywhere ----
            if (!string.IsNullOrEmpty(joinPassword))
            {
                if (serverPassword?.CanWrite != true)
                    Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- FejdStartup.ServerPassword is not writable, the join will stop at the password dialog");
                else
                {
                    serverPassword.SetValue(null, joinPassword, null);
                    Plugin.Log.LogInfo("SELFTEST: AutoConfirmCharacter armed the server password");
                }
            }

            // ---- Queue the join ourselves, the way +connect would ----
            if (!string.IsNullOrEmpty(joinServer))
            {
                if (ZSteamMatchmaking.instance == null)
                    Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- ZSteamMatchmaking.instance is null, cannot queue the join");
                else
                {
                    // QueueServerJoin logs its own "Couldn't resolve IP address." and leaves
                    // nothing queued when the address is malformed; the state dump at the end
                    // is what reports that back here.
                    ZSteamMatchmaking.instance.QueueServerJoin(joinServer);
                    Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter queued a join to {joinServer}");
                }
            }

            // FejdStartup.Update -> CheckPendingJoinRequest picks the queued join up and moves
            // to character selection on its own; wait for that rather than racing it.
            float screenDeadline = Time.realtimeSinceStartup + 30f;
            while (Time.realtimeSinceStartup < screenDeadline)
            {
                if (FejdStartup.instance == null || ZNet.instance != null) yield break; // already past the menu
                var screen = characterScreenField?.GetValue(instance) as GameObject;
                if (screen != null && screen.activeInHierarchy) break;
                yield return null;
            }

            // ---- Character selection ----
            // OnCharacterStart returns silently when no profile is selected -- that is exactly
            // what the last run did six times in a row, logging six successes and joining
            // nothing. Select one before calling it, and say so when there is none to select.
            var profiles = profilesField.GetValue(instance) as IList;
            int profileIndex = (int)profileIndexField.GetValue(instance);
            if (profiles == null || profiles.Count == 0)
            {
                Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- no character profiles exist on this machine, create one first");
                yield break;
            }
            if (profileIndex < 0 || profileIndex >= profiles.Count)
            {
                profileIndexField.SetValue(instance, 0);
                Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter selected character 0 of {profiles.Count} (was {profileIndex})");
            }

            for (int attempt = 1; attempt <= 6; attempt++)
            {
                yield return new WaitForSeconds(2f);

                // ZNet appearing (or the menu scene going away) means we are through.
                if (ZNet.instance != null || FejdStartup.instance == null)
                {
                    Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter joined (attempt {attempt})");
                    yield break;
                }

                try
                {
                    onCharacterStart.Invoke(instance, Array.Empty<object>());
                    Plugin.Log.LogInfo($"SELFTEST: AutoConfirmCharacter called OnCharacterStart (attempt {attempt})");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"SELFTEST: AutoConfirmCharacter character-start attempt {attempt} threw (will retry): {Describe(e)}");
                }
            }

            if (ZNet.instance != null || FejdStartup.instance == null)
            {
                Plugin.Log.LogInfo("SELFTEST: AutoConfirmCharacter joined");
                yield break;
            }

            // Falling off the end used to be silent, which read in the log exactly like a
            // success that happened to be quiet. Say what state it gave up in.
            Plugin.Log.LogError("SELFTEST FAIL: AutoConfirmCharacter -- still on the menu after 6 attempts: " +
                DescribeState(type, instance, profilesField, profileIndexField, characterScreenField));
        }

        /// <summary>Everything needed to tell "wrong password", "no character" and "nothing
        /// queued" apart from the log alone, without another run.</summary>
        private static string DescribeState(Type type, object instance, FieldInfo profilesField, FieldInfo profileIndexField, FieldInfo characterScreenField)
        {
            var queuedField = type.GetField("m_queuedJoinServer", AnyInstance);
            var joinField = type.GetField("m_joinServer", AnyInstance);

            return $"profileIndex={Try(() => profileIndexField.GetValue(instance).ToString())} " +
                   $"profiles={Try(() => ((IList)profilesField.GetValue(instance)).Count.ToString())} " +
                   $"queuedJoin={Try(() => queuedField?.GetValue(instance)?.ToString())} " +
                   $"joinServer={Try(() => joinField?.GetValue(instance)?.ToString())} " +
                   $"characterScreen={Try(() => (characterScreenField?.GetValue(instance) as GameObject)?.activeInHierarchy.ToString())} " +
                   $"passwordDialog={Try(() => ZNet.instance != null ? ZNet.instance.InPasswordDialog().ToString() : "no ZNet")}";
        }

        /// <summary>A diagnostic line is worth nothing if reading it can throw and take the
        /// rest of the line with it.</summary>
        private static string Try(Func<string> read)
        {
            try { return read() ?? "?"; }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }

        /// <summary>
        /// The message of the exception that actually went wrong.
        ///
        /// Everything here is invoked by reflection, so every failure arrives wrapped in a
        /// TargetInvocationException whose own message is the immortal "Exception has been
        /// thrown by the target of an invocation." -- six identical lines saying nothing, which
        /// is exactly what the log showed while a join was failing for a completely unrelated
        /// reason. Unwrapping costs one method and turns those lines back into evidence.
        /// </summary>
        private static string Describe(Exception e)
        {
            var inner = e;
            while (inner.InnerException != null) inner = inner.InnerException;

            return ReferenceEquals(inner, e)
                ? e.Message
                : $"{inner.GetType().Name}: {inner.Message}";
        }
    }
}
