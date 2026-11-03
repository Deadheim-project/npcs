using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// A knocked-out player watching the rest of the match: the body on the arena's edge (the
    /// server's Move), a free camera of our own kept inside the arena, and a key to go home
    /// before the end. The camera is NOT the game's GameCamera.ToggleFreeFly: AzuAntiCheat's
    /// client check reads GameCamera.m_freeFly and kicks anyone who is not a moderator with the
    /// FreeFlyCam bypass, so using it got players kicked. Here m_freeFly stays false; the camera
    /// transform is driven from a prefix on GameCamera.UpdateCamera and the character's input is
    /// shut off by patching the two TakeInput methods, so the body stays put.
    /// Everything that ends the match for this client ends this first (ArenaClient.ClearMatch,
    /// the Return), so nobody is left flying after the teleport home.
    /// </summary>
    internal static class ArenaSpectate
    {
        /// <summary>How far past the arena's edge, and above its middle, the camera may go.</summary>
        internal const float Slack = 15f;
        internal const float MaxHeight = 40f;
        internal const float MaxSpeed = 40f;
        internal const KeyCode LeaveKey = KeyCode.Backspace;

        internal static bool Active { get; private set; }
        /// <summary>Who knocked this player out ("" when nobody is credited).</summary>
        internal static string By { get; private set; } = "";
        internal static bool LeaveAsked { get; private set; }

        private static Vector3 _center;
        private static float _radius;
        private static float _yaw;
        private static float _pitch;
        private static float _speed = 12f;

        internal static void Begin(Vector3 center, float radius, string by)
        {
            _center = center;
            _radius = Mathf.Max(5f, radius);
            By = by ?? "";
            LeaveAsked = false;
            Active = true;
            _speed = 12f;

            // The camera starts where it is, looking where it looked.
            var cam = GameCamera.instance;
            if (cam == null) return;
            var angles = cam.transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x > 180f ? angles.x - 360f : angles.x;
        }

        internal static void End()
        {
            if (!Active) return;
            Active = false;
            LeaveAsked = false;
        }

        /// <summary>Every frame, from the HUD: the key to go home now.</summary>
        internal static void Update()
        {
            if (!Active) return;
            if (ArenaClient.MatchId == 0L || Player.m_localPlayer == null)
            {
                End();
                return;
            }
            if (!LeaveAsked && Input.GetKeyDown(LeaveKey) && !UI.UiInputBlocker.IsTyping && !Console.IsVisible())
            {
                LeaveAsked = true;
                ArenaNet.Request(ArenaWire.ActLeaveSpectate);
            }
        }

        private static bool InputFree() =>
            (!Chat.instance || !Chat.instance.HasFocus()) && !Console.IsVisible() && !Menu.IsVisible() &&
            !TextInput.IsVisible() && !UI.UiInputBlocker.IsTyping;

        /// <summary>The camera, driven by hand: look with the mouse, move with the game's own
        /// movement keys, scroll for speed; kept within the arena's radius (plus
        /// <see cref="Slack"/>) and <see cref="MaxHeight"/> above its middle.</summary>
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static class CameraPatch
        {
            private static bool Prefix(GameCamera __instance, float dt)
            {
                if (!Active) return true;
                var t = __instance.transform;
                if (InputFree())
                {
                    var look = ZInput.GetMouseDelta();
                    look.x += ZInput.GetJoyRightStickX() * 110f * dt;
                    look.y += -ZInput.GetJoyRightStickY() * 110f * dt;
                    _yaw += look.x;
                    _pitch = Mathf.Clamp(_pitch - look.y, -89f, 89f);

                    float wheel = ZInput.GetMouseScrollWheel();
                    if (wheel > 0f) _speed *= 1.2f;
                    if (wheel < 0f) _speed *= 0.8f;
                    _speed = Mathf.Clamp(_speed, 2f, MaxSpeed);

                    var move = Vector3.zero;
                    if (ZInput.GetButton("Left")) move -= Vector3.right;
                    if (ZInput.GetButton("Right")) move += Vector3.right;
                    if (ZInput.GetButton("Forward")) move += Vector3.forward;
                    if (ZInput.GetButton("Backward")) move -= Vector3.forward;
                    if (ZInput.GetButton("Jump")) move += Vector3.up;
                    if (ZInput.GetButton("Crouch")) move -= Vector3.up;
                    var stick = ZInput.GetJoyLeftStick();
                    move += Vector3.right * stick.x + Vector3.forward * stick.y;
                    if (move.magnitude > 1f) move.Normalize();
                    t.position += t.TransformVector(move) * (_speed * dt);
                }

                t.rotation = Quaternion.Euler(0f, _yaw, 0f) * Quaternion.Euler(_pitch, 0f, 0f);

                var pos = t.position;
                var flat = new Vector2(pos.x - _center.x, pos.z - _center.z);
                float max = _radius + Slack;
                if (flat.magnitude > max)
                {
                    flat = flat.normalized * max;
                    pos.x = _center.x + flat.x;
                    pos.z = _center.z + flat.y;
                }
                pos.y = Mathf.Clamp(pos.y, _center.y - 20f, _center.y + MaxHeight);
                t.position = pos;
                return false;
            }
        }

        /// <summary>No input for the character while watching, as the game's free camera did.</summary>
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        private static class ControllerInputPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!Active) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class PlayerInputPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!Active) return true;
                __result = false;
                return false;
            }
        }
    }
}
