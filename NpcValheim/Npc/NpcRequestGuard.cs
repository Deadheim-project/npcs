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
            float maxDistance = 6f, int burst = 8, float seconds = 2f)
        {
            if (view == null || !view.IsValid() || !view.IsOwner()) return false;
            if (!GameApi.TryGetPlayer(sender, out var player) || player == null) return false;
            if (player.IsDead() || player.IsTeleporting()) return false;
            if (npc == null || !IsFinite(npc.position) || !IsFinite(player.transform.position)) return false;
            if ((player.transform.position - npc.position).sqrMagnitude > maxDistance * maxDistance) return false;
            return AllowRate(sender, operation, burst, seconds);
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
