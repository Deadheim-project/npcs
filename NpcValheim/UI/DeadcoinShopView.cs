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
        private const float NoAnswerSeconds = 12f;

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
        private float _openedAt;
        private TMP_InputField _grantPlayer;
        private TMP_InputField _grantAmount;
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

            // Admins at an NPC get a strip at the bottom for crediting donations, which used to
            // mean editing the player's file on the server by hand. Never on the remote (F7)
            // counter: that one is a VIP convenience, not an admin tool.
            bool admin = !_remote && Npc.CanLocalPlayerAdminister();
            if (admin) BuildGrantRow();

            var pane = ValheimUi.CreateInlay(Root, "Offers");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one,
                new Vector2(0f, admin ? 48f : 0f), new Vector2(0f, -40f));

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
            _openedAt = Time.unscaledTime;
        }

        private void BuildGrantRow()
        {
            var row = Row(Root, 40f);
            ValheimUi.Anchor(row, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, 40f));

            ValheimUi.CreateLabel(row, "Admin:", 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            _grantPlayer = ValheimUi.CreateInputField(row, "", 200f, 36f);
            Flexible(_grantPlayer.gameObject);
            _grantAmount = ValheimUi.CreateInputField(row, "100", 90f, 36f);
            _grantAmount.contentType = TMP_InputField.ContentType.IntegerNumber;

            var add = ValheimUi.CreateButton(row, "Adicionar", 120f, 36f, 14);
            add.onClick.AddListener(() => Grant(1));
            var remove = ValheimUi.CreateButton(row, "Remover", 110f, 36f, 14);
            remove.onClick.AddListener(() => Grant(-1));
        }

        private void Grant(int sign)
        {
            if (!(Npc is DeadcoinShopNpc shop)) return;

            string name = (_grantPlayer.text ?? "").Trim();
            if (name.Length == 0)
            {
                Say("Admin: escreva o nome do jogador (como aparece no jogo).");
                return;
            }
            if (!int.TryParse(_grantAmount.text, out int amount) || amount <= 0 || amount > DeadcoinShop.MaxGrant)
            {
                Say($"Admin: quantidade entre 1 e {DeadcoinShop.MaxGrant}.");
                return;
            }

            shop.RequestGrant(name, sign * amount);
            Say($"{(sign > 0 ? "Adicionando" : "Removendo")} {amount} Deadcoins de {name}...");
            // So the admin's own balance line catches up at once when they credited themselves.
            _nextPoll = Time.unscaledTime + 1f;
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

            // "Consulting..." forever reads as a broken shop. After a while, say plainly that
            // the server never answered, which is a server-side problem to look up in its log.
            _balance.text = DeadcoinShop.Balance >= 0
                ? $"<color=#9a9188>Seu saldo:</color> {DeadcoinShop.Balance} Deadcoins"
                : Time.unscaledTime - _openedAt < NoAnswerSeconds
                    ? "<color=#9a9188>Consultando o seu saldo...</color>"
                    : "<color=#9a9188>O servidor não respondeu sobre o seu saldo. Avise um admin.</color>";

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

    /// <summary>
    /// Admin page of the Deadcoins counter: what it sells, at what bundle and price. It edits
    /// the server's DeadcoinShop.Items -- the cfg is saved and every client gets the new list
    /// by ServerSync -- so the counter, the VIP F7 counter and the cfg never disagree.
    /// </summary>
    internal sealed class DeadcoinShopAdminView : NpcViewBase
    {
        private OfferForm _form;
        private RectTransform _list;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            _form = OfferForm.Build(Root, new[] { ("Qtd", "1", 80f), ("Preço", "100", 90f) }, Save);

            var pane = ValheimUi.CreateInlay(Root, "Offers");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -76f));
            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Stretch(area, 4f, 4f);
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        private void Save()
        {
            if (!(Npc is DeadcoinShopNpc shop)) return;
            string prefab = OfferForm.Resolve(_form.Item.text);
            if (prefab == null) { Say("Escolha um item que o jogo conhece."); return; }
            int? amount = _form.Number(0), price = _form.Number(1);
            if (amount == null || amount <= 0 || price == null || price <= 0)
            {
                Say("Quantidade e preço precisam ser números maiores que zero.");
                return;
            }
            shop.RequestSetOffer(prefab, amount.Value, price.Value);
            Say($"Salvando {amount}x {ItemNames.Display(prefab)} por {price} Deadcoins...");
        }

        public override void Refresh()
        {
            string raw = Plugin.DeadcoinShopItems.Value ?? "";
            if (raw == _signature) return;
            _signature = raw;
            Rebuild(raw);
        }

        private void Rebuild(string raw)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var problems = new List<string>();
            // Parse, not Available: an entry the game cannot deliver is listed too (in red), so
            // it can be fixed or removed here instead of only in the file.
            var offers = DeadcoinCatalog.Parse(raw, problems);
            Add(Row(_list, 26f), $"À venda ({offers.Count}) · Editar põe o item no formulário acima; Salvar com o mesmo item muda o preço.",
                13, ValheimUi.Muted);
            foreach (var offer in offers)
            {
                var o = offer;
                var row = Row(_list, 44f);
                _rows.Add(row.gameObject);
                ValheimUi.CreateItemIcon(row, o.Prefab, 34f);
                int max = ItemSpawner.MaxDeliverableAmount(o.Prefab);
                string fault = max <= 0 ? "  <color=#ee6b57>não é item do jogo: fora da loja</color>"
                    : o.Amount > max ? $"  <color=#ee6b57>mais que uma entrega ({max}): fora da loja</color>" : "";
                var label = ValheimUi.CreateLabel(row,
                    $"{o.Amount}x {ItemNames.Display(o.Prefab)}  <size=12><color=#9a9188>{o.Prefab}</color></size>\n" +
                    $"<size=12><color=#9a9188>{o.Price} Deadcoins</color>{fault}</size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);
                var edit = ValheimUi.CreateButton(row, "Editar", 90f, 34f, 14);
                edit.onClick.AddListener(() => _form.Load(o.Prefab, o.Amount, o.Price));
                var remove = ValheimUi.CreateButton(row, "Remover", 100f, 34f, 14);
                remove.onClick.AddListener(() =>
                {
                    if (!(Npc is DeadcoinShopNpc shop)) return;
                    shop.RequestRemoveOffer(o.Prefab);
                    Say($"Retirando {ItemNames.Display(o.Prefab)}...");
                });
            }
            if (offers.Count == 0) Add(Row(_list, 26f), "A loja não vende nada.", 15, ValheimUi.Muted);
            foreach (var problem in problems) Add(Row(_list, 22f), "cfg: " + problem, 13, ValheimUi.Danger);
        }

        private void Add(RectTransform row, string text, int size, Color color)
        {
            _rows.Add(row.gameObject);
            Flexible(ValheimUi.CreateLabel(row, text, size, color, TextAlignmentOptions.Left).gameObject);
        }
    }
}
