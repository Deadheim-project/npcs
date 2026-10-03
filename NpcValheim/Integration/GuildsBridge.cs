using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Integration
{
    /// <summary>
    /// The Guilds mod (Smoothbrain), read by reflection the way Deadheim's PvP module reads it:
    /// no compile-time reference, and nothing breaks without it.
    ///
    /// Guilds lets anyone found a guild from its own window (G → "Criar"). On Deadheim a guild
    /// is founded at the Guild Registrar instead: the window's Create button only points the
    /// player at the NPC, and the creation form itself refuses to submit away from one. Next
    /// to a Registrar, the button opens the form directly -- even with Guilds' own
    /// allowGuildCreation turned off, so the server can switch that off and leave the NPC as
    /// the only way in.
    /// </summary>
    internal static class GuildsBridge
    {
        /// <summary>How close the player must stand to a Registrar for the form to submit.</summary>
        internal const float RegistrarRange = 10f;

        private static bool _resolved;
        private static MethodInfo _getOwnGuild;
        private static MethodInfo _switchUI;
        private static FieldInfo _createGuildUI;
        private static Func<object, string> _guildName;

        internal static bool IsAvailable
        {
            get
            {
                Resolve();
                return _getOwnGuild != null && _switchUI != null && _createGuildUI != null;
            }
        }

        /// <summary>The local player's guild, or null (also null without Guilds).</summary>
        internal static string OwnGuildName()
        {
            if (!IsAvailable) return null;
            try
            {
                object guild = _getOwnGuild.Invoke(null, Array.Empty<object>());
                string name = guild != null && _guildName != null ? _guildName(guild) : null;
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("NpcValheim: Guilds.API.GetOwnGuild failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Opens Guilds' own creation form (name, description, badge, color).</summary>
        internal static bool OpenCreateForm()
        {
            if (!IsAvailable) return false;
            try
            {
                var form = _createGuildUI.GetValue(null) as GameObject;
                if (form == null) return false;
                _switchUI.Invoke(null, new object[] { form, true });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("NpcValheim: could not open the Guilds creation form: " + ex.Message);
                return false;
            }
        }

        internal static bool NearRegistrar(Player player) =>
            player != null && NpcBase.Live.Any(n => n is GuildRegistrarNpc && n != null
                && Vector3.Distance(n.transform.position, player.transform.position) <= RegistrarRange);

        private static void ShowGoToRegistrar() =>
            UnifiedPopup.Push(new WarningPopup("Fundar guilda",
                "Guildas são fundadas com o Registrador de Guildas. Procure-o numa cidade.",
                UnifiedPopup.Pop, false));

        private static Type GuildsType(string name) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Guilds")?.GetType(name, false);

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                Type api = GuildsType("Guilds.API");
                Type ui = GuildsType("Guilds.Interface");
                if (api == null || ui == null) return;

                _getOwnGuild = api.GetMethod("GetOwnGuild", anyStatic, null, Type.EmptyTypes, null);
                _switchUI = ui.GetMethod("SwitchUI", anyStatic, null, new[] { typeof(GameObject), typeof(bool) }, null);
                _createGuildUI = ui.GetField("CreateGuildUI", anyStatic);

                // In Guilds 1.1.x Guild.Name is a public field, not a property.
                Type guildType = _getOwnGuild?.ReturnType;
                FieldInfo nameField = guildType?.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo nameProperty = guildType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (nameField != null) _guildName = g => nameField.GetValue(g) as string;
                else if (nameProperty != null) _guildName = g => nameProperty.GetValue(g) as string;

                Plugin.Log.LogInfo($"NpcValheim: Guilds bridge (GetOwnGuild={_getOwnGuild != null}, " +
                                   $"SwitchUI={_switchUI != null}, CreateGuildUI={_createGuildUI != null}).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("NpcValheim: could not bind the Guilds API: " + ex.Message);
            }
        }

        /// <summary>"Criar" in the no-guild window: straight to the form next to a Registrar,
        /// otherwise a pointer to one.</summary>
        [HarmonyPatch]
        private static class NoGuildCreatePatch
        {
            private static MethodBase TargetMethod() =>
                GuildsType("Guilds.NoGuildUI")?.GetMethod("OnButtonCreate_Clicked", BindingFlags.Public | BindingFlags.Instance);

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix()
            {
                try
                {
                    if (NearRegistrar(Player.m_localPlayer) && OpenCreateForm()) return false;
                    ShowGoToRegistrar();
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("NpcValheim: guild create gate failed: " + ex.Message);
                    return false;
                }
            }
        }

        /// <summary>The form's own submit button. Guards against the form being reached some
        /// other way, or the player walking off with it open.</summary>
        [HarmonyPatch]
        private static class CreateGuildSubmitPatch
        {
            private static MethodBase TargetMethod() =>
                GuildsType("Guilds.CreateGuildUI")?.GetMethod("OnButtonCreate_Clicked", BindingFlags.Public | BindingFlags.Instance);

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix()
            {
                try
                {
                    if (NearRegistrar(Player.m_localPlayer)) return true;
                    ShowGoToRegistrar();
                    return false;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("NpcValheim: guild submit gate failed: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
