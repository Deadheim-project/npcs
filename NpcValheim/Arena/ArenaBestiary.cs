using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB;

namespace NpcValheim.Arena
{
    /// <summary>
    /// One creature of the Bestiário: a variant of a creature of the game, not a prefab of its
    /// own. The arena spawns the base and marks it (npcv_beast = id on the ZDO); every machine
    /// then dresses it from this definition (ArenaBeastBody), and the creature's owner runs its
    /// abilities (ArenaBeastBrain).
    /// </summary>
    internal sealed class ArenaBeastDef
    {
        public string Id = "";
        public string Base = "Troll";
        public string Name = "";
        public string Title = "";
        public bool BossBar = true;

        // visual
        public float Scale = 1f;
        public int[] Tint = { 255, 255, 255 };
        public float TintStrength;
        public int[] Glow = { 0, 0, 0 };
        public float GlowStrength;
        public string Effect = ArenaBestiary.NoEffect;

        // atributos
        public int Level = 1;
        public float HealthMultiplier = 1f;
        public float HealthPerExtraPlayer = 0.6f;
        public float Speed = 1f;
        public float Turn = 1f;
        public float Stagger = 1f;
        public float PushResist;
        public float Perception = 40f;
        public bool FleeLowHealth;

        /// <summary>Damage key (blunt, slash...) to the modifier's name (Normal, Resistente...).
        /// Every key of <see cref="ArenaBestiary.DamageTypes"/> is always present.</summary>
        public readonly Dictionary<string, string> Resistances = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>New numbers for the base's own attacks, by the item's prefab.</summary>
        public List<ArenaBeastAttack> Attacks = new List<ArenaBeastAttack>();
        public List<ArenaBeastAbility> Abilities = new List<ArenaBeastAbility>();

        public string DisplayName => string.IsNullOrEmpty(Name) ? Id : Name;

        public ArenaBeastDef Clone()
        {
            var problems = new List<string>();
            // Through the text, so the attacks and abilities are copies too.
            return ArenaBestiary.ParseOne(ArenaBestiary.ToJson(this), problems) ?? this;
        }
    }

    /// <summary>
    /// The Bestiário: config/NpcValheim/arena-bestiario.json on the server, and the copy the
    /// server sent on every client (on login and on every save). Pure data: the parse, the
    /// limits and the health formula live here so the checks can run them outside the game.
    /// </summary>
    internal static class ArenaBestiary
    {
        internal const string ChallengePrefix = "bestiario:";
        internal const string NoEffect = "Nenhum";
        internal const int MaxCreatures = 64;
        internal const int MaxIdLength = 32;
        internal const int MaxNameLength = 48;
        internal const int MaxTitleLength = 64;
        /// <summary>Of the whole bestiary on the wire, and of one creature in a save.</summary>
        internal const int MaxWireLength = 120000;
        internal const int MaxSaveLength = 16000;

        internal static readonly string[] DamageTypes = { "blunt", "slash", "pierce", "fire", "frost", "lightning", "poison", "spirit" };
        internal static readonly string[] DamageLabels = { "Contundente", "Cortante", "Perfurante", "Fogo", "Gelo", "Raio", "Veneno", "Espiritual" };
        internal static readonly string[] DamageColors = { "#c9b28d", "#e0e0e0", "#b9c4cc", "#ff7a2e", "#7fc8ff", "#f2e35c", "#8fd14f", "#d6b3ff" };

        /// <summary>The modifiers as the admin page names them, weakest first, with how much of
        /// the hit lands. The same eight HitData.DamageModifier has (Ignore left out).</summary>
        internal static readonly (string Name, float Factor)[] Modifiers =
        {
            ("Muito fraco", 2f), ("Fraco", 1.5f), ("Levemente fraco", 1.25f), ("Normal", 1f),
            ("Levemente resistente", 0.75f), ("Resistente", 0.5f), ("Muito resistente", 0.25f), ("Imune", 0f),
        };

