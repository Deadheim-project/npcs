using UnityEngine;
using System.Collections.Generic;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Physically drops a purchased item stack in the world near the buyer -- a marketplace
    /// NPC has no direct write access to another player's inventory, so goods are handed
    /// over the same way vanilla loot works, and vanilla auto-pickup takes it from there.
    /// </summary>
    internal static class ItemSpawner
    {
        internal const int MaxStacksPerDelivery = 32;

        internal static int MaxDeliverableAmount(string prefabName)
        {
            var prefab = ObjectDB.instance?.GetItemPrefab(prefabName);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData?.m_shared == null) return 0;
            int maxStack = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            long amount = (long)maxStack * MaxStacksPerDelivery;
            return amount > int.MaxValue ? int.MaxValue : (int)amount;
        }

        /// <summary>
        /// Puts goods in the player's own bag, one stack at a time, and reports how many
        /// actually fit.
        ///
        /// Inventory.AddItem does not split: asked for more than one stack it refuses the
        /// whole thing and returns null. Every delivery here used to be a single AddItem call,
        /// so buying 100 wood (max stack 50) landed the entire purchase on the ground with a
        /// nearly empty inventory -- and anything whose stack size is 1, like a weapon or a
        /// piece of armour, could never be bought in twos at all. TrySpawn already walks the
        /// stacks to drop them; this is the same walk, into the bag first.
        /// </summary>
        public static int GiveToInventory(Player player, string prefabName, int amount, int quality)
        {
            if (player == null || amount <= 0 || ObjectDB.instance == null) return 0;

            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData?.m_shared == null) return 0;

            int maxStack = Mathf.Max(1, drop.m_itemData.m_shared.m_maxStackSize);
            int safeQuality = Mathf.Clamp(quality, 1, Mathf.Max(1, drop.m_itemData.m_shared.m_maxQuality));
            var inventory = player.GetInventory();
            if (inventory == null) return 0;

            int given = 0;
            while (given < amount)
            {
                int stack = Mathf.Min(maxStack, amount - given);
                // A refusal means the bag is full; stop rather than retry a smaller stack,
                // so a full inventory costs one failed call and not one per unit.
                if (inventory.AddItem(prefabName, stack, safeQuality, 0, 0L, "") == null) break;
                given += stack;
            }
            return given;
        }

        public static bool TrySpawn(string prefabName, int amount, int quality, Vector3 position)
        {
            if (ObjectDB.instance == null) return false;
            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: unknown item prefab '{prefabName}', cannot spawn purchase");
                return false;
            }

            var templateDrop = prefab.GetComponent<ItemDrop>();
            if (templateDrop?.m_itemData?.m_shared == null || amount <= 0) return false;

            int maxStack = Mathf.Max(1, templateDrop.m_itemData.m_shared.m_maxStackSize);
            int maxQuality = Mathf.Max(1, templateDrop.m_itemData.m_shared.m_maxQuality);
            if (amount > MaxDeliverableAmount(prefabName))
            {
                Plugin.Log.LogWarning($"NpcValheim: refused to spawn {amount}x {prefabName}; " +
                                      $"one delivery is limited to {MaxStacksPerDelivery} stacks");
                return false;
            }

            int remaining = amount;
            int stackIndex = 0;
            var created = new List<GameObject>();
            while (remaining > 0)
            {
                int stack = Mathf.Min(maxStack, remaining);
                var offset = new Vector3((stackIndex % 4) * 0.2f, stackIndex / 4 * 0.1f, 0f);
                var instance = Object.Instantiate(prefab, position + offset, Quaternion.identity);
                created.Add(instance);
                var drop = instance.GetComponent<ItemDrop>();
                if (drop?.m_itemData == null)
                {
                    foreach (var spawned in created)
                        if (spawned != null) Object.Destroy(spawned);
                    return false;
                }

                drop.m_itemData.m_stack = stack;
                drop.m_itemData.m_quality = Mathf.Clamp(quality, 1, maxQuality);
                GameApi.SaveItemDrop(drop);
                remaining -= stack;
                stackIndex++;
            }
            return true;
        }
    }
}
