using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The auction house: listings on the left, your own inventory and the sell form on the
    /// right. Same two-column shape as the quest log, for the same reason -- a long scrolling
    /// list plus a detail/action area reads better than one column of everything.
    /// </summary>
    internal sealed class MarketView : NpcViewBase
    {
        private MarketplaceNpc Market => Npc as MarketplaceNpc;

        private TextMeshProUGUI _balance;
        private RectTransform _listings;
        private TextMeshProUGUI _listingsEmpty;
        private RectTransform _inventory;
        private TMP_InputField _sellAmount;
        private TMP_InputField _sellPrice;
        private TextMeshProUGUI _selectedLabel;
        private Image _selectedIcon;
        private Button _sell;

        private string _sellItemName = "";
        private int _sellQuality = 1;
        private string _listingSignature;
        private string _inventorySignature;
        private readonly List<GameObject> _listingRows = new List<GameObject>();
        private readonly List<GameObject> _inventoryRows = new List<GameObject>();

        protected override void OnBuild()
        {
            const float leftWidth = 520f;

            // ---- left: listings ----
            var left = ValheimUi.CreateInlay(Root, "Listings");
            ValheimUi.Anchor(left, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(leftWidth, 0f));

            var header = ValheimUi.CreateLabel(left, "Anúncios", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -36f), new Vector2(-8f, -6f));

            var listArea = ValheimUi.CreateRect("Area", left);
            ValheimUi.Anchor(listArea, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -38f));
            _listings = ValheimUi.CreateScrollList(listArea, spacing: 4f);

            _listingsEmpty = ValheimUi.CreateLabel(listArea, "Carregando...", 15, ValheimUi.Muted,
                TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_listingsEmpty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -50f), new Vector2(-10f, -10f));

            // ---- right: balance, inventory picker, sell form ----
            var right = ValheimUi.CreateInlay(Root, "Sell");
            ValheimUi.Anchor(right, Vector2.zero, Vector2.one, new Vector2(leftWidth + 10f, 0f), Vector2.zero);

            _balance = ValheimUi.CreateLabel(right, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_balance.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -8f));

            // Nothing to deposit into any more: the row of Depositar/Sacar buttons that used
            // to live here existed only to feed a separate wallet, and that wallet is what
            // made the panel show 6000 to a player carrying 300.

            var invHeader = ValheimUi.CreateLabel(right, "Seu inventário", 17, ValheimUi.Orange,
                TextAlignmentOptions.Left, display: true);
            ValheimUi.Anchor((RectTransform)invHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -74f), new Vector2(-10f, -44f));

            var invArea = ValheimUi.CreateRect("InvArea", right);
            ValheimUi.Anchor(invArea, new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(6f, 150f), new Vector2(-6f, -78f));
            _inventory = ValheimUi.CreateScrollList(invArea, spacing: 3f);

            // selected item + price form, pinned to the bottom of the right pane
            var form = ValheimUi.CreateRect("Form", right);
            ValheimUi.Anchor(form, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(10f, 8f), new Vector2(-10f, 144f));

            var selectedRow = ValheimUi.CreateRect("Selected", form);
            ValheimUi.Anchor(selectedRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -44f), new Vector2(0f, 0f));
            var selectedLayout = selectedRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            selectedLayout.spacing = 8f;
            selectedLayout.childControlWidth = true;
            selectedLayout.childControlHeight = true;
            selectedLayout.childForceExpandWidth = false;
            selectedLayout.childAlignment = TextAnchor.MiddleLeft;

            _selectedIcon = ValheimUi.CreateItemIcon(selectedRow, null, 38f);
            _selectedLabel = ValheimUi.CreateLabel(selectedRow, "(nenhum item selecionado)", 16,
                ValheimUi.Muted, TextAlignmentOptions.Left);
            _selectedLabel.textWrappingMode = TextWrappingModes.NoWrap;

            var priceRow = ValheimUi.CreateRect("Price", form);
            ValheimUi.Anchor(priceRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -88f), new Vector2(0f, -48f));
            var priceLayout = priceRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            priceLayout.spacing = 6f;
            priceLayout.childControlWidth = false;
            priceLayout.childControlHeight = true;
            priceLayout.childAlignment = TextAnchor.MiddleLeft;

            var qtyLabel = ValheimUi.CreateLabel(priceRow, "Qtd", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
            ((RectTransform)qtyLabel.transform).sizeDelta = new Vector2(34f, 34f);
            _sellAmount = ValheimUi.CreateInputField(priceRow, "1", 70f, 34f);
            var priceLabel = ValheimUi.CreateLabel(priceRow, "Preço/un", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
            ((RectTransform)priceLabel.transform).sizeDelta = new Vector2(80f, 34f);
            _sellPrice = ValheimUi.CreateInputField(priceRow, "10", 70f, 34f);

            _sell = ValheimUi.CreateButton(form, "Anunciar", 0f, 40f, 16);
            _sell.onClick.AddListener(OnSell);

            // Trading with the merchant himself lives on the Loja tab; this one is strictly
            // the auction house.
            ValheimUi.Anchor((RectTransform)_sell.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0f, 0f), new Vector2(0f, 40f));

            Market?.RequestMarketData();
        }

        public override void Refresh()
        {
            var market = Market;
            if (market == null) return;

            // Your own pocket, read directly -- the auction house never holds money for you.
            _balance.text = $"Suas moedas: {MarketplaceNpc.CoinsOf(Player)}";

            var signature = string.Join("|", market.CachedListings.Select(l =>
                $"{l.Id}:{l.Amount}:{l.PricePerUnit}:{l.IsMine}"));
            if (signature != _listingSignature)
            {
                _listingSignature = signature;
                RebuildListings(market);
            }
            _listingsEmpty.gameObject.SetActive(market.CachedListings.Count == 0);
            _listingsEmpty.text = market.HasSyncedOnce ? "(nenhum anúncio)" : "Carregando...";

            RebuildInventoryIfChanged();
        }

        private void RebuildListings(MarketplaceNpc market)
        {
            foreach (var row in _listingRows) if (row != null) Object.Destroy(row);
            _listingRows.Clear();

            foreach (var listing in market.CachedListings)
            {
                var row = Row(_listings, 46f);
                _listingRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, listing.ItemName, 38f);

                var text = ValheimUi.CreateLabel(row,
                    $"{ValheimUi.Localize(DisplayName(listing.ItemName))} x{listing.Amount}\n" +
                    $"<size=12><color=#9a9188>{listing.PricePerUnit}/un — {listing.OwnerName}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(text.gameObject);

                var id = listing.Id;
                if (listing.IsMine)
                {
                    var cancel = ValheimUi.CreateButton(row, "Cancelar", 110f, 36f, 14);
                    cancel.onClick.AddListener(() =>
                    {
                        market.RequestCancelListing(id);
                        Say("Cancelamento enviado; os itens voltam pelo correio.");
                    });
                }
                else
                {
                    int unitPrice = listing.PricePerUnit;
                    var buy = ValheimUi.CreateButton(row, "Comprar 1", 110f, 36f, 14);
                    buy.onClick.AddListener(() =>
                    {
                        // Paid up front, exactly like the shop. If the listing has since gone
                        // or the price moved, the server posts the money straight back.
                        if (!MarketplaceNpc.TryPay(Player, unitPrice))
                        {
                            Say($"Você tem {MarketplaceNpc.CoinsOf(Player)} moedas; custa {unitPrice}.");
                            return;
                        }
                        market.RequestBuy(id, 1, unitPrice);
                        Say($"Compra enviada por {unitPrice} moedas; os itens chegam pelo correio.");
                    });
                }
            }
        }

        private void RebuildInventoryIfChanged()
        {
            var items = Player.GetInventory().GetAllItems()
                .Where(i => i != null && i.m_shared != null && i.m_dropPrefab != null)
                .GroupBy(i => new { Prefab = i.m_dropPrefab.name, i.m_quality })
                .Select(g => g.First())
                .ToList();

            var signature = string.Join("|", items.Select(i => $"{i.m_dropPrefab.name}:{i.m_quality}"));
            if (signature == _inventorySignature) return;
            _inventorySignature = signature;

            foreach (var row in _inventoryRows) if (row != null) Object.Destroy(row);
            _inventoryRows.Clear();

            foreach (var item in items)
            {
                string prefabName = item.m_dropPrefab.name;
                int quality = item.m_quality;

                var button = ValheimUi.CreateButton(_inventory, "", 0f, 42f, 15);
                _inventoryRows.Add(button.gameObject);

                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                label.alignment = TextAlignmentOptions.Left;
                label.text = $"{ValheimUi.Localize(item.m_shared.m_name)}  " +
                             $"<size=12><color=#9a9188>Q{quality}</color></size>";
                Iconify(button, prefabName);

                button.onClick.AddListener(() =>
                {
                    _sellItemName = prefabName;
                    _sellQuality = quality;
                    _selectedLabel.text = $"{ValheimUi.Localize(DisplayName(prefabName))} (Q{quality})";
                    _selectedLabel.color = ValheimUi.Beige;
                    _selectedIcon.sprite = ValheimUi.FindItemIcon(prefabName);
                    _selectedIcon.enabled = _selectedIcon.sprite != null;
                });
            }
        }

        // ---- actions ----

        private void OnSell()
        {
            if (string.IsNullOrEmpty(_sellItemName)) { Say("Selecione um item primeiro."); return; }
            if (!int.TryParse(_sellAmount.text, out int amount) || amount <= 0) { Say("Quantidade inválida."); return; }
            if (!int.TryParse(_sellPrice.text, out int price) || price <= 0) { Say("Preço inválido."); return; }

            var inventory = Player.GetInventory();
            if (ItemNames.Count(inventory, _sellItemName, _sellQuality) < amount)
            {
                Say($"Você não tem {amount}x {ValheimUi.Localize(DisplayName(_sellItemName))}.");
                return;
            }

            ItemNames.Remove(inventory, _sellItemName, amount, _sellQuality);
            Market.RequestSell(_sellItemName, _sellQuality, amount, price);
            Say($"Anunciado {amount}x {ValheimUi.Localize(DisplayName(_sellItemName))} a {price}/un.");
            _inventorySignature = null; // stock changed, redraw the picker
        }

        internal static string DisplayName(string prefabName)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            return shared != null ? shared.m_name : prefabName;
        }
    }
}
