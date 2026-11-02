using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// A knocked-out player watching the rest of the match: the body on the arena's edge (the
    /// server's Move), the game's own free camera (GameCamera.ToggleFreeFly) kept inside the
    /// arena, and a key to go home before the end. While the free camera is on the game takes
    /// no input for the character (PlayerController.TakeInput), so the body stays put.
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
        private static bool _turnedOn;

        private static readonly AccessTools.FieldRef<GameCamera, float> Yaw =
            AccessTools.FieldRefAccess<GameCamera, float>("m_freeFlyYaw");
        private static readonly AccessTools.FieldRef<GameCamera, float> Pitch =
            AccessTools.FieldRefAccess<GameCamera, float>("m_freeFlyPitch");
        private static readonly AccessTools.FieldRef<GameCamera, float> Speed =
            AccessTools.FieldRefAccess<GameCamera, float>("m_freeFlySpeed");
        private static readonly AccessTools.FieldRef<GameCamera, Transform> Lockon =
            AccessTools.FieldRefAccess<GameCamera, Transform>("m_freeFlyLockon");

        internal static void Begin(Vector3 center, float radius, string by)
        {
            _center = center;
            _radius = Mathf.Max(5f, radius);
            By = by ?? "";
            LeaveAsked = false;
            Active = true;

            var cam = GameCamera.instance;
            if (cam == null || GameCamera.InFreeFly()) return;
            // The free camera starts where the camera is, looking where it looked.
            var angles = cam.transform.eulerAngles;
            Yaw(cam) = angles.y;
            Pitch(cam) = angles.x > 180f ? angles.x - 360f : angles.x;
            Speed(cam) = Mathf.Min(Speed(cam), 20f);
            cam.ToggleFreeFly();
            _turnedOn = true;
        }

        internal static void End()
        {
            if (!Active) return;
            Active = false;
            LeaveAsked = false;
            if (_turnedOn && GameCamera.instance != null && GameCamera.InFreeFly()) GameCamera.instance.ToggleFreeFly();
            _turnedOn = false;
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

        /// <summary>The free camera, kept over the arena: within its radius (plus
        /// <see cref="Slack"/>) and <see cref="MaxHeight"/> above its middle, never locked onto
        /// something that would carry it away, never faster than <see cref="MaxSpeed"/>.</summary>
        [HarmonyPatch]
        private static class FreeFlyPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(GameCamera), "UpdateFreeFly");

            private static void Postfix(GameCamera __instance)
            {
                if (!Active || !_turnedOn) return;
                Lockon(__instance) = null;
                if (Speed(__instance) > MaxSpeed) Speed(__instance) = MaxSpeed;

                var t = __instance.transform;
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
            }
        }

        /// <summary>In the free camera the right mouse button takes a screenshot (GameCamera.LateUpdate);
        /// someone watching a match would fill their screenshots folder.</summary>
        [HarmonyPatch(typeof(GameCamera), nameof(GameCamera.ScreenShot))]
        private static class ScreenShotPatch
        {
            private static bool Prefix() => !Active;
        }
    }
}
