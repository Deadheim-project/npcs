using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB;

namespace NpcValheim.Arena
{
    /// <summary>One arena as the file keeps it: the points as the old Local block wrote them
    /// (x,y,z,giro) and a box per kind of match.</summary>
    internal sealed class ArenaLayoutMap
    {
        public bool On;
        public string Name = "";
        public string Gold = "";
        public string Green = "";
        public string Spectator = "";
        /// <summary>Aligned with <see cref="ArenaConfig.MapTypes"/>.</summary>
        public bool[] Types = { true, true, true, true, true };

        public ArenaLayoutMap Clone()
        {
            var copy = (ArenaLayoutMap)MemberwiseClone();
            copy.Types = (bool[])Types.Clone();
            return copy;
        }
    }

    /// <summary>One challenge as the file keeps it. The id never changes, so a rename keeps
    /// its records (best times) and the admin page keeps track of it while it is edited.</summary>
    internal sealed class ArenaLayoutChallenge
    {
        public string Id = "";
        public bool On;
        public string Name = "";
        public string Creature = "";
        public int Level = 1;
        public float Health = 1f;
        public string Reward = "Coins";
        public int Amount;
        public string Extras = "";
        public string Map = "";
        /// <summary>The challenge's own rating for the PvE rating: beating a harder one pays more.</summary>
        public int Difficulty = ArenaLayout.DefaultDifficulty;

        public ArenaLayoutChallenge Clone() => (ArenaLayoutChallenge)MemberwiseClone();
    }

    /// <summary>
    /// Every arena and every challenge, as many as the admin makes: config/NpcValheim/arena-locais.json
    /// on the server, and the copy the server sent on every client (on login and after every
    /// change). Until 0.1.89 they were the fixed [Arena - Local 1..8] and [Arena - Desafio 1..5]
    /// blocks of the cfg; the first start of this version moves what those blocks had into the
    /// file (<see cref="CaptureLegacy"/>), and the cfg forgets them.
    /// </summary>
    internal static class ArenaLayout
    {
        internal const int DefaultDifficulty = 1500;
        internal const int MinDifficulty = 100;
        internal const int MaxDifficulty = 5000;
        internal const int MaxIdLength = 40;

        private static List<ArenaLayoutMap> _maps = new List<ArenaLayoutMap>();
        private static List<ArenaLayoutChallenge> _challenges = new List<ArenaLayoutChallenge>();

        internal static List<ArenaLayoutMap> Maps => _maps;
        internal static List<ArenaLayoutChallenge> Challenges => _challenges;

        /// <summary>Bumped on every change, for the pages to redraw.</summary>
        internal static int Revision { get; private set; }

        /// <summary>The server once it read the file; a client once the server sent it.</summary>
        internal static bool Loaded { get; private set; }

        internal static string Hash { get; private set; } = "";

        /// <summary>What the old cfg blocks had when this start read them, waiting for the
        /// server to write the file (only when there is no file yet).</summary>
        private static List<ArenaLayoutMap> _legacyMaps;
        private static List<ArenaLayoutChallenge> _legacyChallenges;

        internal static ArenaLayoutChallenge Challenge(string id) =>
            string.IsNullOrEmpty(id) ? null : _challenges.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        internal static void Replace(List<ArenaLayoutMap> maps, List<ArenaLayoutChallenge> challenges, string hash = null)
        {
            _maps = maps ?? new List<ArenaLayoutMap>();
            _challenges = challenges ?? new List<ArenaLayoutChallenge>();
            Hash = hash ?? ArenaBestiary.HashOf(ToJson(_maps, _challenges));
            Loaded = true;
            Revision++;
        }

        /// <summary>A new session: nothing known until the server says so.</summary>
        internal static void Forget()
        {
            _maps = new List<ArenaLayoutMap>();
            _challenges = new List<ArenaLayoutChallenge>();
            Hash = "";
            Loaded = false;
            Revision++;
        }

        // ------------------------------------------------------------------ ids

        /// <summary>Lower-case letters, digits, _ and -: safe in the wire and in the file.</summary>
        internal static bool ValidId(string id) =>
            !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-');

