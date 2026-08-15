using UnityEngine;
using NpcValheim.UI;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Lets an admin mark a spot anywhere in the world and then attach it to a teleporter
    /// later.
    ///
    /// This exists because of a genuine dead end in the first design: "add a destination
    /// here" recorded wherever the admin was standing, but the panel only opens next to the
    /// NPC and blocks movement while it is up. The only reachable destination was therefore
    /// the teleporter's own feet, which is useless. Marking is now decoupled from the panel:
    /// stand where you want people to arrive, press the key, walk back, attach it.
    /// </summary>
    internal sealed class WaypointMarker : MonoBehaviour
    {
        internal static bool HasWaypoint { get; private set; }
        internal static Vector3 Position { get; private set; }
        internal static float Yaw { get; private set; }

        internal static void EnsureCreated()
        {
            var go = new GameObject("NpcValheim_WaypointMarker");
            DontDestroyOnLoad(go);
            go.AddComponent<WaypointMarker>();
        }

        /// <summary>Consumed by the admin panel once the waypoint has been attached.</summary>
        internal static void Clear() => HasWaypoint = false;

        /// <summary>Where a new destination should be bound: the marked point if there is
        /// one, otherwise nothing and the caller falls back to the requester's position.
        ///
        /// This is a separate function rather than two reads at the call site so the suite can
        /// exercise it without a Player. It went untested at first and the panel simply never
        /// consulted the marker -- the key stored a position that nothing read.</summary>
        internal static bool TryGetBindPoint(out Vector3 position, out float yaw)
        {
            position = Position;
            yaw = Yaw;
            return HasWaypoint;
        }

        /// <summary>Records an explicit point. Shared by the key handler and the suite so
        /// neither can drift away from the other.</summary>
        internal static void MarkAt(Vector3 position, float yaw)
        {
            // No permission check here: marking is purely local bookkeeping. Attaching it to
            // an NPC is what the server gates, and it gates it the same as everything else.
            HasWaypoint = true;
            Position = position;
            Yaw = yaw;
        }

        /// <summary>Records where the player is standing. Split out from the key handler so
        /// the scripted demo marks a point through the same code the key does, rather than
        /// through a parallel path that could drift away from it.</summary>
        internal static void MarkHere(Player player)
        {
            if (player == null) return;

            MarkAt(player.transform.position, player.transform.rotation.eulerAngles.y);

            player.Message(MessageHud.MessageType.Center,
                $"Ponto marcado ({Position.x:0}, {Position.z:0})", 0, null);
            Plugin.Log.LogInfo($"NpcValheim: waypoint marked at {Position}");
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null || UiInputBlocker.IsOpen) return;
            if (!Input.GetKeyDown(Plugin.MarkWaypointKey.Value)) return;

            MarkHere(player);
        }
    }
}
