using HarmonyLib;
using NpcValheim.Npc;
using NpcValheim.Prefabs;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Adds our NPC pieces to the Hammer's piece table so players can place them like any
    /// other building piece. Runs after ObjectDB has loaded item prefabs.
    /// </summary>
    [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
    internal static class ObjectDB_Awake_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(ObjectDB __instance)
        {
            if (!__instance.TryGetItemPrefab("Hammer", out var hammerPrefab) || hammerPrefab == null)
                return;

            var itemDrop = hammerPrefab.GetComponent<ItemDrop>();
            var table = itemDrop?.m_itemData?.m_shared?.m_buildPieces;
            if (table == null)
                return;

            foreach (var npcPrefab in NpcPrefabFactory.CustomPrefabs)
            {
                if (!table.m_pieces.Contains(npcPrefab))
                    table.m_pieces.Add(npcPrefab);
            }
        }
    }

    /// <summary>
    /// Service pieces stay registered on every peer because their prefab hashes must match,
    /// but only a confirmed local admin sees them in the Hammer. Placement is authorized
    /// independently by ServiceNpcAuthority on the server.
    /// </summary>
    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    internal static class PieceTable_UpdateAvailable_NpcAdmin_Patch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            bool enabled = Player.m_localPlayer != null && NpcBase.LocalPlayerIsAdmin();
            foreach (var prefab in NpcPrefabFactory.CustomPrefabs)
            {
                var piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece != null) piece.m_enabled = enabled;
            }
        }
    }
}
