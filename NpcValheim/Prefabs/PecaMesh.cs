using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using BepInEx;

namespace NpcValheim.Prefabs
{
    /// <summary>
    /// Loads the PECA mesh the desktop asset pipeline writes (magic "PECA", then verts / uvs /
    /// tris). Same format the alchemy bench already uses -- the mailbox's WoW model arrives as
    /// <c>Assets/Mailbox/model.bin</c> plus albedo/normal PNGs.
    ///
    /// Materials are cloned from a real game prefab so the shader is one Valheim actually
    /// ships. Unity's built-in Standard is not in the game build and renders magenta.
    /// </summary>
    internal static class PecaMesh
    {
        public const string MailboxFolder = "Mailbox";

        public static bool TryAttach(GameObject target, ZNetScene scene, string folder, float targetHeight = 1.6f)
        {
            if (target == null) return false;

            var mesh = LoadMesh(folder);
            if (mesh == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: PECA mesh not found for '{folder}', using the placeholder look");
                return false;
            }

            float height = Mathf.Max(0.01f, mesh.bounds.size.y);
            float scale = targetHeight / height;

            HideExistingRenderers(target);

            var visual = new GameObject("PecaVisual");
            visual.transform.SetParent(target.transform, false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            visual.transform.localScale = Vector3.one * scale;

            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = visual.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = CreateMaterial(folder);

            SetupCollider(target, mesh, scale);
            return true;
        }

        private static void HideExistingRenderers(GameObject target)
        {
            foreach (var renderer in target.GetComponentsInChildren<MeshRenderer>(true))
                renderer.enabled = false;
            foreach (var filter in target.GetComponentsInChildren<MeshFilter>(true))
                filter.sharedMesh = null;
        }

        private static void SetupCollider(GameObject target, Mesh mesh, float scale)
        {
            foreach (var collider in target.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            int pieceLayer = LayerMask.NameToLayer("piece");
            if (pieceLayer >= 0) target.layer = pieceLayer;

            var col = new GameObject("PecaCollision");
            col.transform.SetParent(target.transform, false);
            if (pieceLayer >= 0) col.layer = pieceLayer;

            var box = col.AddComponent<BoxCollider>();
            var bounds = mesh.bounds;
            var size = bounds.size * scale;
            size.x = Mathf.Max(size.x * 0.85f, 0.45f);
            size.y = Mathf.Max(size.y * 0.9f, 0.8f);
            size.z = Mathf.Max(size.z * 0.85f, 0.45f);
            box.center = new Vector3(bounds.center.x * scale, size.y * 0.5f, bounds.center.z * scale);
            box.size = size;
        }

        private static Mesh LoadMesh(string folder)
        {
            var path = AssetPath(folder, "model.bin");
            if (!File.Exists(path)) return null;

            var data = File.ReadAllBytes(path);
            if (data.Length < 12 || data[0] != (byte)'P' || data[1] != (byte)'E' ||
                data[2] != (byte)'C' || data[3] != (byte)'A')
            {
                Plugin.Log.LogError($"NpcValheim: '{path}' is not a PECA mesh");
                return null;
            }

            int offset = 4;
            int vertCount = System.BitConverter.ToInt32(data, offset); offset += 4;
            int triCount = System.BitConverter.ToInt32(data, offset); offset += 4;

            var vertices = new Vector3[vertCount];
            for (int i = 0; i < vertCount; i++)
            {
                vertices[i] = new Vector3(
                    System.BitConverter.ToSingle(data, offset),
                    System.BitConverter.ToSingle(data, offset + 4),
                    System.BitConverter.ToSingle(data, offset + 8));
                offset += 12;
            }

            var uvs = new Vector2[vertCount];
            for (int i = 0; i < vertCount; i++)
            {
                uvs[i] = new Vector2(
                    System.BitConverter.ToSingle(data, offset),
                    System.BitConverter.ToSingle(data, offset + 4));
                offset += 8;
            }

            var triangles = new int[triCount * 3];
            for (int i = 0; i < triangles.Length; i++)
            {
                triangles[i] = System.BitConverter.ToInt32(data, offset);
                offset += 4;
            }

            var mesh = new Mesh
            {
                name = folder,
                indexFormat = vertCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            ApplySmoothNormals(mesh);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static void ApplySmoothNormals(Mesh mesh)
        {
            var vertices = mesh.vertices;
            var triangles = mesh.triangles;
            var accum = new Vector3[vertices.Length];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i];
                int b = triangles[i + 1];
                int c = triangles[i + 2];
                Vector3 n = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]);
                accum[a] += n;
                accum[b] += n;
                accum[c] += n;
            }

            var groups = new System.Collections.Generic.Dictionary<long, System.Collections.Generic.List<int>>(vertices.Length);
            for (int i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                long key = ((long)Mathf.Round(v.x * 8000f) & 0x1FFFFF)
                    | (((long)Mathf.Round(v.y * 8000f) & 0x1FFFFF) << 21)
                    | (((long)Mathf.Round(v.z * 8000f) & 0x1FFFFF) << 42);
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new System.Collections.Generic.List<int>(4);
                    groups[key] = list;
                }
                list.Add(i);
            }

