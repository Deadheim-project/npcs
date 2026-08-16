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

        private bool _spawned;

        /// <summary>
        /// Decides for itself that it has been placed, instead of waiting to be told.
        ///
        /// OnPlaced is only reached if something calls it, and in the real Hammer flow nothing
        /// reliably does -- which is why placing an NPC did nothing in game while a test that
        /// invoked the hook by hand passed every time. What does distinguish a committed piece
        /// from a placement ghost is the ZNetView: the ghost's is never valid, because a
        /// preview is not a networked object. So that is what gets asked.
        /// </summary>
        private void Start()
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;   // a ghost, or not networked yet
            if (!nview.IsOwner()) return;                    // someone else's placement

            StartCoroutine(PlaceNextFrame());
        }

        /// <summary>One frame of patience: the game records who built a piece immediately
        /// after creating it, and reading that in Start can catch it before it is set --
        /// which would spawn an NPC belonging to nobody, and an ownerless NPC is one its
        /// builder cannot administer.</summary>
        private System.Collections.IEnumerator PlaceNextFrame()
        {
            yield return null;
            OnPlaced();
        }

        private void OnPlaced()
        {
            if (_spawned) return;
            _spawned = true;

            var piece = GetComponent<Piece>();
            long ownerId = piece != null ? piece.GetCreator() : 0L;

            // Logged unconditionally: when placement misbehaves there is otherwise no way to
            // tell "the hook never fired" from "it fired and the NPC came out wrong", and
            // those need completely different fixes.
            Plugin.Log.LogInfo($"NpcValheim: placing '{TargetPrefabName}' at {transform.position} for owner {ownerId}");

            if (ZNetScene.instance != null && !string.IsNullOrEmpty(TargetPrefabName))
            {
                var prefab = ZNetScene.instance.GetPrefab(TargetPrefabName);
                if (prefab != null)
                {
                    var instance = Object.Instantiate(prefab, transform.position, transform.rotation);
                    var npc = instance.GetComponent<NpcBase>();
                    if (npc == null)
                        Plugin.Log.LogError($"NpcValheim: spawned '{TargetPrefabName}' has no NpcBase component");
                    else
                        npc.InitializeAfterSpawn(ownerId);
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
