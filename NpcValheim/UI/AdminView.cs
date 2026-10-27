using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// Everything that changes what an NPC *is* rather than what it looks like: its name, its
    /// type-specific settings, and the reusable YAML templates. Only ever built when
    /// <see cref="NpcBase.CanLocalPlayerAdminister"/> allows it.
    /// </summary>
    internal sealed class AdminView : NpcViewBase
    {
        private TMP_InputField _name;
        private TMP_InputField _templateName;
        private TMP_InputField _costItem;
        private TMP_InputField _costAmount;
        private TMP_InputField _cooldown;
        private TMP_InputField _tax;

        private TMP_InputField _destinationName;
        private TMP_InputField _destinationCost;
        private TMP_InputField _destinationCostItem;
        private TMP_InputField _destinationX;
        private TMP_InputField _destinationY;
        private TMP_InputField _destinationZ;
        private enum CoordinateInputState { Empty, Valid, Invalid }

        private RectTransform _templates;
        private readonly List<GameObject> _templateRows = new List<GameObject>();
        private string _templateSignature;

        private TextMeshProUGUI _destinationPreview;
        private TextMeshProUGUI _totemStatus;
        private TextMeshProUGUI _homeLabel;
        private RectTransform _destinations;
        private readonly List<GameObject> _destinationRows = new List<GameObject>();
        private string _destinationSignature;

        private TMP_InputField _buyItem;
        private TMP_InputField _buyPrice;
        private TMP_InputField _buyLot;
        // The pass requirement picked for the item in _buyItem ("" = anyone): sent with
        // "Ele compra"/"Ele vende" and by the Requisito row's own Aplicar.
        private List<string> _bossOptions;
        private int _bossIndex;
        private TMP_InputField _sellDeadcoins;
        private TMP_InputField _sellWeeklyLimit;
        private TMP_InputField _questName;
        private TMP_InputField _questTarget;
        private TMP_InputField _questAmount;
        private TMP_InputField _questCoins;
        private TMP_InputField _questXp;
        private TMP_InputField _questReset;
        private TextMeshProUGUI _questKind;
        private TextMeshProUGUI _questHint;
        private int _questKindIndex;

        private RectTransform _itemResults;
        private readonly List<GameObject> _itemResultRows = new List<GameObject>();
        // Null, not a sentinel string: a stray NUL byte in this initialiser once made git
        // treat the whole file as binary and refuse to merge it.
        private string _itemQuery;
        private RectTransform _buys;
        private readonly List<GameObject> _buyRows = new List<GameObject>();
        private string _buySignature;

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "Admin");
            ValheimUi.Stretch(frame, 0f, 0f);

            var column = ValheimUi.CreateRect("Column", frame);
            ValheimUi.Anchor(column, new Vector2(0f, 0f), new Vector2(0.55f, 1f),
                new Vector2(16f, 12f), new Vector2(-8f, -12f));
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;

            Heading(column, "Nome do NPC");
            var nameRow = Row(column, 40f);
            _name = ValheimUi.CreateInputField(nameRow, Npc.GetHoverName(), 300f, 38f);
            Flexible(_name.gameObject);
            var rename = ValheimUi.CreateButton(nameRow, "Renomear", 130f, 38f, 15);
            rename.onClick.AddListener(() =>
            {
                Npc.RequestSetName(Player, _name.text);
                Say("Nome atualizado.");
            });

            var profile = Npc.BuildProfile();

            if (Npc is TeleporterNpc teleporter)
            {
                // Ordered the way the job is actually done -- name it, price it, say where,
                // read back what that means, then commit -- with the Adicionar button last
                // instead of sitting on the first row above the coordinates it consumes.
                Heading(column, "Novo destino");
                var addRow = Row(column, 40f);
                ValheimUi.CreateLabel(addRow, "Nome", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
                _destinationName = ValheimUi.CreateInputField(addRow, "", 170f, 38f);
                Flexible(_destinationName.gameObject);
                ValheimUi.CreateLabel(addRow, "Custo", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationCost = ValheimUi.CreateInputField(addRow, "0", 60f, 38f);

                // The fare is a price *and* an item, and until now only the price was
                // askable here -- every route was charged in whatever the NPC's default was,
                // so a route could not cost a Ruby on a teleporter priced in Coins. Left
                // empty this still means "the NPC's default item", which is what the
                // existing routes say.
                ValheimUi.CreateLabel(addRow, "em", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationCostItem = ValheimUi.CreateInputField(addRow, "", 120f, 38f);

                // Typed coordinates, because the other two ways of naming a place both need
                // you to physically go there. An admin building a travel network from a map
                // wants to enter the numbers, and `pos` in the console prints exactly these.
                var coordRow = Row(column, 40f);
                ValheimUi.CreateLabel(coordRow, "X", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationX = ValheimUi.CreateInputField(coordRow, "", 90f, 38f);
                ValheimUi.CreateLabel(coordRow, "Y", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationY = ValheimUi.CreateInputField(coordRow, "", 90f, 38f);
                ValheimUi.CreateLabel(coordRow, "Z", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationZ = ValheimUi.CreateInputField(coordRow, "", 90f, 38f);

                var here = ValheimUi.CreateButton(coordRow, "Usar minha posição", 160f, 38f, 14);
                here.onClick.AddListener(() =>
                {
                    var at = Player.transform.position;
                    _destinationX.text = at.x.ToString("0.0", CultureInfo.InvariantCulture);
                    _destinationY.text = at.y.ToString("0.0", CultureInfo.InvariantCulture);
                    _destinationZ.text = at.z.ToString("0.0", CultureInfo.InvariantCulture);
                    Say($"Coordenadas preenchidas com a sua posição ({at.x:0}, {at.y:0}, {at.z:0}).");
                });

                // Says what the button is about to do, in the same words as the result: which
                // name, which coordinates, and what the fare works out to. The rule that three
                // empty boxes mean "where I am standing" was written in dim text under the
                // form, so the form itself showed three empty required-looking fields and
                // nothing else -- this resolves the rule to actual numbers before it is used.
                _destinationPreview = Dim(column, "");
                ValheimUi.SetHeight(_destinationPreview.gameObject, 34f);

                var add = ValheimUi.CreateButton(column, "Adicionar destino", 0f, 40f, 15);
                ValheimUi.SetHeight(add.gameObject, 40f);
                add.onClick.AddListener(() =>
                {
                    if (string.IsNullOrWhiteSpace(_destinationName.text))
                    {
                        Say("Dê um nome ao destino primeiro.");
                        return;
                    }
                    if (!int.TryParse(_destinationCost.text, out int cost) || cost < 0)
                    {
                        Say("Custo invalido. Use 0 para herdar o custo padrao do NPC.");
                        return;
                    }

                    string costItem = (_destinationCostItem.text ?? "").Trim();

                    var coordinateState = ReadCoordinates(out var typed);
                    if (coordinateState == CoordinateInputState.Invalid) return;
                    if (coordinateState == CoordinateInputState.Valid)
                    {
                        teleporter.RequestAddDestination(Player, _destinationName.text, cost, costItem,
                            typed, Player.transform.rotation.eulerAngles.y);
                        // Deliberately not "gravado": only the server can say that, and it
                        // now does -- either the confirmation or the reason it refused.
                        Say($"Enviado: '{_destinationName.text}' em " +
                            $"({typed.x:0}, {typed.y:0}, {typed.z:0}), {DescribeFare(teleporter, cost, costItem)}.");
                        _destinationName.text = "";
                        _destinationSignature = null;
                        return;
                    }

                    teleporter.RequestAddDestination(Player, _destinationName.text, cost, costItem);
                    Say($"Enviado: '{_destinationName.text}' na sua posição, " +
                        $"{DescribeFare(teleporter, cost, costItem)}.");

                    _destinationName.text = "";
                    _destinationSignature = null;
                });

                // The old hint that lived here ("deixe os três vazios para usar sua posição")
                // is now stated by the preview above, against real numbers, instead of asking
                // the reader to hold a rule in their head while filling the form.

                var costRow = Row(column, 38f);
                ValheimUi.CreateLabel(costRow, "Item/custo padrao", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
                _costItem = ValheimUi.CreateInputField(costRow, profile.Teleporter?.CostItem ?? "", 160f, 34f);
                _costAmount = ValheimUi.CreateInputField(costRow,
                    (profile.Teleporter?.CostAmount ?? 0).ToString(CultureInfo.InvariantCulture), 70f, 34f);

                var cooldownRow = Row(column, 38f);
                ValheimUi.CreateLabel(cooldownRow, "Cooldown (s)", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
                _cooldown = ValheimUi.CreateInputField(cooldownRow,
                    (profile.Teleporter?.CooldownSeconds ?? 0f).ToString(CultureInfo.InvariantCulture), 70f, 34f);

                var apply = ValheimUi.CreateButton(column, "Aplicar configuração", 0f, 40f, 15);
                ValheimUi.SetHeight(apply.gameObject, 40f);
                apply.onClick.AddListener(() =>
                {
                    int.TryParse(_costAmount.text, out int amount);
                    float.TryParse(_cooldown.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float cd);
                    teleporter.RequestConfigureCost(Player, _costItem.text, amount, cd);
                    Say("Aplicando configuração...");
                });

                // Totem da Cidade (Deadheim): the totem's one-way trip lands here, and/or this
                // teleporter sends each player back to the totem they built, for the default fare.
                Heading(column, "Totem da Cidade");
                var totemRow = Row(column, 40f);
                var arrival = ValheimUi.CreateButton(totemRow, "Chegada do Totem aqui", 0f, 38f, 14);
                Flexible(arrival.gameObject);
                arrival.onClick.AddListener(() =>
                {
                    if (!Integration.DeadheimTotemApi.IsAvailable)
                    {
                        Say(Integration.DeadheimTotemApi.NotInstalled);
                        return;
                    }
                    teleporter.RequestSetTotemArrival(Player);
                    Say("Enviado: chegada do Totem na sua posição.");
                });
                var home = ValheimUi.CreateButton(totemRow, "", 0f, 38f, 14);
                Flexible(home.gameObject);
                _homeLabel = home.GetComponentInChildren<TextMeshProUGUI>();
                home.onClick.AddListener(() =>
                {
                    bool on = !teleporter.HomeEnabled;
                    teleporter.RequestSetHome(Player, on);
                    Say(on ? "Ligando Voltar para casa..." : "Desligando Voltar para casa...");
                });
                _totemStatus = Dim(column, "");
            }
            else if (Npc is MarketplaceNpc market)
            {
                Heading(column, "Mercado");
                var taxRow = Row(column, 38f);
                ValheimUi.CreateLabel(taxRow, "Taxa (%)", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
                _tax = ValheimUi.CreateInputField(taxRow,
                    (profile.Marketplace?.TaxPercent ?? 0).ToString(CultureInfo.InvariantCulture), 70f, 34f);
                var applyTax = ValheimUi.CreateButton(taxRow, "Aplicar", 110f, 34f, 15);
                applyTax.onClick.AddListener(() =>
                {
                    int.TryParse(_tax.text, out int tax);
                    market.RequestConfigureTax(Player, tax);
                    Say("Taxa aplicada.");
                });

                // An auction house has no counter of its own -- it hosts other players'
                // listings and keeps a cut. Offering it a price list to fill in invited the
                // admin to configure something it would never use.
                if (!market.HasShop)
                {
                    Dim(column, "Esta é uma casa de leilão: ela não compra nem vende, " +
                                "apenas hospeda anúncios de jogadores e retém a taxa acima.");
                }
                else
                {
                    BuildBossLock(column, market);

                    // Two lists on one counter: he deals only in what he is told to deal in.
                    // The price buys a lot, not a unit: "10 / 50" is 50 Wood for 10 coins. Without
                    // it the cheapest anything could go for was a coin apiece, and selling bulk
                    // goods meant pricing them one by one.
                    Heading(column, "Balcão (item / preço / lote)");
                    var buyRow = Row(column, 40f);
                    _buyItem = ValheimUi.CreateInputField(buyRow, "", 130f, 38f);
                    Flexible(_buyItem.gameObject);
                    _buyPrice = ValheimUi.CreateInputField(buyRow, "1", 60f, 38f);
                    _buyLot = ValheimUi.CreateInputField(buyRow, "1", 55f, 38f);
                    var addBuy = ValheimUi.CreateButton(buyRow, "Ele compra", 110f, 38f, 14);
                    var addSell = ValheimUi.CreateButton(buyRow, "Ele vende", 110f, 38f, 14);
                    addBuy.onClick.AddListener(() => SetPrice(market, selling: false));
                    addSell.onClick.AddListener(() => SetPrice(market, selling: true));

                    // Deadcoins and the weekly limit (A3) are sell-only and edited separately
                    // from the Coins price above: this keeps "Ele vende" a plain gold edit, and
                    // leaves whichever of these an admin does not touch exactly as it was (see
                    // RequestSetSellExtra / RequestSetPrice, which each preserve the other's data).
                    var sellExtraRow = Row(column, 40f);
                    ValheimUi.CreateLabel(sellExtraRow, "Deadcoins/lote", 14, ValheimUi.Muted, TextAlignmentOptions.Left);
                    _sellDeadcoins = ValheimUi.CreateInputField(sellExtraRow, "0", 70f, 38f);
                    ValheimUi.CreateLabel(sellExtraRow, "Limite/semana", 14, ValheimUi.Muted, TextAlignmentOptions.Left);
                    _sellWeeklyLimit = ValheimUi.CreateInputField(sellExtraRow, "0", 70f, 38f);
                    var setSellExtra = ValheimUi.CreateButton(sellExtraRow, "Definir (venda)", 150f, 38f, 14);
                    Flexible(setSellExtra.gameObject);
                    setSellExtra.onClick.AddListener(() => SetSellExtra(market));

                    // Typing the exact prefab name meant knowing it beforehand -- and on a modded
                    // server that is hundreds of names nobody has memorised. The same box now
                    // searches, matching either the prefab name or the name the player reads, and
                    // picking a result fills it in.
                    Dim(column, "Digite o item acima para buscar; clique num resultado para preencher. " +
                                "Preço e Deadcoins valem por lote (3º campo; 1 = por unidade). " +
                                "O requisito acima vai junto com \"Ele compra\"/\"Ele vende\"; o Aplicar dele muda só o requisito do item. " +
                                "\"Ele vende\" muda o preço em moedas e o lote; \"Definir (venda)\" só muda Deadcoins/limite.");
                    var searchArea = ValheimUi.CreateRect("SearchArea", column);
                    // 70 rather than 150: the boss lock and Deadcoins rows above took the
                    // difference, and the column was already full.
                    ValheimUi.SetHeight(searchArea.gameObject, 70f);
                    _itemResults = ValheimUi.CreateScrollList(searchArea, spacing: 2f);
                }
            }

            if (Npc is QuestGiverNpc giver) BuildQuestMaker(column, giver);

            Heading(column, "Salvar como modelo");
            var saveRow = Row(column, 40f);
            _templateName = ValheimUi.CreateInputField(saveRow, "", 220f, 38f);
            Flexible(_templateName.gameObject);
            var save = ValheimUi.CreateButton(saveRow, "Salvar", 130f, 38f, 15);
            save.onClick.AddListener(() =>
            {
                if (string.IsNullOrEmpty(_templateName.text)) { Say("Dê um nome ao modelo primeiro."); return; }
                Npc.RequestSaveAsTemplate(Player, _templateName.text);
                _templateSignature = null;
            });

            var remove = ValheimUi.CreateButton(column, "Remover NPC", 0f, 40f, 15);
            ValheimUi.SetHeight(remove.gameObject, 40f);
            bool removalArmed = false;
            remove.onClick.AddListener(() =>
            {
                if (!removalArmed)
                {
                    removalArmed = true;
                    remove.GetComponentInChildren<TextMeshProUGUI>().text = "Confirmar remoção";
                    Say("Clique novamente para remover este NPC e seu snapshot.");
                    return;
                }

                if (!ServiceNpcAuthority.RequestRemoval(Npc))
                {
                    removalArmed = false;
                    Say("Não foi possível enviar a remoção ao servidor.");
                    return;
                }

                UiRoot.RequestClose();
            });

            // ---- right: destinations (teleporters) above saved templates ----
            var right = ValheimUi.CreateRect("Right", frame);
            ValheimUi.Anchor(right, new Vector2(0.55f, 0f), new Vector2(1f, 1f),
                new Vector2(8f, 12f), new Vector2(-16f, -12f));

            bool isTeleporter = Npc is TeleporterNpc;
            bool isMarket = Npc is MarketplaceNpc shop && shop.HasShop;
            // Teleporters and merchants each have a second list to manage, so the pane is
            // split; everything else gives the whole pane to templates.
            float split = isTeleporter || isMarket ? 0.5f : 0f;

            if (isTeleporter || isMarket)
            {
                var topPane = ValheimUi.CreateRect(isTeleporter ? "Destinations" : "BuyList", right);
                ValheimUi.Anchor(topPane, new Vector2(0f, split), new Vector2(1f, 1f),
                    new Vector2(0f, 6f), new Vector2(0f, 0f));

                var topHeader = ValheimUi.CreateLabel(topPane,
                    isTeleporter ? "Destinos" : "Balcao", 18, ValheimUi.Orange,
                    TextAlignmentOptions.Center, display: true);
                ValheimUi.Anchor((RectTransform)topHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                    new Vector2(0f, -34f), new Vector2(0f, 0f));

                var topArea = ValheimUi.CreateRect("Area", topPane);
                ValheimUi.Anchor(topArea, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -38f));
                var list = ValheimUi.CreateScrollList(topArea, spacing: 4f);
                if (isTeleporter) _destinations = list; else _buys = list;
            }

            var templatePane = ValheimUi.CreateRect("Templates", right);
            ValheimUi.Anchor(templatePane, new Vector2(0f, 0f), new Vector2(1f, split == 0f ? 1f : split),
                Vector2.zero, new Vector2(0f, isTeleporter ? -6f : 0f));

            var header = ValheimUi.CreateLabel(templatePane, "Modelos salvos", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -34f), new Vector2(0f, 0f));

            var area = ValheimUi.CreateRect("Area", templatePane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -38f));
            _templates = ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        /// <summary>
        /// One row: which boss's pass the item in the box below requires. Per item, not per
        /// counter: one Mercador can sell open goods next to locked ones. Stepped with arrows
        /// through "nenhum" plus the bosses in [BossPass] Bosses; Aplicar sets it on an item
        /// already on the counter, and "Ele compra"/"Ele vende" send it with the price. With
        /// "Nenhum" a price edit leaves the item's pass as it was; only Aplicar removes one.
        ///
        /// A requirement the config no longer lists still shows up in the list, under its
        /// prefab name: hiding it would make an item look open while the server keeps it shut.
        /// </summary>
        private void BuildBossLock(Transform column, MarketplaceNpc market)
        {
            _bossOptions = new List<string> { "" };
            _bossOptions.AddRange(BossPassCatalog.Current().Select(e => e.Boss));
            foreach (var boss in market.BossesRequiredHere())
                if (!_bossOptions.Contains(boss)) _bossOptions.Add(boss);
            _bossIndex = 0;

            var row = Row(column, 38f);
            ValheimUi.CreateLabel(row, "Requisito do item", 15, ValheimUi.Beige, TextAlignmentOptions.Left);
            var previous = ValheimUi.CreateButton(row, "<", 34f, 34f, 16);
            var label = ValheimUi.CreateLabel(row, DescribeLock(""), 14, ValheimUi.Yellow,
                TextAlignmentOptions.Center);
            Flexible(label.gameObject);
            var next = ValheimUi.CreateButton(row, ">", 34f, 34f, 16);
            var apply = ValheimUi.CreateButton(row, "Aplicar", 100f, 34f, 15);

            void Step(int delta)
            {
                _bossIndex = (_bossIndex + delta + _bossOptions.Count) % _bossOptions.Count;
                label.text = DescribeLock(_bossOptions[_bossIndex]);
            }
            previous.onClick.AddListener(() => Step(-1));
            next.onClick.AddListener(() => Step(1));
            apply.onClick.AddListener(() =>
            {
                string item = _buyItem != null ? _buyItem.text?.Trim() ?? "" : "";
                if (item.Length == 0) { Say("Digite o item do balcão no campo abaixo."); return; }
                // The server answers with what it actually did, see RPC_SetItemBoss.
                market.RequestSetItemBoss(Player, item, SelectedBoss());
                _buySignature = null;
                Say("Enviando o requisito ao servidor...");
            });
        }

        private string SelectedBoss() =>
            _bossOptions != null && _bossIndex >= 0 && _bossIndex < _bossOptions.Count ? _bossOptions[_bossIndex] : "";

        private static string DescribeLock(string boss)
        {
            if (string.IsNullOrEmpty(boss)) return "Nenhum (livre)";
            var entry = BossPassCatalog.Find(boss);
            return entry == null
                ? $"{boss} (fora da config!)"
                : $"{entry.Name} ({BossPassCatalog.ShopName(entry)})";
        }

        /// <summary>Adds or removes one entry on one side of the merchant's counter.</summary>
        private void SetPrice(MarketplaceNpc market, bool selling)
        {
            string item = _buyItem.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(item)) { Say("Informe o nome do prefab do item."); return; }
            if (ObjectDB.instance?.GetItemPrefab(item) == null)
            {
                Say($"Item '{item}' nao existe. Use o nome do prefab (ex.: Wood).");
                return;
            }
            if (!int.TryParse(_buyPrice.text, out int price) || price < 0) { Say("Preco invalido."); return; }
            if (!int.TryParse(_buyLot.text, out int lot) || lot < 1 || lot > MarketplaceNpc.MaxLot)
            {
                Say($"Lote invalido: use de 1 a {MarketplaceNpc.MaxLot}.");
                return;
            }

            // No result message here: only the server knows whether the change took, and it
            // answers with one either way (see MarketplaceNpc.RPC_SetPrice). Printing success
            // straight after the click is how an admin ends up staring at a merchant that
            // "não compra nada" right after being told it does.
            market.RequestSetPrice(Player, item, price, selling, lot, SelectedBoss());
            _buyItem.text = "";
            _buySignature = null;
        }

        /// <summary>A3: Deadcoins price and/or weekly limit for a sell item, leaving its Coins
        /// price exactly as it was (see MarketplaceNpc.RPC_SetSellExtra).</summary>
        private void SetSellExtra(MarketplaceNpc market)
        {
            string item = _buyItem.text?.Trim() ?? "";
            if (string.IsNullOrEmpty(item)) { Say("Informe o nome do prefab do item."); return; }
            if (ObjectDB.instance?.GetItemPrefab(item) == null)
            {
                Say($"Item '{item}' nao existe. Use o nome do prefab (ex.: Wood).");
                return;
            }
            if (!int.TryParse(_sellDeadcoins.text, out int deadcoins) || deadcoins < 0)
            {
                Say("Deadcoins/un invalido.");
                return;
            }
            if (!int.TryParse(_sellWeeklyLimit.text, out int weeklyLimit) || weeklyLimit < 0)
            {
                Say("Limite semanal invalido.");
                return;
            }

            market.RequestSetSellExtra(Player, item, deadcoins, weeklyLimit);
            _buySignature = null;
        }

        public override void Refresh()
        {
            RefreshItemSearch();
            RefreshDestinationPreview();
            RefreshTotem();
            RefreshDestinations();
            RefreshBuyList();
            RefreshTemplates();
        }

        /// <summary>Reads back both totem settings as they stand: whether the arrival is at this
        /// teleporter (Deadheim's synced setting) and whether "Voltar para casa" is on.</summary>
        private void RefreshTotem()
        {
            if (_totemStatus == null || !(Npc is TeleporterNpc teleporter)) return;

            bool home = teleporter.HomeEnabled;
            if (_homeLabel != null) _homeLabel.text = home ? "Voltar para casa: ligado" : "Voltar para casa: desligado";

            if (!Integration.DeadheimTotemApi.IsAvailable)
            {
                _totemStatus.text = Integration.DeadheimTotemApi.NotInstalled;
                return;
            }
            if (!Integration.DeadheimTotemApi.TryArrival(out Vector3 arrival))
            {
                _totemStatus.text = "Chegada do Totem: ainda não definida.";
                return;
            }
            float distance = Vector3.Distance(arrival, Npc.transform.position);
            _totemStatus.text = distance <= TeleporterNpc.MaxArrivalDistance
                ? $"Chegada do Totem: neste teleportador ({distance:0} m)."
                : $"Chegada do Totem: em outro lugar ({arrival.x:0}, {arrival.z:0}).";
        }

        /// <summary>Filters the item catalogue as the admin types. Rebuilt only when the query
        /// actually changes -- redrawing a list of matches every frame would fight the click
        /// that is trying to land on one.</summary>
        private void RefreshItemSearch()
        {
            if (_itemResults == null || _buyItem == null) return;

            string query = (_buyItem.text ?? "").Trim();
            if (query == _itemQuery) return;
            _itemQuery = query;

            foreach (var row in _itemResultRows) if (row != null) Object.Destroy(row);
            _itemResultRows.Clear();

            // One character matches most of the game; wait until the query says something.
            if (query.Length < 2) return;

            foreach (var match in SearchItems(query, limit: 30))
            {
                var row = Row(_itemResults, 34f);
                _itemResultRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, match, 26f);

                string display = ItemNames.Display(match);
                var label = ValheimUi.CreateLabel(row,
                    display == match ? match : $"{display}  <color=#6b6259>{match}</color>",
                    14, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var pick = ValheimUi.CreateButton(row, "Usar", 70f, 30f, 13);
                var picked = match;
                pick.onClick.AddListener(() =>
                {
                    _buyItem.text = picked;
                    Say($"Item selecionado: {ItemNames.Display(picked)}");
                });
            }
        }

        /// <summary>Item prefabs whose prefab name or displayed name contains the query.
        /// Both are searched because an admin knows one or the other, rarely both -- and on a
        /// modded server the displayed name is usually the only one they have seen.</summary>
        private static List<string> SearchItems(string query, int limit)
        {
            var results = new List<string>();
            if (ObjectDB.instance?.m_items == null) return results;

            foreach (var prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null) continue;
                var drop = prefab.GetComponent<ItemDrop>();
                if (drop?.m_itemData?.m_shared == null) continue;

                string prefabName = prefab.name;
                if (prefabName.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    ItemNames.Display(prefabName).IndexOf(query, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                results.Add(prefabName);
                if (results.Count >= limit) break;
            }

            // An exact prefab name should not be buried under partial matches.
            results.Sort((a, b) =>
            {
                bool aExact = string.Equals(a, query, System.StringComparison.OrdinalIgnoreCase);
                bool bExact = string.Equals(b, query, System.StringComparison.OrdinalIgnoreCase);
                if (aExact != bExact) return aExact ? -1 : 1;
                return a.Length != b.Length ? a.Length - b.Length : string.CompareOrdinal(a, b);
            });
            return results;
        }

        /// <summary>
        /// A form for writing a new quest without leaving the game.
        ///
        /// Quests were YAML-only, which is fine for someone with the server's filesystem open
        /// in another window and useless for anyone else. What it produces *is* that same
        /// YAML -- created here, editable on disk afterwards -- so this is a second door into
        /// the existing format rather than a parallel system.
        /// </summary>
        private void BuildQuestMaker(Transform column, QuestGiverNpc giver)
        {
            Heading(column, "Criar missão");

            var idRow = Row(column, 38f);
            ValheimUi.CreateLabel(idRow, "Título", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questName = ValheimUi.CreateInputField(idRow, "", 240f, 34f);
            Flexible(_questName.gameObject);

            var kindRow = Row(column, 38f);
            ValheimUi.CreateLabel(kindRow, "Tipo", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questKind = ValheimUi.CreateLabel(kindRow, "Collect", 15, ValheimUi.Yellow,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.SetWidth(_questKind.gameObject, 110f);

            // Cycled rather than a dropdown: five options, and a dropdown is a whole widget to
            // build and keep from falling behind the panel.
            var cycle = ValheimUi.CreateButton(kindRow, "Trocar tipo", 130f, 34f, 14);
            cycle.onClick.AddListener(() =>
            {
                _questKindIndex = (_questKindIndex + 1) % QuestKinds.Length;
                _questKind.text = QuestKinds[_questKindIndex].ToString();
                Say(DescribeKind(QuestKinds[_questKindIndex]));
            });

            var targetRow = Row(column, 38f);
            ValheimUi.CreateLabel(targetRow, "Alvo", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questTarget = ValheimUi.CreateInputField(targetRow, "", 180f, 34f);
            Flexible(_questTarget.gameObject);
            ValheimUi.CreateLabel(targetRow, "Qtd", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
            _questAmount = ValheimUi.CreateInputField(targetRow, "1", 60f, 34f);

            var rewardRow = Row(column, 38f);
            ValheimUi.CreateLabel(rewardRow, "Moedas", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questCoins = ValheimUi.CreateInputField(rewardRow, "50", 70f, 34f);
            ValheimUi.CreateLabel(rewardRow, "XP", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questXp = ValheimUi.CreateInputField(rewardRow, "0", 70f, 34f);
            ValheimUi.CreateLabel(rewardRow, "Cooldown h", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            _questReset = ValheimUi.CreateInputField(rewardRow, "0", 60f, 34f);

            var create = ValheimUi.CreateButton(column, "Criar missão", 0f, 38f, 15);
            ValheimUi.SetHeight(create.gameObject, 38f);
            create.onClick.AddListener(() =>
            {
                string title = (_questName.text ?? "").Trim();
                if (title.Length == 0) { Say("Dê um título à missão."); return; }
                if ((_questTarget.text ?? "").Trim().Length == 0)
                {
                    Say(DescribeKind(QuestKinds[_questKindIndex]));
                    return;
                }

                int.TryParse(_questAmount.text, out int amount);
                int.TryParse(_questCoins.text, out int coins);
                int.TryParse(_questXp.text, out int xp);
                int.TryParse(_questReset.text, out int reset);

                // Semicolons are the field separator on the wire, so they cannot survive in
                // free text -- stripped here rather than corrupting the packet.
                string Clean(string s) => (s ?? "").Replace(';', ',').Replace('\n', ' ').Trim();

                var packed = string.Join(";", new[]
                {
                    Clean(title), Clean(title), ((int)QuestKinds[_questKindIndex]).ToString(),
                    Clean(_questTarget.text), Mathf.Max(1, amount).ToString(),
                    Mathf.Max(0, coins).ToString(), Mathf.Max(0, xp).ToString(),
                    Mathf.Max(0, reset).ToString(), Clean(title),
                });

                giver.RequestCreateQuest(Player, packed);
                Say($"Missão '{title}' criada e adicionada a este NPC.");
                _questName.text = "";
                _questTarget.text = "";
            });

            _questHint = Dim(column, DescribeKind(QuestObjectiveKind.Collect));
            ValheimUi.SetHeight(_questHint.gameObject, 34f);
        }

        private static readonly QuestObjectiveKind[] QuestKinds =
        {
            QuestObjectiveKind.Collect, QuestObjectiveKind.Kill, QuestObjectiveKind.Gather,
            QuestObjectiveKind.Talk, QuestObjectiveKind.Explore,
        };

        /// <summary>What the Alvo field means for the chosen kind. Each objective reads a
        /// completely different thing out of it, and guessing wrong produces a quest that
        /// looks fine and can never be finished.</summary>
        private static string DescribeKind(QuestObjectiveKind kind)
        {
            switch (kind)
            {
                case QuestObjectiveKind.Collect: return "Alvo = prefab do item. Confere a bolsa na entrega; comprar vale.";
                case QuestObjectiveKind.Kill: return "Alvo = prefab da criatura, ex: Greyling.";
                case QuestObjectiveKind.Gather: return "Alvo = prefab do item. Conta o que você PEGA do chão; comprar não vale.";
                case QuestObjectiveKind.Talk: return "Alvo = nome do NPC, o mesmo que aparece sobre a cabeça dele.";
                case QuestObjectiveKind.Explore: return "Alvo = \"x,z\" no mapa. Qtd = raio em metros que conta como chegar.";
                default: return "";
            }
        }

        /// <summary>Reads the three coordinate boxes. All three have to be filled and valid --
        /// a half-typed coordinate is a mistake, not an instruction, and falling back to the
        /// admin's position on a typo would bind a destination somewhere they never meant.</summary>
        private CoordinateInputState ReadCoordinates(out Vector3 position)
        {
            position = Vector3.zero;
            if (_destinationX == null || _destinationY == null || _destinationZ == null)
                return CoordinateInputState.Invalid;

            var culture = CultureInfo.InvariantCulture;
            var style = NumberStyles.Float;

            // Comma accepted as a decimal point: the game's own console prints coordinates
            // one way and a Brazilian keyboard types them another.
            string x = (_destinationX.text ?? "").Trim().Replace(',', '.');
            string y = (_destinationY.text ?? "").Trim().Replace(',', '.');
            string z = (_destinationZ.text ?? "").Trim().Replace(',', '.');
            if (x.Length == 0 && y.Length == 0 && z.Length == 0) return CoordinateInputState.Empty;

            if (!float.TryParse(x, style, culture, out float px) ||
                !float.TryParse(y, style, culture, out float py) ||
                !float.TryParse(z, style, culture, out float pz))
            {
                Say("Coordenadas incompletas: preencha X, Y e Z, ou deixe os três vazios.");
                return CoordinateInputState.Invalid;
            }

            position = new Vector3(px, py, pz);
            return TeleporterNpc.IsValidDestinationPosition(position)
                ? CoordinateInputState.Valid
                : CoordinateInputState.Invalid;
        }

        private void RefreshBuyList()
        {
            if (_buys == null || !(Npc is MarketplaceNpc market)) return;

            var buys = market.GetBuyPrices();
            var buyLots = market.GetBuyLots();
            var buyBosses = market.GetBuyBosses();
            var sells = market.GetSellOffers();
            var signature = string.Join("|", buys.Select(kv =>
                                $"B{kv.Key}:{kv.Value}:{(buyLots.TryGetValue(kv.Key, out int lot) ? lot : 1)}" +
                                $":{(buyBosses.TryGetValue(kv.Key, out var boss) ? boss : "")}")) + "/" +
                            string.Join("|", sells.Select(kv =>
                                $"S{kv.Key}:{kv.Value.Gold}:{kv.Value.Deadcoins}:{kv.Value.WeeklyLimit}:{kv.Value.Lot}:{kv.Value.Boss}")) +
                            "/" + market.RequiredBoss;
            if (signature == _buySignature) return;
            _buySignature = signature;

            foreach (var row in _buyRows) if (row != null) Object.Destroy(row);
            _buyRows.Clear();

            if (buys.Count == 0 && sells.Count == 0)
            {
                _buyRows.Add(Dim(_buys, "(o NPC ainda nao negocia nada)").gameObject);
                return;
            }

            AddSellRows(market, sells);
            AddPriceRows(market, buys, buyLots, buyBosses, selling: false);
        }

        /// <summary>A3: one row per sell item, both prices and the weekly limit together, with
        /// a remove button for each half (Coins goes through RequestSetPrice, Deadcoins and
        /// the limit through RequestSetSellExtra -- each leaves the other alone).</summary>
        private void AddSellRows(MarketplaceNpc market, Dictionary<string, SellOffer> offers)
        {
            foreach (var kv in offers)
            {
                var item = kv.Key;
                var offer = kv.Value;
                var row = Row(_buys, 44f);
                _buyRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, item, 34f);
                string perLot = MarketplaceNpc.PerLot(offer.Lot);
                string gold = offer.Gold > 0 ? $"{offer.Gold} moedas{perLot}" : "";
                string dc = offer.Deadcoins > 0 ? $"{offer.Deadcoins} Deadcoins{perLot}" : "";
                string price = gold.Length > 0 && dc.Length > 0 ? $"{gold} · {dc}" : gold + dc;
                string weekly = offer.WeeklyLimit > 0 ? $" · limite {offer.WeeklyLimit}/semana" : "";
                var label = ValheimUi.CreateLabel(row,
                    ItemNames.Display(item) + "\n<size=11><color=#9a9188>vende: " + price + weekly +
                    PassNote(market, offer.Boss) + "</color></size>",
                    15, ValheimUi.Orange, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var removeGold = ValheimUi.CreateButton(row, "Sem moedas", 100f, 34f, 13);
                removeGold.onClick.AddListener(() =>
                {
                    market.RequestSetPrice(Player, item, 0, selling: true);
                    Say($"{ItemNames.Display(item)}: preço em moedas removido.");
                    _buySignature = null;
                });
                var removeDc = ValheimUi.CreateButton(row, "Sem Deadcoins", 120f, 34f, 13);
                removeDc.onClick.AddListener(() =>
                {
                    market.RequestSetSellExtra(Player, item, 0, offer.WeeklyLimit);
                    Say($"{ItemNames.Display(item)}: preço em Deadcoins removido.");
                    _buySignature = null;
                });
            }
        }

        /// <summary>" · passe de Eikthyr" on a row whose item requires one.</summary>
        private static string PassNote(MarketplaceNpc market, string itemBoss)
        {
            string boss = market.EffectiveBoss(itemBoss);
            return boss.Length == 0 ? "" : " · passe de " + (BossPassCatalog.Find(boss)?.Name ?? boss);
        }

        private void AddPriceRows(MarketplaceNpc market, Dictionary<string, int> prices, Dictionary<string, int> lots,
            Dictionary<string, string> bosses, bool selling)
        {
            foreach (var kv in prices)
            {
                var row = Row(_buys, 44f);
                _buyRows.Add(row.gameObject);

                int lot = lots.TryGetValue(kv.Key, out int found) ? found : 1;
                ValheimUi.CreateItemIcon(row, kv.Key, 34f);
                var label = ValheimUi.CreateLabel(row,
                    ItemNames.Display(kv.Key) + "\n<size=11><color=#9a9188>" +
                    (selling ? "vende" : "compra") + " a " + kv.Value + MarketplaceNpc.PerLot(lot) +
                    PassNote(market, bosses.TryGetValue(kv.Key, out var boss) ? boss : "") + "</color></size>",
                    15, selling ? ValheimUi.Orange : ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var item = kv.Key;
                var remove = ValheimUi.CreateButton(row, "Remover", 100f, 34f, 14);
                remove.onClick.AddListener(() =>
                {
                    market.RequestSetPrice(Player, item, 0, selling);
                    Say(ItemNames.Display(item) + " removido.");
                    _buySignature = null;
                });
            }
        }

        /// <summary>
        /// Reads the new-destination form back in plain words, before it is committed.
        ///
        /// Everything here was already true of the form; none of it was visible. Three empty
        /// coordinate boxes look required, so "leave them empty to use your own position" read
        /// as an error waiting to happen rather than as the normal case; and a cost of 0 is
        /// ambiguous between free and "whatever the NPC charges", which are different numbers.
        /// Stating the outcome costs nothing and removes both guesses.
        /// </summary>
        private void RefreshDestinationPreview()
        {
            if (_destinationPreview == null || !(Npc is TeleporterNpc teleporter)) return;

            string name = (_destinationName?.text ?? "").Trim();
            if (name.Length == 0)
            {
                _destinationPreview.text = "Dê um nome ao destino para começar.";
                return;
            }

            // Parsed quietly: this runs every frame, and ReadCoordinates reports its failures
            // to the player, which would turn a half-typed number into a stream of complaints.
            var culture = CultureInfo.InvariantCulture;
            string x = (_destinationX?.text ?? "").Trim().Replace(',', '.');
            string y = (_destinationY?.text ?? "").Trim().Replace(',', '.');
            string z = (_destinationZ?.text ?? "").Trim().Replace(',', '.');

            string where;
            if (x.Length == 0 && y.Length == 0 && z.Length == 0)
            {
                var at = Player != null ? Player.transform.position : Vector3.zero;
                where = $"onde você está agora ({at.x:0}, {at.y:0}, {at.z:0})";
            }
            else if (float.TryParse(x, NumberStyles.Float, culture, out float px) &&
                     float.TryParse(y, NumberStyles.Float, culture, out float py) &&
                     float.TryParse(z, NumberStyles.Float, culture, out float pz))
            {
                var typed = new Vector3(px, py, pz);
                where = TeleporterNpc.IsValidDestinationPosition(typed)
                    ? $"em ({px:0}, {py:0}, {pz:0})"
                    : $"<color=#d66>em ({px:0}, {py:0}, {pz:0}) — fora do mundo</color>";
            }
            else
            {
                _destinationPreview.text =
                    "<color=#d66>Coordenadas incompletas: preencha X, Y e Z, ou apague os três para usar sua posição.</color>";
                return;
            }

            string fare = !int.TryParse((_destinationCost?.text ?? "").Trim(), out int cost) || cost < 0
                ? "<color=#d66>custo inválido</color>"
                : DescribeFare(teleporter, cost, (_destinationCostItem?.text ?? "").Trim());

            _destinationPreview.text = $"Vai gravar «{name}» {where} — {fare}.";
        }

        /// <summary>The fare a route is about to charge, said the same way in the preview
        /// and in the confirmation. Two knobs -- a price and an item, either of which may
        /// defer to the NPC -- make four outcomes, which is two too many to leave the reader
        /// to work out.</summary>
        private static string DescribeFare(TeleporterNpc teleporter, int cost, string costItem)
        {
            if (!string.IsNullOrEmpty(costItem))
                return cost > 0
                    ? $"custa {cost} {ItemNames.Display(costItem)}"
                    : $"grátis (custo 0 em {ItemNames.Display(costItem)})";

            if (cost > 0)
            {
                string npcItem = teleporter.CostItem;
                return string.IsNullOrEmpty(npcItem)
                    ? $"<color=#d66>custa {cost}, mas o NPC não tem item de cobrança padrão</color>"
                    : $"custa {cost} {ItemNames.Display(npcItem)} (item padrão do NPC)";
            }

            int fallback = teleporter.CostOf(null);
            string item = teleporter.CostItem;
            return fallback > 0 && !string.IsNullOrEmpty(item)
                ? $"usa o padrão do NPC: {fallback} {ItemNames.Display(item)}"
                : "grátis (o NPC não cobra nada)";
        }

        private void RefreshDestinations()
        {
            if (_destinations == null || !(Npc is TeleporterNpc teleporter)) return;

            var destinations = teleporter.GetDestinations();
            var signature = string.Join("|", destinations.Select(d =>
                $"{d.Id}:{d.Name}:{teleporter.CostOf(d)}:{teleporter.CostItemOf(d)}"));
            if (signature == _destinationSignature) return;
            _destinationSignature = signature;

            foreach (var row in _destinationRows) if (row != null) Object.Destroy(row);
            _destinationRows.Clear();

            if (destinations.Count == 0)
            {
                _destinationRows.Add(Dim(_destinations, "(nenhum destino ainda)").gameObject);
                return;
            }

            foreach (var destination in destinations)
            {
                var row = Row(_destinations, 42f);
                _destinationRows.Add(row.gameObject);

                int fare = teleporter.CostOf(destination);
                string fareItem = teleporter.CostItemOf(destination);
                string price = fare > 0 && !string.IsNullOrEmpty(fareItem)
                    ? $"{fare} {ItemNames.Display(fareItem)}"
                    : "grátis";

                var label = ValheimUi.CreateLabel(row,
                    $"{destination.Name}\n<size=11><color=#9a9188>" +
                    $"{destination.Position.x:0}, {destination.Position.z:0} — {price}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var id = destination.Id;
                var remove = ValheimUi.CreateButton(row, "Remover", 110f, 34f, 14);
                remove.onClick.AddListener(() =>
                {
                    teleporter.RequestRemoveDestination(Player, id);
                    Say($"Removendo '{destination.Name}'...");
                    _destinationSignature = null;
                });
            }
        }

        private void RefreshTemplates()
        {
            Npc.RequestTemplateIndex();

            // Only the presets that make sense here. Offering a merchant's counter to a
            // teleporter was one wrong click away from replacing a travel network with a
            // price list.
            var names = Npc.GetServerTemplateNames();
            var signature = string.Join("|", names);
            if (signature == _templateSignature) return;
            _templateSignature = signature;

            foreach (var row in _templateRows) if (row != null) Object.Destroy(row);
            _templateRows.Clear();

            if (names.Count == 0)
            {
                var empty = Dim(_templates, "(nenhum modelo para este tipo de NPC)");
                _templateRows.Add(empty.gameObject);
                return;
            }

            foreach (var name in names)
            {
                var row = Row(_templates, 40f);
                _templateRows.Add(row.gameObject);

                var label = ValheimUi.CreateLabel(row, name, 15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var captured = name;
                var apply = ValheimUi.CreateButton(row, "Aplicar", 110f, 34f, 14);
                apply.onClick.AddListener(() =>
                {
                    Npc.RequestApplyTemplateByName(Player, captured);
                });
            }
        }
    }
}
