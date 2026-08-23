using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    public class MailRecipient
    {
        public bool IsHouse;
        public string Name;
        public long PlayerId;
        public int MemberCount;
        public bool IsMine;
    }

    /// <summary>
    /// The post office. Market proceeds and unsold stock still arrive here, and players can
    /// also write to any known character or to a house. The box itself is a placeable piece
    /// (the WoW mailbox mesh), not a Player clone -- furniture does not need hair and armor.
    ///
    /// Same authority model as everything else: the mailbox is read and emptied only on the
    /// peer that owns the ZDO, and a client only ever sees its own mail because the server
    /// answers per-sender.
    /// </summary>
    public class MailboxNpc : NpcBase
    {
        private const int MaxClaimsPerRequest = 20;
        public override float NameplateHeight => 1.85f;
        public override bool ShowsAppearanceTab => false;
        protected override string DefaultNpcName => "Caixa Postal";

        public List<MailEntry> CachedMail { get; private set; } = new List<MailEntry>();
        public List<MailRecipient> CachedRecipients { get; private set; } = new List<MailRecipient>();
        public string LastStatus { get; set; } = "";
        public bool HasSyncedOnce { get; private set; }
        public bool HasDirectoryOnce { get; private set; }

        /// <summary>Fired on the client that asked for mail, including stamp polls
        /// that never open the Caixa Postal panel.</summary>
        internal static event Action<List<MailEntry>> MailSynced;

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

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_RequestMail", (Action<long>)RPC_RequestMail);
            Nview.Register("RPC_MailData", (Action<long, string>)RPC_MailData);
            Nview.Register("RPC_ClaimMail", (Action<long, string>)RPC_ClaimMail);
            Nview.Register("RPC_ClaimAllMail", (Action<long>)RPC_ClaimAllMail);
            Nview.Register("RPC_RequestDirectory", (Action<long>)RPC_RequestDirectory);
            Nview.Register("RPC_DirectoryData", (Action<long, string>)RPC_DirectoryData);
            Nview.Register("RPC_SendMail", (Action<long, string>)RPC_SendMail);
            Nview.Register("RPC_CreateHouse", (Action<long, string>)RPC_CreateHouse);
            Nview.Register("RPC_InviteHouse", (Action<long, string>)RPC_InviteHouse);
            Nview.Register("RPC_KickHouse", (Action<long, string>)RPC_KickHouse);
            Nview.Register("RPC_MailStatus", (Action<long, string>)RPC_MailStatus);
            Nview.Register("RPC_MarkMailRead", (Action<long, string>)RPC_MarkMailRead);
        }

        public void RequestMail()
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_RequestMail");
        }

        public void RequestDirectory()
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_RequestDirectory");
        }

        public void RequestClaim(string mailId)
        {
            if (Nview == null || !Nview.IsValid() || string.IsNullOrEmpty(mailId)) return;
            Nview.InvokeRPC("RPC_ClaimMail", mailId);
        }

        public void RequestClaimAll()
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_ClaimAllMail");
        }

        /// <summary>The player opened a letter in Correio. Stamp polls must not call this.</summary>
        public void RequestMarkRead(string mailId)
        {
            if (Nview == null || !Nview.IsValid() || string.IsNullOrEmpty(mailId)) return;
            Nview.InvokeRPC("RPC_MarkMailRead", mailId);
        }

        public void RequestSend(bool toHouse, string target, string subject, string body)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_SendMail", PackSend(toHouse, target, subject, body));
        }

        public void RequestCreateHouse(string name)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_CreateHouse", name ?? "");
        }

        public void RequestInvite(string houseName, string playerName)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_InviteHouse", MailWire.Sanitize(houseName) + ";" + MailWire.Sanitize(playerName));
        }

        public void RequestKick(string houseName, long memberId)
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_KickHouse",
                MailWire.Sanitize(houseName) + ";" + memberId.ToString(CultureInfo.InvariantCulture));
        }

        private void RPC_RequestMail(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-read", 6f, 12, 2f)) return;
            RememberSender(sender);
            SendMailTo(sender);
        }

        private void RPC_MailData(long sender, string packed)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            CachedMail = MailWire.Unpack(packed);
            HasSyncedOnce = true;
            MailSynced?.Invoke(CachedMail);
        }

        private void RPC_ClaimMail(long sender, string mailId)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-claim", 6f, 8, 2f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            if (!TryDeliverClaim(mailId, playerId))
                ReplyStatus(sender, "Não foi possível entregar esta encomenda; ela continua guardada.");
            SendMailTo(sender);
        }

        private void RPC_ClaimAllMail(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-claim-all", 6f, 2, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            int delivered = 0;
            foreach (var entry in MailDatabase.GetMail(playerId).Take(MaxClaimsPerRequest))
            {
                if (TryDeliverClaim(entry.Id, playerId)) delivered++;
            }
            if (delivered >= MaxClaimsPerRequest)
                ReplyStatus(sender, $"{delivered} encomendas entregues. Abra novamente para continuar.");
            SendMailTo(sender);
        }

        private void RPC_RequestDirectory(long sender)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-directory", 6f, 6, 2f)) return;
            RememberSender(sender);
            foreach (var player in GameApi.ListOnlinePlayers())
                PlayerDirectory.Remember(player.Id, player.Name);
            Nview.InvokeRPC(sender, "RPC_DirectoryData", PackDirectory(GameApi.GetPlayerId(sender)));
        }

        private void RPC_DirectoryData(long sender, string packed)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            CachedRecipients = UnpackDirectory(packed);
            HasDirectoryOnce = true;
        }

        private void RPC_SendMail(long sender, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-send", 6f, 4, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            RememberSender(sender);

            if (!TryUnpackSend(packed, out var toHouse, out var target, out var subject, out var body))
            {
                ReplyStatus(sender, "Mensagem inválida.");
                return;
            }

            string senderName = GameApi.GetPlayerName(sender);
            if (toHouse)
            {
                var house = HouseDatabase.FindByName(target);
                if (house == null)
                {
                    ReplyStatus(sender, "Casa não encontrada.");
                    return;
                }
                int sent = MailDatabase.SendToHouse(house.Name, playerId, senderName, subject, body);
                ReplyStatus(sender, sent > 0
                    ? $"Enviado para a casa {house.Name} ({sent} membro(s))."
                    : "A casa não tem membros.");
            }
            else
            {
                var known = PlayerDirectory.FindByName(target);
                if (known == null)
                {
                    ReplyStatus(sender, "Jogador não encontrado. Ele precisa ter entrado no servidor ao menos uma vez.");
                    return;
                }
                MailDatabase.SendMessage(known.Id, playerId, senderName, subject, body);
                ReplyStatus(sender, $"Mensagem enviada para {known.Name}.");
            }

            SendMailTo(sender);
            Nview.InvokeRPC(sender, "RPC_DirectoryData", PackDirectory(playerId));
        }

        private void RPC_CreateHouse(long sender, string name)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "house-create", 6f, 2, 10f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            RememberSender(sender);

            if (HouseDatabase.FindOwnedBy(playerId) != null)
            {
                ReplyStatus(sender, "Você já lidera uma casa.");
                return;
            }
            if (!HouseDatabase.TryNormalizeName(name, out _))
            {
                ReplyStatus(sender, "Nome da casa inválido (2–24 letras, números, espaço, - ou _).");
                return;
            }
            if (HouseDatabase.FindByName(name) != null)
            {
                ReplyStatus(sender, "Já existe uma casa com esse nome.");
                return;
            }

            var house = HouseDatabase.Create(name, playerId, GameApi.GetPlayerName(sender));
            ReplyStatus(sender, house != null ? $"Casa '{house.Name}' criada." : "Não foi possível criar a casa.");
            Nview.InvokeRPC(sender, "RPC_DirectoryData", PackDirectory(playerId));
        }

        private void RPC_InviteHouse(long sender, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "house-invite", 6f, 4, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            RememberSender(sender);

            var parts = (packed ?? "").Split(new[] { ';' }, 2);
            if (parts.Length != 2)
            {
                ReplyStatus(sender, "Convite inválido.");
                return;
            }

            var member = PlayerDirectory.FindByName(parts[1]);
            if (member == null)
            {
                ReplyStatus(sender, "Jogador não encontrado.");
                return;
            }
            if (!HouseDatabase.AddMember(parts[0], playerId, member.Id))
            {
                ReplyStatus(sender, "Só o líder da casa pode convidar.");
                return;
            }
            ReplyStatus(sender, $"{member.Name} entrou na casa.");
            Nview.InvokeRPC(sender, "RPC_DirectoryData", PackDirectory(playerId));
        }

        private void RPC_KickHouse(long sender, string packed)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "house-kick", 6f, 4, 5f)) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var parts = (packed ?? "").Split(new[] { ';' }, 2);
            if (parts.Length != 2 || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var memberId))
            {
                ReplyStatus(sender, "Remoção inválida.");
                return;
            }
            if (!HouseDatabase.RemoveMember(parts[0], playerId, memberId))
            {
                ReplyStatus(sender, "Não foi possível remover (só o líder, e não a si mesmo).");
                return;
            }
            ReplyStatus(sender, "Membro removido.");
            Nview.InvokeRPC(sender, "RPC_DirectoryData", PackDirectory(playerId));
        }

        private void RPC_MailStatus(long sender, string message)
        {
            if (!NpcRequestGuard.IsResponseFromOwner(Nview, sender)) return;
            LastStatus = message ?? "";
        }

        private void RPC_MarkMailRead(long sender, string mailId)
        {
            if (!NpcRequestGuard.AllowNearby(Nview, transform, sender, "mail-mark-read", 6f, 12, 2f)) return;
            RememberSender(sender);
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;
            MailDatabase.MarkRead(mailId, playerId);
            SendMailTo(sender);
        }

        /// <summary>Hands a parcel over in the world. Coins become a real Coins stack rather
        /// than market balance, so mail is useful without ever touching a marketplace. A
        /// written letter has nothing to drop -- claiming it just marks it read/removed.</summary>
        private bool TryDeliverClaim(string mailId, long recipient)
        {
            string token = Guid.NewGuid().ToString("N");
            var entry = MailDatabase.BeginClaim(mailId, recipient, token);
            if (entry == null) return false;

            if (!Deliver(entry, recipient))
            {
                MailDatabase.ReleaseClaim(mailId, recipient, token);
                return false;
            }

            if (MailDatabase.CompleteClaim(mailId, recipient, token)) return true;

            // Do not unlock after the world item exists: retrying could duplicate it. Keeping
            // the persisted "delivering" state makes the exceptional case visible and safe
            // for administrative reconciliation.
            Plugin.Log.LogError($"NpcValheim: delivered mail {mailId} but could not finalize its claim");
            return false;
        }

        private bool Deliver(MailEntry entry, long recipient)
        {
            if (entry == null || entry.PlayerId != recipient) return false;
            var dropPos = transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f;
            if (entry.IsCoins)
                return ItemSpawner.TrySpawn(MarketplaceNpc.CoinPrefabName, entry.Coins, 1, dropPos);
            else if (!string.IsNullOrEmpty(entry.ItemName) && entry.Amount > 0)
                return ItemSpawner.TrySpawn(entry.ItemName, entry.Amount, entry.Quality, dropPos);

            // Written messages have no attachment. Claiming one is just an acknowledgement.
            return entry.IsMessage;
        }

        /// <summary>`target` is a transient peer id -- fine for addressing the reply, wrong
        /// for looking anything up. Mail is filed under the stable character id, so it has to
        /// be resolved first.
        ///
        /// This exact confusion has now cost two bugs: the marketplace once paid sellers 0,
        /// and the mailbox silently showed every player an empty box, because the parcel was
        /// *written* under the character id and *read* under the peer id. Anything that
        /// indexes player data must go through GameApi.GetPlayerId.</summary>
        private void SendMailTo(long target)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(target);
            if (playerId == 0L) return;
            string name = GameApi.GetPlayerName(target);
            var mail = MailDatabase.GetMail(playerId, name);
            Plugin.Log.LogInfo($"NpcValheim: mailbox send to {name} id={playerId} letters={mail.Count}");
            Nview.InvokeRPC(target, "RPC_MailData", MailWire.Pack(mail));
        }

        private void RememberSender(long sender)
        {
            GameApi.RememberIdentity(sender);
        }

        private void ReplyStatus(long target, string message)
        {
            LastStatus = message;
            Nview.InvokeRPC(target, "RPC_MailStatus", message ?? "");
        }

        private static string PackSend(bool toHouse, string target, string subject, string body) =>
            (toHouse ? "house" : "player") + ";" + MailWire.Sanitize(target) + ";" +
            MailWire.Sanitize(subject) + ";" + MailWire.EscapeBody(body);

        private static bool TryUnpackSend(string packed, out bool toHouse, out string target,
            out string subject, out string body)
        {
            toHouse = false;
            target = subject = body = "";
            var p = (packed ?? "").Split(new[] { ';' }, 4);
            if (p.Length != 4) return false;
            toHouse = p[0] == "house";
            target = p[1].Trim();
            subject = p[2].Trim();
            body = MailWire.UnescapeBody(p[3]).Trim();
            return target.Length > 0 && (subject.Length > 0 || body.Length > 0);
        }

        private static string PackDirectory(long viewerId)
        {
            var sb = new StringBuilder();
            foreach (var player in PlayerDirectory.All().Take(500))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("P;").Append(player.Id.ToString(CultureInfo.InvariantCulture))
                  .Append(';').Append(MailWire.Sanitize(player.Name));
            }
            foreach (var house in HouseDatabase.All().Take(100))
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("H;").Append(MailWire.Sanitize(house.Name)).Append(';')
                  .Append(house.MemberIds.Count.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(HouseDatabase.IsMember(house, viewerId) ? "1" : "0").Append(';')
                  .Append(house.OwnerId.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static List<MailRecipient> UnpackDirectory(string packed)
        {
            var result = new List<MailRecipient>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                if (result.Count >= 600) break;
                if (line.Length > 512) continue;
                var p = line.Split(';');
                if (p.Length < 3) continue;
                if (p[0] == "P")
                {
                    result.Add(new MailRecipient
                    {
                        IsHouse = false,
                        PlayerId = long.TryParse(p[1], out var id) ? id : 0L,
                        Name = p[2],
                    });
                }
                else if (p[0] == "H" && p.Length >= 5)
                {
                    result.Add(new MailRecipient
                    {
                        IsHouse = true,
                        Name = p[1],
                        MemberCount = int.TryParse(p[2], out var n) ? n : 0,
                        IsMine = p[3] == "1",
                        PlayerId = long.TryParse(p[4], out var owner) ? owner : 0L,
                    });
                }
            }
            return result;
        }
    }
}
