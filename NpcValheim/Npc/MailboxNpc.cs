using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using NpcValheim.Persistence;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Where market proceeds and unsold stock come back to their owner. Splitting this out
    /// from the marketplace is what makes offline trading work: a sale never needs both
    /// parties present, it just posts to the recipient's mail.
    ///
    /// Same authority model as everything else -- the mailbox is read and emptied only on
    /// the peer that owns the NPC's ZDO, and a client only ever sees its own mail because
    /// the server answers per-sender.
    /// </summary>
    public class MailboxNpc : NpcBase
    {
        public List<MailEntry> CachedMail { get; private set; } = new List<MailEntry>();
        public bool HasSyncedOnce { get; private set; }

        protected override void RegisterRpc()
        {
            Nview.Register("RPC_RequestMail", (Action<long>)RPC_RequestMail);
            Nview.Register("RPC_MailData", (Action<long, string>)RPC_MailData);
            Nview.Register("RPC_ClaimMail", (Action<long, string>)RPC_ClaimMail);
            Nview.Register("RPC_ClaimAllMail", (Action<long>)RPC_ClaimAllMail);
        }

        public void RequestMail()
        {
            if (Nview == null || !Nview.IsValid()) return;
            Nview.InvokeRPC("RPC_RequestMail");
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

        private void RPC_RequestMail(long sender)
        {
            if (!Nview.IsOwner()) return;
            SendMailTo(sender);
        }

        private void RPC_MailData(long sender, string packed)
        {
            CachedMail = Unpack(packed);
            HasSyncedOnce = true;
        }

        private void RPC_ClaimMail(long sender, string mailId)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            var entry = MailDatabase.Claim(mailId, playerId);
            if (entry != null) Deliver(entry, playerId);
            SendMailTo(sender);
        }

        private void RPC_ClaimAllMail(long sender)
        {
            if (!Nview.IsOwner()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            foreach (var entry in MailDatabase.GetMail(playerId))
            {
                var claimed = MailDatabase.Claim(entry.Id, playerId);
                if (claimed != null) Deliver(claimed, playerId);
            }
            SendMailTo(sender);
        }

        /// <summary>Hands a parcel over in the world. Coins become a real Coins stack rather
        /// than market balance, so mail is useful without ever touching a marketplace.</summary>
        private void Deliver(MailEntry entry, long recipient)
        {
            var dropPos = transform.position + Vector3.up + UnityEngine.Random.insideUnitSphere * 0.5f;
            if (entry.IsCoins)
                ItemSpawner.TrySpawn(MarketplaceNpc.CoinPrefabName, entry.Coins, 1, dropPos);
            else
                ItemSpawner.TrySpawn(entry.ItemName, entry.Amount, entry.Quality, dropPos);
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
            Nview.InvokeRPC(target, "RPC_MailData", Pack(MailDatabase.GetMail(playerId)));
        }

        // Wire format: one parcel per line, "id;subject;item;quality;amount;coins".
        private static string Pack(List<MailEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(e.Id).Append(';')
                  .Append(Sanitize(e.Subject)).Append(';')
                  .Append(Sanitize(e.ItemName)).Append(';')
                  .Append(e.Quality.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Amount.ToString(CultureInfo.InvariantCulture)).Append(';')
                  .Append(e.Coins.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Sanitize(string s) =>
            (s ?? "").Replace(';', ' ').Replace('\n', ' ');

        private static List<MailEntry> Unpack(string packed)
        {
            var result = new List<MailEntry>();
            if (string.IsNullOrEmpty(packed)) return result;

            foreach (var line in packed.Split('\n'))
            {
                var p = line.Split(';');
                if (p.Length != 6) continue;
                result.Add(new MailEntry
                {
                    Id = p[0],
                    Subject = p[1],
                    ItemName = p[2],
                    Quality = int.TryParse(p[3], out var q) ? q : 1,
                    Amount = int.TryParse(p[4], out var a) ? a : 0,
                    Coins = int.TryParse(p[5], out var c) ? c : 0,
                });
            }
            return result;
        }
    }
}
