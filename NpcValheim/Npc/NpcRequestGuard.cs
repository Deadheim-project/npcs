using System;
using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>Shared admission checks for public NPC RPCs. Object ownership only decides
    /// where a ZNetView RPC executes; it does not prove that the sender is a real nearby
    /// player or keep one peer from flooding a database-backed operation.</summary>
    internal static class NpcRequestGuard
    {
        private sealed class Window
        {
            public float Started;
            public int Count;
        }

        private static readonly Dictionary<string, Window> Windows = new Dictionary<string, Window>();
        private static float _nextCleanup;

        public static bool AllowNearby(ZNetView view, Transform npc, long sender, string operation,
            float maxDistance = 6f, int burst = 8, float seconds = 2f) =>
            AllowNearby(view, npc, sender, operation, out _, maxDistance, burst, seconds);

        /// <summary>
        /// Same check, but names the condition that refused it.
        ///
        /// A refusal that only reports "not allowed" is indistinguishable from a bug. The live
        /// 0.1.22 log said "out of range of the NPC, or too many in a row" and could not tell
        /// an admin standing at the counter from one who had walked away -- or from a sender
        /// whose character the server failed to resolve at all, which is a different problem
        /// with a different fix.
        /// </summary>
        public static bool AllowNearby(ZNetView view, Transform npc, long sender, string operation,
            out string reason, float maxDistance = 6f, int burst = 8, float seconds = 2f)
        {
            reason = null;
            if (view == null || !view.IsValid()) { reason = "the NPC has no valid network view here"; return false; }
            if (!view.IsOwner()) { reason = "this machine does not own the NPC"; return false; }

            if (!GameApi.TryGetPlayer(sender, out var player) || player == null)
            {
                reason = "the server could not resolve the sender's character";
                return false;
            }
            if (player.IsDead()) { reason = "the sender is dead"; return false; }
            if (player.IsTeleporting()) { reason = "the sender is teleporting"; return false; }

            if (npc == null || !IsFinite(npc.position) || !IsFinite(player.transform.position))
            {
                reason = "a position is not a finite number";
                return false;
            }

            float distance = Vector3.Distance(player.transform.position, npc.position);
            if (distance > maxDistance)
            {
                reason = $"the sender is {distance:0.0}m from the NPC, limit is {maxDistance:0.0}m";
                return false;
            }

            if (!AllowRate(sender, operation, burst, seconds))
            {
                reason = $"more than {burst} '{operation}' within {seconds:0.#}s";
                return false;
            }
            return true;
        }

        public static bool AllowRate(long sender, string operation, int burst, float seconds)
        {
            if (sender == 0L || string.IsNullOrEmpty(operation) || burst <= 0 || seconds <= 0f) return false;
            float now = Time.realtimeSinceStartup;
            string key = sender + ":" + operation;
            if (!Windows.TryGetValue(key, out var window) || now - window.Started >= seconds)
            {
                Windows[key] = new Window { Started = now, Count = 1 };
                Cleanup(now);
                return true;
            }

            if (window.Count >= burst) return false;
            window.Count++;
            return true;
        }

        /// <summary>Client-side response handlers accept data only from the current ZDO owner
        /// (normally the dedicated server). Without this check another peer can forge item,
        /// currency, directory or template responses.</summary>
        public static bool IsResponseFromOwner(ZNetView view, long sender)
        {
            if (view == null || !view.IsValid() || sender == 0L) return false;
            long owner = view.GetZDO().GetOwner();
            if (owner != 0L && sender == owner) return true;

            long serverPeer = GameApi.GetServerPeerId();
            if (serverPeer != 0L && sender == serverPeer) return true;

            // Host/single-player responses can be routed back under the local RPC id.
            return ZNet.instance != null && ZNet.instance.IsServer() &&
                   sender == GameApi.LocalRpcSenderId();
        }

        private static void Cleanup(float now)
        {
            if (now < _nextCleanup) return;
            _nextCleanup = now + 30f;
            var stale = new List<string>();
            foreach (var pair in Windows)
                if (now - pair.Value.Started > 60f) stale.Add(pair.Key);
            foreach (var key in stale) Windows.Remove(key);
        }

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
