using System;
using System.Collections.Generic;
using BepInEx.Bootstrap;
using BepInEx.Configuration;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The Guilds and Groups mods each stop members from hurting one another (their own
    /// "Friendly fire" toggle, checked in Character.RPC_Damage and on area attacks), and
    /// Deadheim's no-friendly-fire rule is not the only one. In a match the teams are the
    /// arena's: two guildmates on opposite sides must be able to fight. Those mods only read
    /// their toggle, so for as long as this player is in a match it is turned on here, on this
    /// machine, and put back afterwards. Same-side teammates are still protected by
    /// <see cref="ArenaCombat"/>. Read by name, so nothing breaks without either mod.
    /// </summary>
    internal static class ArenaAllies
    {
        private sealed class Toggle
        {
            public string Plugin, Section, Key;
            public ConfigFile File;
            public ConfigEntryBase Entry;
            public object Original;
            public bool Held;
        }

        private static readonly List<Toggle> Toggles = new List<Toggle>
        {
            new Toggle { Plugin = "org.bepinex.plugins.guilds", Section = "1 - General", Key = "Friendly fire in guilds" },
            new Toggle { Plugin = "org.bepinex.plugins.groups", Section = "1 - General", Key = "Friendly fire in groups" },
        };

        /// <summary>Called every tick: friendly fire on while in a match, as it was otherwise.</summary>
        internal static void Update(bool inMatch)
        {
            // ServerSync sends an admin's change of a synced toggle to the server, which would
            // flip it for everyone and stay flipped if the admin left mid-match.
            if (inMatch && UI.AdminPanelNet.LocalIsAdmin()) return;

            foreach (var toggle in Toggles)
            {
                if (toggle.Held == inMatch) continue;
                try
                {
                    if (!Resolve(toggle)) { toggle.Held = inMatch; continue; }
                    if (inMatch) Hold(toggle); else Release(toggle);
                }
                catch (Exception e)
                {
                    toggle.Held = inMatch;
                    Plugin.Log.LogWarning($"NpcValheim Arena: fogo amigo de {toggle.Plugin} nao mudou: {e.Message}");
                }
            }
        }

        private static bool Resolve(Toggle toggle)
        {
            if (toggle.Entry != null) return true;
            if (!Chainloader.PluginInfos.TryGetValue(toggle.Plugin, out var info) || info.Instance == null) return false;
            var config = info.Instance.Config;
            foreach (var definition in config.Keys)
            {
                if (definition.Section != toggle.Section || definition.Key != toggle.Key) continue;
                toggle.File = config;
                toggle.Entry = config[definition];
                return true;
            }
            return false;
        }

        private static void Hold(Toggle toggle)
        {
            toggle.Original = toggle.Entry.BoxedValue;
            toggle.Held = true;
            Set(toggle, Enum.Parse(toggle.Entry.SettingType, "On"));
        }

        private static void Release(Toggle toggle)
        {
            toggle.Held = false;
            Set(toggle, toggle.Original);
        }

        /// <summary>Not written to the player's cfg file: a crash mid-match must not leave it on.</summary>
        private static void Set(Toggle toggle, object value)
        {
            bool save = toggle.File.SaveOnConfigSet;
            toggle.File.SaveOnConfigSet = false;
            try { toggle.Entry.BoxedValue = value; }
            finally { toggle.File.SaveOnConfigSet = save; }
        }
    }
}
