using System.Collections.Generic;
using TMPro;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Deadcoins counter. One list, one button per row: each offer is a fixed bundle
    /// (the amount comes from the server's list), so unlike the merchant there is no quantity
    /// to choose.
    ///
    /// The balance shown is the server's, asked for when the tab opens and every few seconds
    /// after that. The ledger is a file on the server that an admin edits by hand when a
    /// donation arrives, and nothing tells the client when that happens.
    ///
    /// Hosted two ways: as the tab of a DeadcoinShopNpc, or on its own from the VIP directory
    /// (F7), where there is no NPC and <see cref="Shop"/> is null. The requests follow suit.
    /// </summary>
    internal sealed class DeadcoinShopView : NpcViewBase
    {
        private const float PollSeconds = 5f;

        /// <summary>The NPC this counter was opened at, or null when opened remotely.</summary>
        private DeadcoinShopNpc Shop => Npc as DeadcoinShopNpc;

        private TextMeshProUGUI _balance;
        private RectTransform _list;
        private TextMeshProUGUI _empty;

        private string _signature;
        private int _offerCount;
        private int _seenMessage;
        private float _nextPoll;
        private bool _remote;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            // Decided once, by reference: an NPC destroyed while the panel is open also compares
            // equal to null under Unity's operator, and must not quietly turn this into a remote
            // counter.
            _remote = ReferenceEquals(Npc, null);

            _balance = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_balance.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            var pane = ValheimUi.CreateInlay(Root, "Offers");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            var header = ValheimUi.CreateLabel(pane, "À venda por Deadcoins", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -38f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "Esta loja não tem nada à venda.", 15, ValheimUi.Muted,
                TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            // Messages from before this panel opened are not about anything on it.
            _seenMessage = DeadcoinShop.MessageRevision;
        }

        public override void Refresh()
        {
            var shop = _remote ? null : Shop;
            if (!_remote && shop == null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                DeadcoinShop.RequestBalance(shop);
            }

            _balance.text = DeadcoinShop.Balance < 0
                ? "<color=#9a9188>Consultando o seu saldo...</color>"
                : $"<color=#9a9188>Seu saldo:</color> {DeadcoinShop.Balance} Deadcoins";

            if (DeadcoinShop.MessageRevision != _seenMessage)
            {
                _seenMessage = DeadcoinShop.MessageRevision;
                Say(DeadcoinShop.LastMessage);
            }

            // The list is the server's, delivered by ServerSync; a cfg reload on the server
            // reaches this panel while it is open. The server re-reads its own copy on every
            // purchase regardless, so this one is only ever a display.
            string raw = Plugin.DeadcoinShopItems.Value ?? "";
            if (raw != _signature)
            {
                _signature = raw;
                Rebuild(shop, DeadcoinCatalog.Available(raw, null));
            }
            _empty.gameObject.SetActive(_offerCount == 0);
        }

        private void Rebuild(DeadcoinShopNpc shop, List<DeadcoinOffer> offers)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();
            _offerCount = offers.Count;

            foreach (var offer in offers)
            {
                var row = Row(_list, 46f);
                _rows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, offer.Prefab, 36f);
                var label = ValheimUi.CreateLabel(row,
                    $"{offer.Amount}x {ItemNames.Display(offer.Prefab)}\n" +
                    $"<size=12><color=#9a9188>{offer.Price} Deadcoins</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var buy = ValheimUi.CreateButton(row, "Comprar", 110f, 36f, 14);
                buy.onClick.AddListener(() => Buy(shop, offer));
            }
        }

        private void Buy(DeadcoinShopNpc shop, DeadcoinOffer offer)
        {
            // A courtesy only -- the server checks the balance it holds, not this copy.
            if (DeadcoinShop.Balance >= 0 && DeadcoinShop.Balance < offer.Price)
            {
                Say($"Você tem {DeadcoinShop.Balance} Deadcoins; custa {offer.Price}.");
                return;
            }
            if (!DeadcoinShop.TryBeginPurchase())
            {
                Say("Aguarde a compra anterior terminar.");
                return;
            }

            if (DeadcoinShop.RequestBuy(shop, offer))
            {
                Say($"Comprando {offer.Amount}x {ItemNames.Display(offer.Prefab)}...");
                return;
            }
            DeadcoinShop.EndPurchase();
            Say("O pedido não chegou ao servidor.");
        }
    }
}
