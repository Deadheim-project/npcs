using System;
using System.Collections.Generic;
using System.Linq;

namespace NpcValheim
{
    /// <summary>
    /// The "entry|entry|..." lists of the cfg (arenas, the Intendente's items, the Deadcoins
    /// counter), edited one entry at a time from an admin page. Only the entry named is
    /// touched: one the mod cannot read stays in the text exactly as the admin wrote it, so
    /// saving from the NPC never quietly eats a line someone was halfway through fixing by hand.
    /// </summary>
    internal static class CfgList
    {
        internal static List<string> Entries(string raw) =>
            (raw ?? "").Split('|').Select(e => e.Trim()).Where(e => e.Length > 0).ToList();

        /// <summary>The text with <paramref name="line"/> in place of the first entry with the
        /// same key, or appended when there is none.</summary>
        internal static string Set(string raw, string line, Func<string, string> keyOf, StringComparison comparison)
        {
            var entries = Entries(raw);
            string key = keyOf(line);
            int at = entries.FindIndex(e => string.Equals(keyOf(e), key, comparison));
            if (at >= 0) entries[at] = line;
            else entries.Add(line);
            return string.Join("|", entries);
        }

        /// <summary>The text without any entry keyed <paramref name="key"/>.</summary>
        internal static string Remove(string raw, string key, Func<string, string> keyOf, StringComparison comparison,
            out bool removed)
        {
            var entries = Entries(raw);
            key = (key ?? "").Trim();
            removed = entries.RemoveAll(e => string.Equals(keyOf(e), key, comparison)) > 0;
            return string.Join("|", entries);
        }

        /// <summary>The value of <paramref name="name"/>= in a "a=1;b=2" entry, or "".</summary>
        internal static string Field(string entry, string name)
        {
            foreach (var field in (entry ?? "").Split(';'))
            {
                int eq = field.IndexOf('=');
                if (eq > 0 && field.Substring(0, eq).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return field.Substring(eq + 1).Trim();
            }
            return "";
        }
    }
}
