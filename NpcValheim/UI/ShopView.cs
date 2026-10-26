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

        private const float WeeklyPollSeconds = 5f;
        private float _nextWeeklyPoll;

        // The boss pass (BossPass), per item: a locked row offers its pass, and this panel is
        // what "Passe" opens in place of the lists -- for the boss in _lockBoss.
        private string _lockBoss;
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
            // Name what it counts: lots, since a price can be for 50 Wood at once (each row
            // says its own lot; a row without one is a lot of 1).
            var amountLabel = ValheimUi.CreateLabel(amountRow, "Lotes por operação", 15, ValheimUi.Beige,
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

            var back = ValheimUi.CreateButton(buttons, "Voltar", 160f, 48f, 16);
            ValheimUi.SetWidth(back.gameObject, 160f);
            back.onClick.AddListener(() => { _lockBoss = null; _sellSignature = _buySignature = null; });

            _lock.gameObject.SetActive(false);
        }

        /// <summary>The boss whose pass this player still lacks for an item, or "" when the
        /// item is open to them.</summary>
        private static string MissingPass(MarketplaceNpc market, string itemBoss)
        {
            string boss = market.EffectiveBoss(itemBoss);
            return boss.Length > 0 && !BossPass.HasLocalPass(boss) ? boss : "";
        }

        /// <summary>" · passe de Eikthyr" after the price of an item that requires one.</summary>
        private static string PassText(MarketplaceNpc market, string itemBoss)
        {
            string boss = market.EffectiveBoss(itemBoss);
            if (boss.Length == 0) return "";
            string name = BossPassCatalog.Find(boss)?.Name ?? boss;
            return BossPass.HasLocalPass(boss)
                ? $" <color=#9a9188>· passe de {name}</color>"
                : $" <color=#ee6b57>· requer o passe de {name}</color>";
        }

        /// <summary>In place of a locked item's buttons: opens that boss's pass panel.</summary>
        private void PassButton(RectTransform row, string boss)
        {
            var open = ValheimUi.CreateButton(row, "Passe", 100f, 36f, 14);
            open.onClick.AddListener(() => { _lockBoss = boss; _lockSignature = null; });
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
            var required = market.BossesRequiredHere();
            if (required.Count > 0 && Time.unscaledTime >= _nextPassPoll)
            {
                // Often while something is shut, so a pass bought or won elsewhere opens it in
                // place; rarely once all are open, only to notice an admin taking one away.
                _nextPassPoll = Time.unscaledTime + (required.All(BossPass.HasLocalPass) ? 30f : 5f);
                market.RequestBossPassStatus();
            }

            // The pass just arrived (bought here or won): back to the lists, item unlocked.
            if (_lockBoss != null && BossPass.HasLocalPass(_lockBoss)) _lockBoss = null;
            string boss = _lockBoss;
            bool locked = boss != null;
            _lock.gameObject.SetActive(locked);
            _amountRow.gameObject.SetActive(!locked);
            _sellPane.gameObject.SetActive(!locked);
            _buyPane.gameObject.SetActive(!locked);
            if (locked)
            {
                RefreshLock(boss);
                return;
            }

            // Read straight off the player's own inventory for Coins -- there is no round trip
            // and no second wallet to disagree with it. Deadcoins is the server's own cache,
            // shared with every other Deadcoins panel (F7, boss pass, Treinador, Montarias).
            string deadcoinsBalance = DeadcoinShop.Balance >= 0 ? DeadcoinShop.Balance.ToString() : "?";
            _balance.text = $"<color=#9a9188>Na sua bolsa:</color> {MarketplaceNpc.CoinsOf(Player)} moedas" +
                            $"   <color=#9a9188>Deadcoins:</color> {deadcoinsBalance}";

            if (Time.unscaledTime >= _nextWeeklyPoll)
            {
                _nextWeeklyPoll = Time.unscaledTime + WeeklyPollSeconds;
                market.RequestSellWeeklyStatus();
            }

            var sells = market.GetSellOffers();
            var sellSignature = string.Join("|", sells.Select(kv =>
                $"{kv.Key}:{kv.Value.Gold}:{kv.Value.Deadcoins}:{kv.Value.WeeklyLimit}:{kv.Value.Lot}:{MarketDeadcoinSale.WeeklyUsedOf(kv.Key)}" +
                $":{market.EffectiveBoss(kv.Value.Boss)}:{MissingPass(market, kv.Value.Boss)}")) + "|" + BossPass.Known;
            if (sellSignature != _sellSignature)
            {
                _sellSignature = sellSignature;
                RebuildSells(market, sells);
            }
            _sellsEmpty.gameObject.SetActive(sells.Count == 0);
            _sellsEmpty.text = "Este mercador não tem\nnada à venda.";

            var buys = market.GetBuyPrices();
            var buyLots = market.GetBuyLots();
            var buyBosses = market.GetBuyBosses();
            var buySignature = string.Join("|", buys.Select(kv =>
                $"{kv.Key}:{kv.Value}:{(buyLots.TryGetValue(kv.Key, out int lot) ? lot : 1)}" +
                $":{(buyBosses.TryGetValue(kv.Key, out var b) ? b : "")}:{MissingPass(market, buyBosses.TryGetValue(kv.Key, out var m) ? m : "")}")) +
                "|" + BossPass.Known;
            if (buySignature != _buySignature)
            {
                _buySignature = buySignature;
                RebuildBuys(market, buys, buyLots, buyBosses);
            }
            _buysEmpty.gameObject.SetActive(buys.Count == 0);
            _buysEmpty.text = "Este mercador não compra\nnada no momento.";
        }

        private void RebuildSells(MarketplaceNpc market, Dictionary<string, SellOffer> sells)
        {
            foreach (var row in _sellRows) if (row != null) Object.Destroy(row);
            _sellRows.Clear();

            foreach (var kv in sells)
            {
                var item = kv.Key;
                var offer = kv.Value;
                var row = Row(_sells, 46f);
                _sellRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, item, 36f);

                string weeklyText = offer.WeeklyLimit > 0
                    ? $" <color=#9a9188>· {MarketDeadcoinSale.WeeklyUsedOf(item)}/{offer.WeeklyLimit} esta semana</color>"
                    : "";
                string perLot = MarketplaceNpc.PerLot(offer.Lot);
                string priceText = offer.Gold > 0 && offer.Deadcoins > 0
                    ? $"{offer.Gold} moedas ou {offer.Deadcoins} Deadcoins{perLot}"
                    : offer.Gold > 0
                        ? $"{offer.Gold} moedas{perLot}"
                        : $"{offer.Deadcoins} Deadcoins{perLot}";
                var label = ValheimUi.CreateLabel(row,
                    $"{LotPrefix(offer.Lot)}{ItemNames.Display(item)}\n<size=12><color=#9a9188>{priceText}</color>{weeklyText}{PassText(market, offer.Boss)}</size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                string missing = MissingPass(market, offer.Boss);
                if (missing.Length > 0)
                {
                    PassButton(row, missing);
                    continue;
                }

                bool maxedOut = offer.WeeklyLimit > 0 && MarketDeadcoinSale.WeeklyUsedOf(item) >= offer.WeeklyLimit;
                if (maxedOut)
                {
                    var tag = ValheimUi.CreateLabel(row, "Limite da semana atingido", 13, ValheimUi.Muted,
                        TextAlignmentOptions.Right);
                    ValheimUi.SetWidth(tag.gameObject, 170f);
                    continue;
                }

                if (offer.Gold > 0)
                {
                    var buy = ValheimUi.CreateButton(row, "Comprar", 100f, 36f, 14);
                    buy.onClick.AddListener(() => BuyGold(market, item, offer));
                }
                if (offer.Deadcoins > 0)
                {
                    var buyDc = ValheimUi.CreateButton(row, "Deadcoins", 100f, 36f, 14);
                    buyDc.onClick.AddListener(() => BuyDeadcoins(market, item, offer));
                }
            }
        }

        /// <summary>"50x " in front of an item sold in lots, nothing for a single unit.</summary>
        private static string LotPrefix(int lot) => lot > 1 ? $"{lot}x " : "";

        /// <summary>Lots on the stepper times the row's lot: the units that actually change
        /// hands, which is what the server and the weekly limit count.</summary>
        private bool TryUnits(int lot, out int lots, out int units)
        {
            units = 0;
            if (!TryAmount(out lots)) return false;
            units = lots * Mathf.Max(1, lot);
            if (units <= 10000) return true;
            Say($"Quantidade grande demais: {lots} lotes de {lot} passam de 10000 itens.");
            return false;
        }

        /// <summary>Checked here because the Coins path cannot give coins back once they
        /// left (see RPC_BuyFromNpc): going over the weekly limit there loses them.</summary>
        private bool WithinWeeklyLimit(string item, SellOffer offer, int units)
        {
            if (offer.WeeklyLimit <= 0) return true;
            int left = offer.WeeklyLimit - MarketDeadcoinSale.WeeklyUsedOf(item);
            if (units <= left) return true;
            Say($"Limite semanal de {ItemNames.Display(item)}: restam {Mathf.Max(0, left)} esta semana.");
            return false;
        }

        private void BuyGold(MarketplaceNpc market, string item, SellOffer offer)
        {
            if (!TryUnits(offer.Lot, out int lots, out int amount)) return;
            if (!WithinWeeklyLimit(item, offer, amount)) return;

            int cost = MarketplaceNpc.PayoutFor(offer.Gold, lots);
            Plugin.Log.LogInfo($"NpcValheim: buy click {amount}x {item} ({lots} lot(s) of {offer.Lot}) at {offer.Gold} -> cost {cost}, " +
                               $"coins in bag {MarketplaceNpc.CoinsOf(Player)}");
            if (cost <= 0) { Say("Quantidade inválida."); return; }

            // The coins leave the pocket here. The server closes the sale at the price this
            // row showed even if an admin changes it meanwhile, and never pays coins back on
            // the strength of this number -- see RPC_BuyFromNpc.
            if (!MarketplaceNpc.TryPay(Player, cost))
            {
                Say($"Você tem {MarketplaceNpc.CoinsOf(Player)} moedas; custa {cost}.");
                return;
            }

            // The server answers with a delivery (MarketplaceNpc.DeliverLocal), which reports what reached the bag and
            // what (if anything) had to go on the floor. Promising the floor up front
            // described the old delivery, and described it wrongly.
            market.RequestBuyFromNpc(item, amount, cost);
        }

        private void BuyDeadcoins(MarketplaceNpc market, string item, SellOffer offer)
        {
            if (!TryUnits(offer.Lot, out int lots, out int amount)) return;
            if (!WithinWeeklyLimit(item, offer, amount)) return;

            int cost = MarketplaceNpc.PayoutFor(offer.Deadcoins, lots);
            if (cost <= 0) { Say("Quantidade inválida."); return; }
            if (DeadcoinShop.Balance >= 0 && DeadcoinShop.Balance < cost)
            {
                Say($"Você tem {DeadcoinShop.Balance} Deadcoins; custa {cost}.");
                return;
            }

            market.RequestBuyDeadcoins(item, amount, cost);
            Say($"Comprando {amount}x {ItemNames.Display(item)}...");
        }

        private void RebuildBuys(MarketplaceNpc market, Dictionary<string, int> buys, Dictionary<string, int> lots,
            Dictionary<string, string> bosses)
        {
            foreach (var row in _buyRows) if (row != null) Object.Destroy(row);
            _buyRows.Clear();

            foreach (var kv in buys)
            {
                var item = kv.Key;
                var price = kv.Value;
                int lot = lots.TryGetValue(item, out int found) ? found : 1;
                string itemBoss = bosses.TryGetValue(item, out var own) ? own : "";
                var row = Row(_buys, 46f);
                _buyRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, item, 36f);
                var label = ValheimUi.CreateLabel(row,
                    $"{LotPrefix(lot)}{ItemNames.Display(item)}\n<size=12><color=#9a9188>{price} moedas{MarketplaceNpc.PerLot(lot)}</color>{PassText(market, itemBoss)}</size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                string missing = MissingPass(market, itemBoss);
                if (missing.Length > 0)
                {
                    PassButton(row, missing);
                    continue;
                }

                var sell = ValheimUi.CreateButton(row, "Vender", 110f, 36f, 14);
                sell.onClick.AddListener(() =>
                {
                    if (!TryUnits(lot, out _, out int amount)) return;

                    var inventory = Player.GetInventory();
                    if (ItemNames.Count(inventory, item, -1) < amount)
                    {
                        Say($"Você não tem {amount}x {ItemNames.Display(item)}.");
                        return;
                    }

                    // No "sold" message here: whether this succeeded is entirely up to the
                    // server (the merchant's price table can be stale by the time this lands,
                    // see RPC_SellToNpc). It answers with either a payment or the stack back,
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
            bool deadcoinsUnlocked = BossPass.LocalDeadcoinsUnlocked(boss);
            string signature = $"{boss}|{BossPass.Known}|{entry?.Name}|{entry?.Shop}|{entry?.Gold}|{entry?.Deadcoins}|{radius}|{deadcoinsUnlocked}";
            if (signature == _lockSignature) return;
            _lockSignature = signature;

            bool offered = entry != null && BossPass.Known;
            _payGold.gameObject.SetActive(offered && entry.Gold > 0);
            _payDeadcoins.gameObject.SetActive(offered && entry.Deadcoins > 0 && deadcoinsUnlocked);

            if (!BossPass.Known)
            {
                _lockTitle.text = "Item trancado";
                _lockBody.text = "Consultando o seu passe...";
                return;
            }
            if (entry == null)
            {
                _lockTitle.text = "Item trancado";
                _lockBody.text = $"Este item exige o passe de um boss ('{boss}') que o servidor não tem configurado.\n" +
                                 "Fale com um admin.";
                return;
            }

            _lockTitle.text = $"Passe de {entry.Name}";
            bool payable = entry.Gold > 0 || (entry.Deadcoins > 0 && deadcoinsUnlocked);
            _lockBody.text =
                $"Os itens marcados com {entry.Name} só podem ser comprados e vendidos com o passe dele.\n\n" +
                $"Ele é de graça para quem derrotar {entry.Name}: vale acertar um golpe nele ou estar " +
                $"a até {radius:0} m quando ele morrer.\n\n" +
                (payable
                    ? "Ou pague uma vez. O passe é deste personagem e abre todo mercador que exige " +
                      $"{entry.Name}."
                    : entry.Deadcoins > 0 && !deadcoinsUnlocked
                        ? $"O pagamento em Deadcoins só é aceito depois que {entry.Name} morrer uma vez " +
                          "neste servidor (por qualquer jogador). Por ora, só derrotando."
                        : "Este passe não está à venda: só derrotando o boss.");

            SetLabel(_payGold, $"Pagar {entry.Gold} moedas");
            SetLabel(_payDeadcoins, $"Pagar {entry.Deadcoins} Deadcoins");
        }

        private static void SetLabel(Button button, string text)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }

        private void PayGold() => RequestPass(BossPass.MethodGold);

        private void PayDeadcoins() => RequestPass(BossPass.MethodDeadcoins);

        private void RequestPass(string method)
        {
            var market = Market;
            string boss = _lockBoss;
            if (market == null || string.IsNullOrEmpty(boss)) return;
            string said = BossPass.BeginPurchase(BossPassCatalog.Find(boss), method,
                price => market.RequestBossPass(boss, method, price));
            if (said != null) Say(said);
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