            var normals = new Vector3[vertices.Length];
            foreach (var list in groups.Values)
            {
                Vector3 n = Vector3.zero;
                for (int i = 0; i < list.Count; i++)
                    n += accum[list[i]];
                if (n.sqrMagnitude < 1e-12f)
                    n = Vector3.up;
                n.Normalize();
                for (int i = 0; i < list.Count; i++)
                    normals[list[i]] = n;
            }

            mesh.normals = normals;
        }

        private static Material CreateMaterial(string folder)
        {
            var albedo = LoadTexture(folder, "basecolor.png");
            var bump = LoadTexture(folder, "normal.png");

            Shader shader = null;
            foreach (var name in new[] { "Legacy Shaders/Diffuse", "Diffuse", "Unlit/Texture" })
            {
                shader = Shader.Find(name);
                if (shader != null) break;
            }
            if (shader == null)
            {
                Plugin.Log.LogWarning("NpcValheim: no shader for the mailbox material");
                return null;
            }

            var mat = new Material(shader);
            mat.color = Color.white;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.white);
            if (albedo != null)
            {
                mat.mainTexture = albedo;
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", albedo);
            }
            if (bump != null && mat.HasProperty("_BumpMap"))
                mat.SetTexture("_BumpMap", bump);
            if (mat.HasProperty("_Metallic")) mat.SetFloat("_Metallic", 0f);
            if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0.15f);
            return mat;
        }

        private static Texture2D LoadTexture(string folder, string fileName)
        {
            var path = AssetPath(folder, fileName);
            if (!File.Exists(path)) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!Npc.GameApi.TryLoadImage(tex, File.ReadAllBytes(path)))
            {
                Plugin.Log.LogWarning($"NpcValheim: failed to decode '{path}'");
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.name = fileName;
            return tex;
        }

        internal static Sprite LoadIcon(string folder)
        {
            foreach (var fileName in new[] { "Selo_Png.png", "Selo_Icon.png", "Selo_Icon.jpeg", "hud-icon.png" })
            {
                var tex = LoadTexture(folder, fileName);
                if (tex == null) continue;
                PunchPureWhiteBackground(tex);
                return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }

            Plugin.Log.LogWarning($"NpcValheim: no hammer icon found in Assets/{folder}");
            return null;
        }

        /// <summary>
        /// Only RGB 250+ becomes transparent. Blue rune, parchment and wax stay intact.
        /// </summary>
        internal static void PunchPureWhiteBackground(Texture2D tex)
        {
            if (tex == null) return;
            var pixels = tex.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.r >= 250 && p.g >= 250 && p.b >= 250)
                    pixels[i] = new Color32(p.r, p.g, p.b, 0);
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        private static string AssetPath(string folder, string fileName)
        {
            var fromPlugins = Path.Combine(Paths.PluginPath, "NpcValheim", "Assets", folder, fileName);
            if (File.Exists(fromPlugins)) return fromPlugins;

            var assemblyDir = Path.GetDirectoryName(typeof(PecaMesh).Assembly.Location);
            return string.IsNullOrEmpty(assemblyDir)
                ? fromPlugins
                : Path.Combine(assemblyDir, "Assets", folder, fileName);
        }
    }
}