        /// <summary>An id for a new challenge, from its name, not taken by <paramref name="taken"/>.</summary>
        internal static string NewId(string name, IEnumerable<string> taken)
        {
            var used = new HashSet<string>(taken ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            foreach (char raw in (name ?? "").Normalize(NormalizationForm.FormD).ToLowerInvariant())
            {
                if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
                if ((raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9')) sb.Append(raw);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= 24) break;
            }
            string stem = sb.ToString().Trim('_');
            if (stem.Length == 0) stem = "desafio";
            string id = stem;
            for (int n = 2; used.Contains(id); n++) id = stem + "_" + n;
            return id;
        }

        // ------------------------------------------------------------------ json

        internal static string ToJson(IEnumerable<ArenaLayoutMap> maps, IEnumerable<ArenaLayoutChallenge> challenges, bool indent = false) =>
            JsonSerializer.Serialize(new BsonDocument
            {
                ["versao"] = 1,
                ["arenas"] = new BsonArray(maps.Select(m => (BsonValue)new BsonDocument
                {
                    ["ativa"] = m.On,
                    ["nome"] = m.Name ?? "",
                    ["ouro"] = m.Gold ?? "",
                    ["verde"] = m.Green ?? "",
                    ["espectador"] = m.Spectator ?? "",
                    ["tipos"] = new BsonArray(ArenaConfig.MapTypes.Where((t, k) => k < m.Types.Length && m.Types[k]).Select(t => (BsonValue)t)),
                })),
                ["desafios"] = new BsonArray(challenges.Select(c => (BsonValue)new BsonDocument
                {
                    ["id"] = c.Id ?? "",
                    ["ativo"] = c.On,
                    ["nome"] = c.Name ?? "",
                    ["criatura"] = c.Creature ?? "",
                    ["nivel"] = c.Level,
                    ["vida"] = ArenaBestiary.N(c.Health),
                    ["recompensa"] = c.Reward ?? "",
                    ["quantidade"] = c.Amount,
                    ["extras"] = c.Extras ?? "",
                    ["arena"] = c.Map ?? "",
                    ["dificuldade"] = c.Difficulty,
                })),
            }, indent);

        /// <summary>The whole file (or what the server sent). An entry with a problem is left out
        /// and named; false when the text is not the file at all.</summary>
        internal static bool Parse(string json, List<string> problems, out List<ArenaLayoutMap> maps, out List<ArenaLayoutChallenge> challenges)
        {
            maps = new List<ArenaLayoutMap>();
            challenges = new List<ArenaLayoutChallenge>();
            BsonValue root;
            try { root = JsonSerializer.Deserialize(json ?? ""); }
            catch (Exception e)
            {
                problems?.Add("o arquivo não é um JSON válido: " + e.Message);
                return false;
            }
            if (!root.IsDocument)
            {
                problems?.Add("o arquivo precisa ser um objeto com \"arenas\" e \"desafios\"");
                return false;
            }
            var doc = root.AsDocument;

            if (doc["arenas"].IsArray)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int i = 0;
                foreach (var v in doc["arenas"].AsArray)
                {
                    i++;
                    if (!v.IsDocument) { problems?.Add($"arena {i}: não é um objeto"); continue; }
                    var d = v.AsDocument;
                    var m = new ArenaLayoutMap
                    {
                        On = ArenaBestiary.Bool(d["ativa"], false),
                        Name = ArenaSettingsParser.CleanMapName(ArenaBestiary.Str(d["nome"])),
                        Gold = ArenaWire.Clean(ArenaBestiary.Str(d["ouro"])).Trim(),
                        Green = ArenaWire.Clean(ArenaBestiary.Str(d["verde"])).Trim(),
                        Spectator = ArenaWire.Clean(ArenaBestiary.Str(d["espectador"])).Trim(),
                    };
                    if (d["tipos"].IsArray)
                    {
                        var listed = d["tipos"].AsArray.Select(t => ArenaBestiary.Str(t).Trim().ToLowerInvariant()).ToList();
                        for (int k = 0; k < ArenaConfig.MapTypes.Length; k++) m.Types[k] = listed.Contains(ArenaConfig.MapTypes[k]);
                    }
                    if (m.Name.Length == 0 && m.Gold.Length == 0 && m.Green.Length == 0) continue;
                    if (m.Name.Length > 0 && !names.Add(m.Name))
                    {
                        problems?.Add($"arena \"{m.Name}\": nome repetido, a segunda foi ignorada");
                        continue;
                    }
                    maps.Add(m);
                }
            }

            if (doc["desafios"].IsArray)
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int i = 0;
                foreach (var v in doc["desafios"].AsArray)
                {
                    i++;
                    if (!v.IsDocument) { problems?.Add($"desafio {i}: não é um objeto"); continue; }
                    var d = v.AsDocument;
                    var c = new ArenaLayoutChallenge
                    {
                        Id = ArenaBestiary.Str(d["id"]).Trim().ToLowerInvariant(),
                        On = ArenaBestiary.Bool(d["ativo"], false),
                        Name = ArenaSettingsParser.CleanMapName(ArenaBestiary.Str(d["nome"])),
                        Creature = ArenaWire.Clean(ArenaBestiary.Str(d["criatura"])).Trim(),
                        Level = ClampInt(d["nivel"], 1, 1, 10),
                        Health = ClampFloat(d["vida"], 1f, 0.1f, 100f),
                        Reward = ArenaWire.Clean(ArenaBestiary.Str(d["recompensa"])).Trim(),
                        Amount = ClampInt(d["quantidade"], 0, 0, 9999),
                        Extras = ArenaWire.Clean(ArenaBestiary.Str(d["extras"])).Trim(),
                        Map = ArenaSettingsParser.CleanMapName(ArenaBestiary.Str(d["arena"])),
                        Difficulty = ClampInt(d["dificuldade"], DefaultDifficulty, MinDifficulty, MaxDifficulty),
                    };
                    if (!ValidId(c.Id)) c.Id = NewId(c.Name, ids.Concat(challenges.Select(x => x.Id)));
                    if (!ids.Add(c.Id))
                    {
                        problems?.Add($"desafio \"{c.Id}\": id repetido, o segundo foi ignorado");
                        continue;
                    }
                    challenges.Add(c);
                }
            }
            return true;
        }

        private static int ClampInt(BsonValue v, int fallback, int min, int max) =>
            v != null && v.IsNumber ? Math.Max(min, Math.Min(max, (int)Math.Round(v.AsDouble))) : fallback;

        private static float ClampFloat(BsonValue v, float fallback, float min, float max) =>
            v != null && v.IsNumber && !double.IsNaN(v.AsDouble) ? (float)Math.Max(min, Math.Min(max, v.AsDouble)) : fallback;

        // ------------------------------------------------------------------ the old cfg blocks

        private static readonly (bool On, string Name, string Creature, int Level, float Health, int Coins)[] Defaults =
        {
            (true, "Troll Furioso", "Troll", 3, 2f, 300),
            (true, "Fenring Alfa", "Fenring", 3, 3f, 500),
            (false, "Abominacao Antiga", "Abomination", 3, 2f, 400),
            (false, "Rei Goblin", "GoblinBrute", 3, 3f, 600),
            (false, "Buscador Gigante", "SeekerBrute", 3, 3f, 800),
        };

        /// <summary>The challenges a server with no file and no old blocks starts with.</summary>
        internal static List<ArenaLayoutChallenge> DefaultChallenges()
        {
            var list = new List<ArenaLayoutChallenge>();
            foreach (var d in Defaults)
                list.Add(new ArenaLayoutChallenge
                {
                    Id = NewId(d.Name, list.Select(c => c.Id)), On = d.On, Name = d.Name, Creature = d.Creature, Level = d.Level,
                    Health = d.Health, Reward = "Coins", Amount = d.Coins,
                });
            return list;
        }

        /// <summary>
        /// The [Arena - Local N] and [Arena - Desafio N] blocks still in the cfg, read from the
        /// entries BepInEx keeps for keys nobody binds any more (they leave the file right after,
        /// with the other orphans). Kept until the server writes the file -- and only used when
        /// there is no file yet.
        /// </summary>
        internal static void CaptureLegacy(IDictionary<BepInEx.Configuration.ConfigDefinition, string> orphans)
        {
            if (orphans == null || orphans.Count == 0) return;
            var maps = new SortedDictionary<int, Dictionary<string, string>>();
            var challenges = new SortedDictionary<int, Dictionary<string, string>>();
            foreach (var kv in orphans)
            {
                var target = Block(kv.Key.Section, "Arena - Local ", maps) ?? Block(kv.Key.Section, "Arena - Desafio ", challenges);
                if (target != null) target[kv.Key.Key] = kv.Value ?? "";
            }
            if (maps.Count == 0 && challenges.Count == 0) return;

            _legacyMaps = new List<ArenaLayoutMap>();
            foreach (var b in maps.Values)
            {
                var m = new ArenaLayoutMap
                {
                    On = Flag(b, "Ativo", false),
                    Name = ArenaSettingsParser.CleanMapName(Get(b, "Nome")),
                    Gold = Get(b, "Ouro").Trim(),
                    Green = Get(b, "Verde").Trim(),
                    Spectator = Get(b, "Espectador").Trim(),
                };
                string[] labels = { "1v1", "2v2", "3v3", "FFA", "PvE" };
                for (int k = 0; k < labels.Length; k++) m.Types[k] = Flag(b, "Tipo" + labels[k], true);
                if (m.Name.Length == 0 && m.Gold.Length == 0 && m.Green.Length == 0) continue;
                _legacyMaps.Add(m);
            }

            _legacyChallenges = new List<ArenaLayoutChallenge>();
            foreach (var b in challenges.Values)
            {
                var c = new ArenaLayoutChallenge
                {
                    On = Flag(b, "Ativo", false),
                    Name = ArenaSettingsParser.CleanMapName(Get(b, "Nome")),
                    Creature = Get(b, "Criatura").Trim(),
                    Level = Number(b, "Nivel", 1, 1, 10),
                    Health = (float)Real(b, "Vida", 1, 0.1, 100),
                    Reward = Get(b, "Recompensa").Trim(),
                    Amount = Number(b, "Quantidade", 0, 0, 9999),
                    Extras = Get(b, "RecompensasExtras").Trim(),
                    Map = ArenaSettingsParser.CleanMapName(Get(b, "Arena")),
                };
                if (c.Creature.Length == 0 && c.Name.Length == 0) continue;
                c.Id = NewId(c.Name, _legacyChallenges.Select(x => x.Id));
                _legacyChallenges.Add(c);
            }
        }

        /// <summary>An arena of the even older [Arena - Partida] Maps list, waiting with the blocks.</summary>
        internal static void AddLegacy(ArenaLayoutMap map)
        {
            _legacyMaps ??= new List<ArenaLayoutMap>();
            if (_legacyMaps.Any(m => string.Equals(m.Name, map.Name, StringComparison.OrdinalIgnoreCase))) return;
            _legacyMaps.Add(map);
        }

        private static Dictionary<string, string> Block(string section, string prefix, SortedDictionary<int, Dictionary<string, string>> into)
        {
            if (section == null || !section.StartsWith(prefix, StringComparison.Ordinal)) return null;
            if (!int.TryParse(section.Substring(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return null;
            if (!into.TryGetValue(n, out var block)) into[n] = block = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return block;
        }

        private static string Get(Dictionary<string, string> b, string key) => b.TryGetValue(key, out var v) ? v ?? "" : "";

        private static bool Flag(Dictionary<string, string> b, string key, bool fallback) =>
            bool.TryParse(Get(b, key).Trim(), out bool v) ? v : fallback;

        private static int Number(Dictionary<string, string> b, string key, int fallback, int min, int max) =>
            int.TryParse(Get(b, key).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? Math.Max(min, Math.Min(max, v)) : fallback;

        private static double Real(Dictionary<string, string> b, string key, double fallback, double min, double max) =>
            double.TryParse(Get(b, key).Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v)
                ? Math.Max(min, Math.Min(max, v)) : fallback;

        // ------------------------------------------------------------------ file (server)

        internal static string FilePath => Persistence.NpcStoragePaths.ArenaLayoutFile;

        /// <summary>Reads the file. Without one, it is written from the old cfg blocks this start
        /// found, or from the five example challenges. A file that does not parse is kept aside
        /// (.invalido) and the arena starts empty rather than overwrite it.</summary>
        internal static void LoadOrSeed(string path, List<string> problems, out List<ArenaLayoutMap> maps, out List<ArenaLayoutChallenge> challenges)
        {
            if (!File.Exists(path))
            {
                bool legacy = _legacyMaps != null || _legacyChallenges != null;
                maps = _legacyMaps ?? new List<ArenaLayoutMap>();
                challenges = _legacyChallenges != null && _legacyChallenges.Count > 0 ? _legacyChallenges : DefaultChallenges();
                Save(path, maps, challenges);
                if (legacy)
                    problems?.Add($"migrado da cfg antiga: {maps.Count} arena(s) e {challenges.Count} desafio(s) foram para {path}");
                _legacyMaps = null;
                _legacyChallenges = null;
                return;
            }
            _legacyMaps = null;
            _legacyChallenges = null;
            if (!Parse(File.ReadAllText(path, Encoding.UTF8), problems, out maps, out challenges))
            {
                string aside = path + ".invalido";
                File.Copy(path, aside, overwrite: true);
                problems?.Add("cópia guardada em " + aside);
            }
        }

        /// <summary>Written to a temp file and moved over, so a crash mid-write leaves the old one.</summary>
        internal static void Save(string path, IEnumerable<ArenaLayoutMap> maps, IEnumerable<ArenaLayoutChallenge> challenges)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(maps, challenges, indent: true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
