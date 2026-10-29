using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Arena
{
    /// <summary>How the closed gates look ([Arena - Portao] Style).</summary>
    public enum ArenaGateStyle
    {
        Nenhum,
        Parede,
        Pecas,
        Ambos,
    }

    /// <summary>
    /// The skin of the gates: what a player sees around each team's start while the
    /// preparation lasts. Only the look -- the barrier itself is ArenaClient.HoldAtGate, and
    /// nothing here collides or touches the network. Both teams' gates are drawn when the
    /// map is known (the Maps cfg is synced), each in its team's colour.
    ///
    /// It rises when the player enters, and when the battle begins the wall fades and the
    /// pieces sink into the ground.
    /// </summary>
    internal static class ArenaGate
    {
        private static GameObject _root;
        private static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private static Texture2D _generated;

        internal static void Show(string mapName, int mySide, Vector3 mySpawn, float radius)
        {
            Clear();
            var style = ArenaConfig.GateStyle?.Value ?? ArenaGateStyle.Ambos;
            if (style == ArenaGateStyle.Nenhum) return;

            try
            {
                _root = new GameObject("NpcValheim_ArenaGates");
                var runner = _root.AddComponent<ArenaGateRunner>();

                var map = ArenaConfig.ConfiguredMaps(null)
                    .FirstOrDefault(m => string.Equals(m.Name, mapName, StringComparison.OrdinalIgnoreCase));
                foreach (int side in new[] { ArenaSide.Gold, ArenaSide.Green })
                {
                    if (side != mySide && map == null) continue;
                    var spawn = side == mySide ? mySpawn : map.SpawnOf(side);
                    if (style == ArenaGateStyle.Parede || style == ArenaGateStyle.Ambos)
                        BuildWall(runner, spawn, radius, side);
                    if (style == ArenaGateStyle.Pecas || style == ArenaGateStyle.Ambos)
                        BuildPieces(runner, spawn, radius);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: portao visual falhou: " + e.Message);
                Clear();
            }
        }

        /// <summary>The battle began: the gates go down instead of vanishing.</summary>
        internal static void Open()
        {
            if (_root == null) return;
            _root.GetComponent<ArenaGateRunner>()?.Open();
            _root = null;
        }

        internal static void Clear()
        {
            if (_root != null) UnityEngine.Object.Destroy(_root);
            _root = null;
        }

        // ------------------------------------------------------------------------- wall

        private static void BuildWall(ArenaGateRunner runner, Vector3 center, float radius, int side)
        {
            var shader = FindShader();
            if (shader == null)
            {
                Plugin.Log.LogWarning("NpcValheim Arena: nenhum shader transparente para a parede do portao");
                return;
            }

            float height = Mathf.Clamp(ArenaConfig.GateHeight?.Value ?? 4f, 0.5f, 50f);
            var tex = WallTexture();
            float circumference = 2f * Mathf.PI * radius;
            float tileWidth = height * tex.width / Mathf.Max(1f, tex.height);
            float tilesAround = Mathf.Max(1f, Mathf.Round(circumference / Mathf.Max(0.1f, tileWidth)));
            int segments = Mathf.Clamp(Mathf.CeilToInt(circumference), 24, 256);

            // A ring of quads, open at top and bottom; the shader draws both faces.
            var vertices = new Vector3[(segments + 1) * 2];
            var uv = new Vector2[vertices.Length];
            var colors = new Color[vertices.Length];
            var triangles = new int[segments * 6];
            for (int i = 0; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                var dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * radius;
                vertices[i * 2] = dir + Vector3.down * 0.5f;
                vertices[i * 2 + 1] = dir + Vector3.up * height;
                float u = i / (float)segments * tilesAround;
                uv[i * 2] = new Vector2(u, 0f);
                uv[i * 2 + 1] = new Vector2(u, 1f);
                // Strong at the floor, almost gone at the top: the curtain has no hard edge.
                colors[i * 2] = Color.white;
                colors[i * 2 + 1] = new Color(1f, 1f, 1f, 0.15f);
                if (i == segments) break;
                int t = i * 6, v = i * 2;
                triangles[t] = v; triangles[t + 1] = v + 1; triangles[t + 2] = v + 2;
                triangles[t + 3] = v + 2; triangles[t + 4] = v + 1; triangles[t + 5] = v + 3;
            }

            var mesh = new Mesh { name = "ArenaGateWall" };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();

            var go = new GameObject("Wall_" + ArenaSide.Name(side));
            go.transform.SetParent(runner.transform, false);
            go.transform.position = center;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            var mat = new Material(shader) { mainTexture = tex };
            renderer.sharedMaterial = mat;

            runner.AddWall(mesh, uv, mat, TeamColor(side));
        }

        private static Shader FindShader()
        {
            // Sprites/Default ships in every Unity build, blends, and culls nothing.
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Legacy Shaders/Particles/Alpha Blended" })
            {
                var shader = Shader.Find(name);
                if (shader != null) return shader;
            }
            return null;
        }

        private static Color TeamColor(int side)
        {
            string raw = side == ArenaSide.Gold ? ArenaConfig.GateColorGold?.Value : ArenaConfig.GateColorGreen?.Value;
            if (TryParseColor(raw, out var color)) return color;
            var fallback = side == ArenaSide.Gold ? new Color(1f, 0.76f, 0.24f) : new Color(0.3f, 0.86f, 0.42f);
            fallback.a = 0.6f;
            return fallback;
        }

        internal static bool TryParseColor(string raw, out Color color)
        {
            color = Color.white;
            var text = (raw ?? "").Trim().TrimStart('#');
            if (text.Length != 6 && text.Length != 8) return false;
            return ColorUtility.TryParseHtmlString("#" + text, out color);
        }

        private static Texture2D WallTexture()
        {
            string name = (ArenaConfig.GateTexture?.Value ?? "").Trim();
            if (name.Length > 0)
            {
                string path = Path.IsPathRooted(name) ? name : Path.Combine(NpcStoragePaths.AssetsDirectory, "ArenaGate", name);
                if (Textures.TryGetValue(path, out var cached) && cached != null) return cached;
                if (File.Exists(path))
                {
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (Npc.GameApi.TryLoadImage(tex, File.ReadAllBytes(path)))
                    {
                        tex.wrapMode = TextureWrapMode.Repeat;
                        tex.filterMode = FilterMode.Bilinear;
                        tex.name = Path.GetFileName(path);
                        Textures[path] = tex;
                        return tex;
                    }
                }
                Plugin.Log.LogWarning($"NpcValheim Arena: textura do portao '{path}' nao carregou; usando a padrao");
                Textures[path] = Generated();
                return Textures[path];
            }
            return Generated();
        }

        /// <summary>The default: white bars on a thin curtain, with a band of light that the
        /// scroll carries up. Tinted by the team colour; the upward fade is the mesh's.</summary>
        private static Texture2D Generated()
        {
            if (_generated != null) return _generated;
            const int w = 64, h = 128;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { name = "ArenaGateDefault" };
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                float fy = y / (float)(h - 1);
                float band = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(fy * Mathf.PI * 2f)), 12f);
                for (int x = 0; x < w; x++)
                {
                    float fx = x / (float)(w - 1);
                    float edge = Mathf.Min(fx, 1f - fx);
                    float bar = edge < 0.06f ? 1f : 0f;
                    float a = Mathf.Clamp01(0.35f + 0.55f * bar + band * 0.45f);
                    pixels[y * w + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels(pixels);
            tex.wrapMode = TextureWrapMode.Repeat;
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(true);
            _generated = tex;
            return tex;
        }

        // ------------------------------------------------------------------------ pieces

        private static void BuildPieces(ArenaGateRunner runner, Vector3 center, float radius)
        {
            string name = (ArenaConfig.GatePrefab?.Value ?? "").Trim();
            if (name.Length == 0) return;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"NpcValheim Arena: prefab do portao '{name}' nao existe");
                return;
            }

            var template = VisualCopy(prefab, runner.transform);
            if (template == null) return;

            float spacing = Mathf.Max(0.25f, ArenaConfig.GatePrefabSpacing?.Value ?? 2f);
            float scale = Mathf.Clamp(ArenaConfig.GatePrefabScale?.Value ?? 1f, 0.05f, 20f);
            float yaw = ArenaConfig.GatePrefabYaw?.Value ?? 0f;
            int count = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * radius / spacing), 3, 200);

            var placed = new List<GameObject>();
            for (int i = 0; i < count; i++)
            {
                float a = i / (float)count * Mathf.PI * 2f;
                var outward = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                var piece = UnityEngine.Object.Instantiate(template, runner.transform);
                piece.name = name;
                piece.transform.position = center + outward * radius;
                piece.transform.rotation = Quaternion.LookRotation(outward) * Quaternion.Euler(0f, yaw, 0f);
                piece.transform.localScale = prefab.transform.localScale * scale;
                piece.SetActive(true);
                placed.Add(piece);
            }
            UnityEngine.Object.Destroy(template);

            // Deep enough to hide it whole when the gates open (bounds exist only once active).
            float sink = 0.5f;
            foreach (var r in placed[0].GetComponentsInChildren<Renderer>())
                sink = Mathf.Max(sink, r.bounds.max.y - placed[0].transform.position.y + 0.5f);
            foreach (var piece in placed) runner.AddPiece(piece.transform, sink);
        }

        /// <summary>A copy of a game prefab that is only its looks: made under an inactive
        /// parent so no Awake runs (no ZDO, no piece registration), then stripped of every
        /// script, collider and body. Renderers, lights and particles stay.</summary>
        private static GameObject VisualCopy(GameObject prefab, Transform parent)
        {
            var holder = new GameObject("ArenaGateTemplate");
            holder.SetActive(false);
            holder.transform.SetParent(parent, false);
            var copy = UnityEngine.Object.Instantiate(prefab, holder.transform);
            try
            {
                // Passes: a script another one requires survives until the dependent is gone.
                for (int pass = 0; pass < 3; pass++)
                    foreach (var c in copy.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in copy.GetComponentsInChildren<Joint>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in copy.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var c in copy.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"NpcValheim Arena: prefab do portao '{prefab.name}': {e.Message}");
            }
            copy.SetActive(false);
            copy.transform.SetParent(parent, false);
            UnityEngine.Object.Destroy(holder);
            return copy;
        }
    }

    /// <summary>Animates one set of gates: the texture scroll, the rise, and the opening.</summary>
    internal sealed class ArenaGateRunner : MonoBehaviour
    {
        private sealed class Wall
        {
            public Mesh Mesh;
            public Vector2[] BaseUv;
            public Vector2[] Uv;
            public Material Material;
            public Color Color;
        }

        private sealed class Piece
        {
            public Transform Transform;
            public Vector3 Rest;
            public float Sink;
        }

        private readonly List<Wall> _walls = new List<Wall>();
        private readonly List<Piece> _pieces = new List<Piece>();
        private float _shown;
        private bool _opening;
        private float _scroll;

        private const float RiseSeconds = 0.6f;
        private const float OpenSeconds = 1.5f;

        internal void AddWall(Mesh mesh, Vector2[] uv, Material material, Color color)
        {
            _walls.Add(new Wall { Mesh = mesh, BaseUv = uv, Uv = (Vector2[])uv.Clone(), Material = material, Color = color });
            Apply();
        }

        internal void AddPiece(Transform piece, float sink)
        {
            _pieces.Add(new Piece { Transform = piece, Rest = piece.position, Sink = sink });
            Apply();
        }

        internal void Open() => _opening = true;

        private void Update()
        {
            float step = Time.deltaTime;
            _shown = _opening
                ? _shown - step / OpenSeconds
                : Mathf.Min(1f, _shown + step / RiseSeconds);
            if (_opening && _shown <= 0f)
            {
                Destroy(gameObject);
                return;
            }

            float speed = ArenaConfig.GateScrollSpeed?.Value ?? 0f;
            if (Mathf.Abs(speed) > 0.0001f)
            {
                _scroll = Mathf.Repeat(_scroll + speed * step, 1f);
                foreach (var wall in _walls)
                {
                    if (wall.Mesh == null) continue;
                    for (int i = 0; i < wall.Uv.Length; i++)
                        wall.Uv[i] = new Vector2(wall.BaseUv[i].x, wall.BaseUv[i].y - _scroll);
                    wall.Mesh.uv = wall.Uv;
                }
            }
            Apply();
        }

        private void Apply()
        {
            float eased = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_shown));
            foreach (var wall in _walls)
            {
                if (wall.Material == null) continue;
                var c = wall.Color;
                c.a *= eased;
                wall.Material.color = c;
            }
            foreach (var piece in _pieces)
            {
                if (piece.Transform == null) continue;
                piece.Transform.position = piece.Rest + Vector3.down * (piece.Sink * (1f - eased));
            }
        }

        private void OnDestroy()
        {
            foreach (var wall in _walls)
            {
                if (wall.Mesh != null) Destroy(wall.Mesh);
                if (wall.Material != null) Destroy(wall.Material);
            }
        }
    }
}
