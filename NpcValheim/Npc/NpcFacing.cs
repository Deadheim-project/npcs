using UnityEngine;
using NpcValheim.UI;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Where an NPC faces, as numbers: pure, so tools/checks runs the same sums the game does.
    ///
    /// Why an NPC needs a stored facing at all: it is a Player clone, and on the peer that owns
    /// its ZDO (the server) Character.CustomFixedUpdate keeps running -- PlayerNpcPatch skips
    /// Player.FixedUpdate, not the MonoUpdaters call. Every step on the ground, UpdateMotion
    /// turns the body toward m_lookYaw, which nothing ever set on an NPC: Quaternion.identity,
    /// world north. That is the "it turns and ends up staring at the wall". Giving the
    /// Character the stored facing (SetLookDir) makes the same code hold it there instead.
    /// </summary>
    internal static class NpcFacing
    {
        /// <summary>0 to 360.</summary>
        internal static float Normalize(float yaw)
        {
            if (float.IsNaN(yaw) || float.IsInfinity(yaw)) return 0f;
            yaw %= 360f;
            if (yaw < 0f) yaw += 360f;
            return yaw >= 359.995f ? 0f : yaw;
        }

        /// <summary>The yaw that faces from <paramref name="from"/> to <paramref name="to"/>
        /// (Unity's: 0 = +Z, 90 = +X); the current one when they stand on the same spot.</summary>
        internal static float Toward(Vector3 from, Vector3 to, float current)
        {
            float dx = to.x - from.x, dz = to.z - from.z;
            if (dx * dx + dz * dz < 0.0001f) return Normalize(current);
            return Normalize(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg);
        }

        internal static float Away(Vector3 from, Vector3 to, float current) => Normalize(Toward(from, to, current) + 180f);

        /// <summary>Signed difference to <paramref name="target"/>, -180 to 180.</summary>
        internal static float Delta(float current, float target)
        {
            float d = Normalize(target) - Normalize(current);
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>How far the head turns toward a point: the yaw left of the body (clamped
        /// to ±<paramref name="maxYaw"/>, and nothing once it is behind past
        /// <paramref name="giveUp"/>) and the pitch up.</summary>
        internal static (float Yaw, float Pitch) HeadTurn(Vector3 head, float bodyYaw, Vector3 target,
            float maxYaw = 65f, float maxPitch = 25f, float giveUp = 120f)
        {
            float yaw = Delta(bodyYaw, Toward(head, target, bodyYaw));
            if (Mathf.Abs(yaw) > giveUp) return (0f, 0f);
            float flat = new Vector2(target.x - head.x, target.z - head.z).magnitude;
            float pitch = Mathf.Atan2(target.y - head.y, Mathf.Max(0.1f, flat)) * Mathf.Rad2Deg;
            return (Mathf.Clamp(yaw, -maxYaw, maxYaw), Mathf.Clamp(pitch, -maxPitch, maxPitch));
        }

        /// <summary>The name over an NPC: "Bjorn · Mestre da Arena" once the admin has named it,
        /// just the role while it still carries the role as its name.</summary>
        internal static string Titled(string name, string role)
        {
            name = (name ?? "").Trim();
            role = (role ?? "").Trim();
            if (role.Length == 0 || string.Equals(name, role, System.StringComparison.OrdinalIgnoreCase) || name == "NPC")
                return name.Length > 0 && name != "NPC" ? name : role;
            return $"{name} <size=75%><color=#c9b28a>· {role}</color></size>";
        }

        /// <summary>When an idle emote is due next: <paramref name="roll"/> is 0 to 1.</summary>
        internal static float NextIdle(float now, float roll, float min = 40f, float max = 90f) =>
            now + Mathf.Lerp(min, max, Mathf.Clamp01(roll));

        /// <summary>Whether to play an idle emote now: someone near enough to see it, and not
        /// while that player has this NPC's window open (they are reading it).</summary>
        internal static bool IdleDue(float now, float due, float playerDistance, bool talking, float range = 20f) =>
            now >= due && playerDistance <= range && !talking;
    }

    /// <summary>
    /// The NPC noticing the local player, on this client only (nothing on the wire, like the
    /// greeting): the head follows the nearest player within a few metres, and while that
    /// player has its window open the body turns to them -- the "Visual" child, never the
    /// root the server owns and ZSyncTransform carries -- and back to its stored facing a few
    /// seconds after the window closes. An NPC set to stand still (a counter against a wall)
    /// only moves the head.
    /// </summary>
    internal sealed class NpcPresence : MonoBehaviour
    {
        private const float LookRange = 6f;
        private const float TurnSpeed = 220f;
        private const float BackAfter = 3f;
        private const float MaxBodyTurn = 150f;

        private NpcBase _npc;
        private Transform _visual;
        private Quaternion _visualBase;
        private Animator _animator;
        private Transform _head, _neck;
        private float _bodyTurn;
        private float _headWeight;
        private float _lastTalk = -100f;
        private float _nextBones;

        private void Awake()
        {
            _npc = GetComponent<NpcBase>();
            _visual = transform.Find("Visual");
            if (_visual != null) _visualBase = _visual.localRotation;
        }

        /// <summary>The bones move with the body prefab (Aparência can swap it), so they are
        /// looked up again now and then rather than once.</summary>
        private void FindBones()
        {
            _nextBones = Time.time + 5f;
            _animator = GetComponentInChildren<Animator>();
            _head = null;
            _neck = null;
            if (_animator == null || !_animator.isHuman) return;
            _head = Utils.GetBoneTransform(_animator, HumanBodyBones.Head);
            _neck = Utils.GetBoneTransform(_animator, HumanBodyBones.Neck);
        }

        private void LateUpdate()
        {
            if (_npc == null || _visual == null) return;
            if (Time.time >= _nextBones || (_animator == null && _head == null)) FindBones();
            var player = Player.m_localPlayer;
            float distance = player != null ? Vector3.Distance(player.transform.position, transform.position) : float.MaxValue;
            bool talking = UiRoot.OpenNpc == _npc;
            if (talking) _lastTalk = Time.time;
            float bodyYaw = transform.eulerAngles.y;

            // The body: toward the player while talking, back to the stored facing after.
            float wantBody = 0f;
            if (_npc.TurnsToTalker && player != null && Time.time - _lastTalk < BackAfter && distance < LookRange * 2f)
                wantBody = Mathf.Clamp(NpcFacing.Delta(bodyYaw, NpcFacing.Toward(transform.position, player.transform.position, bodyYaw)),
                    -MaxBodyTurn, MaxBodyTurn);
            _bodyTurn = Mathf.MoveTowards(_bodyTurn, wantBody, TurnSpeed * Time.deltaTime);
            _visual.localRotation = _visualBase * Quaternion.Euler(0f, _bodyTurn, 0f);

            // The head, after the animator has posed it this frame.
            if (_head == null) return;
            bool look = player != null && distance <= LookRange;
            _headWeight = Mathf.MoveTowards(_headWeight, look ? 1f : 0f, Time.deltaTime * 2f);
            if (_headWeight <= 0.001f || player == null) return;
            var target = player.GetHeadPoint();
            var (yaw, pitch) = NpcFacing.HeadTurn(_head.position, bodyYaw + _bodyTurn, target);
            var right = _visual.right;
            if (_neck != null)
            {
                _neck.rotation = Quaternion.AngleAxis(yaw * 0.4f * _headWeight, Vector3.up) *
                                 Quaternion.AngleAxis(-pitch * 0.4f * _headWeight, right) * _neck.rotation;
                yaw *= 0.6f;
                pitch *= 0.6f;
            }
            _head.rotation = Quaternion.AngleAxis(yaw * _headWeight, Vector3.up) *
                             Quaternion.AngleAxis(-pitch * _headWeight, right) * _head.rotation;
        }
    }
}
