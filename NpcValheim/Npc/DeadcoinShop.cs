using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BepInEx;
using UnityEngine;

namespace NpcValheim.Npc
{
    /// <summary>One line of the Deadcoins counter: which item, how many per purchase, and
    /// what it costs.</summary>
    public sealed class DeadcoinOffer
    {
        public string Prefab;
        public int Amount;
        public int Price;
    }

    /// <summary>
    /// The Deadcoins price list, read from the DeadcoinShop.Items config.
    ///
    /// Same text as the ShopItems line of the old DonationShop mod
    /// ("prefab=X;amount=N;price=P|..."), so the server's existing value can be pasted over
    /// as it is. The one difference is that a bad entry is skipped and named in the log
    /// instead of being half-read: the old default opened with
    /// "prefabs=Blueberriesamount=50;price=150", which no reader could make sense of.
    /// </summary>
    internal static class DeadcoinCatalog
    {
        /// <summary>The old DonationShop default, with its first entry repaired.</summary>
        internal const string DefaultItems =
            "prefab=Blueberries;amount=50;price=150|prefab=Raspberry;amount=50;price=150|" +
            "prefab=Thistle;amount=50;price=200|prefab=Cloudberry;amount=50;price=100|" +
            "prefab=Wood;amount=50;price=100|prefab=Stone;amount=50;price=100|" +
            "prefab=RoundLog;amount=50;price=100|prefab=FineWood;amount=50;price=150|" +
            "prefab=IronNails;amount=10;price=110|prefab=IronOre;amount=50;price=700|" +
            "prefab=SilverOre;amount=50;price=1000|prefab=GreydwarfEye;amount=500;price=250|" +
            "prefab=SurtlingCore;amount=100;price=100|prefab=PortalToken;amount=1;price=750|" +
            "prefab=ResetToken;amount=1;price=250|prefab=Coins;amount=1000;price=500";

        /// <summary>
        /// Every well-formed entry, in the order written. Pure text -- whether the item exists
        /// is <see cref="Available"/>'s question, because only a running game can answer it.
        ///
        /// A non-positive amount or price is refused rather than clamped: a price of 0 hands the
        /// item out for free, and a negative one is exactly how the old mod let a modified
        /// client raise its own balance.
        /// </summary>
        internal static List<DeadcoinOffer> Parse(string raw, List<string> problems)
        {
            var offers = new List<DeadcoinOffer>();
            if (string.IsNullOrWhiteSpace(raw)) return offers;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in raw.Split('|'))
            {
                var text = entry.Trim();
                if (text.Length == 0) continue;

                string prefab = null;
                bool hasAmount = false, hasPrice = false;
                int amount = 0, price = 0;
                foreach (var field in text.Split(';'))
                {
                    int equals = field.IndexOf('=');
                    if (equals <= 0) continue;
                    var key = field.Substring(0, equals).Trim();
                    var value = field.Substring(equals + 1).Trim();

                    if (key.Equals("prefab", StringComparison.OrdinalIgnoreCase))
                        prefab = value;
                    else if (key.Equals("amount", StringComparison.OrdinalIgnoreCase))
                        hasAmount = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount);
                    else if (key.Equals("price", StringComparison.OrdinalIgnoreCase))
                        hasPrice = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out price);
                }

                if (string.IsNullOrEmpty(prefab) || !hasAmount || !hasPrice)
                {
                    problems?.Add($"malformed entry \"{text}\"");
                    continue;
                }
                if (amount <= 0 || price <= 0)
                {
                    problems?.Add($"{prefab}: amount and price must be positive (amount={amount}, price={price})");
                    continue;
                }
                if (!seen.Add(prefab))
                {
                    problems?.Add($"{prefab} is listed more than once; the first entry wins");
                    continue;
                }

