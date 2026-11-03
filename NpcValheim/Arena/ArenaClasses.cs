using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LiteDB;

namespace NpcValheim.Arena
{
    /// <summary>One piece of a class kit: the item's prefab and its quality (level).</summary>
    internal sealed class ArenaKitItem
    {
        public string Prefab = "";
        public int Quality = 1;

        public ArenaKitItem Clone() => (ArenaKitItem)MemberwiseClone();

        /// <summary>"SwordIron:3" (the quality may be left out: 1).</summary>
        internal static ArenaKitItem Parse(string text)
        {
            text = ArenaWire.Clean(text ?? "").Trim();
            if (text.Length == 0) return null;
            var item = new ArenaKitItem();
            int colon = text.IndexOf(':');
            item.Prefab = (colon < 0 ? text : text.Substring(0, colon)).Trim();
            if (colon >= 0 && int.TryParse(text.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int q))
                item.Quality = q;
            item.Quality = Math.Max(1, Math.Min(ArenaClasses.MaxQuality, item.Quality));
            return item.Prefab.Length == 0 ? null : item;
        }

        public override string ToString() => Quality > 1 ? Prefab + ":" + Quality.ToString(CultureInfo.InvariantCulture) : Prefab;
    }

    /// <summary>
    /// A class of the Arena balanceada: the armour every player of it wears (fixed), the belt of
    /// up to 8 weapons, shields and ammunition they switch between with the keys 1 to 8, the
    /// health, stamina and eitr that stand in for food, and the skill levels that stand in for
    /// their own. Nothing of it is ever a real item: the client puts these pieces straight into
    /// the character's equipment fields for the match (ArenaKit).
    /// </summary>
    internal sealed class ArenaClassDef
    {
        public string Id = "";
        public bool On = true;
        public string Name = "";
        public string Description = "";
        public ArenaKitItem Chest;
        public ArenaKitItem Legs;
        public ArenaKitItem Helmet;
        public ArenaKitItem Cape;
        public ArenaKitItem Utility;
        public List<ArenaKitItem> Belt = new List<ArenaKitItem>();
        /// <summary>Prefabs of the belt that come equipped when the match starts.</summary>
        public List<string> Start = new List<string>();
        public float Health = 150f;
        public float Stamina = 150f;
        public float Eitr;
        /// <summary>Skill name (Skills.SkillType) -> level. Any other skill is at <see cref="SkillLevel"/>.</summary>
        public Dictionary<string, int> Skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int SkillLevel = 20;

        public IEnumerable<(string Slot, ArenaKitItem Item)> Armor()
        {
            yield return ("peito", Chest);
            yield return ("pernas", Legs);
            yield return ("cabeca", Helmet);
            yield return ("capa", Cape);
            yield return ("utilitario", Utility);
        }

        public int SkillOf(string skill) => skill != null && Skills.TryGetValue(skill, out int v) ? v : SkillLevel;

        public string SkillsText =>
            string.Join(",", Skills.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));

