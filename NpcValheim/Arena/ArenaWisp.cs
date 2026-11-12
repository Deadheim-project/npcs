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
            if (player == null) return;
            var nview = player.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid() && nview.IsOwner())
            {
                var zdo = nview.GetZDO();
                if (zdo.GetInt(ZdoWisp, 0) != (on ? 1 : 0)) zdo.Set(ZdoWisp, on ? 1 : 0);
            }
            // Local player: at once, without waiting for the next Update.
            Apply(player, on);
        }

        /// <summary>True for a defeated player still watching (any client reads it off the ZDO).</summary>
        internal static bool IsWisp(Character character)
        {
            if (!(character is Player player) || player == null) return false;
            var nview = player.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() && nview.GetZDO().GetInt(ZdoWisp, 0) == 1;
        }

        internal static bool LocalIsWisp => IsWisp(Player.m_localPlayer);

        /// <summary>Flies the local wisp to <paramref name="eye"/> (where its free camera is).
        /// Physics is off for it (see FixedPatch), so the position is ours alone.</summary>
        internal static void Follow(Vector3 eye)
        {
            var player = Player.m_localPlayer;
            if (player == null || !LocalIsWisp) return;
            var pos = eye - Vector3.up * 1f;
            player.transform.position = pos;
            var body = player.GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = pos;
                body.velocity = Vector3.zero;
            }
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
            private readonly List<Collider> _off = new List<Collider>();
            private GameObject _visual;
            private Rigidbody _body;
            private bool _gravity;

            internal void Show(Player player)
            {
                if (_visual != null) return;
                var scene = ZNetScene.instance;
                var prefab = scene != null ? scene.GetPrefab(PrefabName) : null;
                if (prefab == null) return; // the model stays: better visible than a ghost with nothing

                // Nothing to bump into, hit or be hit by: every collider off, on every client.
                foreach (var c in player.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null || !c.enabled) continue;
                    c.enabled = false;
                    _off.Add(c);
                }
                if (player == Player.m_localPlayer)
                {
                    _body = player.GetComponent<Rigidbody>();
                    if (_body != null) { _gravity = _body.useGravity; _body.useGravity = false; }
                }

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
                // The local camera sits where the wisp is: it would only block the view.
                if (player == Player.m_localPlayer) _visual.SetActive(false);
            }

            internal void Restore()
            {
                foreach (var r in _hidden)
                    if (r != null) r.enabled = true;
                _hidden.Clear();
                foreach (var c in _off)
                    if (c != null) c.enabled = true;
                _off.Clear();
                if (_body != null) _body.useGravity = _gravity;
                _body = null;
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

        /// <summary>The local wisp is moved by hand (Follow): the game's own physics for it stops.</summary>
        [HarmonyPatch(typeof(Character), "CustomFixedUpdate")]
        private static class FixedPatch
        {
            private static bool Prefix(Character __instance) =>
                __instance != Player.m_localPlayer || !LocalIsWisp;
        }

        /// <summary>A wisp cannot use or equip anything, from the hotbar or the inventory.</summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
        private static class UseItemPatch
        {
            private static bool Prefix(Humanoid __instance) => !IsWisp(__instance);
        }

        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
        private static class EquipItemPatch
        {
            private static bool Prefix(Humanoid __instance) => !IsWisp(__instance);
        }

        [HarmonyPatch(typeof(Player), nameof(Player.ConsumeItem))]
        private static class ConsumeItemPatch
        {
            private static bool Prefix(Player __instance) => !IsWisp(__instance);
        }

        [HarmonyPatch(typeof(Player), "AutoPickup")]
        private static class AutoPickupPatch
        {
            private static bool Prefix(Player __instance) => !IsWisp(__instance);
        }

        /// <summary>Monsters (vanilla AI) never take a wisp for an enemy.</summary>
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
        private static class EnemyPatch
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                if (__result && (IsWisp(a) || IsWisp(b))) __result = false;
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
