using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NpcValheim.Persistence
{
    /// <summary>
    /// Reads/writes NpcProfile YAML files on disk. All calls here run on whichever peer owns
    /// the NPC's ZDO (the dedicated server in a real multiplayer setup, the host in a solo
    /// game) -- see NpcBase's RPC_ApplyProfile/RPC_SaveAsTemplate/RPC_* handlers, which are
    /// the only callers. That keeps the files always authoritative and server-side.
    ///
    /// Two kinds of file, same NpcProfile shape:
    ///  - npcs/instances/&lt;profileId&gt;.yaml -- live mirror of one placed NPC, rewritten every
    ///    time an admin changes anything about it (name, armor, hair, teleport cost, ...).
    ///  - npcs/templates/&lt;name&gt;.yaml -- a reusable preset an admin explicitly saved, meant to
    ///    be applied to other NPCs later ("Aplicar modelo" in the admin panel) so a
    ///    well-configured NPC doesn't have to be rebuilt by hand every time.
    ///
    /// Template listing in the UI reads this folder directly on whoever is running the UI --
    /// correct for the common case (host/solo play, where client and server share a
    /// filesystem) but not for a remote admin on a real dedicated server, who won't see the
    /// server's template list locally. Applying a template by name still resolves correctly
    /// for them either way, since that always loads from the *owning* peer's disk via RPC.
    /// </summary>
    public static class NpcConfigStore
    {
        private static string BaseDir => Path.Combine(Paths.PluginPath, "NpcValheim", "npcs");
        private static string TemplatesDir => Path.Combine(BaseDir, "templates");
        private static string InstancesDir => Path.Combine(BaseDir, "instances");

        public static string TemplatePath(string name) => Path.Combine(TemplatesDir, SanitizeFileName(name) + ".yaml");

        /// <summary>
        /// Where one placed NPC's mirror lives, as "&lt;name&gt;--&lt;id&gt;.yaml".
        ///
        /// It used to be the bare profile id, which meant a folder of a hundred files called
        /// things like 02514e532ee1493e9dfa513fdd0ecf9a.yaml -- unusable for anyone trying to
        /// find the merchant they just renamed. The id stays in the name because it is what
        /// makes the file unique and what the ZDO actually stores; the readable half is there
        /// for humans, and the loader finds the file by id regardless of what it says.
        /// </summary>
        public static string InstancePath(string profileId, string displayName = null)
        {
            var existing = FindInstanceFile(profileId);
            if (existing != null && displayName == null) return existing;

            string slug = Slug(displayName);
            return Path.Combine(InstancesDir, $"{slug}--{SanitizeFileName(profileId)}.yaml");
        }

        /// <summary>Locates an instance file by its id, whatever readable prefix it carries.
        /// Falls back to the old bare-id name so files written before this change still
        /// load.</summary>
        private static string FindInstanceFile(string profileId)
        {
            if (!Directory.Exists(InstancesDir) || string.IsNullOrEmpty(profileId)) return null;

            string id = SanitizeFileName(profileId);
            var match = Directory.GetFiles(InstancesDir, "*" + id + ".yaml").FirstOrDefault();
            return match;
        }

        /// <summary>A file-name-safe, readable form of an NPC's name: "Halvard, o Mercador"
        /// becomes "halvard-o-mercador".</summary>
        private static string Slug(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "npc";

            // Accents are stripped rather than kept: "Guardião" and "Guardiao" naming two
            // different files, on a filesystem that may or may not treat them as equal, is a
            // problem nobody wants to debug.
            var decomposed = name.Trim().ToLowerInvariant()
                .Normalize(System.Text.NormalizationForm.FormD);

            var sb = new System.Text.StringBuilder();
            foreach (char c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) ==
                    System.Globalization.UnicodeCategory.NonSpacingMark) continue;

                if (c >= 'a' && c <= 'z' || c >= '0' && c <= '9') sb.Append(c);
                else if ((c == ' ' || c == '-' || c == '_') &&
                         sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
            }

            var slug = sb.ToString().Trim('-');
            if (slug.Length > 40) slug = slug.Substring(0, 40).TrimEnd('-');
            return slug.Length == 0 ? "npc" : slug;
        }

        private static readonly ISerializer Serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        private static readonly IDeserializer Deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        public static void SaveTemplate(string name, NpcProfile profile) => Save(TemplatesDir, name, profile);

        public static NpcProfile LoadTemplate(string name) => Load(TemplatesDir, name);

        public static List<string> ListTemplates() => ListNames(TemplatesDir);

        /// <summary>Writes the mirror, renaming the file when the NPC has been renamed so the
        /// folder keeps telling the truth instead of accumulating stale names.</summary>
        public static void SaveInstance(string profileId, NpcProfile profile)
        {
            Directory.CreateDirectory(InstancesDir);

            var wanted = InstancePath(profileId, profile?.Name);
            var existing = FindInstanceFile(profileId);
            if (existing != null && !string.Equals(existing, wanted, System.StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(existing); }
                catch (System.Exception e)
                {
                    Plugin.Log.LogWarning($"NpcValheim: could not remove the old profile file '{existing}': {e.Message}");
                }
            }

            File.WriteAllText(wanted, Serializer.Serialize(profile));
        }

        public static NpcProfile LoadInstance(string profileId)
        {
            var path = FindInstanceFile(profileId);
            return path != null ? LoadFile(path) : null;
        }

        private static void Save(string dir, string name, NpcProfile profile)
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SanitizeFileName(name) + ".yaml");
            File.WriteAllText(path, Serializer.Serialize(profile));
        }

        private static NpcProfile Load(string dir, string name)
        {
            var path = Path.Combine(dir, SanitizeFileName(name) + ".yaml");
            return File.Exists(path) ? LoadFile(path) : null;
        }

        private static NpcProfile LoadFile(string path)
        {
            try
            {
                return Deserializer.Deserialize<NpcProfile>(File.ReadAllText(path));
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: failed to parse profile yaml '{path}': {e.Message}");
                return null;
            }
        }

        private static List<string> ListNames(string dir)
        {
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.yaml")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n)
                .ToList();
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
