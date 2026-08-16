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
        private TMP_InputField _destinationX;
        private TMP_InputField _destinationY;
        private TMP_InputField _destinationZ;
        private TextMeshProUGUI _waypointHint;

        private RectTransform _templates;
        private readonly List<GameObject> _templateRows = new List<GameObject>();
        private string _templateSignature;

        private RectTransform _destinations;
        private readonly List<GameObject> _destinationRows = new List<GameObject>();
        private string _destinationSignature;

        private TMP_InputField _buyItem;
        private TMP_InputField _buyPrice;
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
                Heading(column, "Novo destino");
                var addRow = Row(column, 40f);
                ValheimUi.CreateLabel(addRow, "Nome", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
                _destinationName = ValheimUi.CreateInputField(addRow, "", 170f, 38f);
                Flexible(_destinationName.gameObject);
                ValheimUi.CreateLabel(addRow, "Custo", 14, ValheimUi.Beige, TextAlignmentOptions.Right);
                _destinationCost = ValheimUi.CreateInputField(addRow, "0", 60f, 38f);

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

                var here = ValheimUi.CreateButton(coordRow, "Onde estou", 130f, 38f, 14);
                here.onClick.AddListener(() =>
                {
                    var at = Player.transform.position;
                    _destinationX.text = at.x.ToString("0.0", CultureInfo.InvariantCulture);
                    _destinationY.text = at.y.ToString("0.0", CultureInfo.InvariantCulture);
                    _destinationZ.text = at.z.ToString("0.0", CultureInfo.InvariantCulture);
                    Say($"Coordenadas preenchidas com a sua posição ({at.x:0}, {at.y:0}, {at.z:0}).");
                });

                var add = ValheimUi.CreateButton(addRow, "Adicionar", 160f, 38f, 15);
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

                    // Three ways to name a place, in order of how explicit they are: typed
                    // coordinates beat a marked point, which beats "wherever I am standing".
                    // Whatever the admin said most deliberately is what wins.
                    if (TryReadCoordinates(out var typed))
                    {
                        teleporter.RequestAddDestination(Player, _destinationName.text, cost,
                            typed, Player.transform.rotation.eulerAngles.y);
                        Say($"Destino '{_destinationName.text}' gravado em " +
                            $"({typed.x:0}, {typed.y:0}, {typed.z:0}), custo {cost}.");
                        _destinationName.text = "";
                        _destinationSignature = null;
                        return;
                    }

                    if (WaypointMarker.TryGetBindPoint(out var point, out float pointYaw))
                    {
                        teleporter.RequestAddDestination(Player, _destinationName.text, cost, point, pointYaw);
                        Say($"Destino '{_destinationName.text}' gravado no ponto marcado " +
                            $"({point.x:0}, {point.z:0}).");
                        WaypointMarker.Clear();
                    }
                    else
                    {
                        teleporter.RequestAddDestination(Player, _destinationName.text, cost);
                        Say($"Destino '{_destinationName.text}' gravado na sua posição.");
                    }

                    _destinationName.text = "";
                    _destinationSignature = null;
                });

                // Tells the admin which of the two the button is about to do, and how to get
                // the other one.
                _waypointHint = Dim(column, "");
                ValheimUi.SetHeight(_waypointHint.gameObject, 20f);

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
                    Say("Configuração aplicada.");
                });
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
                    return;
                }

                // Two lists on one counter: he deals only in what he is told to deal in.
                Heading(column, "Balcão (item / preço)");
                var buyRow = Row(column, 40f);
                _buyItem = ValheimUi.CreateInputField(buyRow, "", 150f, 38f);
                Flexible(_buyItem.gameObject);
                _buyPrice = ValheimUi.CreateInputField(buyRow, "1", 60f, 38f);
                var addBuy = ValheimUi.CreateButton(buyRow, "Ele compra", 125f, 38f, 14);
                var addSell = ValheimUi.CreateButton(buyRow, "Ele vende", 125f, 38f, 14);
                addBuy.onClick.AddListener(() => SetPrice(market, selling: false));
                addSell.onClick.AddListener(() => SetPrice(market, selling: true));

                // Typing the exact prefab name meant knowing it beforehand -- and on a modded
                // server that is hundreds of names nobody has memorised. The same box now
                // searches, matching either the prefab name or the name the player reads, and
                // picking a result fills it in.
                Dim(column, "Digite para buscar; clique num resultado para preencher.");
                var searchArea = ValheimUi.CreateRect("SearchArea", column);
                ValheimUi.SetHeight(searchArea.gameObject, 150f);
                _itemResults = ValheimUi.CreateScrollList(searchArea, spacing: 2f);
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
                Say($"Modelo '{_templateName.text}' salvo.");
                _templateSignature = null;
            });

            // ---- right: destinations (teleporters) above saved templates ----
            var right = ValheimUi.CreateRect("Right", frame);
            ValheimUi.Anchor(right, new Vector2(0.55f, 0f), new Vector2(1f, 1f),
                new Vector2(8f, 12f), new Vector2(-16f, -12f));

            bool isTeleporter = Npc is TeleporterNpc;
            bool isMarket = Npc is MarketplaceNpc;
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

            market.RequestSetPrice(Player, item, price, selling);
            string side = selling ? "vender" : "comprar";
            Say(price > 0
                ? $"O NPC passa a {side} {ItemNames.Display(item)} por {price}/un."
                : $"{ItemNames.Display(item)} removido da lista de {side}.");
            _buyItem.text = "";
            _buySignature = null;
        }

        public override void Refresh()
        {
            RefreshItemSearch();
            RefreshWaypointHint();
            RefreshDestinations();
            RefreshBuyList();
            RefreshTemplates();
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
        private bool TryReadCoordinates(out Vector3 position)
        {
            position = Vector3.zero;
            if (_destinationX == null || _destinationY == null || _destinationZ == null) return false;

            var culture = CultureInfo.InvariantCulture;
            var style = NumberStyles.Float;

            // Comma accepted as a decimal point: the game's own console prints coordinates
            // one way and a Brazilian keyboard types them another.
            string x = (_destinationX.text ?? "").Trim().Replace(',', '.');
            string y = (_destinationY.text ?? "").Trim().Replace(',', '.');
            string z = (_destinationZ.text ?? "").Trim().Replace(',', '.');
            if (x.Length == 0 && y.Length == 0 && z.Length == 0) return false;

            if (!float.TryParse(x, style, culture, out float px) ||
                !float.TryParse(y, style, culture, out float py) ||
                !float.TryParse(z, style, culture, out float pz))
            {
                Say("Coordenadas incompletas: preencha X, Y e Z, ou deixe os três vazios.");
                return false;
            }

            position = new Vector3(px, py, pz);
            return true;
        }

        private void RefreshWaypointHint()
        {
            if (_waypointHint == null) return;

            if (_destinationX != null && (_destinationX.text ?? "").Trim().Length > 0)
            {
                _waypointHint.text = "<color=#ffd24a>Usando as coordenadas digitadas.</color> " +
                                     "Limpe X/Y/Z para voltar ao ponto marcado ou à sua posição.";
                return;
            }

            _waypointHint.text = WaypointMarker.HasWaypoint
                ? $"<color=#ffd24a>Ponto marcado em ({WaypointMarker.Position.x:0}, " +
                  $"{WaypointMarker.Position.z:0})</color> — será usado ao adicionar."
                : $"Sem ponto marcado: usará sua posição atual. Pressione " +
                  $"<color=#ffd24a>{Plugin.MarkWaypointKey.Value}</color> onde quer chegar.";
        }

        private void RefreshBuyList()
        {
            if (_buys == null || !(Npc is MarketplaceNpc market)) return;

            var buys = market.GetBuyPrices();
            var sells = market.GetSellPrices();
            var signature = string.Join("|", buys.Select(kv => $"B{kv.Key}:{kv.Value}")) + "/" +
                            string.Join("|", sells.Select(kv => $"S{kv.Key}:{kv.Value}"));
            if (signature == _buySignature) return;
            _buySignature = signature;

            foreach (var row in _buyRows) if (row != null) Object.Destroy(row);
            _buyRows.Clear();

            if (buys.Count == 0 && sells.Count == 0)
            {
                _buyRows.Add(Dim(_buys, "(o NPC ainda nao negocia nada)").gameObject);
                return;
            }

            AddPriceRows(market, sells, selling: true);
            AddPriceRows(market, buys, selling: false);
        }

        private void AddPriceRows(MarketplaceNpc market, Dictionary<string, int> prices, bool selling)
        {
            foreach (var kv in prices)
            {
                var row = Row(_buys, 44f);
                _buyRows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, kv.Key, 34f);
                var label = ValheimUi.CreateLabel(row,
                    ItemNames.Display(kv.Key) + "\n<size=11><color=#9a9188>" +
                    (selling ? "vende" : "compra") + " a " + kv.Value + "/un</color></size>",
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

        private void RefreshDestinations()
        {
            if (_destinations == null || !(Npc is TeleporterNpc teleporter)) return;

            var destinations = teleporter.GetDestinations();
            var signature = string.Join("|", destinations.Select(d => $"{d.Id}:{d.Name}"));
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

                var label = ValheimUi.CreateLabel(row,
                    $"{destination.Name}\n<size=11><color=#9a9188>" +
                    $"{destination.Position.x:0}, {destination.Position.z:0}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var id = destination.Id;
                var remove = ValheimUi.CreateButton(row, "Remover", 110f, 34f, 14);
                remove.onClick.AddListener(() =>
                {
                    teleporter.RequestRemoveDestination(Player, id);
                    Say($"Destino '{destination.Name}' removido.");
                    _destinationSignature = null;
                });
            }
        }

        private void RefreshTemplates()
        {
            // Only the presets that make sense here. Offering a merchant's counter to a
            // teleporter was one wrong click away from replacing a travel network with a
            // price list.
            var names = NpcConfigStore.ListTemplatesFor(Npc.ProfileType);
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
                    Say($"Modelo '{captured}' aplicado.");
                });
            }
        }
    }
}
