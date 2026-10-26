using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim.Npc
{
    public class MailRecipient
    {
        public string Name;
        public long PlayerId;
    }

    /// <summary>
    /// The post office. Market proceeds and unsold stock arrive here, and players can write
    /// to any character the server has seen, with or without a parcel. The box itself is a
    /// placeable piece (the WoW mailbox mesh), not a Player clone -- furniture does not need
    /// hair and armor.
    ///
    /// Every request goes to the server through the service channel, and every answer comes
    /// back to the player through a global RPC. The box used to answer ZNetView RPCs, which
    /// run on whichever peer owns its ZDO -- a nearby player as often as the server. That
    /// player's machine then read and wrote its own empty LiteDB: the box showed nothing,
    /// letters went into a file nobody else reads, the address book only held whoever that
    /// player had met, and a parcel the sender had already taken out of the bag was lost.
    /// </summary>
    public class MailboxNpc : NpcBase
    {
        private const int MaxClaimsPerRequest = 20;
        /// <summary>Names in one directory answer. Far above any real player count; it only
        /// keeps a corrupt directory from producing an unbounded packet.</summary>
        private const int MaxDirectoryEntries = 2000;

        public override float NameplateHeight => 1.85f;
        public override bool ShowsAppearanceTab => false;
        protected override string DefaultNpcName => "Caixa Postal";

        internal const string ActionRead = "RPC_RequestMail";
        internal const string ActionDirectory = "RPC_RequestDirectory";
        internal const string ActionClaim = "RPC_ClaimMail";
        internal const string ActionClaimAll = "RPC_ClaimAllMail";
        internal const string ActionMarkRead = "RPC_MarkMailRead";
        internal const string ActionSend = "RPC_SendMail";

        private const string RpcResponse = "NpcValheim_MailResponse";

        // ---- client cache ----
        //
        // One player, one pile of mail: every box in the world shows the same letters, and the
        // answer is addressed to the player rather than to a box. So the cache is too.

        private static List<MailEntry> _mail = new List<MailEntry>();
        private static List<MailRecipient> _recipients = new List<MailRecipient>();
        private static string _status = "";
        private static bool _synced;
        private static bool _directoryOnce;

        public List<MailEntry> CachedMail => _mail;
        public List<MailRecipient> CachedRecipients => _recipients;
        public string LastStatus { get => _status; set => _status = value ?? ""; }
        public bool HasSyncedOnce => _synced;
        public bool HasDirectoryOnce => _directoryOnce;

        /// <summary>Called once per connection (see ServiceNpcAuthority.TryRegister). What is
        /// cached belongs to the previous session, possibly on another server.</summary>
        internal static void Register(ZRoutedRpc rpc)
        {
            _mail = new List<MailEntry>();
            _recipients = new List<MailRecipient>();
            _status = "";
            _synced = _directoryOnce = false;
            _rememberedThisSession.Clear();
            rpc.Register(RpcResponse, (Action<long, string, string>)RPC_Response);
        }

        protected override void OnPlacedExtra()
        {
            EnsureMailboxName();
        }

        private void Start()
        {
            EnsureMailboxName();
        }

        private void EnsureMailboxName()
        {
            if (Nview == null || !Nview.IsValid()) return;
            var name = Nview.GetZDO().GetString(ZdoKeys.Name, "");
            if (!string.IsNullOrWhiteSpace(name) && name != "NPC") return;
            if (ZNet.instance != null && !ZNet.instance.IsServer()) return;
            Nview.GetZDO().Set(ZdoKeys.Name, "Caixa Postal");
        }

        /// <summary>Hammer placement lands on this object directly (it is a static piece, not
        /// a stub-spawned Player). Piece.GetCreator is who put it down.</summary>
        private void OnPlaced()
        {
            var piece = GetComponent<Piece>();
            InitializeAfterSpawn(piece != null ? piece.GetCreator() : 0L);
        }

        // ---- client -> server ----

        public void RequestMail() => Send(ActionRead, "");

        public void RequestDirectory() => Send(ActionDirectory, "");

        public void RequestClaim(string mailId)
        {
            if (string.IsNullOrEmpty(mailId)) return;
            Send(ActionClaim, mailId);
        }

        public void RequestClaimAll() => Send(ActionClaimAll, "");

        /// <summary>The player opened a letter in Correio.</summary>
        public void RequestMarkRead(string mailId)
        {
            if (string.IsNullOrEmpty(mailId)) return;
            Send(ActionMarkRead, mailId);
        }

        public void RequestSend(string target, string subject, string body) =>
            Send(ActionSend, PackSend(target, subject, body, "", 1, 0));

        /// <summary>Envia carta com um stack de item anexado. O cliente já removeu o item do
        /// inventário antes de chamar este método; o servidor valida e, se recusar, devolve.</summary>
        public void RequestSendWithAttachment(string target, string subject, string body,
            string itemName, int quality, int amount) =>
            Send(ActionSend, PackSend(target, subject, body, itemName, quality, amount));

        private void Send(string action, string payload)
        {
            if (InvokeServiceAction(action, payload)) return;
            _status = "O pedido não chegou ao servidor. Tente de novo.";
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, _status, 0, null);
        }

        // ---- server ----

        internal override bool DispatchServiceAction(long sender, string action, string payload)
        {
            switch (action)
            {
                case ActionRead: ServeRead(sender); return true;
                case ActionDirectory: ServeDirectory(sender); return true;
                case ActionClaim: ServeClaim(sender, payload ?? ""); return true;
                case ActionClaimAll: ServeClaimAll(sender); return true;
                case ActionMarkRead: ServeMarkRead(sender, payload ?? ""); return true;
                case ActionSend: ServeSend(sender, payload ?? ""); return true;
                default: return base.DispatchServiceAction(sender, action, payload);
            }
        }

        private void ServeRead(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-read", out string refusal, 6f, 12, 2f))
            {
                ReplyStatus(sender, "Chegue mais perto da caixa postal.");
                Plugin.Log.LogInfo($"NpcValheim: mailbox read refused for peer {sender} -- {refusal}");
                return;
            }
            GameApi.RememberIdentity(sender);
            SendMailTo(sender);
        }

        private void ServeDirectory(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-directory", out string refusal, 6f, 6, 2f))
            {
                ReplyStatus(sender, "Chegue mais perto da caixa postal.");
                Plugin.Log.LogInfo($"NpcValheim: mailbox directory refused for peer {sender} -- {refusal}");
                return;
            }
            GameApi.RememberIdentity(sender);
            RememberOnlinePlayers(force: true);
            SendDirectoryTo(sender);
        }

        private void ServeClaim(long sender, string mailId)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-claim", out string refusal, 6f, 8, 2f))
            {
                Plugin.Log.LogWarning($"NpcValheim: mail claim {mailId} refused for peer {sender} -- {refusal}");
                ReplyStatus(sender, "Chegue mais perto da caixa postal e tente de novo.");
                return;
            }
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                ReplyStatus(sender, "O servidor não identificou seu personagem. Tente de novo.");
                return;
            }

            // Looked up the way the box lists it, across every id of this character (see
            // PlayerDirectory.IdsFor), and claimed under the id it is actually filed under. A
            // claim against the current id alone refused letters the box was showing.
            var owned = MailDatabase.GetMail(playerId, GameApi.GetPlayerName(sender))
                .FirstOrDefault(entry => entry.Id == mailId);
            if (owned == null)
                ReplyStatus(sender, "Esta encomenda não está mais na sua caixa.");
            else if (!TryDeliverClaim(sender, mailId, owned.PlayerId))
                ReplyStatus(sender, "Não foi possível entregar esta encomenda; ela continua guardada.");
            SendMailTo(sender);
        }

        private void ServeClaimAll(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-claim-all", out string refusal, 6f, 2, 5f))
            {
                Plugin.Log.LogWarning($"NpcValheim: mail claim-all refused for peer {sender} -- {refusal}");
                ReplyStatus(sender, "Espere um instante e tente de novo, perto da caixa postal.");
                return;
            }
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                ReplyStatus(sender, "O servidor não identificou seu personagem. Tente de novo.");
                return;
            }

            int delivered = 0, failed = 0;
            foreach (var entry in MailDatabase.GetMail(playerId, GameApi.GetPlayerName(sender)).Take(MaxClaimsPerRequest))
            {
                if (TryDeliverClaim(sender, entry.Id, entry.PlayerId)) delivered++;
                else failed++;
            }
            if (failed > 0)
                ReplyStatus(sender, $"{delivered} entregue(s); {failed} não puderam ser entregues e continuam guardadas.");
            else if (delivered >= MaxClaimsPerRequest)
                ReplyStatus(sender, $"{delivered} entregue(s). Clique de novo para receber o resto.");
            SendMailTo(sender);
        }

        private void ServeMarkRead(long sender, string mailId)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-mark-read", 6f, 12, 2f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            MailDatabase.MarkRead(mailId, playerId);
            SendMailTo(sender);
        }

        /// <summary>
        /// A letter, with or without a parcel. When there is a parcel the sender's client has
        /// already taken it out of the bag, so every refusal from the moment the request can
        /// be read hands it back -- a refused letter used to keep the items, and a recipient
        /// with a full box swallowed them while the sender was told it went through.
        /// </summary>
        private void ServeSend(long sender, string packed)
        {
            if (!TryUnpackSend(packed, out var target, out var subject, out var body,
                    out var attachItem, out var attachQuality, out var attachAmount))
            {
                Plugin.Log.LogWarning($"NpcValheim: malformed mail from peer {sender}: \"{packed}\"");
                ReplyStatus(sender, "Mensagem inválida.");
                return;
            }

            bool hasAttachment = attachItem.Length > 0 && attachAmount > 0;
            // Only something that could really have left a bag is ever handed back.
            var prefab = hasAttachment ? ObjectDB.instance?.GetItemPrefab(attachItem) : null;
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            if (hasAttachment && (shared == null ||
                                  attachQuality > Mathf.Max(1, shared.m_maxQuality) ||
                                  attachAmount > ItemSpawner.MaxDeliverableAmount(attachItem)))
            {
                Plugin.Log.LogWarning($"NpcValheim: refused a parcel of {attachAmount}x {attachItem} from peer {sender}: not a real stack");
                ReplyStatus(sender, "Anexo inválido.");
                return;
            }

            void Refuse(string why, string message)
            {
                Plugin.Log.LogWarning($"NpcValheim: refused mail from peer {sender} to '{target}' -- {why}");
                ReplyStatus(sender, message);
                if (hasAttachment) ReturnAttachment(sender, attachItem, attachQuality, attachAmount);
            }

            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-send", out string refusal, 6f, 4, 5f))
            {
                Refuse(refusal, "Envio recusado; fique perto da caixa postal e tente de novo.");
                return;
            }
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L)
            {
                Refuse("no player id for the peer", "O servidor não identificou seu personagem. Tente de novo.");
                return;
            }
            GameApi.RememberIdentity(sender);

            if (target.Length == 0)
            {
                Refuse("no recipient", "Informe o destinatário.");
                return;
            }
            if (!hasAttachment && subject.Length == 0 && body.Length == 0)
            {
                Refuse("an empty letter", "Escreva um assunto ou uma mensagem.");
                return;
            }

            if (hasAttachment)
            {
                bool isVip = VipShopShortcut.SenderIsVip(sender);
                int cap = isVip ? MaxAmountForVip(shared) : Mathf.Max(1, shared.m_maxStackSize);
                if (attachAmount > cap)
                {
                    Refuse($"{attachAmount}x {attachItem} is over the {(isVip ? "VIP" : "non-VIP")} cap of {cap}",
                        isVip
                            ? $"VIP pode enviar no máximo {cap} unidade(s) desse item por carta."
                            : "Somente VIP pode enviar mais de 1 stack de item por carta.");
                    return;
                }
            }

            var known = PlayerDirectory.FindByName(target);
            if (known == null)
            {
                Refuse("unknown recipient",
                    "Jogador não encontrado. Confira o nome; ele precisa ter entrado no servidor ao menos uma vez.");
                return;
            }

            string senderName = GameApi.GetPlayerName(sender);
            var letter = hasAttachment
                ? MailDatabase.SendMessageWithAttachment(known.Id, playerId, senderName, subject, body,
                    attachItem, attachQuality, attachAmount)
                : MailDatabase.SendMessage(known.Id, playerId, senderName, subject, body);
            if (letter == null)
            {
                Refuse("the recipient's box is full", $"A caixa de {known.Name} está cheia.");
                return;
            }

            Plugin.Log.LogInfo(
                $"NpcValheim: mail from {senderName} ({playerId}) to {known.Name} ({known.Id})" +
                (hasAttachment ? $" with {attachAmount}x {attachItem}" : ""));
            ReplyStatus(sender, hasAttachment
                ? $"Pacote enviado para {known.Name}."
                : $"Mensagem enviada para {known.Name}.");
            SendMailTo(sender);
        }

        /// <summary>Máximo de quantidade que VIP pode enviar (VipMaxAttachments stacks completos).</summary>
        private static int MaxAmountForVip(ItemDrop.ItemData.SharedData shared)
        {
            int maxStack = Math.Max(1, shared.m_maxStackSize);
            int vipStacks = Math.Max(MailDatabase.MaxAttachmentsNonVip, Plugin.VipMaxAttachments.Value);
            long cap = (long)maxStack * vipStacks;
            return cap > int.MaxValue ? int.MaxValue : (int)cap;
        }

        /// <summary>Hands a parcel over. Coins become a real Coins stack rather than market
        /// balance, so mail is useful without ever touching a marketplace. A written letter
        /// has nothing to hand over -- claiming it just removes it.</summary>
        private static bool TryDeliverClaim(long sender, string mailId, long recipient)
        {
            string token = Guid.NewGuid().ToString("N");
            var entry = MailDatabase.BeginClaim(mailId, recipient, token);
            if (entry == null) return false;

            if (!Deliver(sender, entry, recipient))
            {
                MailDatabase.ReleaseClaim(mailId, recipient, token);
                return false;
            }

            if (MailDatabase.CompleteClaim(mailId, recipient, token)) return true;

            // Do not unlock after the parcel was sent: retrying could duplicate it. Keeping
            // the persisted "delivering" state makes the exceptional case visible and safe
            // for administrative reconciliation.
            Plugin.Log.LogError($"NpcValheim: delivered mail {mailId} but could not finalize its claim");
            return false;
        }

        /// <summary>Sends the parcel to the claimant's own machine, the only place their
        /// inventory can be written. Validated here so the client is only ever told to create
        /// something TrySpawn would also have accepted.</summary>
        private static bool Deliver(long sender, MailEntry entry, long recipient)
        {
            if (entry == null || entry.PlayerId != recipient) return false;

            string itemName;
            int amount, quality;
            if (entry.IsCoins)
            {
                itemName = MarketplaceNpc.CoinPrefabName;
                amount = entry.Coins;
                quality = 1;
            }
            else if (!string.IsNullOrEmpty(entry.ItemName) && entry.Amount > 0)
            {
                itemName = entry.ItemName;
                amount = entry.Amount;
                quality = Mathf.Max(1, entry.Quality);
            }
            else
            {
                // Written messages have no attachment. Claiming one is just an acknowledgement.
                return entry.IsMessage;
            }

            if (amount <= 0 || amount > ItemSpawner.MaxDeliverableAmount(itemName))
            {
                Plugin.Log.LogWarning($"NpcValheim: refused to deliver mail parcel {amount}x {itemName}");
                return false;
            }

            Plugin.Log.LogInfo($"NpcValheim: mail {entry.Id} delivered to {recipient}: {amount}x {itemName}");
            Respond(sender, "parcel", PackStack(itemName, quality, amount));
            return true;
        }

        private static void ReturnAttachment(long sender, string itemName, int quality, int amount)
        {
            Plugin.Log.LogInfo($"NpcValheim: returning a refused parcel of {amount}x {itemName} to peer {sender}");
            Respond(sender, "returned", PackStack(itemName, quality, amount));
        }

        /// <summary>`target` is a transient peer id -- fine for addressing the reply, wrong
        /// for looking anything up. Mail is filed under the stable character id, so it has to
        /// be resolved first. Anything that indexes player data must go through
        /// GameApi.GetPlayerId.</summary>
        private static void SendMailTo(long target)
        {
            long playerId = GameApi.GetPlayerId(target);
            if (playerId == 0L) return;
            string name = GameApi.GetPlayerName(target);
            var mail = MailDatabase.GetMail(playerId, name);
            Respond(target, "mail", MailWire.Pack(mail));
        }

        private static void SendDirectoryTo(long target)
        {
            var sb = new StringBuilder();
            foreach (var player in PlayerDirectory.All().Take(MaxDirectoryEntries))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(player.Id.ToString(CultureInfo.InvariantCulture))
                  .Append(';').Append(MailWire.Sanitize(player.Name));
            }
            Respond(target, "directory", sb.ToString());
        }

        private static void ReplyStatus(long target, string message) => Respond(target, "status", message ?? "");

        private static void Respond(long peer, string kind, string payload) =>
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, RpcResponse, new object[] { kind, payload ?? "" });

        // ---- the address book ----

        private static readonly HashSet<long> _rememberedThisSession = new HashSet<long>();
        private static float _nextDirectorySweep;

        /// <summary>
        /// Server: files every connected character in the address book.
        ///
        /// The book used to learn a name only when that player touched a mailbox, or happened
        /// to be online while someone else opened the list -- and even that second path never
        /// worked on the dedicated server, which holds no Player objects for its peers. So the
        /// search could not find most of the server. Once per character per session is
        /// enough: a name does not change while connected.
        /// </summary>
        internal static void RememberOnlinePlayers(bool force = false)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!force && Time.unscaledTime < _nextDirectorySweep) return;
            _nextDirectorySweep = Time.unscaledTime + 30f;

            foreach (var player in GameApi.ListOnlinePlayers())
            {
                if (player.Id == 0L || !_rememberedThisSession.Add(player.Id)) continue;
                try { PlayerDirectory.Remember(player.Id, player.Name); }
                catch (Exception e)
                {
                    _rememberedThisSession.Remove(player.Id);
                    Plugin.Log.LogWarning($"NpcValheim: could not file {player.Name} in the mail directory: {e.Message}");
                }
            }
        }

        // ---- server -> client ----

        private static void RPC_Response(long sender, string kind, string payload)
        {
            if (!ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            switch (kind)
            {
                case "mail":
                    _mail = MailWire.Unpack(payload);
                    _synced = true;
                    break;
                case "directory":
                    _recipients = UnpackDirectory(payload);
                    _directoryOnce = true;
                    break;
                case "status":
                    _status = payload ?? "";
                    break;
                case "parcel":
                    ReceiveStack(payload, "Recebido");
                    break;
                case "returned":
                    ReceiveStack(payload, "Pacote devolvido");
                    break;
                default:
                    Plugin.Log.LogWarning($"NpcValheim: malformed Correio answer '{kind}': \"{payload}\"");
                    break;
            }
        }

        /// <summary>Client side: a parcel arrives, or a refused one comes back. The bag first;
        /// only what does not fit goes on the ground, at the player's feet, and they are told.</summary>
        private static void ReceiveStack(string packed, string caption)
        {
            var player = Player.m_localPlayer;
            if (!TryUnpackStack(packed, out string itemName, out int quality, out int amount) || player == null)
            {
                Plugin.Log.LogError($"NpcValheim: could not hand over a mail parcel \"{packed}\"");
                return;
            }

            int given = ItemSpawner.GiveToInventory(player, itemName, amount, quality);
            Plugin.Log.LogInfo($"NpcValheim: mail parcel {amount}x {itemName}: {given} went into the bag");
            if (given > 0)
                player.Message(MessageHud.MessageType.TopLeft,
                    $"{caption}: {given}x {ItemNames.Display(itemName)}", given, null);

            int left = amount - given;
            if (left <= 0) return;

            ItemSpawner.TrySpawn(itemName, left, quality,
                player.transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f);
            player.Message(MessageHud.MessageType.Center,
                $"Inventário cheio: {left}x {ItemNames.Display(itemName)} caiu no chão", 0, null);
        }

        // ---- wire ----

        private static string PackStack(string itemName, int quality, int amount) =>
            itemName + ";" + quality.ToString(CultureInfo.InvariantCulture) + ";" +
            amount.ToString(CultureInfo.InvariantCulture);

        private static bool TryUnpackStack(string packed, out string itemName, out int quality, out int amount)
        {
            itemName = "";
            quality = 1;
            amount = 0;
            var parts = (packed ?? "").Split(';');
            if (parts.Length != 3 || parts[0].Length == 0) return false;
            itemName = parts[0];
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out quality) ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)) return false;
            quality = Mathf.Max(1, quality);
            return amount > 0;
        }

        /// <summary>"target;subject;body;item;quality;amount". The body is escaped (MailWire),
        /// so none of the six fields holds a ';'.</summary>
        private static string PackSend(string target, string subject, string body,
            string itemName, int quality, int amount) =>
            MailWire.Sanitize(target) + ";" + MailWire.Sanitize(subject) + ";" + MailWire.EscapeBody(body) + ";" +
            MailWire.Sanitize(itemName ?? "") + ";" +
            quality.ToString(CultureInfo.InvariantCulture) + ";" +
            amount.ToString(CultureInfo.InvariantCulture);

        internal static bool TryUnpackSend(string packed, out string target, out string subject, out string body,
            out string attachItem, out int attachQuality, out int attachAmount)
        {
            target = subject = body = attachItem = "";
            attachQuality = 1;
            attachAmount = 0;

            var p = (packed ?? "").Split(';');
            if (p.Length != 6) return false;
            if (!int.TryParse(p[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out attachQuality) ||
                !int.TryParse(p[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out attachAmount) ||
                attachAmount < 0) return false;

            target = p[0].Trim();
            subject = p[1].Trim();
            body = MailWire.UnescapeBody(p[2]).Trim();
            attachItem = p[3].Trim();
            attachQuality = Math.Max(1, attachQuality);
            return true;
        }

        private static List<MailRecipient> UnpackDirectory(string packed)
        {
            var result = new List<MailRecipient>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= MaxDirectoryEntries) break;
                if (line.Length > 512) continue;
                int split = line.IndexOf(';');
                if (split <= 0 || split == line.Length - 1) continue;
                result.Add(new MailRecipient
                {
                    PlayerId = long.TryParse(line.Substring(0, split), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var id) ? id : 0L,
                    Name = line.Substring(split + 1),
                });
            }
            return result;
        }
    }
}
