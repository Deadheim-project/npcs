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

        // The boss lock (BossPass): shown instead of the counter to a player without the pass.
        private RectTransform _amountRow;
        private RectTransform _sellPane;
        private RectTransform _buyPane;
        private RectTransform _lock;
        private TextMeshProUGUI _lockTitle;
        private TextMeshProUGUI _lockBody;
        private Button _payGold;
        private Button _payDeadcoins;
        private string _lockSignature;
        private float _nextPassPoll;
        private int _seenPassMessage;

        protected override void OnBuild()
        {
            _balance = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_balance.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            // A stepper rather than a bare box: the amount decides what every button on the
            // page does, so it has to be obvious and adjustable without typing.
            var amountRow = ValheimUi.CreateRect("Amount", Root);
            _amountRow = amountRow;
            ValheimUi.Anchor(amountRow, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(-215f, -72f), new Vector2(215f, -34f));
            var amountLayout = amountRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            amountLayout.spacing = 6f;
            amountLayout.childControlWidth = true;
            amountLayout.childControlHeight = true;
            amountLayout.childForceExpandWidth = false;
            amountLayout.childAlignment = TextAnchor.MiddleCenter;

            // "Quantidade" sitting directly under "Suas moedas" read as a control for editing
            // your money -- which is exactly what somebody would fear after the balance bug.
            // Name what it counts.
            var amountLabel = ValheimUi.CreateLabel(amountRow, "Itens por operação", 15, ValheimUi.Beige,
                TextAlignmentOptions.Right);
            ValheimUi.SetWidth(amountLabel.gameObject, 150f);

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

            _sells = BuildColumn("O NPC vende", true, out _sellsEmpty, out _sellPane);
            _buys = BuildColumn("O NPC compra", false, out _buysEmpty, out _buyPane);
            BuildLock();

            // Messages from before this panel opened are not about anything on it.
            _seenPassMessage = BossPass.MessageRevision;

            Market?.RequestMarketData();
        }

        /// <summary>What a player without the pass sees in place of the two lists: why the
        /// counter is shut, how to open it for free, and the two ways to pay.</summary>
        private void BuildLock()
        {
            _lock = ValheimUi.CreateInlay(Root, "BossLock");
            ValheimUi.Anchor(_lock, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            _lockTitle = ValheimUi.CreateLabel(_lock, "", 24, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)_lockTitle.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -64f), new Vector2(-24f, -16f));

            _lockBody = ValheimUi.CreateLabel(_lock, "", 17, ValheimUi.Beige, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_lockBody.transform, Vector2.zero, Vector2.one,
                new Vector2(60f, 100f), new Vector2(-60f, -76f));

            var buttons = ValheimUi.CreateRect("Pay", _lock);
            ValheimUi.Anchor(buttons, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(24f, 30f), new Vector2(-24f, 78f));
            var layout = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 24f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childAlignment = TextAnchor.MiddleCenter;

            _payGold = ValheimUi.CreateButton(buttons, "", 260f, 48f, 16);
            ValheimUi.SetWidth(_payGold.gameObject, 260f);
            Iconify(_payGold, MarketplaceNpc.CoinPrefabName);
            _payGold.onClick.AddListener(PayGold);

            _payDeadcoins = ValheimUi.CreateButton(buttons, "", 260f, 48f, 16);
            ValheimUi.SetWidth(_payDeadcoins.gameObject, 260f);
            _payDeadcoins.onClick.AddListener(PayDeadcoins);

            _lock.gameObject.SetActive(false);
        }

        private RectTransform BuildColumn(string title, bool left, out TextMeshProUGUI empty, out RectTransform pane)
        {
            pane = ValheimUi.CreateInlay(Root, title);
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

            if (BossPass.MessageRevision != _seenPassMessage)
            {
                _seenPassMessage = BossPass.MessageRevision;
                Say(BossPass.LastMessage);
            }

            // The lock is the server's to enforce (RPC_BuyFromNpc / RPC_SellToNpc); this only
            // decides what to draw, from the passes the server says this character holds.
            string boss = market.HasShop ? market.RequiredBoss : "";
            if (boss.Length > 0 && Time.unscaledTime >= _nextPassPoll)
            {
                // Often while shut, so a pass bought or won elsewhere opens it in place; rarely
                // once open, only to notice an admin taking it away.
                _nextPassPoll = Time.unscaledTime + (BossPass.HasLocalPass(boss) ? 30f : 5f);
                market.RequestBossPassStatus();
            }

            bool locked = boss.Length > 0 && !BossPass.HasLocalPass(boss);
            _lock.gameObject.SetActive(locked);
            _amountRow.gameObject.SetActive(!locked);
            _sellPane.gameObject.SetActive(!locked);
            _buyPane.gameObject.SetActive(!locked);
            if (locked)
            {
                RefreshLock(boss);
                return;
            }

            // Read straight off the player's own inventory. There is no round trip and no
            // second wallet to disagree with it -- what the panel says is what you are
            // carrying.
            _balance.text = $"<color=#9a9188>Na sua bolsa:</color> {MarketplaceNpc.CoinsOf(Player)} moedas";

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

                    // The server answers with RPC_DeliverItem, which reports what reached the
                    // bag and what (if anything) had to go on the floor. Promising the floor
                    // up front described the old delivery, and described it wrongly.
                    market.RequestBuyFromNpc(item, amount, cost);
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

                    // No "sold" message here: whether this succeeded is entirely up to the
                    // server (the merchant's price table can be stale by the time this lands,
                    // see RPC_SellToNpc). It answers with either RPC_Paid or RPC_ReturnItem,
                    // and one of those two always tells the player what actually happened.
                    ItemNames.Remove(inventory, item, amount, -1);
                    market.RequestSellToNpc(item, 1, amount);
                });
            }
        }

        private void RefreshLock(string boss)
        {
            string deadcoins = BossPass.DeadcoinBalance >= 0 ? BossPass.DeadcoinBalance.ToString() : "?";
            _balance.text = $"<color=#9a9188>Na sua bolsa:</color> {MarketplaceNpc.CoinsOf(Player)} moedas" +
                            $"   <color=#9a9188>Deadcoins:</color> {deadcoins}";

            var entry = BossPassCatalog.Find(boss);
            float radius = Plugin.BossPassKillRadius?.Value ?? 60f;
            string signature = $"{boss}|{BossPass.Known}|{entry?.Name}|{entry?.Shop}|{entry?.Gold}|{entry?.Deadcoins}|{radius}";
            if (signature == _lockSignature) return;
            _lockSignature = signature;

            bool offered = entry != null && BossPass.Known;
            _payGold.gameObject.SetActive(offered && entry.Gold > 0);
            _payDeadcoins.gameObject.SetActive(offered && entry.Deadcoins > 0);

            if (!BossPass.Known)
            {
                _lockTitle.text = "Loja trancada";
                _lockBody.text = "Consultando o seu passe...";
                return;
            }
            if (entry == null)
            {
                _lockTitle.text = "Loja trancada";
                _lockBody.text = $"Esta loja exige o passe de um boss ('{boss}') que o servidor não tem configurado.\n" +
                                 "Fale com um admin.";
                return;
            }

            _lockTitle.text = $"{BossPassCatalog.ShopName(entry)} trancada";
            _lockBody.text =
                $"Para comprar e vender aqui você precisa do passe de {entry.Name}.\n\n" +
                $"Ele é de graça para quem derrotar {entry.Name}: vale acertar um golpe nele ou estar " +
                $"a até {radius:0} m quando ele morrer.\n\n" +
                (entry.Gold > 0 || entry.Deadcoins > 0
                    ? "Ou pague uma vez. O passe é deste personagem e abre todo mercador que exige " +
                      $"{entry.Name}."
                    : "Este passe não está à venda: só derrotando o boss.");

            SetLabel(_payGold, $"Pagar {entry.Gold} moedas");
            SetLabel(_payDeadcoins, $"Pagar {entry.Deadcoins} Deadcoins");
        }

        private static void SetLabel(Button button, string text)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        private void PayGold()
        {
            var entry = BossPassCatalog.Find(Market?.RequiredBoss);
            if (entry == null || entry.Gold <= 0) return;

            // Nothing leaves the bag yet. The server first checks the whole purchase and
            // answers with a quote; the coins are taken when that arrives (BossPass.Pay).
            int coins = MarketplaceNpc.CoinsOf(Player);
            if (coins < entry.Gold)
            {
                Say($"Você tem {coins} moedas; o passe custa {entry.Gold}.");
                return;
            }
            RequestPass(BossPass.MethodGold, entry.Gold);
        }

        private void PayDeadcoins()
        {
            var entry = BossPassCatalog.Find(Market?.RequiredBoss);
            if (entry == null || entry.Deadcoins <= 0) return;

            // A courtesy only -- the server charges the balance it holds, not this copy.
            if (BossPass.DeadcoinBalance >= 0 && BossPass.DeadcoinBalance < entry.Deadcoins)
            {
                Say($"Você tem {BossPass.DeadcoinBalance} Deadcoins; o passe custa {entry.Deadcoins}.");
                return;
            }
            RequestPass(BossPass.MethodDeadcoins, entry.Deadcoins);
        }

        private void RequestPass(string method, int price)
        {
            if (!BossPass.TryBeginPurchase())
            {
                Say("Aguarde o pagamento anterior terminar.");
                return;
            }
            if (Market != null && Market.RequestBossPass(method, price))
            {
                Say("Pedindo o passe ao servidor...");
                return;
            }
            BossPass.EndPurchase();
            Say("O pedido não chegou ao servidor.");
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
