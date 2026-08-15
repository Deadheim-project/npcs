using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>
    /// What the Hammer actually places. Valheim's placement-ghost preview is built for
    /// simple static pieces (it swaps materials/disables scripts on the same prefab you're
    /// about to place) -- it doesn't know how to neuter a full Player-derived Character
    /// (health bar, animator, hover nameplate all kept showing through the "ghost").
    /// So the piece you place is this lightweight, no-Character stub; the instant it's
    /// placed it spawns the real NPC (a separate, un-placeable prefab) at the same spot and
    /// deletes itself. The real NPC is never itself run through the ghost-preview system.
    /// </summary>
    public class NpcSpawnerStub : MonoBehaviour
    {
        public string TargetPrefabName;

        private void OnPlaced()
        {
            var piece = GetComponent<Piece>();
            long ownerId = piece != null ? piece.GetCreator() : 0L;

            if (ZNetScene.instance != null && !string.IsNullOrEmpty(TargetPrefabName))
            {
                var prefab = ZNetScene.instance.GetPrefab(TargetPrefabName);
                if (prefab != null)
                {
                    var instance = Object.Instantiate(prefab, transform.position, transform.rotation);
                    var npc = instance.GetComponent<NpcBase>();
                    npc?.InitializeAfterSpawn(ownerId);
                }
                else
                {
                    Plugin.Log.LogError($"NpcValheim: target prefab '{TargetPrefabName}' not found, could not spawn NPC");
                }
            }

            var nview = GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
                nview.Destroy();
            else
                Object.Destroy(gameObject);
        }
    }
}
