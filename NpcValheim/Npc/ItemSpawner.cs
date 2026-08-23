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
