using System.Collections.Generic;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Translates between the two names every item has, because mixing them up silently
    /// breaks things rather than throwing.
    ///
    /// Everything the mod stores -- listings, mail, quest targets, teleport costs -- uses the
    /// <b>prefab</b> name ("Wood"), which is stable and language-independent. But
    /// <c>Inventory.CountItems</c> and <c>Inventory.RemoveItem</c> match on the item's
    /// <b>shared</b> name, which is a localization key ("$item_wood"). Passing a prefab name
    /// to those returns 0 and removes nothing, with no error.
    ///
    /// Measured in-game rather than assumed: with 60 wood in the inventory,
    /// CountItems("Wood") returned 0 and CountItems("$item_wood") returned 60. That one
    /// mismatch was quietly breaking selling, depositing coins, quest hand-in and paid
    /// teleports all at once -- and worse, RemoveItem was a no-op, so a listing could be
    /// created without the stack ever leaving the seller's bag.
    /// </summary>
    internal static class ItemNames
    {
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();

        /// <summary>The name Inventory lookups actually match on. Falls back to the input, so
        /// an unknown prefab behaves as before rather than becoming empty.</summary>
        public static string Shared(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return prefabName;
            if (Cache.TryGetValue(prefabName, out var cached)) return cached;

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            var result = shared != null && !string.IsNullOrEmpty(shared.m_name) ? shared.m_name : prefabName;

            // Only cache once ObjectDB can actually answer; otherwise an early call would
            // pin the fallback forever.
            if (prefab != null) Cache[prefabName] = result;
            return result;
        }

        /// <summary>The name to show a player: the shared name run through localization, so
        /// "Wood" reads as "Madeira" rather than as "$item_wood".</summary>
        public static string Display(string prefabName)
        {
            var shared = Shared(prefabName);
            if (Localization.instance == null || string.IsNullOrEmpty(shared)) return shared;
            var localized = Localization.instance.Localize(shared);
            return string.IsNullOrEmpty(localized) ? shared : localized;
        }

        public static int Count(Inventory inventory, string prefabName, int quality) =>
            inventory == null ? 0 : inventory.CountItems(Shared(prefabName), quality, false);

        public static void Remove(Inventory inventory, string prefabName, int amount, int quality) =>
            inventory?.RemoveItem(Shared(prefabName), amount, quality, false);
    }
}
