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
            yield return RequestAuthoritativePlacement();
        }

        private System.Collections.IEnumerator RequestAuthoritativePlacement()
        {
            if (_spawned) yield break;
            _spawned = true;

            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) yield break;

            // Routed RPC and ZDO replication use separate queues. Retry briefly so the server
            // can wait for Piece.SetCreator instead of accepting a client-supplied owner id.
            for (int attempt = 0; attempt < 10 && this != null && nview.IsValid(); attempt++)
            {
                ServiceNpcAuthority.RequestPlacement(TargetPrefabName, nview.GetZDO().m_uid);
                yield return new WaitForSeconds(0.5f);
            }

            if (this != null && nview.IsValid())
                Plugin.Log.LogWarning(
                    $"NpcValheim: server did not consume placer stub '{TargetPrefabName}' after 10 attempts");
        }
    }
}
