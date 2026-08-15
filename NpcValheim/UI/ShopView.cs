using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The merchant's own counter, both sides of it: what he sells on the left, what he buys
    /// on the right. Separate from the auction house tab, because they are different trades --
    /// here you deal with him at his posted price, there you deal with other players at
    /// theirs.
    ///
    /// He only ever handles items an admin put on these lists. There is deliberately no
    /// "accepts anything" mode: a merchant who buys every item at a flat rate is an infinite
    /// coin faucet and flattens the economy the server is trying to have.
    /// </summary>
    internal sealed class ShopView : NpcViewBase
    {
        private MarketplaceNpc Market => Npc as MarketplaceNpc;

        private TextMeshProUGUI _balance;
        private RectTransform _sells;
        private RectTransform _buys;
        private TextMeshProUGUI _sellsEmpty;
        private TextMeshProUGUI _buysEmpty;
        private TMP_InputField _amount;

        private string _sellSignature;
        private string _buySignature;
        private readonly List<GameObject> _sellRows = new List<GameObject>();
        private readonly List<GameObject> _buyRows = new List<GameObject>();

        protected override void OnBuild()
        {
            _balance = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_balance.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            // A stepper rather than a bare box: the amount decides what every button on the
            // page does, so it has to be obvious and adjustable without typing.
            var amountRow = ValheimUi.CreateRect("Amount", Root);
            ValheimUi.Anchor(amountRow, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-215f, -72f), new Vector2(215f, -34f));
            var amountLayout = amountRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            amountLayout.spacing = 6f;
            amountLayout.childControlWidth = true;
            amountLayout.childControlHeight = true;
            amountLayout.childForceExpandWidth = false;
            amountLayout.childAlignment = TextAnchor.MiddleCenter;

            var amountLabel = ValheimUi.CreateLabel(amountRow, "Quantidade", 15, ValheimUi.Beige,
                TextAlignmentOptions.Right);
            ValheimUi.SetWidth(amountLabel.gameObject, 100f);

            var minus = ValheimUi.CreateButton(amountRow, "−", 38f, 34f, 18);
            minus.onClick.AddListener(() => Nudge(-1));

            _amount = ValheimUi.CreateInputField(amountRow, "1", 64f, 34f);
            ValheimUi.SetWidth(_amount.gameObject, 64f);

            var plus = ValheimUi.CreateButton(amountRow, "+", 38f, 34f, 18);
            plus.onClick.AddListener(() => Nudge(1));

            foreach (int preset in new[] { 10, 50 })
            {
                int value = preset;
                var chip = ValheimUi.CreateButton(amountRow, preset.ToString(), 44f, 34f, 14);
                chip.onClick.AddListener(() => _amount.text = value.ToString());
            }

            _sells = BuildColumn("O NPC vende", true, out _sellsEmpty);
            _buys = BuildColumn("O NPC compra", false, out _buysEmpty);

            Market?.RequestMarketData();
        }

        private RectTransform BuildColumn(string title, bool left, out TextMeshProUGUI empty)
        {
            var pane = ValheimUi.CreateInlay(Root, title);
            ValheimUi.Anchor(pane,
                new Vector2(left ? 0f : 0.5f, 0f), new Vector2(left ? 0.5f : 1f, 1f),
                new Vector2(left ? 0f : 6f, 0f), new Vector2(left ? -6f : 0f, -76f));

            var header = ValheimUi.CreateLabel(pane, title, 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -38f));
            var list = ValheimUi.CreateScrollList(area, spacing: 4f);

            empty = ValheimUi.CreateLabel(area, "", 15, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));
            return list;
        }

        public override void Refresh()
        {
            var market = Market;
            if (market == null) return;

            // Read straight off the player's own inventory. There is no round trip and no
            // second wallet to disagree with it -- what the panel says is what you are
            // carrying.
            _balance.text = $"Suas moedas: {MarketplaceNpc.CoinsOf(Player)}";

            var sells = market.GetSellPrices();
            var sellSignature = string.Join("|", sells.Select(kv => $"{kv.Key}:{kv.Value}"));
            if (sellSignature != _sellSignature)
            {
                _sellSignature = sellSignature;
                RebuildSells(market, sells);
            }
            _sellsEmpty.gameObject.SetActive(sells.Count == 0);
            _sellsEmpty.text = "Este mercador não tem\nnada à venda.";

            var buys = market.GetBuyPrices();
            var buySignature = string.Join("|", buys.Select(kv => $"{kv.Key}:{kv.Value}"));
            if (buySignature != _buySignature)
            {
                _buySignature = buySignature;
                RebuildBuys(market, buys);
            }
            _buysEmpty.gameObject.SetActive(buys.Count == 0);
            _buysEmpty.text = "Este mercador não compra\nnada no momento.";
        }

        private void RebuildSells(MarketplaceNpc market, Dictionary<string, int> sells)
        {
            foreach (var row in _sellRows) if (row != null) Object.Destroy(row);
            _sellRows.Clear();

            foreach (var kv in sells)
            {
                var item = kv.Key;
                var price = kv.Value;
                var row = Row(_sells, 46f);
                _sellRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, item, 36f);
                var label = ValheimUi.CreateLabel(row,
                    $"{ItemNames.Display(item)}\n<size=12><color=#9a9188>{price} moedas/un</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var buy = ValheimUi.CreateButton(row, "Comprar", 110f, 36f, 14);
                buy.onClick.AddListener(() =>
                {
                    if (!TryAmount(out int amount)) return;

                    int cost = MarketplaceNpc.PayoutFor(price, amount);
                    if (cost <= 0) { Say("Quantidade inválida."); return; }

                    // The coins leave the pocket here, and the server hands them back if it
                    // refuses the sale -- see RPC_BuyFromNpc.
                    if (!MarketplaceNpc.TryPay(Player, cost))
                    {
                        Say($"Você tem {MarketplaceNpc.CoinsOf(Player)} moedas; custa {cost}.");
                        return;
                    }

                    market.RequestBuyFromNpc(item, amount, cost);
                    Say($"Comprando {amount}x {ItemNames.Display(item)} por {cost}; cai no chão aqui.");
                });
            }
        }

        private void RebuildBuys(MarketplaceNpc market, Dictionary<string, int> buys)
        {
            foreach (var row in _buyRows) if (row != null) Object.Destroy(row);
            _buyRows.Clear();

            foreach (var kv in buys)
            {
                var item = kv.Key;
                var price = kv.Value;
                var row = Row(_buys, 46f);
                _buyRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, item, 36f);
                var label = ValheimUi.CreateLabel(row,
                    $"{ItemNames.Display(item)}\n<size=12><color=#9a9188>{price} moedas/un</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var sell = ValheimUi.CreateButton(row, "Vender", 110f, 36f, 14);
                sell.onClick.AddListener(() =>
                {
                    if (!TryAmount(out int amount)) return;

                    var inventory = Player.GetInventory();
                    if (ItemNames.Count(inventory, item, -1) < amount)
                    {
                        Say($"Você não tem {amount}x {ItemNames.Display(item)}.");
                        return;
                    }

                    ItemNames.Remove(inventory, item, amount, -1);
                    market.RequestSellToNpc(item, 1, amount);
                    Say($"Vendido {amount}x {ItemNames.Display(item)} por {price * amount} moedas.");
                });
            }
        }

        private void Nudge(int delta)
        {
            int.TryParse(_amount.text, out int current);
            _amount.text = Mathf.Clamp(current + delta, 1, 10000).ToString();
        }

        private bool TryAmount(out int amount)
        {
            if (int.TryParse(_amount.text, out amount) && amount > 0 && amount <= 10000) return true;
            Say("Quantidade inválida.");
            return false;
        }
    }
}