        public ArenaClassDef Clone()
        {
            var copy = (ArenaClassDef)MemberwiseClone();
            copy.Chest = Chest?.Clone();
            copy.Legs = Legs?.Clone();
            copy.Helmet = Helmet?.Clone();
            copy.Cape = Cape?.Clone();
            copy.Utility = Utility?.Clone();
            copy.Belt = Belt.Select(b => b.Clone()).ToList();
            copy.Start = new List<string>(Start);
            copy.Skills = new Dictionary<string, int>(Skills, StringComparer.OrdinalIgnoreCase);
            return copy;
        }
    }

    /// <summary>What a piece of the kit is, for the checks: the slot it goes to.</summary>
    internal enum ArenaKitSlot { Unknown, Weapon, Shield, Ammo, Chest, Legs, Helmet, Cape, Utility, Other }

    /// <summary>What a player wears, as their ZDO shows it to the server (VisEquipment): the
    /// prefab of each slot ("" = nothing) and the quality where the game publishes one.</summary>
    internal sealed class ArenaWornKit
    {
        public string Right = "", Left = "", RightBack = "", LeftBack = "";
        public int RightQuality, LeftQuality, RightBackQuality, LeftBackQuality;
        public string Chest = "", Legs = "", Helmet = "", Cape = "", Utility = "", Trinket = "";
        public int CapeQuality;
    }

    /// <summary>
    /// The classes of the Arena balanceada: config/NpcValheim/arena-classes.json on the server,
    /// sent to every client the way the arenas are (hash on the hello, the JSON when it
    /// differs and after every change). The admin edits them in F8 > Arena > Balanceada.
    /// </summary>
    internal static class ArenaClasses
    {
        internal const int MaxBelt = 8;
        internal const int MaxQuality = 10;
        internal const int MaxSkill = 100;
        internal const float MinVital = 25f;
        internal const float MaxVital = 1000f;

        private static List<ArenaClassDef> _all = new List<ArenaClassDef>();

        internal static List<ArenaClassDef> All => _all;

        /// <summary>Bumped on every change, for the pages to redraw.</summary>
        internal static int Revision { get; private set; }

        /// <summary>The server once it read the file; a client once the server sent it.</summary>
        internal static bool Loaded { get; private set; }

        internal static string Hash { get; private set; } = "";

        internal static ArenaClassDef Find(string id) =>
            string.IsNullOrEmpty(id) ? null : _all.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

        internal static void Replace(List<ArenaClassDef> classes, string hash = null)
        {
            _all = classes ?? new List<ArenaClassDef>();
            Hash = hash ?? ArenaBestiary.HashOf(ToJson(_all));
            Loaded = true;
            Revision++;
        }

        internal static void Forget()
        {
            _all = new List<ArenaClassDef>();
            Hash = "";
            Loaded = false;
            Revision++;
        }

        // ------------------------------------------------------------------ json

        internal static BsonDocument ToDoc(ArenaClassDef c) => new BsonDocument
        {
            ["id"] = c.Id ?? "",
            ["ativa"] = c.On,
            ["nome"] = c.Name ?? "",
            ["descricao"] = c.Description ?? "",
            ["armadura"] = new BsonDocument(c.Armor().ToDictionary(a => a.Slot, a => (BsonValue)(a.Item?.ToString() ?? ""))),
            ["cinto"] = new BsonArray(c.Belt.Select(b => (BsonValue)b.ToString())),
            ["comeca"] = new BsonArray(c.Start.Select(s => (BsonValue)s)),
            ["vida"] = ArenaBestiary.N(c.Health),
            ["vigor"] = ArenaBestiary.N(c.Stamina),
            ["eitr"] = ArenaBestiary.N(c.Eitr),
            ["skills"] = c.SkillsText,
            ["skillPadrao"] = c.SkillLevel,
        };

        internal static string ToJson(IEnumerable<ArenaClassDef> classes, bool indent = false) =>
            JsonSerializer.Serialize(new BsonDocument
            {
                ["versao"] = 1,
                ["classes"] = new BsonArray(classes.Select(c => (BsonValue)ToDoc(c))),
            }, indent);

        /// <summary>One class, as the admin page sends it to the server.</summary>
        internal static string ToJsonOne(ArenaClassDef c) => JsonSerializer.Serialize(ToDoc(c), false);

        /// <summary>The whole file. A class with a problem in its own text (no id, a repeated
        /// one) is left out and named; false when the text is not the file at all.</summary>
        internal static bool Parse(string json, List<string> problems, out List<ArenaClassDef> classes)
        {
            classes = new List<ArenaClassDef>();
            BsonValue root;
            try { root = JsonSerializer.Deserialize(json ?? ""); }
            catch (Exception e)
            {
                problems?.Add("o arquivo não é um JSON válido: " + e.Message);
                return false;
            }
            if (!root.IsDocument || !root.AsDocument["classes"].IsArray)
            {
                problems?.Add("o arquivo precisa ser um objeto com \"classes\"");
                return false;
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var v in root.AsDocument["classes"].AsArray)
            {
                i++;
                if (!v.IsDocument) { problems?.Add($"classe {i}: não é um objeto"); continue; }
                var c = FromDoc(v.AsDocument);
                if (!ArenaLayout.ValidId(c.Id)) c.Id = ArenaLayout.NewId(c.Name.Length > 0 ? c.Name : "classe", ids.Concat(classes.Select(x => x.Id)));
                if (!ids.Add(c.Id))
                {
                    problems?.Add($"classe \"{c.Id}\": id repetido, a segunda foi ignorada");
                    continue;
                }
                classes.Add(c);
            }
            return true;
        }

        /// <summary>One class from the admin page; null (and why) when it is not one.</summary>
        internal static ArenaClassDef ParseOne(string json, out string problem)
        {
            problem = null;
            try
            {
                var root = JsonSerializer.Deserialize(json ?? "");
                if (root.IsDocument) return FromDoc(root.AsDocument);
            }
            catch (Exception e)
            {
                problem = "classe malformada: " + e.Message;
                return null;
            }
            problem = "classe malformada";
            return null;
        }

        private static ArenaClassDef FromDoc(BsonDocument d)
        {
            var c = new ArenaClassDef
            {
                Id = ArenaBestiary.Str(d["id"]).Trim().ToLowerInvariant(),
                On = ArenaBestiary.Bool(d["ativa"], true),
                Name = ArenaSettingsParser.CleanMapName(ArenaBestiary.Str(d["nome"])),
                Description = ArenaWire.Clean(ArenaBestiary.Str(d["descricao"])).Trim(),
                Health = Real(d["vida"], 150f, MinVital, MaxVital),
                Stamina = Real(d["vigor"], 150f, MinVital, MaxVital),
                Eitr = Real(d["eitr"], 0f, 0f, MaxVital),
                SkillLevel = Whole(d["skillPadrao"], 20, 0, MaxSkill),
            };
            if (c.Description.Length > 300) c.Description = c.Description.Substring(0, 300);
            if (d["armadura"].IsDocument)
            {
                var a = d["armadura"].AsDocument;
                c.Chest = ArenaKitItem.Parse(ArenaBestiary.Str(a["peito"]));
                c.Legs = ArenaKitItem.Parse(ArenaBestiary.Str(a["pernas"]));
                c.Helmet = ArenaKitItem.Parse(ArenaBestiary.Str(a["cabeca"]));
                c.Cape = ArenaKitItem.Parse(ArenaBestiary.Str(a["capa"]));
                c.Utility = ArenaKitItem.Parse(ArenaBestiary.Str(a["utilitario"]));
            }
            if (d["cinto"].IsArray)
                foreach (var b in d["cinto"].AsArray)
                {
                    var item = ArenaKitItem.Parse(ArenaBestiary.Str(b));
                    if (item != null && c.Belt.Count < MaxBelt) c.Belt.Add(item);
                }
            if (d["comeca"].IsArray)
                foreach (var s in d["comeca"].AsArray)
                {
                    string prefab = ArenaWire.Clean(ArenaBestiary.Str(s)).Trim();
                    if (prefab.Length > 0 && !c.Start.Contains(prefab, StringComparer.OrdinalIgnoreCase)) c.Start.Add(prefab);
                }
            c.Skills = ParseSkills(ArenaBestiary.Str(d["skills"]));
            return c;
        }

        /// <summary>"Swords:60,Blocking:40": a skill without a valid level is left out.</summary>
        internal static Dictionary<string, int> ParseSkills(string text)
        {
            var skills = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in (text ?? "").Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = part.IndexOf(':');
                if (colon <= 0) continue;
                string name = part.Substring(0, colon).Trim();
                if (name.Length == 0 || !int.TryParse(part.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int level)) continue;
                skills[name] = Math.Max(0, Math.Min(MaxSkill, level));
            }
            return skills;
        }

        private static int Whole(BsonValue v, int fallback, int min, int max) =>
            v != null && v.IsNumber ? Math.Max(min, Math.Min(max, (int)Math.Round(v.AsDouble))) : fallback;

        private static float Real(BsonValue v, float fallback, float min, float max) =>
            v != null && v.IsNumber && !double.IsNaN(v.AsDouble) ? (float)Math.Max(min, Math.Min(max, v.AsDouble)) : fallback;

        // ------------------------------------------------------------------ the classes a server starts with

        private static ArenaKitItem K(string prefab, int quality = 1) => new ArenaKitItem { Prefab = prefab, Quality = quality };

        private static List<ArenaKitItem> Belt(params ArenaKitItem[] items) => items.ToList();

        /// <summary>
        /// Mistlands is the progression baseline for the balanced arena classes. Their weapon
        /// damage is intentionally different; class balance comes from trade-offs in defence,
        /// reach, mobility, stamina and eitr, not from a shared DPS target.
        /// </summary>
        internal static List<ArenaClassDef> Defaults() => new List<ArenaClassDef>
        {
            new ArenaClassDef
            {
                Id = "guerreiro", Name = "Guerreiro",
                Description = "Linha de frente com armadura de carapaça e escudo. Mistwalker e Demolisher oferecem golpes diferentes; arco e flecha são uma resposta situacional, sem transformar a classe em arqueiro.",
                Chest = K("ArmorCarapaceChest", 3), Legs = K("ArmorCarapaceLegs", 3), Helmet = K("HelmetCarapace", 3), Cape = K("CapeFeather", 3),
                Belt = Belt(K("SwordMistwalker", 3), K("MaceDemolisher", 3), K("ShieldCarapace", 3), K("BowSpineSnap", 3), K("ArrowCarapace")),
                Start = { "SwordMistwalker", "ShieldCarapace", "ArrowCarapace" },
                Health = 180f, Stamina = 130f, Eitr = 0f, SkillLevel = 20,
                Skills = ParseSkills("Blocking:60,Swords:35,Clubs:35,Bows:20,Dodge:20,Run:10"),
            },
            new ArenaClassDef
            {
                Id = "berserker", Name = "Berserker / Lanceiro",
                Description = "Pressão corpo a corpo e controle de espaço: Himminafl e lança dão alcance, enquanto Krom e Jotun Bane mudam o tipo de aproximação. Sem escudo, depende de vigor e posicionamento.",
                Chest = K("ArmorCarapaceChest", 3), Legs = K("ArmorCarapaceLegs", 3), Helmet = K("HelmetCarapace", 3), Cape = K("CapeFeather", 3),
                Belt = Belt(K("AtgeirHimminAfl", 3), K("SwordKrom", 3), K("SpearCarapace", 3), K("AxeJotunBane", 3)),
                Start = { "AtgeirHimminAfl" },
                Health = 210f, Stamina = 150f, Eitr = 0f, SkillLevel = 20,
                Skills = ParseSkills("Axes:50,Polearms:50,Spears:50,Dodge:30,Run:20"),
            },
            new ArenaClassDef
            {
                Id = "arqueiro", Name = "Arqueiro",
                Description = "Controle à distância para as arenas de Mistlands: Spinesnap e os tipos de flecha permitem adaptar o disparo à situação. A armadura leve prioriza mobilidade; espada, faca e broquel ajudam quando a distância fecha.",
                Chest = K("ArmorFenringChest", 3), Legs = K("ArmorFenringLegs", 3), Helmet = K("HelmetCarapace", 3), Cape = K("CapeFeather", 3),
                Belt = Belt(K("BowSpineSnap", 3), K("SwordMistwalker", 3), K("KnifeSkollAndHati", 3), K("ShieldCarapaceBuckler", 3),
                    K("ArrowCarapace"), K("ArrowFire"), K("ArrowFrost"), K("ArrowPoison")),
                Start = { "BowSpineSnap", "ArrowCarapace" },
                Health = 200f, Stamina = 160f, Eitr = 0f, SkillLevel = 20,
                Skills = ParseSkills("Bows:70,Dodge:40,Run:40,Swords:30,Knives:30,Blocking:30"),
            },
            new ArenaClassDef
            {
                Id = "mago", Name = "Mago",
                Description = "Controle e suporte: fogo atinge área, gelo interrompe avanços e a barreira protege. A armadura de eitr reforça esse papel, com menor proteção física; o eitr limita o ritmo dos feitiços.",
                Chest = K("ArmorMageChest", 3), Legs = K("ArmorMageLegs", 3), Helmet = K("HelmetMage", 3), Cape = K("CapeFeather", 3),
                Belt = Belt(K("StaffFireball", 3), K("StaffIceShards", 3), K("StaffShield", 3), K("KnifeSkollAndHati", 3)),
                Start = { "StaffFireball" },
                Health = 220f, Stamina = 110f, Eitr = 200f, SkillLevel = 20,
                Skills = ParseSkills("ElementalMagic:60,Knives:30,Dodge:30,Run:30,BloodMagic:20"),
            },
            new ArenaClassDef
            {
                Id = "assassino", Name = "Assassino",
                Description = "Mobilidade e aproximação: Skoll e Hati são o foco; Mistwalker é uma opção de maior alcance e o broquel ajuda na retirada. A armadura leve favorece esquiva, mas oferece menos segurança em trocas prolongadas.",
                Chest = K("ArmorFenringChest", 3), Legs = K("ArmorFenringLegs", 3), Helmet = K("HelmetCarapace", 3), Cape = K("CapeFeather", 3),
                Belt = Belt(K("KnifeSkollAndHati", 3), K("SwordMistwalker", 3), K("ShieldCarapaceBuckler", 3)),
                Start = { "KnifeSkollAndHati" },
                Health = 240f, Stamina = 180f, Eitr = 0f, SkillLevel = 20,
                Skills = ParseSkills("Dodge:60,Run:60,Sneak:60,Knives:55,Jump:40,Blocking:30"),
            },
        };

        // ------------------------------------------------------------------ checks

        /// <summary>
        /// What is wrong with a class, in the admin's words. <paramref name="slotOf"/> says what
        /// an item is (null = no world to ask: only the shape is checked); <paramref name="maxQuality"/>
        /// the item's top quality (0 = unknown).
        /// </summary>
        internal static List<string> Problems(ArenaClassDef c, Func<string, ArenaKitSlot> slotOf = null, Func<string, int> maxQuality = null,
            Func<string, bool> skillKnown = null)
        {
            var problems = new List<string>();
            if (c.Name.Trim().Length == 0) problems.Add("sem nome");
            if (c.Belt.Count == 0) problems.Add("o cinto está vazio: precisa de pelo menos uma arma");
            if (c.Belt.Count > MaxBelt) problems.Add($"o cinto tem {c.Belt.Count} itens: no máximo {MaxBelt}");
            foreach (var start in c.Start)
                if (!c.Belt.Any(b => Same(b.Prefab, start)))
                    problems.Add($"\"{start}\" começa equipado mas não está no cinto");
            foreach (var skill in c.Skills.Keys)
                if (skillKnown != null && !skillKnown(skill)) problems.Add($"skill \"{skill}\" não existe");
            if (slotOf == null) return problems;

            foreach (var b in c.Belt)
            {
                var slot = slotOf(b.Prefab);
                if (slot == ArenaKitSlot.Unknown) problems.Add($"cinto: \"{b.Prefab}\" não é um item do jogo");
                else if (slot != ArenaKitSlot.Weapon && slot != ArenaKitSlot.Shield && slot != ArenaKitSlot.Ammo)
                    problems.Add($"cinto: \"{b.Prefab}\" não é arma, escudo nem munição");
                CheckQuality(b, maxQuality, "cinto", problems);
            }
            var expected = new[] { ArenaKitSlot.Chest, ArenaKitSlot.Legs, ArenaKitSlot.Helmet, ArenaKitSlot.Cape, ArenaKitSlot.Utility };
            int k = 0;
            foreach (var (slotName, item) in c.Armor())
            {
                var want = expected[k++];
                if (item == null) continue;
                var slot = slotOf(item.Prefab);
                if (slot == ArenaKitSlot.Unknown) problems.Add($"{slotName}: \"{item.Prefab}\" não é um item do jogo");
                else if (slot != want) problems.Add($"{slotName}: \"{item.Prefab}\" não vai nesse lugar");
                CheckQuality(item, maxQuality, slotName, problems);
            }
            return problems;
        }

        private static void CheckQuality(ArenaKitItem item, Func<string, int> maxQuality, string where, List<string> problems)
        {
            int max = maxQuality?.Invoke(item.Prefab) ?? 0;
            if (max > 0 && item.Quality > max) problems.Add($"{where}: \"{item.Prefab}\" vai só até o nível {max}");
        }

        private static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether what a player wears is this class's kit, as the server sees it on the ZDO:
        /// in the hands and on the back only items of the belt, at the belt's quality; the
        /// class's armour piece by piece; no trinket. Null when it matches, the first difference
        /// in words when it does not.
        /// </summary>
        internal static string Mismatch(ArenaClassDef c, ArenaWornKit worn)
        {
            if (c == null || worn == null) return "sem classe";
            string hand = Hand(c, worn.Right, worn.RightQuality, "mão direita") ?? Hand(c, worn.Left, worn.LeftQuality, "mão esquerda") ??
                          Hand(c, worn.RightBack, worn.RightBackQuality, "costas") ?? Hand(c, worn.LeftBack, worn.LeftBackQuality, "costas");
            if (hand != null) return hand;
            string armor = Piece(c.Chest, worn.Chest, "peito") ?? Piece(c.Legs, worn.Legs, "pernas") ?? Piece(c.Helmet, worn.Helmet, "cabeça") ??
                           Piece(c.Cape, worn.Cape, "capa") ?? Piece(c.Utility, worn.Utility, "utilitário");
            if (armor != null) return armor;
            if (c.Cape != null && worn.CapeQuality > 0 && worn.CapeQuality != c.Cape.Quality)
                return $"capa no nível {worn.CapeQuality}, a classe usa {c.Cape.Quality}";
            if (!string.IsNullOrEmpty(worn.Trinket)) return $"bugiganga \"{worn.Trinket}\" fora do kit";
            return null;
        }

        private static string Hand(ArenaClassDef c, string prefab, int quality, string where)
        {
            if (string.IsNullOrEmpty(prefab)) return null;
            var matches = c.Belt.Where(b => Same(b.Prefab, prefab)).ToList();
            if (matches.Count == 0) return $"{where}: \"{prefab}\" não é do cinto da classe";
            if (quality > 0 && !matches.Any(b => b.Quality == quality)) return $"{where}: \"{prefab}\" no nível {quality}, a classe usa {matches[0].Quality}";
            return null;
        }

        private static string Piece(ArenaKitItem want, string worn, string where)
        {
            string expected = want?.Prefab ?? "";
            if (Same(expected, worn)) return null;
            if (expected.Length == 0) return $"{where}: \"{worn}\" fora do kit";
            return string.IsNullOrEmpty(worn) ? $"{where}: sem \"{expected}\"" : $"{where}: \"{worn}\" em vez de \"{expected}\"";
        }

        // ------------------------------------------------------------------ file (server)

        internal static string FilePath => Persistence.NpcStoragePaths.ArenaClassesFile;

        /// <summary>Reads the file, or writes the five classes of a new server. A file that does
        /// not parse is kept aside (.invalido) and the balanced arena starts with no class.</summary>
        internal static List<ArenaClassDef> LoadOrSeed(string path, List<string> problems)
        {
            if (!File.Exists(path))
            {
                var seeded = Defaults();
                Save(path, seeded);
                return seeded;
            }
            if (Parse(File.ReadAllText(path, Encoding.UTF8), problems, out var classes)) return classes;
            string aside = path + ".invalido";
            File.Copy(path, aside, overwrite: true);
            problems?.Add("cópia guardada em " + aside);
            return classes;
        }

        internal static void Save(string path, IEnumerable<ArenaClassDef> classes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, ToJson(classes, indent: true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
    }
}