        internal static readonly string[] Effects = { NoEffect, "Chamas", "Fumaça escura", "Gotas de veneno", "Névoa gelada", "Faíscas", "Runas brilhando" };

        /// <summary>What the base may be: the challenge's creatures, none of them a boss.</summary>
        internal static string[] Bases => ArenaSettingsParser.ChallengeCreatures;

        private static List<ArenaBeastDef> _all = new List<ArenaBeastDef>();

        /// <summary>Bumped on every change, for the pages to redraw.</summary>
        internal static int Revision { get; private set; }

        /// <summary>Whether this machine has a bestiary at all: the server once it read the file,
        /// a client once the server sent it. Before that a "bestiario:id" is not missing, only
        /// not known yet.</summary>
        internal static bool Loaded { get; private set; }

        /// <summary>Of the JSON the list came from (<see cref="HashOf"/>): what the server says on
        /// the hello, for a client to ask for the list only when its own is another.</summary>
        internal static string Hash { get; private set; } = "";

        internal static IReadOnlyList<ArenaBeastDef> All => _all;

        internal static ArenaBeastDef Find(string id) =>
            string.IsNullOrEmpty(id) ? null : _all.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

        internal static void Replace(List<ArenaBeastDef> beasts, string hash = null)
        {
            _all = beasts ?? new List<ArenaBeastDef>();
            Hash = hash ?? HashOf(ToJson(_all));
            Loaded = true;
            Revision++;
        }

        /// <summary>A new session: nothing known until the server says so.</summary>
        internal static void Forget()
        {
            _all = new List<ArenaBeastDef>();
            Hash = "";
            Loaded = false;
            Revision++;
        }

