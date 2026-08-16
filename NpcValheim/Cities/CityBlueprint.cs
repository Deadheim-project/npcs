using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace NpcValheim.Cities
{
    /// <summary>One piece of a city, in coordinates relative to the city centre.</summary>
    public sealed class BlueprintPiece
    {
        public string Name;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
        public Vector3 Scale = Vector3.one;

        public BlueprintPiece() { }

        public BlueprintPiece(string name, Vector3 position, float yaw = 0f)
        {
            Name = name;
            Position = position;
            Rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    /// <summary>
    /// A city as a flat list of pieces around a local origin, plus the NPCs that staff it.
    ///
    /// Two ways to get one: <see cref="CityLayout"/> generates it from code (what the three
    /// shipped cities use -- no third-party assets, nothing to license), or <see cref="Parse"/>
    /// reads a PlanBuild <c>.blueprint</c> file, so a city built by hand in game can replace a
    /// generated one by dropping the file into the cities folder. The server never needs
    /// PlanBuild itself installed: the format is plain text and this is the whole reader.
    /// </summary>
    public sealed class CityBlueprint
    {
        public string Name;
        public readonly List<BlueprintPiece> Pieces = new List<BlueprintPiece>();

        /// <summary>Where each staff NPC stands, relative to the centre. Key is the prefab
        /// name (NpcValheim_Marketplace, ...), value its offset and facing.</summary>
        public readonly List<BlueprintPiece> Npcs = new List<BlueprintPiece>();

        /// <summary>How far out the city reaches, used to size the terrain levelling and to
        /// keep two cities from being sited on top of each other.</summary>
        public float Radius
        {
            get
            {
                float r = 0f;
                foreach (var p in Pieces)
                    r = Mathf.Max(r, new Vector2(p.Position.x, p.Position.z).magnitude);
                return r;
            }
        }

        public void Add(string prefab, Vector3 position, float yaw = 0f) =>
            Pieces.Add(new BlueprintPiece(prefab, position, yaw));

        public void AddNpc(string prefab, Vector3 position, float yaw = 0f) =>
            Npcs.Add(new BlueprintPiece(prefab, position, yaw));

        /// <summary>
        /// Reads PlanBuild's text format. Piece lines are semicolon-separated:
        /// <c>name;category;posX;posY;posZ;rotX;rotY;rotZ;rotW;infoJson;scaleX;scaleY;scaleZ</c>
        /// with invariant-culture numbers. Sections are introduced by <c>#Pieces</c>,
        /// <c>#Terrain</c> and <c>#SnapPoints</c>; only pieces are read here, because levelling
        /// is done by <see cref="CityBuilder"/> against the real ground rather than replayed
        /// from whatever the blueprint's original terrain looked like.
        ///
        /// A malformed line is skipped with a warning instead of aborting the file -- half a
        /// city standing is worth more than none, and the log names the line to fix.
        /// </summary>
        public static CityBlueprint Parse(string text, string fallbackName)
        {
            var bp = new CityBlueprint { Name = fallbackName };
            if (string.IsNullOrEmpty(text)) return bp;

            var section = "pieces";   // files written before sections existed start straight in
            int lineNumber = 0;

            foreach (var raw in text.Split('\n'))
            {
                lineNumber++;
                var line = raw.Trim();
                if (line.Length == 0) continue;

                if (line[0] == '#')
                {
                    var header = line.Substring(1).Trim();
                    if (header.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                        bp.Name = header.Substring(5).Trim();
                    else if (header.Equals("Pieces", StringComparison.OrdinalIgnoreCase)) section = "pieces";
                    else if (header.Equals("Terrain", StringComparison.OrdinalIgnoreCase)) section = "terrain";
                    else if (header.Equals("SnapPoints", StringComparison.OrdinalIgnoreCase)) section = "snap";
                    continue;
                }

                if (section != "pieces") continue;

                var piece = ParsePiece(line);
                if (piece == null)
                {
                    Plugin.Log.LogWarning($"NpcValheim: skipping unreadable blueprint line {lineNumber} in '{fallbackName}': {line}");
                    continue;
                }
                bp.Pieces.Add(piece);
            }

            return bp;
        }

        public static CityBlueprint ParseFile(string path) =>
            Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));

        private static BlueprintPiece ParsePiece(string line)
        {
            var f = line.Split(';');
            if (f.Length < 9 || string.IsNullOrWhiteSpace(f[0])) return null;

            if (!TryFloat(f[2], out var px) || !TryFloat(f[3], out var py) || !TryFloat(f[4], out var pz) ||
                !TryFloat(f[5], out var rx) || !TryFloat(f[6], out var ry) || !TryFloat(f[7], out var rz) ||
                !TryFloat(f[8], out var rw))
                return null;

            var piece = new BlueprintPiece
            {
                Name = f[0].Trim(),
                Position = new Vector3(px, py, pz),
                Rotation = new Quaternion(rx, ry, rz, rw),
            };

            // Scale is a later addition to the format; blueprints without it are unscaled
            // rather than zero-sized.
            if (f.Length >= 13 && TryFloat(f[10], out var sx) && TryFloat(f[11], out var sy) && TryFloat(f[12], out var sz))
                piece.Scale = new Vector3(sx, sy, sz);

            return piece;
        }

        private static bool TryFloat(string s, out float value) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
