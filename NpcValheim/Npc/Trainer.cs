using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Integration;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>What one thing costs at the Treinador. 0 in a currency means it is not sold that way.</summary>
    public sealed class TrainerPrice
    {
        public int Gold;
        public int Deadcoins;
    }

    /// <summary>The price of a lesson whose target level (the level after it) is From..To.</summary>
    public sealed class TrainerLessonBand
    {
        public int From;
        public int To;
        public int Gold;
        public int Deadcoins;
    }

    /// <summary>The Treinador's price list, as [Trainer] Prices has it.</summary>
    public sealed class TrainerPrices
    {
        public TrainerPrice Study;
        public TrainerPrice Protection;
        public List<TrainerLessonBand> Lessons = new List<TrainerLessonBand>();

        /// <summary>The band a lesson to this level is priced by, or null when it is not sold.</summary>
        public TrainerLessonBand LessonTo(int target) => Lessons.Find(b => target >= b.From && target <= b.To);
    }

    /// <summary>One step of [Trainer] LessonCaps: holding this boss's pass (or none at all,
    /// when Boss is null) lets lessons go up to Level.</summary>
    public sealed class TrainerCap
    {
        public string Boss;
        public int Level;
    }

    /// <summary>The numbers of [Trainer] that are not lists, clamped to what makes sense. A
    /// snapshot: the server takes one per request, so a cfg saved mid-purchase cannot mix two.</summary>
    public sealed class TrainerSettings
    {
        public float StudyBonus;
        public int StudyMinutes;
        public int StudyMaxSkill;
        public float ProtectionFactor;
        public bool ProtectionPveOnly;
        public int LessonMaxLevel;
        public int LessonsPerWeek;

        internal static TrainerSettings Current() => new TrainerSettings
        {
            StudyBonus = Clamp(Plugin.TrainerStudyBonus?.Value ?? 0.5f, 0f, 10f),
            StudyMinutes = Math.Max(0, Math.Min(24 * 60, Plugin.TrainerStudyMinutes?.Value ?? 60)),
            StudyMaxSkill = Math.Max(0, Math.Min(100, Plugin.TrainerStudyMaxSkill?.Value ?? 50)),
            ProtectionFactor = Clamp(Plugin.TrainerProtectionFactor?.Value ?? 0.5f, 0f, 1f),
            ProtectionPveOnly = Plugin.TrainerProtectionPveOnly?.Value ?? true,
            LessonMaxLevel = Math.Max(0, Math.Min(100, Plugin.TrainerLessonMaxLevel?.Value ?? 50)),
            LessonsPerWeek = Math.Max(0, Plugin.TrainerLessonsPerWeek?.Value ?? 5),
        };

        private static float Clamp(float value, float min, float max) =>
            float.IsNaN(value) || float.IsInfinity(value) ? min : Math.Max(min, Math.Min(max, value));
    }

    /// <summary>
    /// [Trainer] Prices and LessonCaps, read as text. Pure, so the wire check runs it outside
    /// the game.
    ///
    /// Prices has the shape of BossPass.Bosses (key=value;... entries separated by |), one
    /// entry per thing sold: item=study, item=protection, and item=lesson;levels=A-B once per
    /// band of target levels. A negative price is refused rather than clamped, as in the other
    /// lists; 0 means "not sold that way", and an entry with both at 0 is not sold at all.
    /// </summary>
    internal static class TrainerCatalog
    {
        internal const string ItemStudy = "study";
        internal const string ItemProtection = "protection";
        internal const string ItemLesson = "lesson";

        /// <summary>A proposal, not a decision: about 20 Coins per Deadcoin, as the Mestre das
        /// Montarias' table, with lessons dearer in each band the boss passes open.</summary>
        internal const string DefaultPrices =
            "item=study;gold=1000;deadcoins=50|" +
            "item=protection;gold=1000;deadcoins=50|" +
            "item=lesson;levels=1-15;gold=300;deadcoins=15|" +
            "item=lesson;levels=16-25;gold=600;deadcoins=30|" +
            "item=lesson;levels=26-35;gold=1000;deadcoins=50|" +
            "item=lesson;levels=36-45;gold=1600;deadcoins=80|" +
            "item=lesson;levels=46-50;gold=2400;deadcoins=120";

        internal const string DefaultCaps = "none:15|gd_king:25|Bonemass:35|Dragon:45|GoblinKing:50";

        /// <summary>The cap key that needs no pass.</summary>
        internal const string NoPass = "none";

        internal static TrainerPrices ParsePrices(string raw, List<string> problems)
        {
            var result = new TrainerPrices();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            foreach (var entry in raw.Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                string item = null, levels = null;
                int gold = 0, deadcoins = 0;
                bool badNumber = false;
                foreach (var field in text.Split(';'))
                {
                    int equals = field.IndexOf('=');
                    if (equals <= 0) continue;
                    var key = field.Substring(0, equals).Trim();
                    var value = field.Substring(equals + 1).Trim();

                    if (Is(key, "item")) item = value.ToLowerInvariant();
                    else if (Is(key, "levels")) levels = value;
                    else if (Is(key, "gold")) badNumber |= !TryInt(value, out gold);
                    else if (Is(key, "deadcoins")) badNumber |= !TryInt(value, out deadcoins);
                }

                if (item != ItemStudy && item != ItemProtection && item != ItemLesson)
                {
                    problems?.Add($"\"{text}\": item must be study, protection or lesson");
                    continue;
                }
                if (badNumber || gold < 0 || deadcoins < 0)
                {
                    problems?.Add($"\"{text}\": gold and deadcoins must be whole numbers, 0 or more");
                    continue;
                }
                if (gold == 0 && deadcoins == 0)
                {
                    problems?.Add($"\"{text}\" has no price, so it is not sold");
                    continue;
                }

                if (item == ItemLesson)
                {
                    if (!TryLevels(levels, out int from, out int to))
                    {
                        problems?.Add($"\"{text}\": levels must be A-B (or one level), from 1 to 100");
                        continue;
                    }
                    var overlap = result.Lessons.Find(b => from <= b.To && to >= b.From);
                    if (overlap != null)
                    {
                        problems?.Add($"lesson levels {from}-{to} overlap {overlap.From}-{overlap.To}; the first entry wins");
                        continue;
                    }
                    result.Lessons.Add(new TrainerLessonBand { From = from, To = to, Gold = gold, Deadcoins = deadcoins });
                    continue;
                }

                var price = new TrainerPrice { Gold = gold, Deadcoins = deadcoins };
                if (item == ItemStudy)
                {
                    if (result.Study != null) problems?.Add("study is listed more than once; the first entry wins");
                    else result.Study = price;
                }
                else
                {
                    if (result.Protection != null) problems?.Add("protection is listed more than once; the first entry wins");
                    else result.Protection = price;
                }
            }
            result.Lessons.Sort((a, b) => a.From.CompareTo(b.From));
            return result;
        }

        /// <summary>"15" or "16-25", inside 1..100.</summary>
        private static bool TryLevels(string text, out int from, out int to)
        {
            from = to = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            int dash = text.IndexOf('-');
            if (dash < 0)
            {
                if (!TryInt(text.Trim(), out from)) return false;
                to = from;
            }
            else if (!TryInt(text.Substring(0, dash).Trim(), out from) || !TryInt(text.Substring(dash + 1).Trim(), out to))
                return false;
            return from >= 1 && to <= 100 && from <= to;
        }

        /// <summary>"none:15|gd_king:25|...": every well-formed step, in the order written.</summary>
        internal static List<TrainerCap> ParseCaps(string raw, List<string> problems)
        {
            var result = new List<TrainerCap>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in raw.Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;
                int colon = text.IndexOf(':');
                string key = colon > 0 ? text.Substring(0, colon).Trim() : "";
                if (colon <= 0 || !TryInt(text.Substring(colon + 1).Trim(), out int level) || level < 0 || level > 100)
                {
                    problems?.Add($"\"{text}\" is not <boss or none>:<level 0-100>");
                    continue;
                }

                bool none = key.Equals(NoPass, StringComparison.OrdinalIgnoreCase);
                if (!none && !BossPassCatalog.IsCleanId(key))
                {
                    problems?.Add($"\"{key}\" is not a boss prefab name");
                    continue;
                }
                string id = none ? NoPass : key;
                if (!seen.Add(id))
                {
                    problems?.Add($"{id} is listed more than once; the first entry wins");
                    continue;
                }
                result.Add(new TrainerCap { Boss = none ? null : key, Level = level });
            }
            return result;
        }

        /// <summary>
        /// How high lessons go for someone holding `passes`: the best step they qualify for,
        /// never above `maxLevel`. `from` is the boss whose pass gave it, or null for the step
        /// with no pass. No step at all means no lessons (0).
        /// </summary>
        internal static int CapFor(List<TrainerCap> caps, ICollection<string> passes, int maxLevel, out string from)
        {
            from = null;
            int cap = 0;
            if (caps != null)
                foreach (var step in caps)
                {
                    if (step.Boss != null && (passes == null || !passes.Contains(step.Boss))) continue;
                    if (step.Level <= cap) continue;
                    cap = step.Level;
                    from = step.Boss;
                }
            return Math.Max(0, Math.Min(cap, maxLevel));
        }

        /// <summary>
        /// Why a lesson from `level` (the whole level shown, 0-99) cannot be sold, or null when
        /// it can: then `target` is the level after it and `band` its price. The server's whole
        /// rule for lessons, in one place the wire check can reach.
        /// </summary>
        internal static string LessonRefusal(TrainerPrices prices, int level, int cap, int used, int perWeek,
            out int target, out TrainerLessonBand band)
        {
            target = level + 1;
            band = null;
            if (level < 0 || level >= 100) return "Nível de skill inválido.";
            if (target > cap)
                return cap <= 0
                    ? "O Treinador ainda não ensina você: o passe de um chefe libera as aulas."
                    : $"O Treinador só ensina você até o nível {cap}.";
            if (perWeek > 0 && used >= perWeek)
                return $"Você já fez as {perWeek} aulas desta semana. A semana vira na segunda-feira.";
            band = prices?.LessonTo(target);
            return band == null ? $"O Treinador não dá aula para o nível {target}." : null;
        }

        private static string _cachedPricesRaw;
        private static TrainerPrices _cachedPrices = new TrainerPrices();
        private static string _cachedCapsRaw;
        private static List<TrainerCap> _cachedCaps = new List<TrainerCap>();

        /// <summary>The price list as configured right now, re-parsed when the text changes;
        /// problems are logged once per change. On a client this is the server's text by
        /// ServerSync and only a display.</summary>
        internal static TrainerPrices Prices()
        {
            string raw = Plugin.TrainerPrices?.Value ?? "";
            if (string.Equals(raw, _cachedPricesRaw, StringComparison.Ordinal)) return _cachedPrices;

            var problems = new List<string>();
            _cachedPrices = ParsePrices(raw, problems);
            _cachedPricesRaw = raw;
            foreach (var problem in problems) Plugin.Log.LogWarning("NpcValheim: Trainer.Prices -- " + problem);
            Plugin.Log.LogInfo($"NpcValheim: Treinador sells study={(_cachedPrices.Study != null)}, " +
                               $"protection={(_cachedPrices.Protection != null)}, {_cachedPrices.Lessons.Count} lesson band(s)");
            return _cachedPrices;
        }

        internal static List<TrainerCap> Caps()
        {
            string raw = Plugin.TrainerLessonCaps?.Value ?? "";
            if (string.Equals(raw, _cachedCapsRaw, StringComparison.Ordinal)) return _cachedCaps;

            var problems = new List<string>();
            _cachedCaps = ParseCaps(raw, problems);
            _cachedCapsRaw = raw;
            foreach (var problem in problems) Plugin.Log.LogWarning("NpcValheim: Trainer.LessonCaps -- " + problem);
            return _cachedCaps;
        }

        /// <summary>"Bonemass" -> the name the boss pass list gives it ("Bonemass", "Ancião"...).</summary>
        internal static string BossName(string boss) =>
            string.IsNullOrEmpty(boss) ? "" : BossPassCatalog.Find(boss)?.Name ?? boss;

        private static bool Is(string key, string expected) => key.Equals(expected, StringComparison.OrdinalIgnoreCase);

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>One lesson as the ledger has it.</summary>
    public sealed class TrainerLessonRecord
    {
        public long PlayerId;
        public string Week;
        public string Skill;
        public int Target;
        public string Token;
    }

    /// <summary>
    /// The lessons given, for the weekly limit. One text file on the server, treinador.txt in
    /// the mod folder next to bosspass.txt, one line per lesson:
    ///
    ///   &lt;character id&gt;;&lt;week&gt;;&lt;skill&gt;;&lt;target&gt;;&lt;how&gt;;&lt;when&gt;;&lt;name&gt;;&lt;purchase&gt;
    ///
    /// Same reasoning as the boss pass ledger: "how many did they take" and "give them one
    /// back" are a text editor away, and the file is re-read whenever it changes. The week is
    /// the Monday that opens it on the server's clock, so a week turns at once for everyone.
    /// </summary>
    internal static class TrainerLedger
    {
        internal const string FileName = "treinador.txt";

        private const string Header =
            "# Aulas do Treinador (NpcValheim). Uma linha por aula:\n" +
            "# <id do personagem>;<semana>;<skill>;<nivel alvo>;<como>;<quando>;<nome>;<compra>\n" +
            "# <semana> e a segunda-feira que abre a semana (aaaa-mm-dd, relogio do servidor).\n" +
            "# Apagar a linha devolve a aula ao limite da semana. O servidor rele este arquivo sempre que ele muda.\n";

        internal static string FilePath => Path.Combine(NpcStoragePaths.DatabaseDirectory, FileName);

        private static readonly List<TrainerLessonRecord> Lessons = new List<TrainerLessonRecord>();
        private static string _loadedPath;
        private static DateTime _loadedStamp;
        private static long _loadedLength = -1;

        /// <summary>The Monday that opens the week of `day`, as written in the file.</summary>
        internal static string WeekOf(DateTime day)
        {
            var date = day.Date;
            int sinceMonday = ((int)date.DayOfWeek + 6) % 7;
            return date.AddDays(-sinceMonday).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        internal static string FormatLine(long playerId, string week, string skill, int target, string source,
            DateTime when, string name, string token) =>
            playerId.ToString(CultureInfo.InvariantCulture) + ";" + Clean(week) + ";" + Clean(skill) + ";" +
            target.ToString(CultureInfo.InvariantCulture) + ";" + Clean(source) + ";" +
            when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ";" + Clean(name) + ";" + Clean(token);

        /// <summary>The lessons a file lists. Lines that are not lessons are skipped and named.
        /// Only the first two fields decide the limit; a line an admin adds by hand needs no more.</summary>
        internal static List<TrainerLessonRecord> Parse(IEnumerable<string> lines, List<string> problems)
        {
            var result = new List<TrainerLessonRecord>();
            if (lines == null) return result;

            int number = 0;
            foreach (var raw in lines)
            {
                number++;
                var line = (raw ?? "").Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                var fields = line.Split(';');
                if (fields.Length < 2 ||
                    !long.TryParse(fields[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long playerId) ||
                    playerId == 0L || fields[1].Trim().Length == 0)
                {
                    problems?.Add($"line {number} is not \"<character id>;<week>;...\": \"{line}\"");
                    continue;
                }

                result.Add(new TrainerLessonRecord
                {
                    PlayerId = playerId,
                    Week = fields[1].Trim(),
                    Skill = fields.Length > 2 ? fields[2].Trim() : "",
                    Target = fields.Length > 3 && int.TryParse(fields[3].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int target) ? target : 0,
                    Token = fields.Length > 7 ? fields[7].Trim() : "",
                });
            }
            return result;
        }

        internal static int CountWeek(IEnumerable<TrainerLessonRecord> lessons, long playerId, string week) =>
            lessons?.Count(l => l.PlayerId == playerId && string.Equals(l.Week, week, StringComparison.Ordinal)) ?? 0;

        internal static int CountWeek(long playerId, string week)
        {
            Refresh();
            return CountWeek(Lessons, playerId, week);
        }

        /// <summary>Writes a lesson. Throws when the file cannot be written: the caller has just
        /// taken payment and has to give it back.</summary>
        internal static void Record(long playerId, string name, string week, string skill, int target, string source, string token)
        {
            if (playerId == 0L) throw new ArgumentException("no character to record a lesson for");
            Refresh();
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.WriteAllText(path, Header);
            File.AppendAllText(path, FormatLine(playerId, week, skill, target, source, DateTime.Now, name, token) + Environment.NewLine);

            Lessons.Add(new TrainerLessonRecord { PlayerId = playerId, Week = week, Skill = skill, Target = target, Token = token });
            Remember(path);
        }

        /// <summary>Takes back the lesson of one purchase (it could not be delivered). False
        /// when no line has that purchase.</summary>
        internal static bool Remove(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            var path = FilePath;
            if (!File.Exists(path)) return false;

            var lines = File.ReadAllLines(path);
            var kept = lines.Where(l =>
            {
                var fields = (l ?? "").Split(';');
                return !(fields.Length > 7 && string.Equals(fields[7].Trim(), token, StringComparison.Ordinal));
            }).ToArray();
            if (kept.Length == lines.Length) return false;

            File.WriteAllLines(path, kept);
            _loadedLength = -1; // re-read on the next question
            return true;
        }

        private static void Refresh()
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                if (_loadedLength != -1 || !string.Equals(path, _loadedPath, StringComparison.Ordinal))
                {
                    Lessons.Clear();
                    _loadedPath = path;
                    _loadedLength = -1;
                }
                return;
            }

            var info = new FileInfo(path);
            if (string.Equals(path, _loadedPath, StringComparison.Ordinal) &&
                info.LastWriteTimeUtc == _loadedStamp && info.Length == _loadedLength) return;

            var problems = new List<string>();
            var parsed = Parse(File.ReadAllLines(path), problems);
            Lessons.Clear();
            Lessons.AddRange(parsed);
            Remember(path);
            foreach (var problem in problems) Plugin.Log.LogWarning($"NpcValheim: {FileName} -- {problem}");
            Plugin.Log.LogInfo($"NpcValheim: {FileName} read: {parsed.Count} lesson(s)");
        }

        private static void Remember(string path)
        {
            var info = new FileInfo(path);
            _loadedPath = path;
            _loadedStamp = info.LastWriteTimeUtc;
            _loadedLength = info.Length;
        }

        private static string Clean(string text) =>
            (text ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>
    /// What the server granted, as it travels to the buyer: which purchase (the token), what,
    /// paid how, and the server's own numbers for the effect -- the client applies what it is
    /// told, not its copy of the cfg. Fixed shape, one field per line, whatever the item.
    /// </summary>
    public sealed class TrainerGrant
    {
        public string Token = "";
        public string Item = "";
        public string Method = "";
        public int Price;
        /// <summary>Deadcoins left after a Deadcoins purchase; -1 for Coins.</summary>
        public int Balance = -1;
        public string Skill = "";
        public int Target;
        public float Bonus;
        public int Minutes;
        public int MaxSkill;
        public float Factor;
        public bool PveOnly = true;

        internal const int FieldCount = 12;

        internal string Pack() => string.Join("\n", new[]
        {
            Token, Item, Method, I(Price), I(Balance), Skill ?? "", I(Target), F(Bonus), I(Minutes), I(MaxSkill),
            F(Factor), PveOnly ? "1" : "0",
        });

        internal static bool TryUnpack(string payload, out TrainerGrant grant)
        {
            grant = null;
            var p = (payload ?? "").Split('\n');
            if (p.Length != FieldCount || p[0].Length == 0 ||
                !TryI(p[3], out int price) || !TryI(p[4], out int balance) || !TryI(p[6], out int target) ||
                !TryF(p[7], out float bonus) || !TryI(p[8], out int minutes) || !TryI(p[9], out int maxSkill) ||
                !TryF(p[10], out float factor) || (p[11] != "0" && p[11] != "1"))
                return false;

            grant = new TrainerGrant
            {
                Token = p[0], Item = p[1], Method = p[2], Price = price, Balance = balance, Skill = p[5],
                Target = target, Bonus = bonus, Minutes = minutes, MaxSkill = maxSkill, Factor = factor,
                PveOnly = p[11] == "1",
            };
            return true;
        }

        private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static bool TryI(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        private static bool TryF(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);
    }

    /// <summary>
    /// Both halves of the Treinador: the purchase rules the server runs, and the buyer's side of
    /// the answer. Same shape as the Mestre das Montarias (MountTrainer) and the boss pass.
    ///
    /// The server decides with its own copy of everything: the price, the Deadcoins (charged on
    /// the DeadcoinLedger), the boss passes (BossPassLedger) that set how high lessons go, and
    /// the weekly count of lessons (TrainerLedger). What the client sends is what it clicked,
    /// the price it was shown and, for a lesson, the skill and its level -- a level the server
    /// cannot see, so the grant carries the target and the client never raises a skill past it.
    ///
    /// The effect itself runs on the buyer's machine, because skills and death do: the study
    /// and the protection through Deadheim (DeadheimTrainerApi), the lesson right here. If it
    /// cannot be applied, the client sends the purchase back (RpcUndo) and the server returns
    /// what that one purchase took, once: the Deadcoins and the lesson's place in the week.
    ///
    /// Coins, as at the Mestre das Montarias, stay in the buyer's bag: the server grants and
    /// the client pays when the grant has been applied. A modified client can skip paying, or
    /// give itself the effect with no purchase at all; that is Valheim's trust model for
    /// anything on the player's own character, and why Deadcoins never leave the server.
    /// </summary>
    internal static class Trainer
    {
        private const string RpcResponse = "NpcValheim_TrainerResponse";
        private const string RpcUndo = "NpcValheim_TrainerUndo";
        private const float PurchaseTimeout = 10f;
        private const float GrantSeconds = 120f;

        internal const string ActionStatus = "RPC_TrainerStatus";
        internal const string ActionBuy = "RPC_TrainerBuy";
        internal const string MethodGold = "gold";
        internal const string MethodDeadcoins = "deadcoins";

        // ---- client state ----

        /// <summary>Whether the server has answered a status request in this session.</summary>
        internal static bool Known { get; private set; }
        internal static int DeadcoinBalance { get; private set; } = -1;
        internal static int LessonsUsed { get; private set; }
        internal static int LessonsPerWeek { get; private set; }
        internal static int LessonCap { get; private set; }
        /// <summary>The boss whose pass gives <see cref="LessonCap"/>, or "" for the step with no pass.</summary>
        internal static string LessonCapBoss { get; private set; } = "";
        internal static int StatusRevision { get; private set; }

        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }

        private static float _purchaseSentAt = -PurchaseTimeout;

        // ---- server state ----

        private sealed class PendingGrant
        {
            public long PlayerId;
            public string Who;
            public string Item;
            public string Method;
            public int Price;
            public string Skill;
            public int Target;
            public string LedgerPath;
            public float Expires;
        }

        private static readonly Dictionary<string, PendingGrant> Pending = new Dictionary<string, PendingGrant>(StringComparer.Ordinal);

        /// <summary>Once per connection, from ServiceNpcAuthority.TryRegister.</summary>
        internal static void Register(ZRoutedRpc rpc)
        {
            Known = false;
            DeadcoinBalance = -1;
            LessonsUsed = LessonsPerWeek = LessonCap = 0;
            LessonCapBoss = "";
            LastMessage = null;
            Pending.Clear();
            EndPurchase();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
            rpc.Register(RpcUndo, (Action<long, string, string>)RPC_Undo);
        }

        // ---- client -> server ----

        /// <summary>"item\nmethod\nshown price\nskill\nlevel"; the last two are empty for a buff.</summary>
        internal static string BuyPayload(string item, string method, int shownPrice, string skill, int level) =>
            item + "\n" + method + "\n" + shownPrice.ToString(CultureInfo.InvariantCulture) + "\n" + (skill ?? "") + "\n" +
            (string.IsNullOrEmpty(skill) ? "" : level.ToString(CultureInfo.InvariantCulture));

        internal static bool TryBeginPurchase()
        {
            if (Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout) return false;
            _purchaseSentAt = Time.realtimeSinceStartup;
            return true;
        }

        internal static void EndPurchase() => _purchaseSentAt = -PurchaseTimeout;

        // ---- server: status ----

        internal static void ServeStatus(long sender)
        {
            // The panel asks every few seconds, so a pass won elsewhere or a new week shows up.
            if (!NpcRequestGuard.AllowRate(sender, "trainer-status", 6, 5f)) return;

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                SendNotice(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }
            try
            {
                SendStatus(sender, playerId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: Treinador status for {playerId} failed: {e.Message}");
                SendNotice(sender, "O servidor não conseguiu ler as aulas e os passes. Tente de novo.");
            }
        }

        private static void SendStatus(long sender, long playerId)
        {
            var settings = TrainerSettings.Current();
            int cap = CapOf(playerId, settings, out string from);
            int used = TrainerLedger.CountWeek(playerId, TrainerLedger.WeekOf(DateTime.Now));
            Send(sender, "status", Int(ReadDeadcoins(sender)) + "\n" + Int(used) + "\n" + Int(settings.LessonsPerWeek) + "\n" +
                                   Int(cap) + "\n" + (from ?? ""));
        }

        private static void SendStatusQuietly(long sender, long playerId)
        {
            try { SendStatus(sender, playerId); }
            catch { /* only a refresh of what the panel shows */ }
        }

        private static int CapOf(long playerId, TrainerSettings settings, out string from) =>
            TrainerCatalog.CapFor(TrainerCatalog.Caps(), BossPassLedger.PassesOf(playerId), settings.LessonMaxLevel, out from);

        private static int ReadDeadcoins(long sender)
        {
            try
            {
                return DeadcoinLedger.TryResolveAccount(sender, out string path, out _) && DeadcoinLedger.TryRead(path, out int balance)
                    ? balance
                    : -1;
            }
            catch
            {
                return -1;
            }
        }

        // ---- server: buying ----

        /// <summary>
        /// A purchase: "item\nmethod\nshown price\nskill\nlevel". The price is only compared,
        /// never charged. Every refusal says why in the log and on the buyer's screen, and
        /// none of them has taken anything.
        /// </summary>
        internal static void ServePurchase(long sender, string payload, string where)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'trainer-buy' from peer {sender} at {where}: \"{payload?.Replace('\n', '|')}\"");

            if (!NpcRequestGuard.AllowRate(sender, "trainer-buy", 4, 2f))
            {
                Refuse(sender, -1, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            var parts = (payload ?? "").Split('\n');
            if (parts.Length != 5 || !TryInt(parts[2], out int shownPrice))
            {
                Refuse(sender, -1, "a malformed request", "Pedido malformado.");
                return;
            }
            string item = parts[0], method = parts[1];

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Refuse(sender, -1, "an unresolved character", "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            var settings = TrainerSettings.Current();
            var prices = TrainerCatalog.Prices();
            var grant = new TrainerGrant { Token = Guid.NewGuid().ToString("N"), Item = item, Method = method };
            TrainerPrice price = null;
            string week = TrainerLedger.WeekOf(DateTime.Now);
            string what;

            switch (item)
            {
                case TrainerCatalog.ItemStudy:
                    what = "o estudo";
                    if (settings.StudyBonus <= 0f || settings.StudyMinutes <= 0 || settings.StudyMaxSkill <= 0)
                    {
                        Refuse(sender, -1, "study is switched off", "O estudo está desligado neste servidor.");
                        return;
                    }
                    price = prices.Study;
                    grant.Bonus = settings.StudyBonus;
                    grant.Minutes = settings.StudyMinutes;
                    grant.MaxSkill = settings.StudyMaxSkill;
                    break;

                case TrainerCatalog.ItemProtection:
                    what = "a proteção";
                    if (settings.ProtectionFactor <= 0f)
                    {
                        Refuse(sender, -1, "protection is switched off", "A proteção está desligada neste servidor.");
                        return;
                    }
                    price = prices.Protection;
                    grant.Factor = settings.ProtectionFactor;
                    grant.PveOnly = settings.ProtectionPveOnly;
                    break;

                case TrainerCatalog.ItemLesson:
                    what = "a aula";
                    if (!TrainerLessons.TryParseSkill(parts[3], out _) || !TryInt(parts[4], out int level))
                    {
                        Refuse(sender, -1, $"no skill '{parts[3]}' at level '{parts[4]}'", "Skill desconhecida.");
                        return;
                    }
                    int cap, used;
                    try
                    {
                        cap = CapOf(playerId, settings, out _);
                        used = TrainerLedger.CountWeek(playerId, week);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogError($"NpcValheim: Treinador could not read passes or lessons: {e.Message}");
                        Refuse(sender, -1, "an unreadable ledger", "O servidor não conseguiu ler as aulas e os passes. Tente de novo.");
                        return;
                    }
                    string refusal = TrainerCatalog.LessonRefusal(prices, level, cap, used, settings.LessonsPerWeek,
                        out int target, out var band);
                    if (refusal != null)
                    {
                        Refuse(sender, -1, $"lesson {parts[3]} {level}->{target}: cap {cap}, {used}/{settings.LessonsPerWeek} this week", refusal);
                        SendStatusQuietly(sender, playerId);
                        return;
                    }
                    price = new TrainerPrice { Gold = band.Gold, Deadcoins = band.Deadcoins };
                    grant.Skill = parts[3];
                    grant.Target = target;
                    break;

                default:
                    Refuse(sender, -1, $"unknown item '{item}'", "O Treinador não vende isso.");
                    return;
            }

            if (price == null)
            {
                Refuse(sender, -1, $"{item} has no price", $"O Treinador não vende {what} no momento.");
                return;
            }
            int amount = method == MethodGold ? price.Gold : method == MethodDeadcoins ? price.Deadcoins : -1;
            if (amount < 0)
            {
                Refuse(sender, -1, $"unknown payment '{method}'", "Forma de pagamento desconhecida.");
                return;
            }
            string currency = method == MethodGold ? "moedas" : "Deadcoins";
            if (amount == 0)
            {
                Refuse(sender, -1, $"{item} is not sold for {method}", $"O Treinador não vende {what} por {currency}.");
                return;
            }
            if (shownPrice != amount)
            {
                Refuse(sender, -1, $"the buyer saw {shownPrice}, the price is {amount}",
                    $"O preço mudou: agora custa {amount} {currency}. Confira e compre de novo.");
                return;
            }
            grant.Price = amount;

            // Deadcoins: charged here, before anything is recorded or sent.
            string ledgerPath = null, who = $"{GameApi.GetPlayerName(sender)} ({playerId})";
            int before = -1, after = -1;
            if (method == MethodDeadcoins)
            {
                if (!DeadcoinLedger.TryResolveAccount(sender, out ledgerPath, out who))
                {
                    Refuse(sender, -1, $"no Deadcoins account for {who}", "O servidor não conseguiu identificar a sua conta.");
                    return;
                }
                try
                {
                    if (!DeadcoinLedger.TryRead(ledgerPath, out before))
                    {
                        Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {ledgerPath}");
                        Refuse(sender, -1, "an unreadable balance file", "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                        return;
                    }
                    if (before < amount)
                    {
                        Refuse(sender, before, $"{who} has {before}, the price is {amount}",
                            $"Você não tem Deadcoins suficientes: saldo {before}, custa {amount}.");
                        return;
                    }
                    after = before - amount;
                    DeadcoinLedger.Write(ledgerPath, after);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: could not charge {who} for {item}: {e.Message}");
                    Refuse(sender, -1, "a ledger error", "Falha ao registrar a compra. Nada foi cobrado.");
                    return;
                }
            }

            // A lesson takes its place in the week now; if that cannot be written, the
            // Deadcoins go back before anyone hears about it.
            if (item == TrainerCatalog.ItemLesson)
            {
                try
                {
                    TrainerLedger.Record(playerId, GameApi.GetPlayerName(sender), week, grant.Skill, grant.Target,
                        $"{method}:{amount}", grant.Token);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: lesson for {who} not written ({e.Message})" +
                                        (ledgerPath != null ? $"; returning {amount} Deadcoins" : ""));
                    if (ledgerPath != null) WriteBack(ledgerPath, before, who, amount);
                    Refuse(sender, ledgerPath != null ? before : -1, "a lesson that could not be written",
                        "Falha ao registrar a aula. Nada foi cobrado.");
                    return;
                }
            }

            PruneGrants();
            Pending[grant.Token] = new PendingGrant
            {
                PlayerId = playerId, Who = who, Item = item, Method = method, Price = amount, Skill = grant.Skill,
                Target = grant.Target, LedgerPath = ledgerPath, Expires = Time.realtimeSinceStartup + GrantSeconds,
            };
            grant.Balance = after;
            Send(sender, "granted", grant.Pack());

            string detail = item == TrainerCatalog.ItemLesson ? $" ({grant.Skill} -> {grant.Target})" : "";
            Plugin.Log.LogInfo($"NpcValheim: {who} bought {item}{detail} for {amount} {(method == MethodGold ? "Coins" : "Deadcoins")} at {where}" +
                               (ledgerPath != null ? $" ({before} -> {after})" : ""));
            if (ledgerPath != null)
            {
                try { DeadcoinLedger.AppendLogLine($"{who} bought {item}{detail} at the Treinador for {amount} Deadcoins (balance {before} -> {after})"); }
                catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }
            }
            if (item == TrainerCatalog.ItemLesson) SendStatusQuietly(sender, playerId);
        }

        /// <summary>
        /// The buyer could not apply a grant (no character in the world, Deadheim too old, the
        /// skill already past the target) and sends the purchase back. A token is good once,
        /// for the character it was issued to, for a short while; an unknown one gets nothing,
        /// since a refund the server cannot account for is a Deadcoins faucet.
        /// </summary>
        private static void RPC_Undo(long sender, string token, string reason)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!NpcRequestGuard.AllowRate(sender, "trainer-undo", 4, 2f)) return;

            PruneGrants();
            if (string.IsNullOrEmpty(token) || !Pending.TryGetValue(token, out var pending))
            {
                Plugin.Log.LogWarning($"NpcValheim: Treinador undo from peer {sender} with an unknown purchase \"{token}\" ({reason})");
                SendNotice(sender, "O servidor não reconheceu a compra a devolver. Fale com um admin.");
                return;
            }
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId != pending.PlayerId)
            {
                Plugin.Log.LogWarning($"NpcValheim: Treinador purchase of {pending.PlayerId} sent back by peer {sender} ({playerId})");
                return;
            }
            Pending.Remove(token);

            int balance = -1;
            if (pending.LedgerPath != null)
            {
                try
                {
                    if (DeadcoinLedger.TryRead(pending.LedgerPath, out int now))
                    {
                        balance = (int)Math.Min(int.MaxValue, (long)now + pending.Price);
                        DeadcoinLedger.Write(pending.LedgerPath, balance);
                        DeadcoinLedger.AppendLogLine($"{pending.Who} got {pending.Price} Deadcoins back from the Treinador " +
                                                     $"({pending.Item} not applied: {reason}) (balance {now} -> {balance})");
                    }
                    else
                        Plugin.Log.LogError($"NpcValheim: REFUND FAILED -- {pending.Who} should get {pending.Price} Deadcoins back, " +
                                            $"but the balance file is not a number: {pending.LedgerPath}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"NpcValheim: REFUND FAILED -- {pending.Who} should get {pending.Price} Deadcoins back: {e.Message}");
                }
            }
            if (pending.Item == TrainerCatalog.ItemLesson)
            {
                try { TrainerLedger.Remove(token); }
                catch (Exception e) { Plugin.Log.LogError($"NpcValheim: lesson of {pending.Who} not taken back from {TrainerLedger.FileName}: {e.Message}"); }
            }

            Plugin.Log.LogWarning($"NpcValheim: {pending.Who} sent back {pending.Item} ({pending.Price} {pending.Method}): {reason}");
            string back = pending.Method == MethodDeadcoins
                ? (balance >= 0 ? $"Seus {pending.Price} Deadcoins foram devolvidos." : "A devolução dos Deadcoins falhou: fale com um admin.")
                : "Nenhuma moeda foi cobrada.";
            Send(sender, "undone", Int(balance) + "\n" + back);
            if (pending.Item == TrainerCatalog.ItemLesson) SendStatusQuietly(sender, playerId);
        }

        private static void WriteBack(string path, int balance, string who, int amount)
        {
            try { DeadcoinLedger.Write(path, balance); }
            catch (Exception undo)
            {
                Plugin.Log.LogError($"NpcValheim: could NOT return {amount} Deadcoins to {who} (balance file should read {balance}): {undo.Message}");
            }
        }

        private static void PruneGrants()
        {
            if (Pending.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            foreach (var stale in Pending.Where(p => p.Value.Expires < now).Select(p => p.Key).ToList())
                Pending.Remove(stale);
        }

        private static void Refuse(long sender, int balance, string reason, string message)
        {
            Plugin.Log.LogWarning($"NpcValheim: refused a Treinador purchase from peer {sender}: {reason}");
            Send(sender, "refused", Int(balance) + "\n" + message);
        }

        // ---- server -> client ----

        private static void SendNotice(long peer, string message) => Send(peer, "notice", message);

        private static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        // ---- client ----

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            var parts = (payload ?? "").Split('\n');

            switch (kind)
            {
                case "status" when parts.Length == 5 && TryInt(parts[0], out int balance) && TryInt(parts[1], out int used) &&
                                   TryInt(parts[2], out int perWeek) && TryInt(parts[3], out int cap):
                    NoteBalance(balance);
                    LessonsUsed = used;
                    LessonsPerWeek = perWeek;
                    LessonCap = cap;
                    LessonCapBoss = parts[4];
                    Known = true;
                    StatusRevision++;
                    break;

                case "notice" when parts.Length == 1:
                    Tell(parts[0]);
                    break;

                case "refused" when parts.Length == 2 && TryInt(parts[0], out int balance):
                    NoteBalance(balance);
                    EndPurchase();
                    Announce(parts[1]);
                    break;

                case "undone" when parts.Length == 2 && TryInt(parts[0], out int balance):
                    NoteBalance(balance);
                    Announce(parts[1]);
                    break;

                case "granted" when TrainerGrant.TryUnpack(payload, out var grant):
                    EndPurchase();
                    Deliver(grant);
                    break;

                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed Treinador answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        /// <summary>
        /// Applies what the server granted. Coins leave the bag only once the effect is in place,
        /// so an honest buyer never pays for something that did not happen; when it cannot be
        /// applied, the purchase goes back to the server, which returns its Deadcoins.
        /// </summary>
        private static void Deliver(TrainerGrant grant)
        {
            string error;
            try
            {
                error = Apply(grant);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: Treinador grant {grant.Item} failed: {e}");
                error = "Não deu para aplicar a compra.";
            }

            if (error != null)
            {
                Plugin.Log.LogWarning($"NpcValheim: Treinador grant {grant.Item} not applied: {error}");
                ZRoutedRpc.instance?.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcUndo, new object[] { grant.Token, error });
                Announce(error + " Devolvendo a compra...");
                return;
            }

            if (grant.Method == MethodGold) PayCoins(grant.Price);
            else NoteBalance(grant.Balance);
            Announce(Done(grant));
        }

        private static string Apply(TrainerGrant grant)
        {
            switch (grant.Item)
            {
                case TrainerCatalog.ItemStudy:
                    if (!DeadheimTrainerApi.IsAvailable) return DeadheimTrainerApi.NotInstalled;
                    return DeadheimTrainerApi.GrantStudy(grant.Bonus, grant.Minutes, grant.MaxSkill);
                case TrainerCatalog.ItemProtection:
                    if (!DeadheimTrainerApi.IsAvailable) return DeadheimTrainerApi.NotInstalled;
                    return DeadheimTrainerApi.GrantProtection(grant.Factor, grant.PveOnly);
                case TrainerCatalog.ItemLesson:
                    return TrainerLessons.Apply(Player.m_localPlayer, grant.Skill, grant.Target);
                default:
                    return "Compra desconhecida.";
            }
        }

        private static string Done(TrainerGrant grant)
        {
            switch (grant.Item)
            {
                case TrainerCatalog.ItemStudy:
                    return $"Estudo ativo: +{grant.Bonus * 100f:0}% de XP nas skills abaixo de {grant.MaxSkill}, por {grant.Minutes} min de jogo.";
                case TrainerCatalog.ItemProtection:
                    return $"Proteção ativa: a próxima morte{(grant.PveOnly ? " para monstro, queda ou afogamento" : "")} " +
                           $"tira {grant.Factor * 100f:0}% menos skill.";
                default:
                    return $"Aula concluída: {TrainerLessons.DisplayName(grant.Skill)} subiu para o nível {grant.Target}.";
            }
        }

        /// <summary>Same as the Mestre das Montarias: the grant has happened, so if the coins
        /// were spent meanwhile what is there is taken and the rest is let go.</summary>
        private static void PayCoins(int price)
        {
            var player = Player.m_localPlayer;
            if (player == null || price <= 0) return;
            if (MarketplaceNpc.TryPay(player, price)) return;

            int have = MarketplaceNpc.CoinsOf(player);
            if (have > 0) MarketplaceNpc.TryPay(player, have);
            Plugin.Log.LogWarning($"NpcValheim: a Treinador purchase of {price} Coins found only {have} in the bag");
        }

        private static void NoteBalance(int balance)
        {
            if (balance < 0) return;
            DeadcoinBalance = balance;
            DeadcoinShop.NoteBalance(balance);
        }

        private static void Announce(string message)
        {
            Tell(message);
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, message, 0, null);
        }

        private static void Tell(string message)
        {
            LastMessage = message;
            MessageRevision++;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The lesson itself, on the buyer's machine: +1 level in one vanilla skill. The level is
    /// the skill's own (Skills.Skill.m_level), without what gear adds, and the fraction past the
    /// whole level is kept, so a lesson is exactly one level.
    /// </summary>
    internal static class TrainerLessons
    {
        /// <summary>Skills.GetSkill is private in the game's assembly: called by reflection, never
        /// directly (a direct call compiles against the publicized one and can fail to JIT).</summary>
        private static readonly System.Reflection.MethodInfo GetSkill =
            AccessTools.Method(typeof(Skills), "GetSkill", new[] { typeof(Skills.SkillType) });

        /// <summary>A skill name exactly as the enum spells it ("Swords"), never None, All or a number.</summary>
        internal static bool TryParseSkill(string name, out Skills.SkillType type)
        {
            type = Skills.SkillType.None;
            if (string.IsNullOrEmpty(name) || name.Length > 32) return false;
            if (!Enum.TryParse(name, false, out type)) return false;
            return Enum.IsDefined(typeof(Skills.SkillType), type) && type.ToString() == name &&
                   type != Skills.SkillType.None && type != Skills.SkillType.All;
        }

        /// <summary>The name the game shows ("Espadas"), or the enum name outside a game.</summary>
        internal static string DisplayName(string skill) =>
            UI.ValheimUi.Localize("$skill_" + (skill ?? "").ToLowerInvariant());

        /// <summary>The level of a skill as it stands, without creating an entry for one never used.</summary>
        internal static float RawLevel(Skills skills, Skills.SkillType type)
        {
            if (skills == null) return 0f;
            foreach (var skill in skills.GetSkillList())
                if (skill?.m_info != null && skill.m_info.m_skill == type) return skill.m_level;
            return 0f;
        }

        /// <summary>
        /// +1 level, never past `target` (the level the server sold). Returns why not, or null.
        /// A skill already at the target -- it levelled up on its own while the request was in
        /// flight, or the client lied about its level -- is refused, so the purchase goes back.
        /// </summary>
        internal static string Apply(Player player, string skillName, int target)
        {
            if (player == null || player != Player.m_localPlayer) return "Você precisa estar no mundo para a aula.";
            if (!TryParseSkill(skillName, out var type)) return "Skill desconhecida.";
            var skills = player.GetSkills();
            if (skills == null || GetSkill == null) return "Não deu para ler as suas skills.";

            var skill = GetSkill.Invoke(skills, new object[] { type }) as Skills.Skill;
            if (skill?.m_info == null) return "Esta skill não existe neste personagem.";

            float level = skill.m_level;
            if (Mathf.Floor(level) + 1f > target)
                return $"{DisplayName(skillName)} já está no nível {Mathf.FloorToInt(level)}.";

            skill.m_level = Mathf.Min(100f, level + 1f);
            player.OnSkillLevelup(type, skill.m_level);
            player.Message(MessageHud.MessageType.TopLeft,
                "$msg_skillup $skill_" + type.ToString().ToLowerInvariant() + ": " + (int)skill.m_level, 0, skill.m_info.m_icon);
            Plugin.Log.LogInfo($"NpcValheim: lesson {type} {level:0.##} -> {skill.m_level:0.##}");
            return null;
        }
    }
}