                offers.Add(new DeadcoinOffer { Prefab = prefab, Amount = amount, Price = price });
            }
            return offers;
        }

        /// <summary>The offers that can actually be handed over on this machine: a real item,
        /// in an amount one delivery can carry. Anything else is left off the counter, so the
        /// server never charges for something the buyer's client would fail to create.</summary>
        internal static List<DeadcoinOffer> Available(string raw, List<string> problems)
        {
            var result = new List<DeadcoinOffer>();
            foreach (var offer in Parse(raw, problems))
            {
                int max = ItemSpawner.MaxDeliverableAmount(offer.Prefab);
                if (max <= 0)
                {
                    problems?.Add($"{offer.Prefab} is not an item this game knows");
                    continue;
                }
                if (offer.Amount > max)
                {
                    problems?.Add($"{offer.Prefab}: {offer.Amount} is more than one delivery carries ({max})");
                    continue;
                }
                result.Add(offer);
            }
            return result;
        }

        private static string _reported;

        /// <summary>The server's own list, read now. Parsed on every call, so saving the cfg
        /// while the server runs changes the next purchase; the problems are logged only when
        /// the text changes, or every click would repeat them.</summary>
        internal static List<DeadcoinOffer> Current()
        {
            string raw = Plugin.DeadcoinShopItems?.Value ?? "";
            var problems = new List<string>();
            var offers = Available(raw, problems);

            if (!string.Equals(raw, _reported, StringComparison.Ordinal))
            {
                _reported = raw;
                foreach (var problem in problems)
                    Plugin.Log.LogWarning("NpcValheim: DeadcoinShop.Items -- " + problem);
                Plugin.Log.LogInfo($"NpcValheim: Deadcoins counter lists {offers.Count} item(s)");
            }
            return offers;
        }
    }

    /// <summary>
    /// Deadcoin balances, one file per player, in the folder the DonationShop mod used:
    /// BepInEx/config/DonationShop/&lt;name&gt;-&lt;account&gt;.json holding a bare number.
    /// Donations are still credited by editing those files, and every balance already on the
    /// server carries over untouched.
    ///
    /// Only the server reads or writes them, and it decides which file is whose from the
    /// connection itself. The old mod used an id the client sent, so a client could spend
    /// another player's balance by naming their account.
    /// </summary>
    internal static class DeadcoinLedger
    {
        internal static string Folder => Path.Combine(Paths.ConfigPath, "DonationShop");

        private static string LogPath => Path.Combine(Path.Combine(Folder, "log"), "log.txt");

        /// <summary>
        /// The account part of a file name, as the old mod wrote it: PlayFabManager's custom id
        /// printed as "Steam_7656...". A crossplay (PlayFab) socket reports its peer in exactly
        /// that form; a Steam socket reports the bare SteamID, which ZNet.ListContainsId also
        /// reads as a Steam account, so it gets the same prefix here.
        /// </summary>
        internal static string CanonicalAccountId(string hostName)
        {
            if (string.IsNullOrWhiteSpace(hostName)) return "";
            hostName = hostName.Trim();
            return hostName.IndexOf('_') > 0 ? hostName : "Steam_" + hostName;
        }

        /// <summary>"&lt;name&gt;-&lt;account&gt;.json", or null when the result could name
        /// anything outside the folder. Separators are refused on every OS, not only the
        /// server's: the file has to mean the same thing wherever it is opened.</summary>
        internal static string FileNameFor(string playerName, string accountId)
        {
            if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(accountId)) return null;

            var name = playerName + "-" + accountId + ".json";
            if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            return name;
        }

        /// <summary>The balance file of the player behind an RPC sender.</summary>
        internal static bool TryResolveAccount(long sender, out string path, out string who)
        {
            path = null;
            string name = GameApi.GetPlayerName(sender);
            string host = GameApi.GetPlatformUserId(sender);

            // Host/solo: the buyer is this process, which has no peer to ask.
            if (string.IsNullOrEmpty(host) && ServiceNpcAuthority.IsAuthoritativeSender(sender))
                host = PlayFabManager.m_customId.ToString();

            string account = CanonicalAccountId(host);
            who = $"{name} ({(account.Length > 0 ? account : "no account")})";
            if (name == "???" || account.Length == 0) return false;

            var file = FileNameFor(name, account);
            if (file == null) return false;
            path = Path.Combine(Folder, file);
            return true;
        }

        /// <summary>
        /// Reads a balance. No file means never credited, which is 0 and not an error.
        ///
        /// A file that does not hold a number is an error, though. Reading it as 0 would let
        /// the next purchase overwrite whatever an admin was in the middle of writing there.
        /// </summary>
        internal static bool TryRead(string path, out int balance)
        {
            balance = 0;
            if (!File.Exists(path)) return true;
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out balance);
        }

        /// <summary>Creates an empty (0) balance file the first time a player opens the counter,
        /// as the old mod did. An admin crediting a donation needs the exact file name, and this
        /// is where they find it.</summary>
        internal static void EnsureExists(string path)
        {
            if (File.Exists(path)) return;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, "0");
        }

        internal static void Write(string path, int balance)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(path, balance.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>One line per purchase in the log the old mod kept, now with a timestamp, a
        /// line break and both balances -- enough to settle a "my Deadcoins vanished" from the
        /// file alone.</summary>
        internal static void AppendLog(string who, DeadcoinOffer offer, int before, int after)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {who} bought {offer.Amount}x {offer.Prefab} " +
                $"for {offer.Price} Deadcoins (balance {before} -> {after}){Environment.NewLine}");
        }
    }

    /// <summary>
    /// Both halves of the Deadcoins counter: the purchase rules the server runs, and the
    /// client's view of its own balance.
    ///
    /// None of it belongs to an NPC. The price list is the server's config and the balance is
    /// the player's file, so the NPC is only a way in. There are two ways in: the NPC's own
    /// service channel, and a VIP-only request that needs no NPC (see RPC_RemoteRequest).
    ///
    /// Answers are addressed to the player, not to the NPC: a purchase has already been charged
    /// by the time its answer is sent, and an answer routed through the NPC would be dropped if
    /// that NPC unloaded on the buyer's machine in the meantime.
    /// </summary>
    internal static class DeadcoinShop
    {
        private const string RpcResponse = "NpcValheim_DeadcoinResponse";
        private const string RpcRemoteRequest = "NpcValheim_DeadcoinRemoteRequest";
        private const float PurchaseTimeout = 10f;

        internal const string ActionBalance = "RPC_DeadcoinBalance";
        internal const string ActionBuy = "RPC_DeadcoinBuy";

        /// <summary>The last balance the server reported, or -1 before it has answered.</summary>
        internal static int Balance { get; private set; } = -1;

        /// <summary>The last thing the server said about a purchase, for the panel's status line.</summary>
        internal static string LastMessage { get; private set; }

        /// <summary>Goes up with every new <see cref="LastMessage"/>, so the panel can tell a
        /// repeated message from one it has already shown.</summary>
        internal static int MessageRevision { get; private set; }

        private static float _purchaseSentAt = -PurchaseTimeout;

        /// <summary>Called once per connection (see ServiceNpcAuthority.TryRegister). The last
        /// balance belongs to the previous session, possibly on another server.</summary>
        internal static void Register(ZRoutedRpc rpc)
        {
            Balance = -1;
            LastMessage = null;
            EndPurchase();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
            rpc.Register(RpcRemoteRequest, (Action<long, string, string>)RPC_RemoteRequest);
        }

        // ---- client -> server ----

        /// <summary>At the counter when `at` is given, remotely (VIP, F7) when it is null.</summary>
        internal static bool RequestBalance(DeadcoinShopNpc at) =>
            at != null ? at.RequestService(ActionBalance, "") : RequestRemote(ActionBalance, "");

        internal static bool RequestBuy(DeadcoinShopNpc at, DeadcoinOffer offer)
        {
            if (offer == null) return false;
            string payload = offer.Prefab + "\n" + offer.Price.ToString(CultureInfo.InvariantCulture);
            return at != null ? at.RequestService(ActionBuy, payload) : RequestRemote(ActionBuy, payload);
        }

        private static bool RequestRemote(string action, string payload)
        {
            if (ZRoutedRpc.instance == null) return false;
            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcRemoteRequest,
                new object[] { action, payload ?? "" });
            return true;
        }

        /// <summary>One purchase in flight at a time, as the old Home panel had. The server's
        /// rate limit is what actually protects the ledger; this only keeps a double click from
        /// becoming two purchases.</summary>
        internal static bool TryBeginPurchase()
        {
            if (Time.realtimeSinceStartup - _purchaseSentAt < PurchaseTimeout) return false;
            _purchaseSentAt = Time.realtimeSinceStartup;
            return true;
        }

        internal static void EndPurchase() => _purchaseSentAt = -PurchaseTimeout;

        // ---- server ----

        /// <summary>
        /// The counter without an NPC, for VIPs pressing F7 (UI/VipShopShortcut). This is the
        /// only thing in the mod that can be used from anywhere; every other NPC has to be
        /// visited.
        ///
        /// VIP status, checked here on the server, is what entitles a player to it. The client
        /// only hides the key from non-VIPs, and a hidden key is not a boundary.
        /// </summary>
        private static void RPC_RemoteRequest(long sender, string action, string payload)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            if (!UI.VipShopShortcut.SenderIsVip(sender))
            {
                // The panel polls, so this would otherwise repeat every few seconds.
                if (NpcRequestGuard.AllowRate(sender, "deadcoin-remote-denied", 1, 60f))
                    Plugin.Log.LogWarning($"NpcValheim: remote Deadcoins request '{action}' from non-VIP peer {sender}");

                // Said either way: a VIP the server does not recognise would otherwise sit in
                // front of "consultando o seu saldo" with no idea why.
                const string vipOnly = "A loja remota é só para VIP. Compre no NPC.";
                if (action == ActionBuy) SendRefusal(sender, -1, vipOnly);
                else SendNotice(sender, vipOnly);
                return;
            }

            switch (action)
            {
                case ActionBalance:
                    ServeBalance(sender);
                    break;
                case ActionBuy:
                    ServePurchase(sender, payload ?? "", "VIP remote (F7)");
                    break;
                default:
                    Plugin.Log.LogWarning($"NpcValheim: unknown remote Deadcoins request '{action}' from peer {sender}");
                    break;
            }
        }

        internal static void ServeBalance(long sender)
        {
            // The panel asks every few seconds while it is open, so that a donation credited by
            // hand shows up without reopening it.
            if (!NpcRequestGuard.AllowRate(sender, "deadcoin-balance", 6, 5f)) return;

            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Plugin.Log.LogWarning($"NpcValheim: no Deadcoins account for peer {sender} ({who})");
                SendNotice(sender, "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            try
            {
                DeadcoinLedger.EnsureExists(path);
                if (!DeadcoinLedger.TryRead(path, out int balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    SendNotice(sender, "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                SendBalance(sender, balance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not read the Deadcoins balance of {who}: {e.Message}");
                SendNotice(sender, "Falha ao ler o seu saldo. Tente de novo.");
            }
        }

        /// <summary>
        /// A purchase. `payload` is "prefab\nprice": the item, and the price the buyer's panel
        /// showed. That price is only compared, never charged.
        ///
        /// Every refusal says why, both in the server log and on the buyer's screen, and none of
        /// them touches the balance. The balance is written before the item is sent, so a
        /// failed write costs the player nothing. `where` names the way in, for the log.
        /// </summary>
        internal static void ServePurchase(long sender, string payload, string where)
        {
            Plugin.Log.LogInfo($"NpcValheim: 'deadcoin-buy' from peer {sender} at {where}: \"{payload}\"");

            if (!NpcRequestGuard.AllowRate(sender, "deadcoin-buy", 4, 2f))
            {
                Refuse(sender, -1, "the rate limit", "Muitos pedidos seguidos. Espere um instante.");
                return;
            }

            var parts = payload.Split('\n');
            if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int shownPrice))
            {
                Refuse(sender, -1, "a malformed request", "Pedido malformado.");
                return;
            }

            // The server's own list, read now. Neither the amount nor the price comes from the
            // client, and a prefab that is not on the list is not for sale at any price.
            string prefab = parts[0];
            var offer = DeadcoinCatalog.Current().Find(o => string.Equals(o.Prefab, prefab, StringComparison.Ordinal));
            if (offer == null)
            {
                Refuse(sender, -1, $"'{prefab}' is not on the list", "Este item não está à venda.");
                return;
            }
            if (shownPrice != offer.Price)
            {
                Refuse(sender, -1, $"the buyer saw {shownPrice}, the price is {offer.Price}",
                    $"O preço de {ItemNames.Display(offer.Prefab)} agora é {offer.Price} Deadcoins. Confira e compre de novo.");
                return;
            }

            if (!DeadcoinLedger.TryResolveAccount(sender, out string path, out string who))
            {
                Refuse(sender, -1, $"no account for {who}", "O servidor não conseguiu identificar a sua conta.");
                return;
            }

            int balance, after;
            try
            {
                if (!DeadcoinLedger.TryRead(path, out balance))
                {
                    Plugin.Log.LogError($"NpcValheim: Deadcoins balance of {who} is not a number: {path}");
                    Refuse(sender, -1, "an unreadable balance file",
                        "Seu saldo de Deadcoins está ilegível no servidor. Fale com um admin.");
                    return;
                }
                if (balance < offer.Price)
                {
                    Refuse(sender, balance, $"{who} has {balance}, the price is {offer.Price}",
                        $"Você não tem Deadcoins suficientes: saldo {balance}, custa {offer.Price}.");
                    return;
                }

                after = balance - offer.Price;
                DeadcoinLedger.Write(path, after);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim: could not charge {who} for {offer.Prefab}: {e.Message}");
                Refuse(sender, -1, "a ledger error", "Falha ao registrar a compra. Nada foi cobrado.");
                return;
            }

            SendDelivery(sender, offer, after);
            Plugin.Log.LogInfo(
                $"NpcValheim: {who} bought {offer.Amount}x {offer.Prefab} for {offer.Price} Deadcoins at {where} ({balance} -> {after})");

            // After the delivery on purpose: the purchase has happened either way, and a log
            // file that cannot be written must not stand between the player and the item.
            try { DeadcoinLedger.AppendLog(who, offer, balance, after); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: Deadcoins purchase log not written: {e.Message}"); }
        }

        private static void Refuse(long sender, int balance, string reason, string message)
        {
            Plugin.Log.LogWarning($"NpcValheim: refused a Deadcoins purchase from peer {sender}: {reason}");
            SendRefusal(sender, balance, message);
        }

        // ---- server -> client ----

        internal static void SendBalance(long peer, int balance) =>
            Send(peer, "balance", balance.ToString(CultureInfo.InvariantCulture));

        /// <summary>A refused purchase. `balance` is -1 when the server could not read it.</summary>
        internal static void SendRefusal(long peer, int balance, string message) =>
            Send(peer, "refused", balance.ToString(CultureInfo.InvariantCulture) + "\n" + message);

        /// <summary>A problem with the balance itself. Status line only: the panel asks for the
        /// balance every few seconds, and a centre-screen message on each ask would be noise.</summary>
        internal static void SendNotice(long peer, string message) => Send(peer, "notice", message);

        internal static void SendDelivery(long peer, DeadcoinOffer offer, int balance) =>
            Send(peer, "deliver", offer.Prefab + "\n" +
                                  offer.Amount.ToString(CultureInfo.InvariantCulture) + "\n" +
                                  balance.ToString(CultureInfo.InvariantCulture));

        private static void Send(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            var parts = (payload ?? "").Split('\n');

            switch (kind)
            {
                case "balance" when parts.Length == 1 && TryInt(parts[0], out int balance):
                    Balance = balance;
                    break;

                case "notice" when parts.Length == 1:
                    Tell(parts[0]);
                    break;

                case "refused" when parts.Length == 2 && TryInt(parts[0], out int balance):
                    if (balance >= 0) Balance = balance;
                    EndPurchase();
                    Tell(parts[1]);
                    Player.m_localPlayer?.Message(MessageHud.MessageType.Center, parts[1], 0, null);
                    break;

                case "deliver" when parts.Length == 3 && TryInt(parts[1], out int amount) &&
                                    TryInt(parts[2], out int balance):
                    Balance = balance;
                    EndPurchase();
                    Deliver(parts[0], amount);
                    Tell($"Compra concluída: {amount}x {ItemNames.Display(parts[0])}. " +
                         $"Saldo: {balance} Deadcoins.");
                    break;

                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed Deadcoins answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        /// <summary>Into the bag first, the ground only for what does not fit -- the same rule
        /// as the merchant's RPC_DeliverItem. The Deadcoins are already spent by the time this
        /// runs, so the one outcome that must not happen is the item not existing.</summary>
        private static void Deliver(string prefab, int amount)
        {
            var player = Player.m_localPlayer;
            if (player == null || amount <= 0 || amount > ItemSpawner.MaxDeliverableAmount(prefab))
            {
                Plugin.Log.LogError($"NpcValheim: could not hand over a paid Deadcoins purchase of {amount}x {prefab}");
                return;
            }

            int given = ItemSpawner.GiveToInventory(player, prefab, amount, 1);
            if (given > 0)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"Recebido: {given}x {ItemNames.Display(prefab)}", given, null);

            int left = amount - given;
            if (left <= 0) return;

            ItemSpawner.TrySpawn(prefab, left, 1,
                player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f);
            player.Message(MessageHud.MessageType.Center,
                $"Inventário cheio: {left}x {ItemNames.Display(prefab)} caiu no chão", 0, null);
        }

        private static void Tell(string message)
        {
            LastMessage = message;
            MessageRevision++;
        }

        private static bool TryInt(string text, out int value) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
