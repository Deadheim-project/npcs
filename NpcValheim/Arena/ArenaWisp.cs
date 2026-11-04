using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The look of a defeated player who watches the rest of the match: the character's model
    /// hidden and the game's "Wisp" prefab shown in its place (visual only: no AI, no drop, no
    /// ZNetView, no collider). The flag lives on the player's own ZDO, so every client applies
    /// the same look to that player from a postfix on Player.Update; clearing the flag (or
    /// destroying the player) brings the model back.
    /// </summary>
    internal static class ArenaWisp
    {
        internal const string PrefabName = "Wisp";
        internal static readonly int ZdoWisp = "npcv_arenaWisp".GetStableHashCode();

        /// <summary>Turns the look on or off for the local player (its ZDO is owned here).</summary>
        internal static void SetLocal(bool on)
        {
            var player = Player.m_localPlayer;
            var nview = player != null ? player.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            var zdo = nview.GetZDO();
            if (zdo.GetInt(ZdoWisp, 0) != (on ? 1 : 0)) zdo.Set(ZdoWisp, on ? 1 : 0);
            // Local player: at once, without waiting for the next Update.
            Apply(player, on);
        }

        private static void Apply(Player player, bool on)
        {
            var state = player.GetComponent<WispState>();
            if (on)
            {
                if (state == null) state = player.gameObject.AddComponent<WispState>();
                state.Show(player);
            }
            else if (state != null)
            {
                state.Restore();
                Object.Destroy(state);
            }
        }

        /// <summary>Holds what was hidden and the wisp shown, so it can all be undone.</summary>
        private sealed class WispState : MonoBehaviour
        {
            private readonly List<Renderer> _hidden = new List<Renderer>();
            private GameObject _visual;

            internal void Show(Player player)
            {
                if (_visual != null) return;
                var scene = ZNetScene.instance;
                var prefab = scene != null ? scene.GetPrefab(PrefabName) : null;
                if (prefab == null) return; // the model stays: better visible than a ghost with nothing

                foreach (var r in player.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    _hidden.Add(r);
                }

                bool previous = ZNetView.m_forceDisableInit;
                ZNetView.m_forceDisableInit = true;
                try { _visual = Object.Instantiate(prefab, player.transform); }
                finally { ZNetView.m_forceDisableInit = previous; }

                _visual.transform.localPosition = Vector3.up * 1f;
                _visual.transform.localRotation = Quaternion.identity;
                Strip(_visual);
            }

            internal void Restore()
            {
                foreach (var r in _hidden)
                    if (r != null) r.enabled = true;
                _hidden.Clear();
                if (_visual != null) Object.Destroy(_visual);
                _visual = null;
            }

            private void OnDestroy() => Restore();

            /// <summary>Only the looks stay: scripts, bodies and colliders go.</summary>
            private static void Strip(GameObject go)
            {
                foreach (var b in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (b != null) Object.Destroy(b);
                foreach (var c in go.GetComponentsInChildren<Collider>(true))
                    if (c != null) Object.Destroy(c);
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true))
                    if (rb != null) Object.Destroy(rb);
            }
        }

        /// <summary>Every client reads the flag off each player's ZDO and applies the look.</summary>
        [HarmonyPatch(typeof(Player), "Update")]
        private static class UpdatePatch
        {
            private static void Postfix(Player __instance)
            {
                var nview = __instance.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) return;
                bool want = nview.GetZDO().GetInt(ZdoWisp, 0) == 1;
                bool has = __instance.GetComponent<WispState>() != null;
                if (want != has) Apply(__instance, want);
            }
        }
    }
}