        /// <summary>FNV-1a (64 bits) of the text, in hex: the same on every machine and every run.</summary>
        internal static string HashOf(string text)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in text ?? "")
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h.ToString("x16", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ challenge

        /// <summary>"bestiario:troll_anciao" -> "troll_anciao"; null for a creature of the game.</summary>
        internal static string BeastIdOf(string creature)
        {
            creature = (creature ?? "").Trim();
            return creature.StartsWith(ChallengePrefix, StringComparison.OrdinalIgnoreCase)
                ? creature.Substring(ChallengePrefix.Length).Trim()
                : null;
        }

        internal static string ChallengeValue(ArenaBeastDef beast) => ChallengePrefix + beast.Id;

        // ------------------------------------------------------------------ numbers

        /// <summary>On top of base x level: the creature's multiplier, and more for each player
        /// past the first. With 60%, a pair faces 1.6x the solo health and a trio 2.2x.</summary>
        internal static float HealthFactor(ArenaBeastDef beast, int players)
        {
            if (beast == null) return 1f;
            int extra = Math.Max(1, players) - 1;
            return beast.HealthMultiplier * (1f + beast.HealthPerExtraPlayer * extra);
        }

        /// <summary>Vida = base x nível x vidaMult x (1 + vidaPorJogadorExtra x (jogadores - 1)).</summary>
        internal static float MaxHealth(float baseHealth, ArenaBeastDef beast, int players) =>
            baseHealth * Math.Max(1, beast?.Level ?? 1) * HealthFactor(beast, players);

        internal static float ModifierFactor(string name)
        {
            foreach (var m in Modifiers)
                if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return m.Factor;
            return 1f;
        }

        internal static string CanonicalModifier(string name)
        {
            foreach (var m in Modifiers)
                if (string.Equals(m.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return m.Name;
            return null;
        }

        internal static string CanonicalBase(string prefab) =>
            Array.Find(Bases, b => string.Equals(b, (prefab ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        internal static bool ValidId(string id) =>
            !string.IsNullOrEmpty(id) && id.Length <= MaxIdLength && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_');

        /// <summary>Lowercase letters, digits and _ out of a name: "Troll Ancião" -> "troll_anciao".</summary>
        internal static string IdFrom(string text)
        {
            var sb = new StringBuilder();
            foreach (char raw in (text ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
                char c = char.ToLowerInvariant(raw);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
                if (sb.Length >= MaxIdLength) break;
            }
            return sb.ToString().Trim('_');
        }

        // ------------------------------------------------------------------ JSON

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static BsonDocument ToBson(ArenaBeastDef b)
        {
            var res = new BsonDocument();
            foreach (var key in DamageTypes)
                res[key] = b.Resistances.TryGetValue(key, out var m) ? m : "Normal";
            return new BsonDocument
            {
                ["id"] = b.Id,
                ["base"] = b.Base,
                ["nome"] = b.Name,
                ["titulo"] = b.Title,
                ["barraChefe"] = b.BossBar,
                ["visual"] = new BsonDocument
                {
                    ["escala"] = N(b.Scale),
                    ["tingimento"] = new BsonArray(b.Tint.Select(c => new BsonValue(c))),
                    ["tingimentoForca"] = N(b.TintStrength),
                    ["brilho"] = new BsonArray(b.Glow.Select(c => new BsonValue(c))),
                    ["brilhoForca"] = N(b.GlowStrength),
                    ["efeito"] = b.Effect,
                },
                ["atributos"] = new BsonDocument
                {
                    ["nivel"] = b.Level,
                    ["vidaMult"] = N(b.HealthMultiplier),
                    ["vidaPorJogadorExtra"] = N(b.HealthPerExtraPlayer),
                    ["velocidade"] = N(b.Speed),
                    ["giro"] = N(b.Turn),
                    ["atordoamento"] = N(b.Stagger),
                    ["resistEmpurrao"] = N(b.PushResist),
                    ["percepcao"] = N(b.Perception),
                    ["fogeComPoucaVida"] = b.FleeLowHealth,
                },
                ["resistencias"] = res,
                ["ataques"] = new BsonArray(b.Attacks.Select(a => (BsonValue)ArenaBeastKit.ToBson(a))),
                ["habilidades"] = new BsonArray(b.Abilities.Select(a => (BsonValue)ArenaBeastKit.ToBson(a))),
            };
        }

        /// <summary>A float as the file shows it: 1.35, not 1.35000002384.</summary>
        internal static BsonValue N(float value) => new BsonValue(Math.Round((double)value, 4));

        internal static string ToJson(ArenaBeastDef beast, bool indent = false) => JsonSerializer.Serialize(ToBson(beast), indent);

        internal static string ToJson(IEnumerable<ArenaBeastDef> beasts, bool indent = false) =>
            JsonSerializer.Serialize(new BsonDocument
            {
                ["versao"] = 1,
                ["criaturas"] = new BsonArray(beasts.Select(b => (BsonValue)ToBson(b))),
            }, indent);

        /// <summary>The whole file (or what the server sent). A creature with a problem is
        /// left out and named in <paramref name="problems"/>; the rest still load. Null when
        /// the text is not JSON at all.</summary>
        internal static List<ArenaBeastDef> Parse(string json, List<string> problems)
        {
            var warnings = new List<string>();
            var result = ParseList(json, problems, warnings);
            problems?.AddRange(warnings);
            return result;
        }

        private static List<ArenaBeastDef> ParseList(string json, List<string> problems, List<string> warnings)
        {
            BsonValue root;
            try { root = JsonSerializer.Deserialize(json ?? ""); }
            catch (Exception e)
            {
                problems?.Add("o arquivo não é um JSON válido: " + e.Message);
                return null;
            }

            BsonArray list = root.IsArray ? root.AsArray
                : root.IsDocument && root.AsDocument["criaturas"].IsArray ? root.AsDocument["criaturas"].AsArray
                : null;
            if (list == null)
            {
                problems?.Add("o arquivo precisa ter a lista \"criaturas\"");
                return null;
            }

            var result = new List<ArenaBeastDef>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsDocument)
                {
                    problems?.Add($"criatura {i + 1}: não é um objeto");
                    continue;
                }
                var beast = ParseOne(list[i].AsDocument, problems, $"criatura {i + 1}", warnings);
                if (beast == null) continue;
                if (!ids.Add(beast.Id))
                {
                    problems?.Add($"\"{beast.Id}\": id repetido, a segunda foi ignorada");
                    continue;
                }
                if (result.Count >= MaxCreatures)
                {
                    problems?.Add($"mais de {MaxCreatures} criaturas: o resto foi ignorado");
                    break;
                }
                result.Add(beast);
            }
            return result;
        }

        internal static ArenaBeastDef ParseOne(string json, List<string> problems, List<string> warnings = null)
        {
            BsonValue value;
            try { value = JsonSerializer.Deserialize(json ?? ""); }
            catch (Exception e)
            {
                problems?.Add("JSON inválido: " + e.Message);
                return null;
            }
            if (!value.IsDocument)
            {
                problems?.Add("a criatura precisa ser um objeto JSON");
                return null;
            }
            return ParseOne(value.AsDocument, problems, null, warnings);
        }

        /// <summary>One creature, checked field by field. Anything out of its range is refused,
        /// not clamped: a typo in the file must not become a 300x troll in the arena. What can be
        /// dropped without changing the fight's numbers (an attack of the first format, a hit
        /// effect the game lacks) goes to <paramref name="warnings"/> instead.</summary>
        internal static ArenaBeastDef ParseOne(BsonDocument d, List<string> problems, string where = null, List<string> warnings = null)
        {
            var b = new ArenaBeastDef();
            string id = Str(d["id"]).Trim();
            string label = where ?? (id.Length > 0 ? $"\"{id}\"" : "criatura");
            if (id.Length > 0) label = $"\"{id}\"";
            int before = problems?.Count ?? 0;
            var local = problems ?? new List<string>();
            void Bad(string what) => local.Add($"{label}: {what}");

            if (!ValidId(id)) Bad($"id precisa ter de 1 a {MaxIdLength} letras minúsculas, números ou _");
            b.Id = id;

            string baseName = CanonicalBase(Str(d["base"]));
            if (baseName == null) Bad($"base \"{Str(d["base"])}\" não está na lista (sem chefes)");
            b.Base = baseName ?? "";

            b.Name = Text(d["nome"]);
            if (b.Name.Length == 0 || b.Name.Length > MaxNameLength) Bad($"nome de 1 a {MaxNameLength} letras");
            b.Title = Text(d["titulo"]);
            if (b.Title.Length > MaxTitleLength) Bad($"título de até {MaxTitleLength} letras");
            b.BossBar = Bool(d["barraChefe"], true);

            var v = d["visual"].IsDocument ? d["visual"].AsDocument : new BsonDocument();
            b.Scale = Num(v["escala"], 1f, 0.3f, 3f, "tamanho", Bad);
            b.Tint = Rgb(v["tingimento"], new[] { 255, 255, 255 }, "tingimento", Bad);
            b.TintStrength = Num(v["tingimentoForca"], 0f, 0f, 1f, "força do tingimento", Bad);
            b.Glow = Rgb(v["brilho"], new[] { 0, 0, 0 }, "brilho", Bad);
            b.GlowStrength = Num(v["brilhoForca"], 0f, 0f, 1f, "força do brilho", Bad);
            string effect = v["efeito"].IsNull ? NoEffect : Str(v["efeito"]);
            b.Effect = Array.Find(Effects, e => string.Equals(e, effect.Trim(), StringComparison.OrdinalIgnoreCase));
            if (b.Effect == null) { Bad($"efeito \"{effect}\" não existe"); b.Effect = NoEffect; }

            var a = d["atributos"].IsDocument ? d["atributos"].AsDocument : new BsonDocument();
            b.Level = (int)Num(a["nivel"], 1f, 1f, 3f, "nível", Bad);
            if (!a["nivel"].IsNull && a["nivel"].IsNumber && Math.Abs(a["nivel"].AsDouble - Math.Round(a["nivel"].AsDouble)) > 0.0001)
                Bad("nível precisa ser inteiro (1 a 3)");
            b.HealthMultiplier = Num(a["vidaMult"], 1f, 0.1f, 100f, "multiplicador de vida", Bad);
            b.HealthPerExtraPlayer = Num(a["vidaPorJogadorExtra"], 0.6f, 0f, 2f, "vida por jogador extra", Bad);
            b.Speed = Num(a["velocidade"], 1f, 0.3f, 2.5f, "velocidade", Bad);
            b.Turn = Num(a["giro"], 1f, 0.2f, 3f, "giro", Bad);
            b.Stagger = Num(a["atordoamento"], 1f, 0f, 10f, "atordoamento", Bad);
            b.PushResist = Num(a["resistEmpurrao"], 0f, 0f, 1f, "resistência a empurrão", Bad);
            b.Perception = Num(a["percepcao"], 40f, 5f, 150f, "percepção", Bad);
            b.FleeLowHealth = Bool(a["fogeComPoucaVida"], false);

            var r = d["resistencias"].IsDocument ? d["resistencias"].AsDocument : new BsonDocument();
            foreach (var key in DamageTypes)
            {
                string raw = r[key].IsNull ? "Normal" : Str(r[key]);
                string mod = CanonicalModifier(raw);
                if (mod == null) Bad($"resistência a {key} \"{raw}\" não existe");
                b.Resistances[key] = mod ?? "Normal";
            }
            foreach (var key in r.Keys)
                if (Array.IndexOf(DamageTypes, key) < 0) Bad($"tipo de dano \"{key}\" não existe");

            b.Attacks = ArenaBeastKit.ParseAttacks(d["ataques"], Bad, w => warnings?.Add($"{label}: {w}"));
            b.Abilities = ArenaBeastKit.ParseAbilities(d["habilidades"], Bad, w => warnings?.Add($"{label}: {w}"));

            if (problems == null) return local.Count > 0 ? null : b;
            return problems.Count > before ? null : b;
        }

        internal static string Str(BsonValue v) =>
            v == null || v.IsNull ? "" : v.IsString ? v.AsString : v.IsNumber ? v.AsDouble.ToString(Inv) : v.ToString();

        /// <summary>Shown text: no tabs or line breaks (the wire splits on them).</summary>
        private static string Text(BsonValue v) => ArenaWire.Clean(Str(v)).Trim();

        internal static bool Bool(BsonValue v, bool fallback) => v != null && v.IsBoolean ? v.AsBoolean : fallback;

        internal static float Num(BsonValue v, float fallback, float min, float max, string what, Action<string> bad)
        {
            if (v == null || v.IsNull) return fallback;
            if (!v.IsNumber)
            {
                bad($"{what} precisa ser um número");
                return fallback;
            }
            double n = v.AsDouble;
            if (double.IsNaN(n) || n < min - 1e-6 || n > max + 1e-6)
            {
                bad(string.Format(Inv, "{0} de {1:0.##} a {2:0.##}", what, min, max));
                return fallback;
            }
            return (float)n;
        }

        private static int[] Rgb(BsonValue v, int[] fallback, string what, Action<string> bad)
        {
            if (v == null || v.IsNull) return (int[])fallback.Clone();
            if (!v.IsArray || v.AsArray.Count != 3 || v.AsArray.Any(c => !c.IsNumber || c.AsDouble < 0 || c.AsDouble > 255))
            {
                bad($"{what} precisa ser [R, G, B] de 0 a 255");
                return (int[])fallback.Clone();
            }
            return v.AsArray.Select(c => (int)Math.Round(c.AsDouble)).ToArray();
        }

        // ------------------------------------------------------------------ file (server)

        internal static string FilePath => Persistence.NpcStoragePaths.BestiaryFile;

        /// <summary>Reads the file, writing the five examples first when there is none. A file
        /// that does not parse at all is kept aside (.invalido) rather than overwritten.</summary>
        internal static List<ArenaBeastDef> LoadOrSeed(string path, List<string> problems)
        {
            if (!File.Exists(path))
            {
                var seeded = Parse(SeedJson, problems) ?? new List<ArenaBeastDef>();
                Save(path, seeded);
                return seeded;
            }
            var loaded = Parse(File.ReadAllText(path, Encoding.UTF8), problems);
            if (loaded == null)
            {
                string aside = path + ".invalido";
                File.Copy(path, aside, overwrite: true);
                problems?.Add("cópia guardada em " + aside);
                return new List<ArenaBeastDef>();
            }
            return loaded;
        }

        /// <summary>Written to a temp file and moved over, so a crash mid-write leaves the old one.</summary>
        internal static void Save(string path, IEnumerable<ArenaBeastDef> beasts)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(beasts, indent: true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        // ------------------------------------------------------------------ examples

        /// <summary>The five creatures of the prototype (docs/bestiario-arena.html). The attacks name
        /// the items of the bases as the game's asset list has them (troll_punch, Wolf_Attack1...);
        /// one the base turns out not to carry is dropped with a warning when the file loads.</summary>
        internal const string SeedJson = @"{ ""versao"": 1, ""criaturas"": [
{ ""id"": ""troll_anciao"", ""base"": ""Troll"", ""nome"": ""Troll Ancião"", ""titulo"": ""Guardião da Floresta Negra"", ""barraChefe"": true,
  ""visual"": { ""escala"": 1.5, ""tingimento"": [95, 122, 74], ""tingimentoForca"": 0.45, ""brilho"": [0, 0, 0], ""brilhoForca"": 0, ""efeito"": ""Nenhum"" },
  ""atributos"": { ""nivel"": 3, ""vidaMult"": 2.5, ""vidaPorJogadorExtra"": 0.6, ""velocidade"": 1, ""giro"": 0.8, ""atordoamento"": 2, ""resistEmpurrao"": 0.7, ""percepcao"": 40, ""fogeComPoucaVida"": false },
  ""resistencias"": { ""blunt"": ""Resistente"", ""slash"": ""Normal"", ""pierce"": ""Muito resistente"", ""fire"": ""Fraco"", ""frost"": ""Normal"", ""lightning"": ""Normal"", ""poison"": ""Normal"", ""spirit"": ""Levemente fraco"" },
  ""ataques"": [
    { ""item"": ""troll_punch"", ""dano"": { ""blunt"": 85 }, ""alcance"": 3.5, ""intervalo"": 2, ""empurrao"": 120, ""efeito"": ""Nenhum"" },
    { ""item"": ""troll_throw"", ""dano"": { ""blunt"": 70 }, ""alcance"": 25, ""intervalo"": 9, ""empurrao"": 60, ""efeito"": ""Nenhum"" } ],
  ""habilidades"": [
    { ""tipo"": ""stomp"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 14, ""dano"": 60, ""raio"": 6, ""empurrao"": 80, ""preparo"": 1.2 },
    { ""tipo"": ""summon"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 60, ""prefab"": ""Greydwarf_Elite"", ""qtd"": 2, ""nivel"": 1, ""maxVivos"": 4 },
    { ""tipo"": ""enrage"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 30, ""velocidade"": 1.35, ""dano"": 1.25 } ] },
{ ""id"": ""senhor_draugr"", ""base"": ""Draugr_Elite"", ""nome"": ""Senhor Draugr em Chamas"", ""titulo"": ""O Juramento que Não Apagou"", ""barraChefe"": true,
  ""visual"": { ""escala"": 1.35, ""tingimento"": [58, 42, 36], ""tingimentoForca"": 0.6, ""brilho"": [255, 106, 26], ""brilhoForca"": 0.7, ""efeito"": ""Chamas"" },
  ""atributos"": { ""nivel"": 3, ""vidaMult"": 6, ""vidaPorJogadorExtra"": 0.6, ""velocidade"": 1.15, ""giro"": 1.2, ""atordoamento"": 1.5, ""resistEmpurrao"": 0.4, ""percepcao"": 40, ""fogeComPoucaVida"": false },
  ""resistencias"": { ""blunt"": ""Normal"", ""slash"": ""Normal"", ""pierce"": ""Resistente"", ""fire"": ""Imune"", ""frost"": ""Fraco"", ""lightning"": ""Normal"", ""poison"": ""Normal"", ""spirit"": ""Muito fraco"" },
  ""ataques"": [
    { ""item"": ""draugr_sword"", ""dano"": { ""slash"": 45, ""fire"": 40 }, ""alcance"": 3, ""intervalo"": 1.6, ""empurrao"": 50, ""efeito"": ""Queimando"" },
    { ""item"": ""draugr_axe"", ""dano"": { ""slash"": 45, ""fire"": 40 }, ""alcance"": 3, ""intervalo"": 1.6, ""empurrao"": 50, ""efeito"": ""Queimando"" } ],
  ""habilidades"": [
    { ""tipo"": ""aura"", ""ligada"": true, ""gatilho"": ""start"", ""valor"": 0, ""tipoDano"": ""fire"", ""dps"": 5, ""raio"": 3, ""lentidao"": 0 },
    { ""tipo"": ""zone"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 12, ""tipoDano"": ""fire"", ""dps"": 15, ""raio"": 3, ""duracao"": 8, ""aviso"": 1.5 },
    { ""tipo"": ""blink"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 18, ""alcance"": 15 },
    { ""tipo"": ""summon"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 50, ""prefab"": ""Draugr"", ""qtd"": 2, ""nivel"": 2, ""maxVivos"": 4 } ] },
{ ""id"": ""lobo_alfa"", ""base"": ""Wolf"", ""nome"": ""Lobo Alfa do Gelo"", ""titulo"": ""Líder da Matilha da Montanha"", ""barraChefe"": true,
  ""visual"": { ""escala"": 1.9, ""tingimento"": [217, 228, 238], ""tingimentoForca"": 0.55, ""brilho"": [127, 200, 255], ""brilhoForca"": 0.4, ""efeito"": ""Névoa gelada"" },
  ""atributos"": { ""nivel"": 3, ""vidaMult"": 10, ""vidaPorJogadorExtra"": 0.6, ""velocidade"": 1.1, ""giro"": 1, ""atordoamento"": 1.3, ""resistEmpurrao"": 0.3, ""percepcao"": 50, ""fogeComPoucaVida"": false },
  ""resistencias"": { ""blunt"": ""Normal"", ""slash"": ""Levemente resistente"", ""pierce"": ""Normal"", ""fire"": ""Fraco"", ""frost"": ""Imune"", ""lightning"": ""Normal"", ""poison"": ""Normal"", ""spirit"": ""Normal"" },
  ""ataques"": [
    { ""item"": ""Wolf_Attack1"", ""dano"": { ""slash"": 40, ""frost"": 30 }, ""alcance"": 2.5, ""intervalo"": 1.2, ""empurrao"": 30, ""efeito"": ""Congelado (lento)"" } ],
  ""habilidades"": [
    { ""tipo"": ""summon"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 40, ""prefab"": ""Wolf"", ""qtd"": 3, ""nivel"": 2, ""maxVivos"": 4 },
    { ""tipo"": ""charge"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 20, ""dano"": 45, ""distancia"": 18, ""preparo"": 1 },
    { ""tipo"": ""roar"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 35, ""raio"": 10, ""lentidao"": 40, ""duracao"": 3 },
    { ""tipo"": ""enrage"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 25, ""velocidade"": 1.25, ""dano"": 1.3 } ] },
{ ""id"": ""abominacao_ancestral"", ""base"": ""Abomination"", ""nome"": ""Abominação Ancestral"", ""titulo"": ""A Raiz que Apodrece"", ""barraChefe"": true,
  ""visual"": { ""escala"": 1.25, ""tingimento"": [60, 74, 46], ""tingimentoForca"": 0.5, ""brilho"": [143, 209, 79], ""brilhoForca"": 0.25, ""efeito"": ""Gotas de veneno"" },
  ""atributos"": { ""nivel"": 2, ""vidaMult"": 2, ""vidaPorJogadorExtra"": 0.6, ""velocidade"": 1, ""giro"": 0.7, ""atordoamento"": 2.5, ""resistEmpurrao"": 0.9, ""percepcao"": 40, ""fogeComPoucaVida"": false },
  ""resistencias"": { ""blunt"": ""Resistente"", ""slash"": ""Levemente fraco"", ""pierce"": ""Normal"", ""fire"": ""Muito fraco"", ""frost"": ""Normal"", ""lightning"": ""Normal"", ""poison"": ""Imune"", ""spirit"": ""Normal"" },
  ""ataques"": [
    { ""item"": ""Abomination_attack1"", ""dano"": { ""blunt"": 90, ""poison"": 30 }, ""alcance"": 4, ""intervalo"": 2.2, ""empurrao"": 140, ""efeito"": ""Envenenado"" } ],
  ""habilidades"": [
    { ""tipo"": ""regen"", ""ligada"": true, ""gatilho"": ""start"", ""valor"": 0, ""semDano"": 6, ""porSeg"": 1.5 },
    { ""tipo"": ""zone"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 12, ""tipoDano"": ""poison"", ""dps"": 12, ""raio"": 3.5, ""duracao"": 10, ""aviso"": 1.5 },
    { ""tipo"": ""summon"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 50, ""prefab"": ""BlobElite"", ""qtd"": 3, ""nivel"": 1, ""maxVivos"": 6 } ] },
{ ""id"": ""golem_runico"", ""base"": ""StoneGolem"", ""nome"": ""Golem Rúnico"", ""titulo"": ""Sentinela das Montanhas"", ""barraChefe"": true,
  ""visual"": { ""escala"": 1.15, ""tingimento"": [108, 112, 120], ""tingimentoForca"": 0.3, ""brilho"": [79, 224, 208], ""brilhoForca"": 0.85, ""efeito"": ""Runas brilhando"" },
  ""atributos"": { ""nivel"": 2, ""vidaMult"": 1.5, ""vidaPorJogadorExtra"": 0.6, ""velocidade"": 0.95, ""giro"": 0.6, ""atordoamento"": 4, ""resistEmpurrao"": 1, ""percepcao"": 45, ""fogeComPoucaVida"": false },
  ""resistencias"": { ""blunt"": ""Fraco"", ""slash"": ""Muito resistente"", ""pierce"": ""Imune"", ""fire"": ""Normal"", ""frost"": ""Resistente"", ""lightning"": ""Fraco"", ""poison"": ""Normal"", ""spirit"": ""Normal"" },
  ""ataques"": [
    { ""item"": ""stonegolem_attack2_left_groundslam"", ""dano"": { ""blunt"": 110 }, ""alcance"": 4, ""intervalo"": 2.5, ""empurrao"": 160, ""efeito"": ""Nenhum"" } ],
  ""habilidades"": [
    { ""tipo"": ""shield"", ""ligada"": true, ""gatilho"": ""hp"", ""valor"": 50, ""totens"": 3, ""vidaTotem"": 400 },
    { ""tipo"": ""rain"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 25, ""projeteis"": 8, ""dano"": 40, ""raio"": 12, ""tipoDano"": ""blunt"" },
    { ""tipo"": ""leap"", ""ligada"": true, ""gatilho"": ""every"", ""valor"": 22, ""dano"": 70, ""raio"": 4, ""alcance"": 20 },
    { ""tipo"": ""timerage"", ""ligada"": true, ""gatilho"": ""time"", ""valor"": 300, ""aumento"": 10 } ] }
] }";
    }
}
