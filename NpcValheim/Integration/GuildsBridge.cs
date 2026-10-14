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
    ///
    /// With [Guildas] SomentePeloNpc on (the default), the whole window is the Registrar's:
    /// Guilds' own key opens nothing, and the Registrar's "Abrir guilda" button is the way in.
    /// </summary>
    internal static class GuildsBridge
    {
        /// <summary>How close the player must stand to a Registrar for the form to submit.</summary>
        internal const float RegistrarRange = 10f;

        private static bool _resolved;
        private static MethodInfo _getOwnGuild;
        private static MethodInfo _switchUI;
        private static MethodInfo _uiIsActive;
        private static MethodInfo _hideUI;
        private static FieldInfo _createGuildUI;
        private static FieldInfo _noGuildUI;
        private static FieldInfo _managementUI;
        private static Func<object, string> _guildName;

        /// <summary>The server wants the guild window to open only at the Registrar.</summary>
        internal static bool WindowNpcOnly => Plugin.GuildWindowNpcOnly == null || Plugin.GuildWindowNpcOnly.Value;

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

        /// <summary>Opens Guilds' own window as its key would: the guild's management page for a
        /// member, the join/found page for someone without one.</summary>
        internal static bool OpenGuildWindow()
        {
            if (!IsAvailable || _noGuildUI == null || _managementUI == null) return false;
            try
            {
                var page = (OwnGuildName() == null ? _noGuildUI : _managementUI).GetValue(null) as GameObject;
                if (page == null) return false;
                _switchUI.Invoke(null, new object[] { page, true });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("NpcValheim: could not open the Guilds window: " + ex.Message);
                return false;
            }
        }

        private static bool GuildWindowOpen()
        {
            if (_uiIsActive == null) return false;
            try { return (bool)_uiIsActive.Invoke(null, Array.Empty<object>()); }
            catch { return false; }
        }

        private static void HideGuildWindow()
        {
            try { _hideUI?.Invoke(null, Array.Empty<object>()); }
            catch (Exception ex) { Plugin.Log.LogWarning("NpcValheim: could not close the Guilds window: " + ex.Message); }
        }

        /// <summary>The founding price, from the server's [Guildas] section (ServerSync). An
        /// item the game does not know is treated as free and logged, rather than a price no
        /// one could ever pay.</summary>
        internal static bool TryGetPrice(out string item, out int amount)
        {
            item = Plugin.GuildCostItem?.Value?.Trim() ?? "";
            amount = Plugin.GuildCostAmount?.Value ?? 0;
            if (amount <= 0 || item.Length == 0) return false;
            if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(item) == null)
            {
                if (_warnedItem != item)
                {
                    _warnedItem = item;
                    Plugin.Log.LogWarning($"NpcValheim: [Guildas] CostItem '{item}' is not an item; founding a guild is free.");
                }
                return false;
            }
            return true;
        }

        private static string _warnedItem;

        internal static string PriceText() =>
            TryGetPrice(out var item, out var amount) ? $"{amount}x {ItemNames.Display(item)}" : null;

        internal static bool CanPay(Player player) =>
            !TryGetPrice(out var item, out var amount)
            || (player != null && ItemNames.Count(player.GetInventory(), item, -1) >= amount);

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
                _uiIsActive = ui.GetMethod("UIIsActive", anyStatic, null, Type.EmptyTypes, null);
                _hideUI = ui.GetMethod("HideUI", anyStatic, null, Type.EmptyTypes, null);
                _createGuildUI = ui.GetField("CreateGuildUI", anyStatic);
                _noGuildUI = ui.GetField("NoGuildUI", anyStatic);
                _managementUI = ui.GetField("GuildManagementUI", anyStatic);

                // In Guilds 1.1.x Guild.Name is a public field, not a property.
                Type guildType = _getOwnGuild?.ReturnType;
                FieldInfo nameField = guildType?.GetField("Name", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo nameProperty = guildType?.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                if (nameField != null) _guildName = g => nameField.GetValue(g) as string;
                else if (nameProperty != null) _guildName = g => nameProperty.GetValue(g) as string;

                Plugin.Log.LogInfo($"NpcValheim: Guilds bridge (GetOwnGuild={_getOwnGuild != null}, " +
                                   $"SwitchUI={_switchUI != null}, CreateGuildUI={_createGuildUI != null}, " +
                                   $"window={_noGuildUI != null && _managementUI != null && _uiIsActive != null && _hideUI != null}).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("NpcValheim: could not bind the Guilds API: " + ex.Message);
            }
        }

        /// <summary>
        /// Guilds' key, shut when [Guildas] SomentePeloNpc is on. Interface.Update is where
        /// Guilds reads its key and also where it closes the window on Esc, so the method keeps
        /// running: a window that was closed before it and is open after it was opened by the
        /// key, and is closed again on the same frame, before it is ever drawn. The Registrar
        /// opens the window from its own button, outside this method, so it is untouched.
        /// </summary>
        [HarmonyPatch]
        private static class GuildKeyPatch
        {
            private static float _nextNotice;

            private static MethodBase TargetMethod() =>
                GuildsType("Guilds.Interface")?.GetMethod("Update", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            private static bool Prepare() => TargetMethod() != null;

            private static void Prefix(out bool __state)
            {
                Resolve();
                __state = GuildWindowOpen();
            }

            private static void Postfix(bool __state)
            {
                try
                {
                    if (__state || !WindowNpcOnly) return;
                    Resolve();
                    if (!GuildWindowOpen()) return;
                    HideGuildWindow();
                    if (Time.unscaledTime < _nextNotice) return;
                    _nextNotice = Time.unscaledTime + 2f;
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                        "A janela de guildas abre no Registrador de Guildas.", 0, null);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("NpcValheim: guild key gate failed: " + ex.Message);
                }
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
        /// other way, or the player walking off with it open, and charges the founding price.
        /// The price is checked before Guilds runs and taken only after, if a guild now
        /// exists: a name that is taken or too short costs nothing.</summary>
        [HarmonyPatch]
        private static class CreateGuildSubmitPatch
        {
            private static MethodBase TargetMethod() =>
                GuildsType("Guilds.CreateGuildUI")?.GetMethod("OnButtonCreate_Clicked", BindingFlags.Public | BindingFlags.Instance);

            private static bool Prepare() => TargetMethod() != null;

            private static bool Prefix(out bool __state)
            {
                __state = false;
                try
                {
                    var player = Player.m_localPlayer;
                    if (!NearRegistrar(player))
                    {
                        ShowGoToRegistrar();
                        return false;
                    }
                    if (!CanPay(player))
                    {
                        UnifiedPopup.Push(new WarningPopup("Fundar guilda",
                            $"Fundar uma guilda custa {PriceText()}.", UnifiedPopup.Pop, false));
                        return false;
                    }
                    __state = OwnGuildName() == null;
                    return true;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("NpcValheim: guild submit gate failed: " + ex.Message);
                    return false;
                }
            }

            private static void Postfix(bool __state)
            {
                if (!__state) return;
                try
                {
                    var player = Player.m_localPlayer;
                    if (player == null || OwnGuildName() == null) return;
                    if (!TryGetPrice(out var item, out var amount)) return;
                    ItemNames.Remove(player.GetInventory(), item, amount, -1);
                    player.Message(MessageHud.MessageType.Center, $"Guilda fundada: {amount}x {ItemNames.Display(item)} pagos.", 0, null);
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("NpcValheim: could not charge the guild founding price: " + ex.Message);
                }
            }
        }
    }
}
