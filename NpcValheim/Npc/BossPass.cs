using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>One boss that can lock a merchant: which creature, what it is called on screen,
    /// which shop it opens, and what skipping the fight costs.</summary>
    public sealed class BossPassEntry
    {
        /// <summary>The creature prefab, e.g. gd_king. This is the id everything stores.</summary>
        public string Boss;
        public string Name;
        public string Shop;
        /// <summary>Coins for the pass, or 0 when it cannot be bought with Coins.</summary>
        public int Gold;
        /// <summary>Deadcoins for the pass, or 0 when it cannot be bought with Deadcoins.</summary>
        public int Deadcoins;
    }

    /// <summary>
    /// The bosses a merchant can require, read from the BossPass.Bosses config.
    ///
    /// Same shape as DeadcoinShop.Items (key=value;... entries separated by |), so an admin
    /// who has edited one has edited the other. Prices live here and not on the NPC: a pass
    /// is per boss, so every merchant that requires the Elder sells the same pass at the same
    /// price, and paying at one opens all of them.
    /// </summary>
    internal static class BossPassCatalog
    {
        /// <summary>The server's table, Eikthyr left out on purpose: his shop is the starting
        /// one. 100 Deadcoins = R$1, the rate the Deadcoins counter already uses.</summary>
        internal const string DefaultBosses =
            "boss=gd_king;name=Ancião;shop=Loja do Pântano;gold=1000;deadcoins=500|" +
            "boss=Bonemass;name=Bonemass;shop=Loja da Montanha;gold=3000;deadcoins=1000|" +
            "boss=Dragon;name=Moder;shop=Loja da Planície;gold=5000;deadcoins=2500|" +
            "boss=GoblinKing;name=Yagluth;shop=Loja de Mistlands;gold=10000;deadcoins=5000|" +
            "boss=SeekerQueen;name=Rainha;shop=Loja de Ashlands;gold=15000;deadcoins=10000|" +
            "boss=Fader;name=Fader;shop=Loja do Norte Profundo;gold=25000;deadcoins=20000";

        /// <summary>
        /// Every well-formed entry, in the order written. Pure text, so the wire check can run
        /// it outside the game.
        ///
        /// A negative price is refused rather than clamped, as in the Deadcoins list. A price
        /// of 0 is allowed and means "not sold that way": a boss with both prices at 0 can only
        /// be opened by killing it.
        /// </summary>
        internal static List<BossPassEntry> Parse(string raw, List<string> problems)
        {
            var result = new List<BossPassEntry>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in raw.Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                string boss = null, name = null, shop = null;
                int gold = 0, deadcoins = 0;
                bool badNumber = false;
                foreach (var field in text.Split(';'))
                {
                    int equals = field.IndexOf('=');
                    if (equals <= 0) continue;
                    var key = field.Substring(0, equals).Trim();
                    var value = field.Substring(equals + 1).Trim();

                    if (Is(key, "boss")) boss = value;
                    else if (Is(key, "name")) name = value;
                    else if (Is(key, "shop")) shop = value;
                    else if (Is(key, "gold")) badNumber |= !TryInt(value, out gold);
                    else if (Is(key, "deadcoins")) badNumber |= !TryInt(value, out deadcoins);
                }

                if (string.IsNullOrEmpty(boss))
                {
                    problems?.Add($"entry without boss= \"{text}\"");
                    continue;
                }
                if (!IsCleanId(boss))
                {
                    problems?.Add($"\"{boss}\" is not a prefab name");
                    continue;
                }
                if (badNumber || gold < 0 || deadcoins < 0)
                {
                    problems?.Add($"{boss}: gold and deadcoins must be whole numbers, 0 or more");
                    continue;
                }
                if (!seen.Add(boss))
                {
                    problems?.Add($"{boss} is listed more than once; the first entry wins");
                    continue;
                }
                if (gold == 0 && deadcoins == 0)
                    problems?.Add($"{boss} has no price, so only killing it opens its shops");

                result.Add(new BossPassEntry
                {
                    Boss = boss,
                    Name = string.IsNullOrEmpty(name) ? boss : OneLine(name),
                    Shop = OneLine(shop),
                    Gold = gold,
                    Deadcoins = deadcoins,
                });
            }
            return result;
        }

        private static string _cachedRaw;
        private static List<BossPassEntry> _cached = new List<BossPassEntry>();

        /// <summary>
        /// The table as configured right now. Re-parsed whenever the text changes, so a cfg
        /// saved with the server running prices the next payment, and the problems are logged
        /// once per change instead of on every call.
        ///
        /// On a client this is the server's text, delivered by ServerSync, and is only ever a
        /// display. The server reads its own copy for every decision.
        /// </summary>
        internal static List<BossPassEntry> Current()
        {
            string raw = Plugin.BossPassBosses?.Value ?? "";
            if (string.Equals(raw, _cachedRaw, StringComparison.Ordinal)) return _cached;

            var problems = new List<string>();
            var parsed = Parse(raw, problems);
            _cached = parsed;
            _cachedRaw = raw;
            foreach (var problem in problems)
                Plugin.Log.LogWarning("NpcValheim: BossPass.Bosses -- " + problem);
            Plugin.Log.LogInfo($"NpcValheim: BossPass knows {parsed.Count} boss(es): " +
                               string.Join(", ", parsed.Select(e => e.Boss)));
            return parsed;
        }

        internal static BossPassEntry Find(string boss) =>
            string.IsNullOrEmpty(boss) ? null : Current().Find(e => string.Equals(e.Boss, boss, StringComparison.Ordinal));

        /// <summary>"Loja do Pântano", or a neutral name when the entry did not give one.</summary>
        internal static string ShopName(BossPassEntry entry) =>
            entry != null && !string.IsNullOrEmpty(entry.Shop) ? entry.Shop : "Esta loja";

        /// <summary>A prefab name that can travel inside the wire formats used here, which
        /// separate fields with ';', '|', ',' and line breaks.</summary>
        internal static bool IsCleanId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (char.IsWhiteSpace(c) || c == ';' || c == '|' || c == ',' || c == '=') return false;
            return true;
        }

        private static string OneLine(string text) =>
            (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();

        private static bool Is(string key, string expected) =>
            key.Equals(expected, StringComparison.OrdinalIgnoreCase);

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Who holds which pass. One text file on the server, bosspass.txt in the mod folder next to
    /// market.db, one line per pass:
    ///
    ///   &lt;character id&gt;;&lt;boss&gt;;&lt;how&gt;;&lt;when&gt;;&lt;name&gt;
    ///
    /// Text and not LiteDB, because the two things an admin will want to do by hand are
    /// "who has this" and "take it away / give it to them", and both are a text editor away.
    /// The file is re-read whenever it changes on disk, so an edit takes effect without a
    /// restart. The first two fields decide who holds what; the third says how it was won,
    /// which the boss pass NPC shows (a kill, Coins, Deadcoins, or anything else an admin
    /// typed); the rest is for whoever reads it.
    ///
    /// Passes are per character (Player.GetPlayerID), the same id the market and the mail
    /// use: the boss was killed by a character, not by an account.
    /// </summary>
    internal static class BossPassLedger
    {
        internal const string FileName = "bosspass.txt";

        private const string Header =
            "# Passes de loja por boss (NpcValheim). Uma linha por passe:\n" +
            "# <id do personagem>;<boss>;<como>;<quando>;<nome>\n" +
            "# Apagar a linha tira o passe. O servidor relê este arquivo sempre que ele muda.\n";

        // How a pass was won, as the panel tells it. The ledger keeps the full source
        // ("gold:1000"); these are its first word, and anything else is an admin's hand.
        internal const string KindKill = "kill";
        internal const string KindGold = "gold";
        internal const string KindDeadcoins = "deadcoins";
        internal const string KindAdmin = "admin";

        internal static string FilePath => Path.Combine(NpcStoragePaths.DatabaseDirectory, FileName);

        /// <summary>Character -> boss -> how it was won (the third field, as written).</summary>
        private static readonly Dictionary<long, Dictionary<string, string>> Passes =
            new Dictionary<long, Dictionary<string, string>>();
        private static string _loadedPath;
        private static DateTime _loadedStamp;
        private static long _loadedLength = -1;

        /// <summary>The passes a file grants. Lines that are not passes are skipped and named,
        /// never guessed at.</summary>
        internal static Dictionary<long, HashSet<string>> Parse(IEnumerable<string> lines, List<string> problems) =>
            ParseSources(lines, problems).ToDictionary(p => p.Key,
                p => new HashSet<string>(p.Value.Keys, StringComparer.Ordinal));

        /// <summary>The same, with how each pass was won. A pass listed twice keeps its first
        /// line: that is when it was won, and an admin's later copy does not rewrite it.</summary>
        internal static Dictionary<long, Dictionary<string, string>> ParseSources(IEnumerable<string> lines,
            List<string> problems)
        {
            var result = new Dictionary<long, Dictionary<string, string>>();
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
                    playerId == 0L || !BossPassCatalog.IsCleanId(fields[1].Trim()))
                {
                    problems?.Add($"line {number} is not \"<character id>;<boss>;...\": \"{line}\"");
                    continue;
                }

                if (!result.TryGetValue(playerId, out var held))
                    result[playerId] = held = new Dictionary<string, string>(StringComparer.Ordinal);
                string boss = fields[1].Trim();
                if (!held.ContainsKey(boss)) held[boss] = fields.Length > 2 ? fields[2].Trim() : "";
            }
            return result;
        }

        /// <summary>The kind of a source as written in the file: "gold:1000" is gold, "kill" a
        /// kill, and whatever else (an admin's "presente", an empty field) the admin's.</summary>
        internal static string KindOf(string source)
        {
            string text = (source ?? "").Trim();
            int colon = text.IndexOf(':');
            string head = (colon >= 0 ? text.Substring(0, colon) : text).Trim().ToLowerInvariant();
            return head == KindKill || head == KindGold || head == KindDeadcoins ? head : KindAdmin;
        }

        internal static string FormatLine(long playerId, string boss, string source, DateTime when, string name) =>
            playerId.ToString(CultureInfo.InvariantCulture) + ";" + boss + ";" + Clean(source) + ";" +
            when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + ";" + Clean(name);

        internal static bool Has(long playerId, string boss)
        {
            if (playerId == 0L || string.IsNullOrEmpty(boss)) return false;
            Refresh();
            return Passes.TryGetValue(playerId, out var held) && held.ContainsKey(boss);
        }

        internal static List<string> PassesOf(long playerId) => KindsOf(playerId).Keys.ToList();

        /// <summary>A character's passes and how each was won (KindOf), by boss.</summary>
        internal static SortedDictionary<string, string> KindsOf(long playerId)
        {
            Refresh();
            var kinds = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (Passes.TryGetValue(playerId, out var held))
                foreach (var pass in held) kinds[pass.Key] = KindOf(pass.Value);
            return kinds;
        }

        /// <summary>
        /// Records a pass. False when the character already had it, in which case nothing is
        /// written. Throws when the file cannot be written: a caller that has just taken
        /// payment has to know the pass did not stick, so it can give the payment back.
        /// </summary>
        internal static bool Grant(long playerId, string name, string boss, string source)
        {
            if (playerId == 0L || !BossPassCatalog.IsCleanId(boss))
                throw new ArgumentException($"not a pass: player {playerId}, boss '{boss}'");
            if (Has(playerId, boss)) return false;

            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (!File.Exists(path)) File.WriteAllText(path, Header);
            File.AppendAllText(path, FormatLine(playerId, boss, source, DateTime.Now, name) + Environment.NewLine);

            if (!Passes.TryGetValue(playerId, out var held))
                Passes[playerId] = held = new Dictionary<string, string>(StringComparer.Ordinal);
            held[boss] = Clean(source);
            Remember(path);
            return true;
        }

        /// <summary>Reloads the file when it changed since the last read -- an admin's edit,
        /// typically. Cheap when it did not: one stat per call.</summary>
        private static void Refresh()
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                if (_loadedLength != -1 || !string.Equals(path, _loadedPath, StringComparison.Ordinal))
                {
                    Passes.Clear();
                    _loadedPath = path;
                    _loadedLength = -1;
                }
                return;
            }

            var info = new FileInfo(path);
            if (string.Equals(path, _loadedPath, StringComparison.Ordinal) &&
                info.LastWriteTimeUtc == _loadedStamp && info.Length == _loadedLength) return;

            var problems = new List<string>();
            var parsed = ParseSources(File.ReadAllLines(path), problems);
            Passes.Clear();
            foreach (var pair in parsed) Passes[pair.Key] = pair.Value;
            Remember(path);

            foreach (var problem in problems)
                Plugin.Log.LogWarning($"NpcValheim: {FileName} -- {problem}");
            Plugin.Log.LogInfo($"NpcValheim: {FileName} read: {parsed.Sum(p => p.Value.Count)} pass(es) " +
                               $"for {parsed.Count} character(s)");
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
    /// The pass itself, both halves: the rules the server runs (who may trade at a locked
    /// counter, what a pass costs, taking the payment) and the client's copy of its own passes.
    ///
    /// Nothing here is decided by the client. The NPC's requirement is read off the server's
    /// ZDO, the price off the server's config, the Deadcoins off the server's balance file,
    /// and the pass is written to the server's ledger. What the client sends is "I want this
    /// counter's pass, paying this way, at the price I was shown", and that price is only
    /// compared, never charged -- same rule as the Deadcoins counter. At the boss pass NPC the
    /// client also names which pass it wants, since that counter sells all of them; the server
    /// looks that name up in its own list, and a name it does not have is refused.
    ///
    /// Paying in Coins needs two steps, because Coins are items in the player's own bag and no
    /// server can reach into a remote inventory. The server first validates the whole purchase
    /// and answers with a one-time token for that character, that boss and that price; only
    /// then does the client take the coins out and send the token back. The usual alternative,
    /// paying first and being refunded on refusal, lets a modified client claim it paid any
    /// amount and be "refunded" coins it never had -- the merchant counter has that hole. Here
    /// a refund only ever returns a quote's own price, once.
    ///
    /// The trust gap that remains is the one Valheim always has: a modified client can send the
    /// token without having removed the coins. It then gets a pass it did not pay for, but it
    /// cannot make coins appear.
    /// </summary>
    internal static class BossPass
    {
        private const string RpcResponse = "NpcValheim_BossPassResponse";
        private const string RpcPay = "NpcValheim_BossPassPay";
        private const float PurchaseTimeout = 10f;
        private const float QuoteSeconds = 120f;

        internal const string ActionStatus = "RPC_BossPassStatus";
        internal const string ActionBuy = "RPC_BossPassBuy";
        internal const string MethodGold = "gold";
        internal const string MethodDeadcoins = "deadcoins";

        // ---- client state ----

        /// <summary>Whether the server has said which passes this character holds yet.</summary>
        internal static bool Known { get; private set; }

        /// <summary>The Deadcoins balance the server last reported, or -1.</summary>
        internal static int DeadcoinBalance { get; private set; } = -1;

        internal static string LastMessage { get; private set; }
        internal static int MessageRevision { get; private set; }

        /// <summary>This character's passes, boss -> how it was won (BossPassLedger.Kind*).</summary>
        private static readonly Dictionary<string, string> OwnPasses = new Dictionary<string, string>(StringComparer.Ordinal);
        private static float _purchaseSentAt = -PurchaseTimeout;

        // ---- server state ----

        private sealed class Quote
        {
            public long PlayerId;
            public string Name;
            public string Boss;
            public int Price;
            public float Expires;
            public string Where;
        }

        private static readonly Dictionary<string, Quote> Quotes = new Dictionary<string, Quote>(StringComparer.Ordinal);

        /// <summary>Called once per connection (see ServiceNpcAuthority.TryRegister). What the
        /// client knew belongs to the previous session, possibly on another server.</summary>
        internal static void Register(ZRoutedRpc rpc)
        {
            Known = false;
            DeadcoinBalance = -1;
            LastMessage = null;
            OwnPasses.Clear();
            Quotes.Clear();
            EndPurchase();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
            rpc.Register(RpcPay, (Action<long, string>)RPC_Pay);
        }

        internal static bool HasLocalPass(string boss) => !string.IsNullOrEmpty(boss) && OwnPasses.ContainsKey(boss);

        /// <summary>How this character won a pass (BossPassLedger.Kind*), or null without it.</summary>
        internal static string LocalPassKind(string boss) =>
            !string.IsNullOrEmpty(boss) && OwnPasses.TryGetValue(boss, out var kind) ? kind : null;

        /// <summary>Changes whenever the passes the server reported do; a panel listing them
        /// compares this instead of the whole set.</summary>
        internal static string LocalPassSignature =>
            Known
                ? string.Join(",", OwnPasses.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
                : "?";

        /// <summary>One purchase in flight at a time, so a double click is not two passes.</summary>
        internal static bool TryBeginPurchase()
        {
            if (Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout) return false;
            _purchaseSentAt = Time.realtimeSinceStartup;
            return true;
        }

        internal static void EndPurchase() => _purchaseSentAt = -PurchaseTimeout;

        private static bool PurchaseInFlight => Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout;

        internal static string BuyPayload(string method, int shownPrice) =>
            method + "\n" + shownPrice.ToString(CultureInfo.InvariantCulture);

        /// <summary>The boss pass NPC's request, which also names the pass.</summary>
        internal static string ChosenBuyPayload(string boss, string method, int shownPrice) =>
            boss + "\n" + BuyPayload(method, shownPrice);

        /// <summary>
        /// The client half of a purchase, shared by the merchant's lock and the boss pass NPC:
        /// the courtesy checks on what this player can see (the server checks again on its
        /// own copy), one purchase in flight, and the request itself, which `send` delivers
        /// through the counter it was made at. Returns what to tell the player.
        /// </summary>
        internal static string BeginPurchase(BossPassEntry entry, string method, Func<int, bool> send)
        {
            if (entry == null) return null;
            int price = method == MethodGold ? entry.Gold : entry.Deadcoins;
            if (price <= 0) return null;

            if (method == MethodGold)
            {
                // Nothing leaves the bag yet. The server first checks the whole purchase and
                // answers with a quote; the coins are taken when that arrives (Pay).
                int coins = MarketplaceNpc.CoinsOf(Player.m_localPlayer);
                if (coins < price) return $"Você tem {coins} moedas; o passe custa {price}.";
            }
            else if (DeadcoinBalance >= 0 && DeadcoinBalance < price)
            {
                return $"Você tem {DeadcoinBalance} Deadcoins; o passe custa {price}.";
            }

            if (!TryBeginPurchase()) return "Aguarde o pagamento anterior terminar.";
            if (send(price)) return "Pedindo o passe ao servidor...";
            EndPurchase();
            return "O pedido não chegou ao servidor.";
        }

        // ---- server: the gate ----

        /// <summary>
        /// Whether a character may trade at a counter that requires `requiredBoss`. Runs on
        /// the server, inside the merchant's own buy and sell handlers.
        ///
        /// Closed when in doubt: a requirement the config no longer lists, or a ledger that
        /// cannot be read, keeps the counter shut rather than open. Removing the lock is the
        /// Admin tab's job, where it is deliberate.
        /// </summary>
        internal static bool CanTrade(long playerId, string requiredBoss, out string refusal)
        {
            refusal = null;
            if (string.IsNullOrEmpty(requiredBoss)) return true;

            var entry = BossPassCatalog.Find(requiredBoss);
            if (entry == null)
            {
                refusal = "Esta loja exige um boss que o servidor não tem configurado. Fale com um admin.";
                return false;
            }

            try
            {
                if (BossPassLedger.Has(playerId, requiredBoss)) return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read {BossPassLedger.FileName}: {e.Message}");
                refusal = "O servidor não conseguiu ler os passes. Tente de novo.";
                return false;
            }

            refusal = $"{BossPassCatalog.ShopName(entry)} está trancada: derrote {entry.Name} ou pague o passe.";
            return false;
        }

        // ---- server: status ----

        internal static void ServeStatus(long sender)
        {
            // The locked counter asks every few seconds, so a pass granted by an admin's edit or
            // a boss dying elsewhere shows up without reopening it.
            if (!NpcRequestGuard.AllowRate(sender, "bosspass-status", 6, 5f)) return;

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                SendNotice(sender, "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            string passes;
            try
            {
                passes = PassList(playerId);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read {BossPassLedger.FileName}: {e.Message}");
                SendNotice(sender, "O servidor não conseguiu ler os passes. Tente de novo.");
                return;
            }

            Send(sender, "status", Int(ReadDeadcoins(sender)) + "\n" + passes);
        }

        /// <summary>"boss=kind,boss=kind": boss ids never hold ',' or '=' (IsCleanId).</summary>
        private static string PassList(long playerId) =>
            string.Join(",", BossPassLedger.KindsOf(playerId).Select(p => p.Key + "=" + p.Value));

        /// <summary>The sender's Deadcoins, or -1 when there is no account or file to read.
        /// Display only; the purchase reads the file again.</summary>
        private static int ReadDeadcoins(long sender)
        {
            try
            {
                return DeadcoinLedger.TryResolveAccount(sender, out string path, out _) &&
                       DeadcoinLedger.TryRead(path, out int balance)
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
        /// A request for the pass of the counter it was sent to. `requiredBoss` is that
        /// counter's lock as the server's ZDO has it; `payload` is "method\nshown price".
        ///
        /// Deadcoins are charged and the pass written right here. Coins get a quote instead,
        /// and the pass is written when the token comes back (RPC_Pay). Every refusal says why,
        /// on the player's screen and in the log, and none of them has taken anything yet.
        /// </summary>
        internal static void ServePurchase(long sender, string requiredBoss, string where, string payload) =>
            ServePurchase(sender, requiredBoss, where, payload, chosen: false);

        /// <summary>A request at the boss pass NPC, where the buyer picks the pass:
        /// "boss\nmethod\nshown price". Everything after the name is the counter's request.</summary>
        internal static void ServeChosenPurchase(long sender, string where, string payload)
        {
            string text = payload ?? "";
            int cut = text.IndexOf('\n');
            ServePurchase(sender, cut < 0 ? "" : text.Substring(0, cut), where,
                cut < 0 ? text : text.Substring(cut + 1), chosen: true);
        }

        private static void ServePurchase(long sender, string requiredBoss, string where, string payload, bool chosen)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'bosspass-buy' from peer {sender} at {where}: \"{payload}\"");

            if (!NpcRequestGuard.AllowRate(sender, "bosspass-buy", 4, 2f))
            {
                Refuse(sender, 0, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            var parts = (payload ?? "").Split('\n');
            if (parts.Length != 2 || !TryInt(parts[1], out int shownPrice))
            {
                Refuse(sender, 0, "a malformed request", "Pedido malformado.");
                return;
            }
            string method = parts[0];

            var entry = BossPassCatalog.Find(requiredBoss);
            if (entry == null)
            {
                Refuse(sender, 0, chosen
                        ? $"the buyer asked for '{requiredBoss}', which is not configured"
                        : $"the counter requires '{requiredBoss}', which is not configured",
                    chosen
                        ? "Esse passe não está mais à venda. Feche e abra o painel de novo."
                        : string.IsNullOrEmpty(requiredBoss)
                            ? "Esta loja não está trancada."
                            : "Esta loja exige um boss que o servidor não tem configurado. Fale com um admin.");
                return;
            }

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Refuse(sender, 0, "an unresolved character", "O servidor não conseguiu identificar o seu personagem.");
                return;
            }

            try
            {
                if (BossPassLedger.Has(playerId, entry.Boss))
                {
                    Refuse(sender, 0, $"{playerId} already holds {entry.Boss}",
                        $"Você já tem o passe de {entry.Name}. {BossPassCatalog.ShopName(entry)} está liberada.");
                    SendStatusQuietly(sender, playerId);
                    return;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read {BossPassLedger.FileName}: {e.Message}");
                Refuse(sender, 0, "an unreadable ledger", "O servidor não conseguiu ler os passes. Tente de novo.");
                return;
            }

            switch (method)
            {
                case MethodGold:
                    QuoteGold(sender, playerId, entry, shownPrice, where);
                    break;
                case MethodDeadcoins:
                    ChargeDeadcoins(sender, playerId, entry, shownPrice, where);
                    break;
                default:
                    Refuse(sender, 0, $"unknown payment '{method}'", "Forma de pagamento desconhecida.");
                    break;
            }
        }

        private static void QuoteGold(long sender, long playerId, BossPassEntry entry, int shownPrice, string where)
        {
            if (entry.Gold <= 0)
            {
                Refuse(sender, 0, $"{entry.Boss} is not sold for Coins", $"O passe de {entry.Name} não é vendido por moedas.");
                return;
            }
            if (shownPrice != entry.Gold)
            {
                Refuse(sender, 0, $"the buyer saw {shownPrice}, the price is {entry.Gold}",
                    $"O passe de {entry.Name} agora custa {entry.Gold} moedas. Confira e pague de novo.");
                return;
            }

            PruneQuotes();
            string token = Guid.NewGuid().ToString("N");
            Quotes[token] = new Quote
            {
                PlayerId = playerId,
                Name = GameApi.GetPlayerName(sender),
                Boss = entry.Boss,
                Price = entry.Gold,
                Expires = Time.realtimeSinceStartup + QuoteSeconds,
                Where = where,
            };
            Send(sender, "pay", token + "\n" + entry.Boss + "\n" + Int(entry.Gold));
        }

        private static void ChargeDeadcoins(long sender, long playerId, BossPassEntry entry, int shownPrice, string where)
        {
            if (entry.Deadcoins <= 0)
            {
                Refuse(sender, 0, $"{entry.Boss} is not sold for Deadcoins", $"O passe de {entry.Name} não é vendido por Deadcoins.");
                return;
            }
            if (shownPrice != entry.Deadcoins)
            {
                Refuse(sender, 0, $"the buyer saw {shownPrice}, the price is {entry.Deadcoins}",
                    $"O passe de {entry.Name} agora custa {entry.Deadcoins} Deadcoins. Confira e pague de novo.");
                return;
            }
            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Refuse(sender, 0, $"no Deadcoins account for {who}", "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            int balance, after;
            try
            {
                if (!DeadcoinLedger.TryRead(path, out balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    Refuse(sender, 0, "an unreadable balance file",
                        "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                if (balance < entry.Deadcoins)
                {
                    Refuse(sender, 0, $"{who} has {balance}, the price is {entry.Deadcoins}",
                        $"Você não tem Deadcoins suficientes: saldo {balance}, custa {entry.Deadcoins}.", balance);
                    return;
                }

                after = balance - entry.Deadcoins;
                DeadcoinLedger.Write(path, after);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not charge {who} for the {entry.Boss} pass: {e.Message}");
                Refuse(sender, 0, "a ledger error", "Falha ao registrar o pagamento. Nada foi cobrado.");
                return;
            }

            // Charged first and written second, so a pass is never handed out unpaid. If the
            // write fails the charge is undone before anyone hears about it.
            try
            {
                BossPassLedger.Grant(playerId, GameApi.GetPlayerName(sender), entry.Boss,
                    $"deadcoins:{entry.Deadcoins}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: {entry.Boss} pass for {who} not written ({e.Message}); returning {entry.Deadcoins} Deadcoins");
                try { DeadcoinLedger.Write(path, balance); }
                catch (Exception undo)
                {
                    Plugin.Log.LogError($"NpcValheim: could NOT return {entry.Deadcoins} Deadcoins to {who} " +
                                        $"(balance file should read {balance}): {undo.Message}");
                }
                Refuse(sender, 0, "a pass that could not be written", "Falha ao registrar o passe. Nada foi cobrado.", balance);
                return;
            }

            Plugin.Log.LogInfo($"NpcValheim: {who} bought the {entry.Boss} pass for {entry.Deadcoins} Deadcoins at {where} ({balance} -> {after})");
            try { DeadcoinLedger.AppendLogLine($"{who} bought the {entry.Boss} pass for {entry.Deadcoins} Deadcoins (balance {balance} -> {after})"); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }

            Send(sender, "granted", entry.Boss + "\n" + Int(after) + "\n0\n" + BossPassLedger.KindDeadcoins + "\n" +
                                    $"{BossPassCatalog.ShopName(entry)} liberada! Passe de {entry.Name} pago com {entry.Deadcoins} Deadcoins.");
        }

        /// <summary>
        /// The second step of a Coins purchase: the client has taken the quoted coins out of
        /// its bag and sends the token back.
        ///
        /// A token is good once, for the character it was issued to. One that is unknown --
        /// forged, used, or older than a server restart -- is refused without a refund, because
        /// a refund for a token the server cannot account for is exactly the coin faucet this
        /// two-step exists to close. A valid token whose pass cannot be granted is refunded its
        /// own quoted price, and only that.
        /// </summary>
        private static void RPC_Pay(long sender, string token)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            // An honest client sends one of these per purchase, and its coins are already gone
            // when it does, so the limit only has to stop a flood, not a second click.
            if (!NpcRequestGuard.AllowRate(sender, "bosspass-pay", 10, 2f)) return;

            PruneQuotes();
            if (string.IsNullOrEmpty(token) || !Quotes.TryGetValue(token, out var quote))
            {
                Plugin.Log.LogWarning($"NpcValheim: Boss pass payment from peer {sender} with an unknown token \"{token}\"");
                Refuse(sender, 0, "an unknown payment token", "Pagamento não reconhecido pelo servidor.");
                return;
            }

            long playerId = GameApi.GetPlayerId(sender);
            if (playerId != quote.PlayerId)
            {
                Plugin.Log.LogWarning($"NpcValheim: Boss pass token for {quote.PlayerId} presented by peer {sender} ({playerId})");
                Refuse(sender, 0, "someone else's token", "Pagamento não reconhecido pelo servidor.");
                return;
            }
            Quotes.Remove(token);

            var entry = BossPassCatalog.Find(quote.Boss);
            string shop = BossPassCatalog.ShopName(entry);
            string name = entry?.Name ?? quote.Boss;

            bool granted;
            try
            {
                granted = BossPassLedger.Grant(playerId, quote.Name, quote.Boss, $"gold:{quote.Price}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: {quote.Boss} pass for {quote.Name} ({playerId}) not written: {e.Message}; refunding {quote.Price}");
                Refuse(sender, quote.Price, "a pass that could not be written", "Falha ao registrar o passe. Suas moedas foram devolvidas.");
                return;
            }

            if (!granted)
            {
                // Won by a kill between the quote and the payment.
                Refuse(sender, quote.Price, $"{playerId} already holds {quote.Boss}",
                    $"Você já tinha o passe de {name}. Suas moedas foram devolvidas.");
                return;
            }

            Plugin.Log.LogInfo($"NpcValheim: {quote.Name} ({playerId}) bought the {quote.Boss} pass for {quote.Price} Coins at {quote.Where}");
            Send(sender, "granted", quote.Boss + "\n-1\n0\n" + BossPassLedger.KindGold + "\n" +
                                    $"{shop} liberada! Passe de {name} pago com {quote.Price} moedas.");
        }

        private static void PruneQuotes()
        {
            if (Quotes.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            foreach (var stale in Quotes.Where(q => q.Value.Expires < now).Select(q => q.Key).ToList())
                Quotes.Remove(stale);
        }

        // ---- server: a boss died ----

        /// <summary>A pass won in a fight, called by BossKillWatcher for each character who
        /// was there. Says so to that player when they are online, which they are -- they were
        /// just standing next to the boss.</summary>
        internal static bool GrantForKill(long peer, long playerId, string name, BossPassEntry entry)
        {
            bool granted;
            try
            {
                granted = BossPassLedger.Grant(playerId, name, entry.Boss, "kill");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: {entry.Boss} pass for {name} ({playerId}) not written: {e.Message}");
                return false;
            }

            if (granted && peer != 0L)
                Send(peer, "unlocked", entry.Boss + "\n" +
                                       $"Vitória sobre {entry.Name}! {BossPassCatalog.ShopName(entry)} liberada.");
            return granted;
        }

        // ---- server -> client ----

        private static void Refuse(long sender, int refund, string reason, string message, int balance = -1)
        {
            Plugin.Log.LogWarning($"NpcValheim: refused a boss pass purchase from peer {sender}: {reason}" +
                                  (refund > 0 ? $" (refunding {refund} Coins)" : ""));
            Send(sender, "refused", Int(refund) + "\n" + Int(balance) + "\n" + message);
        }

        private static void SendStatusQuietly(long sender, long playerId)
        {
            try
            {
                Send(sender, "status", Int(ReadDeadcoins(sender)) + "\n" + PassList(playerId));
            }
            catch
            {
                // Only a refresh of what the panel shows; the refusal already said what happened.
            }
        }

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
                case "status" when parts.Length == 2 && TryInt(parts[0], out int balance):
                    DeadcoinBalance = balance;
                    OwnPasses.Clear();
                    foreach (var pass in parts[1].Split(','))
                    {
                        int eq = pass.IndexOf('=');
                        string boss = eq < 0 ? pass : pass.Substring(0, eq);
                        if (boss.Length > 0) OwnPasses[boss] = eq < 0 ? BossPassLedger.KindAdmin : pass.Substring(eq + 1);
                    }
                    Known = true;
                    break;

                case "notice" when parts.Length == 1:
                    Tell(parts[0]);
                    break;

                case "pay" when parts.Length == 3 && TryInt(parts[2], out int price):
                    Pay(parts[0], parts[1], price);
                    break;

                case "granted" when parts.Length == 5 && TryInt(parts[1], out int balance) &&
                                    TryInt(parts[2], out int change):
                    OwnPasses[parts[0]] = parts[3];
                    if (balance >= 0) DeadcoinBalance = balance;
                    EndPurchase();
                    GiveBack(change);
                    Announce(parts[4]);
                    break;

                case "unlocked" when parts.Length == 2:
                    OwnPasses[parts[0]] = BossPassLedger.KindKill;
                    Announce(parts[1]);
                    break;

                case "refused" when parts.Length == 3 && TryInt(parts[0], out int refund) &&
                                    TryInt(parts[1], out int balance):
                    if (balance >= 0) DeadcoinBalance = balance;
                    EndPurchase();
                    GiveBack(refund);
                    Announce(parts[2]);
                    break;

                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed boss pass answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        /// <summary>The server accepted a Coins purchase: take the coins and send the token
        /// back. A quote that arrives with no purchase in flight was not asked for here, and
        /// is ignored rather than paid.</summary>
        private static void Pay(string token, string boss, int price)
        {
            var player = Player.m_localPlayer;
            if (!PurchaseInFlight || player == null || price <= 0) return;

            if (!MarketplaceNpc.TryPay(player, price))
            {
                EndPurchase();
                Announce($"Você tem {MarketplaceNpc.CoinsOf(player)} moedas; o passe custa {price}.");
                return;
            }

            if (ZRoutedRpc.instance == null)
            {
                MarketplaceNpc.GiveCoins(player, price);
                EndPurchase();
                Announce("O pagamento não chegou ao servidor. Suas moedas foram devolvidas.");
                return;
            }

            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcPay, new object[] { token });
            Tell($"Pagando {price} moedas pelo passe...");
        }

        private static void GiveBack(int coins)
        {
            var player = Player.m_localPlayer;
            if (coins > 0 && player != null) MarketplaceNpc.GiveCoins(player, coins);
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
}
