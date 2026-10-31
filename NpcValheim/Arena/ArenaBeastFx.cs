using System.Collections.Generic;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The warnings of the Bestiário's abilities: a ring on the ground, the charge's lane, smoke
    /// where lacaios will come, the pool of a Zona Perigosa. The creature's owner sends one RPC
    /// (npcv_beastFx) to the players near it; each machine draws it on its own, with no ZDO and
    /// nothing left in the world when its time is up.
    /// </summary>
    internal static class ArenaBeastFx
    {
        internal const string Rpc = "npcv_beastFx";
        /// <summary>Who hears it: the players this close to the creature.</summary>
        internal const float Reach = 80f;

        internal const int Ring = 1;
        internal const int Lane = 2;
        internal const int Smoke = 3;
        internal const int ZoneWarn = 4;
        internal const int ZonePool = 5;
        internal const int Message = 6;
        internal const int Impact = 7;

        private const float MaxRadius = 40f;
        private const float MaxSeconds = 60f;

        // ------------------------------------------------------------------ sending (the owner)

        /// <summary>To every player near the creature (only those of its match, with
        /// <paramref name="matchOnly"/>), this machine included.</summary>
        internal static void Send(Character beast, IEnumerable<Player> to, int kind, Vector3 a, Vector3 b, float radius, float seconds,
            Color color, string text = "")
        {
            if (ZRoutedRpc.instance == null || beast == null) return;
            var pkg = new ZPackage();
            pkg.Write(beast.GetZDOID());
            pkg.Write(kind);
            pkg.Write(a);
            pkg.Write(b);
            pkg.Write(radius);
            pkg.Write(seconds);
            pkg.Write(color.r);
            pkg.Write(color.g);
            pkg.Write(color.b);
            pkg.Write(text ?? "");
            var peers = new HashSet<long>();
            foreach (var player in to)
            {
                var zdo = ArenaCombat.ZdoOf(player);
                if (zdo != null) peers.Add(zdo.GetOwner());
            }
            foreach (long peer in peers)
                ZRoutedRpc.instance.InvokeRoutedRPC(peer, Rpc, new object[] { pkg });
        }

        /// <summary>The players near the creature, for a warning everyone around should see.</summary>
        internal static List<Player> Near(Vector3 at)
        {
            var list = new List<Player>();
            foreach (var player in Player.GetAllPlayers())
                if (player != null && Vector3.Distance(player.transform.position, at) <= Reach) list.Add(player);
            return list;
        }

        // ------------------------------------------------------------------ drawing (everyone)

        internal static void OnRpc(long sender, ZPackage pkg)
        {
            try
            {
                var beastId = pkg.ReadZDOID();
                int kind = pkg.ReadInt();
                var a = pkg.ReadVector3();
                var b = pkg.ReadVector3();
                float radius = Mathf.Clamp(pkg.ReadSingle(), 0.2f, MaxRadius);
                float seconds = Mathf.Clamp(pkg.ReadSingle(), 0.1f, MaxSeconds);
                var color = new Color(pkg.ReadSingle(), pkg.ReadSingle(), pkg.ReadSingle(), 1f);
                string text = pkg.ReadString();
                // Only the creature's owner speaks for it.
                var zdo = ZDOMan.instance?.GetZDO(beastId);
                if (zdo == null || zdo.GetOwner() != sender || !ArenaBeastBody.IsBeast(zdo)) return;
                if (Player.m_localPlayer == null) return;
                Draw(kind, a, b, radius, seconds, color, text);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: aviso de habilidade ilegível: " + e.Message);
            }
        }

        internal static void Draw(int kind, Vector3 a, Vector3 b, float radius, float seconds, Color color, string text)
        {
            switch (kind)
            {
                case Ring: Shape(a, seconds).Circle(radius, color, 0.18f, pulse: false, fill: 0.12f); break;
                case Impact: Shape(a, seconds).Circle(radius, color, 0.35f, pulse: false, fill: 0.3f, grow: true); break;
                case ZoneWarn: Shape(a, seconds).Circle(radius, color, 0.15f, pulse: true, fill: 0.1f); break;
                case ZonePool: Shape(a, seconds).Circle(radius, color, 0.12f, pulse: false, fill: 0.35f); break;
                case Lane: Shape(a, seconds).Lane(b, radius, color); break;
                case Smoke: SmokeAt(a, seconds); break;
                case Message:
                    if (!string.IsNullOrEmpty(text)) Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text, 0, null);
                    break;
            }
        }

        private static ArenaFxShape Shape(Vector3 at, float seconds)
        {
            var go = new GameObject("NpcvBeastFx");
            go.transform.position = at;
            var shape = go.AddComponent<ArenaFxShape>();
            shape.Until = Time.time + seconds;
            shape.Seconds = seconds;
            return shape;
        }

        private static readonly HashSet<string> _warned = new HashSet<string>();

        /// <summary>The smoke of the game's Smoked status effect, standing where a lacaio will come.</summary>
        private static void SmokeAt(Vector3 at, float seconds)
        {
            var prefabs = ArenaBeastBody.Particles("Smoked");
            if (prefabs == null)
            {
                if (_warned.Add("Smoked")) Plugin.Log.LogWarning("NpcValheim Arena: sem a fumaça do jogo (Smoked); o aviso dos lacaios vira um anel");
                Shape(at, seconds).Circle(1.2f, new Color(0.5f, 0.5f, 0.5f), 0.15f, pulse: true, fill: 0.2f);
                return;
            }
            foreach (var prefab in prefabs)
            {
                var go = ArenaBeastBody.Spawn(prefab, at + Vector3.up * 0.8f, Quaternion.identity);
                go.transform.localScale = Vector3.one * 2f;
                Object.Destroy(go, seconds + 0.5f);
            }
        }

        private static Material _material;

        internal static Material SharedMaterial
        {
            get
            {
                if (_material != null) return _material;
                Shader shader = null;
                foreach (var name in new[] { "Sprites/Default", "UI/Default", "Legacy Shaders/Particles/Alpha Blended" })
                    if ((shader = Shader.Find(name)) != null) break;
                _material = new Material(shader) { renderQueue = 3100 };
                return _material;
            }
        }

        /// <summary>The height of the ground under a point, or the point's own when nothing is there.</summary>
        internal static float Ground(Vector3 at)
        {
            int mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            return Physics.Raycast(at + Vector3.up * 2f, Vector3.down, out var hit, 6f, mask, QueryTriggerInteraction.Ignore)
                ? hit.point.y : at.y;
        }
    }

    /// <summary>One warning on the ground, while it lasts: a circle (with a faint fill) or the
    /// charge's lane. Lines and a flat mesh in vertex colours; gone at its time.</summary>
    internal sealed class ArenaFxShape : MonoBehaviour
    {
        public float Until;
        public float Seconds;
        private LineRenderer _line;
        private MeshRenderer _fill;
        private Color _color;
        private float _fillAlpha;
        private bool _pulse;
        private bool _grow;
        private float _width;
        private float _born;

        internal void Circle(float radius, Color color, float width, bool pulse, float fill, bool grow = false)
        {
            const int segments = 48;
            var c = transform.position;
            var points = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                var p = c + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                p.y = ArenaBeastFx.Ground(p) + 0.08f;
                points[i] = p;
            }
            Setup(color, width, pulse, fill, grow);
            _line.loop = true;
            _line.positionCount = segments;
            _line.SetPositions(points);
            Fill(points, c.y);
        }

        internal void Lane(Vector3 to, float halfWidth, Color color)
        {
            var from = transform.position;
            var dir = to - from;
            dir.y = 0f;
            var side = Vector3.Cross(Vector3.up, dir.normalized) * halfWidth;
            var corners = new[] { from - side, to - side, to + side, from + side };
            for (int i = 0; i < corners.Length; i++) corners[i].y = ArenaBeastFx.Ground(corners[i]) + 0.08f;
            Setup(color, 0.15f, pulse: true, fill: 0.2f, grow: false);
            _line.loop = true;
            _line.positionCount = corners.Length;
            _line.SetPositions(corners);
            Fill(corners, (from.y + to.y) * 0.5f);
        }

        private void Setup(Color color, float width, bool pulse, float fill, bool grow)
        {
            _born = Time.time;
            _color = color;
            _width = width;
            _pulse = pulse;
            _grow = grow;
            _fillAlpha = fill;
            _line = gameObject.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.sharedMaterial = ArenaBeastFx.SharedMaterial;
            _line.widthMultiplier = width;
            _line.numCornerVertices = 2;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;
            _line.startColor = _line.endColor = color;
        }

        /// <summary>A flat fan over the outline, in world space.</summary>
        private void Fill(Vector3[] outline, float y)
        {
            if (_fillAlpha <= 0f) return;
            var go = new GameObject("Fill");
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            var verts = new Vector3[outline.Length + 1];
            var center = Vector3.zero;
            foreach (var p in outline) center += p;
            center /= outline.Length;
            verts[0] = center;
            for (int i = 0; i < outline.Length; i++) verts[i + 1] = outline[i];
            var tris = new int[outline.Length * 3];
            for (int i = 0; i < outline.Length; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = 1 + (i + 1) % outline.Length;
                tris[i * 3 + 2] = 1 + i;
            }
            var mesh = new Mesh { vertices = verts, triangles = tris };
            var colors = new Color[verts.Length];
            for (int i = 0; i < colors.Length; i++) colors[i] = new Color(_color.r, _color.g, _color.b, _fillAlpha);
            mesh.colors = colors;
            mesh.RecalculateBounds();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            _fill = go.AddComponent<MeshRenderer>();
            _fill.sharedMaterial = ArenaBeastFx.SharedMaterial;
            _fill.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _fill.receiveShadows = false;
        }

        private void Update()
        {
            if (Time.time >= Until)
            {
                var filter = GetComponentInChildren<MeshFilter>();
                if (filter != null) Destroy(filter.sharedMesh);
                Destroy(gameObject);
                return;
            }
            if (_line == null) return;
            float t = Seconds > 0f ? Mathf.Clamp01((Time.time - _born) / Seconds) : 1f;
            float alpha = _pulse ? 0.55f + 0.45f * Mathf.Abs(Mathf.Sin((Time.time - _born) * 6f)) : 1f;
            if (_grow) alpha = 1f - t;
            // The warning tightens as it comes due.
            _line.widthMultiplier = _width * (_grow ? 1f + t * 2f : 1f + t);
            var c = new Color(_color.r, _color.g, _color.b, alpha);
            _line.startColor = _line.endColor = c;
        }
    }
}
