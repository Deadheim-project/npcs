using System;
using System.Globalization;
using UnityEngine;
using NpcValheim.Integration;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The Alfaiate's server side. Deadheim's transmog owns the rules and the file; this adds
    /// the counter: the player must stand next to the NPC, a change costs Coins per piece and
    /// needs an arena rating (the same rating the Intendente da Arena asks, the lower of
    /// personal and team rating, best across the player's teams). Going back to the real look
    /// is free and needs no rating.
    ///
    /// Coins live in the buyer's bag, as at the Treinador and the Mestre das Montarias: the
    /// server applies the look first and the client pays on the "ok". A modified client could
    /// skip paying, which costs only a cosmetic.
    /// </summary>
    internal static class Tailor
    {
        internal const string ActionStatus = "tailor_status";
        internal const string ActionBuy = "tailor_buy";
        internal const string ActionOff = "tailor_off";

        private const float MaxDistance = 6f;

        internal static void Register(ZRoutedRpc rpc) => TailorNet.Register(rpc);

        internal static bool Enabled => Plugin.TailorEnabled?.Value ?? false;
        internal static int PricePerPiece => Mathf.Max(0, Plugin.TailorPriceCoins?.Value ?? 0);
        internal static int MinRating => Mathf.Max(0, Plugin.TailorMinArenaRating?.Value ?? 0);
        internal static int MinBracket => Mathf.Clamp(Plugin.TailorArenaBracket?.Value ?? 1, 1, 10);

        /// <summary>Payload: the price the client showed, a newline, then "real>app;...".</summary>
        internal static string BuyPayload(int shownPrice, string pairs) =>
            shownPrice.ToString(CultureInfo.InvariantCulture) + "\n" + (pairs ?? "");

        // ---- server ----

        /// <summary>Sends the player's arena rating and the price list.</summary>
        internal static void ServeStatus(long sender, ZNetView view, Transform npc)
        {
            if (!Enabled) return;
            if (!NpcRequestGuard.AllowNearby(view, npc, sender, "tailor-status", out string reason, MaxDistance, 6, 3f))
            {
                Plugin.Log.LogDebug($"NpcValheim: tailor status from peer {sender} refused: {reason}");
                return;
            }
            int rating = Arena.ArenaServer.BestRating(GameApi.GetPlayerId(sender), MinBracket);
            SendResponse(sender, "status:" + I(rating) + "|" + I(MinRating) + "|" + I(PricePerPiece) + "|" + I(MinBracket));
        }

        internal static void ServeBuy(long sender, string payload, ZNetView view, Transform npc, string npcName)
        {
            if (!Enabled) { SendResponse(sender, "error:O Alfaiate está fechado."); return; }
            if (!NpcRequestGuard.AllowNearby(view, npc, sender, "tailor-buy", out string reason, MaxDistance, 3, 5f))
            {
                Plugin.Log.LogWarning($"NpcValheim: tailor buy from peer {sender} refused: {reason}");
                SendResponse(sender, "error:Chegue mais perto do Alfaiate (ou aguarde um instante).");
                return;
            }
            if (!DeadheimTransmogApi.IsAvailable) { SendResponse(sender, "error:" + DeadheimTransmogApi.NotInstalled); return; }

            int nl = payload.IndexOf('\n');
            if (nl <= 0 || !int.TryParse(payload.Substring(0, nl), NumberStyles.Integer, CultureInfo.InvariantCulture, out int shown))
            {
                SendResponse(sender, "error:Pedido inválido.");
                return;
            }
            string pairs = payload.Substring(nl + 1);
            if (pairs.Length > 2048) { SendResponse(sender, "error:Pedido grande demais."); return; }

            string why = DeadheimTransmogApi.ServerCheck(sender, pairs);
            if (why != null) { SendResponse(sender, "error:" + why); return; }

            int pieces = DeadheimTransmogApi.ServerChangedCount(sender, pairs);
            int price = pieces * PricePerPiece;
            if (price != shown)
            {
                SendResponse(sender, $"error:O preço mudou: agora custa {price} moedas. Confira e aplique de novo.");
                return;
            }

            long playerId = GameApi.GetPlayerId(sender);
            if (pieces > 0 && MinRating > 0)
            {
                int best = Arena.ArenaServer.BestRating(playerId, MinBracket);
                if (best < MinRating)
                {
                    string where = MinBracket > 1 ? $" num time {MinBracket}v{MinBracket} ou maior" : "";
                    SendResponse(sender, $"error:Requer rating de arena {MinRating}{where}. O seu é {best}.");
                    return;
                }
            }

            why = DeadheimTransmogApi.ServerApply(sender, pairs);
            if (why != null) { SendResponse(sender, "error:" + why); return; }

            Plugin.Log.LogInfo($"NpcValheim: {GameApi.GetPlayerName(sender)} transmog at {npcName}: {pairs} " +
                               $"({pieces} peça(s), {price} Coins)");
            SendResponse(sender, "ok:" + I(price));
        }

        internal static void ServeOff(long sender, string real, ZNetView view, Transform npc)
        {
            if (!Enabled) { SendResponse(sender, "error:O Alfaiate está fechado."); return; }
            if (!NpcRequestGuard.AllowNearby(view, npc, sender, "tailor-off", out _, MaxDistance, 4, 3f))
            {
                SendResponse(sender, "error:Chegue mais perto do Alfaiate (ou aguarde um instante).");
                return;
            }
            string why = DeadheimTransmogApi.ServerOff(sender, string.IsNullOrEmpty(real) ? "*" : real);
            SendResponse(sender, why != null ? "error:" + why : "off:" + (real == "*" ? "Todas as peças voltaram ao visual real." : "A peça voltou ao visual real."));
        }

        private static void SendResponse(long peer, string message)
        {
            var package = new ZPackage();
            package.Write(message);
            ZRoutedRpc.instance?.InvokeRoutedRPC(peer, TailorNet.RpcResponse, package);
        }

        private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Client-bound RPC. Registered on both sides by Tailor.Register.</summary>
    internal static class TailorNet
    {
        internal const string RpcResponse = "NpcValheim_TailorResponse";

        internal static void Register(ZRoutedRpc rpc)
        {
            if (rpc == null) return;
            rpc.Register<ZPackage>(RpcResponse, RPC_Response);
        }

        private static void RPC_Response(long sender, ZPackage package)
        {
            // Only the server may tell a client it was charged.
            if (package == null || !ServiceNpcAuthority.IsAuthoritativeSender(sender)) return;
            try { TailorClient.OnResponse(package.ReadString()); }
            catch (Exception e) { Plugin.Log.LogWarning($"NpcValheim: invalid tailor response: {e.Message}"); }
        }
    }

    /// <summary>Client side: the request in flight, paying on the "ok", and what the view shows.</summary>
    internal static class TailorClient
    {
        private const float AwaitSeconds = 8f;

        private static float _awaitUntil;
        private static int _awaitPrice;

        internal static int Rating = -1;
        internal static int MinRating;
        internal static int PricePerPiece;
        internal static int Bracket = 1;
        internal static int StatusRevision;
        internal static int ResultRevision;
        internal static string LastResult = "";
        /// <summary>Set by an "ok": the view drops its pending picks and the preview.</summary>
        internal static int AppliedRevision;

        internal static bool Awaiting => Time.unscaledTime < _awaitUntil;

        /// <summary>Called by the apply button. Returns an error text, or null if sent.</summary>
        internal static string BeginBuy(TailorNpc npc, string pairs, int price)
        {
            var player = Player.m_localPlayer;
            if (player == null || npc == null) return "Sem personagem.";
            if (Awaiting) return "Aguarde...";
            if (string.IsNullOrEmpty(pairs)) return "Nenhuma troca escolhida.";
            int have = MarketplaceNpc.CoinsOf(player);
            if (have < price) return $"Você tem {have} moedas; custa {price}.";
            if (!npc.RequestBuy(price, pairs)) return "Não foi possível falar com o servidor.";
            _awaitPrice = price;
            _awaitUntil = Time.unscaledTime + AwaitSeconds;
            return null;
        }

        internal static void OnResponse(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            if (message.StartsWith("status:", StringComparison.Ordinal))
            {
                var p = message.Substring(7).Split('|');
                if (p.Length >= 4)
                {
                    int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out Rating);
                    int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out MinRating);
                    int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out PricePerPiece);
                    int.TryParse(p[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out Bracket);
                    StatusRevision++;
                }
                return;
            }

            if (message.StartsWith("error:", StringComparison.Ordinal))
            {
                _awaitUntil = 0f;
                Publish(message.Substring(6));
                return;
            }

            if (message.StartsWith("off:", StringComparison.Ordinal))
            {
                Publish(message.Substring(4));
                return;
            }

            if (!message.StartsWith("ok:", StringComparison.Ordinal)) return;
            // Only the purchase we are waiting for is paid, and only once.
            if (!Awaiting) return;
            _awaitUntil = 0f;
            int.TryParse(message.Substring(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int price);
            price = Math.Min(price, _awaitPrice);
            PayCoins(price);
            AppliedRevision++;
            Publish(price > 0 ? $"Aparência trocada! Pago: {price} moedas." : "Aparência atualizada!");
        }

        /// <summary>Same as the Treinador: the look already changed, so if the coins were spent
        /// meanwhile what is there is taken and the rest is let go.</summary>
        private static void PayCoins(int price)
        {
            var player = Player.m_localPlayer;
            if (player == null || price <= 0) return;
            if (MarketplaceNpc.TryPay(player, price)) return;
            int have = MarketplaceNpc.CoinsOf(player);
            if (have > 0) MarketplaceNpc.TryPay(player, have);
            Plugin.Log.LogWarning($"NpcValheim: a transmog of {price} Coins found only {have} in the bag");
        }

        private static void Publish(string text)
        {
            LastResult = text;
            ResultRevision++;
        }
    }
}
