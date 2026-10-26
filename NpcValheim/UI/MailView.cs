using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>The mailbox: one row per parcel, with the item's real icon, and a claim-all
    /// at the bottom for when a long auction run has filled it up.</summary>
    internal sealed class MailView : NpcViewBase
    {
        private MailboxNpc Mailbox => Npc as MailboxNpc;

        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private TextMeshProUGUI _header;
        private Button _claimAll;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private GameObject _reader;
        private string _readingId;

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "Mail");
            ValheimUi.Stretch(frame, 0f, 0f);

            _header = ValheimUi.CreateLabel(frame, "Correio", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)_header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -6f));

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 56f), new Vector2(-6f, -40f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "Carregando...", 16, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -14f));

            _claimAll = ValheimUi.CreateButton(frame, "Receber tudo", 220f, 42f, 16);
            ValheimUi.Anchor((RectTransform)_claimAll.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-238f, 8f), new Vector2(-14f, 50f));
            _claimAll.onClick.AddListener(() =>
            {
                Mailbox.RequestClaimAll();
                Say("Recebendo tudo; os itens vão para o inventário.");
            });

            Mailbox?.RequestMail();
        }

        public override void Refresh()
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;

            if (!string.IsNullOrEmpty(mailbox.LastStatus))
            {
                Say(mailbox.LastStatus);
                mailbox.LastStatus = "";
            }

            if (_readingId != null && mailbox.CachedMail.All(m => m.Id != _readingId))
                CloseReader();

            var signature = string.Join("|", mailbox.CachedMail.Select(m => m.Id + (m.Read ? "r" : "u")));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(mailbox);
            }

            bool any = mailbox.CachedMail.Count > 0;
            _empty.gameObject.SetActive(!any);
            _empty.text = mailbox.HasSyncedOnce ? "Sua caixa está vazia." : "Carregando...";
            _claimAll.gameObject.SetActive(any);
            int unread = mailbox.CachedMail.Count(m => m != null && m.IsMessage && !m.Read);
            _header.text = !any
                ? "Correio"
                : unread > 0
                    ? $"Correio — {mailbox.CachedMail.Count}  ·  {unread} nova(s)"
                    : $"Correio — {mailbox.CachedMail.Count} item(ns)";
        }

        private void Rebuild(MailboxNpc mailbox)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            foreach (var entry in mailbox.CachedMail)
            {
                bool message = entry.IsMessage;
                var row = Row(_list, message ? 78f : 48f);
                _rows.Add(row.gameObject);

                if (!message)
                {
                    string iconPrefab = entry.IsCoins ? MarketplaceNpc.CoinPrefabName : entry.ItemName;
                    ValheimUi.CreateItemIcon(row, iconPrefab, 40f);
                }

                string what = entry.IsCoins
                    ? $"{entry.Coins} moedas"
                    : message
                        ? ((entry.Read ? "" : "● ") + entry.Subject)
                        : $"{ValheimUi.Localize(MarketView.DisplayName(entry.ItemName))} x{entry.Amount}";

                string from = string.IsNullOrEmpty(entry.SenderName) ? "" : $"de {entry.SenderName}";

                string extra = message
                    ? (string.IsNullOrEmpty(from) ? (entry.Read ? "lida" : "nova") : from)
                    : entry.Subject;
                if (string.IsNullOrEmpty(extra)) extra = " ";

                var titleColor = message && !entry.Read ? ValheimUi.Orange : ValheimUi.Beige;
                var label = ValheimUi.CreateLabel(row,
                    $"{what}\n<size=12><color=#9a9188>{extra}</color></size>",
                    16, titleColor, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var id = entry.Id;
                if (message)
                {
                    var read = ValheimUi.CreateButton(row, "Ler", 90f, 38f, 15);
                    read.onClick.AddListener(() => OpenReader(id));
                    var dismiss = ValheimUi.CreateButton(row, "Excluir", 110f, 38f, 15);
                    dismiss.onClick.AddListener(() =>
                    {
                        if (_readingId == id) CloseReader();
                        mailbox.RequestClaim(id);
                        Say("Mensagem removida.");
                    });
                }
                else
                {
                    var claim = ValheimUi.CreateButton(row, "Receber", 120f, 38f, 15);
                    claim.onClick.AddListener(() =>
                    {
                        mailbox.RequestClaim(id);
                        Say("Pedido enviado; o item vai para o inventário.");
                    });
                }
            }
        }

        private void OpenReader(string mailId)
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;
            var entry = mailbox.CachedMail.FirstOrDefault(m => m.Id == mailId);
            if (entry == null) return;

            if (!entry.Read)
            {
                entry.Read = true;
                mailbox.RequestMarkRead(mailId);
                _signature = null;
            }

            CloseReader();
            _readingId = mailId;

            _reader = new GameObject("LetterReader", typeof(RectTransform));
            _reader.transform.SetParent(Root, false);
            var overlay = (RectTransform)_reader.transform;
            overlay.anchorMin = Vector2.zero;
            overlay.anchorMax = Vector2.one;
            overlay.offsetMin = Vector2.zero;
            overlay.offsetMax = Vector2.zero;
            var dim = _reader.AddComponent<Image>();
            dim.color = new Color(0f, 0f, 0f, 0.55f);
            dim.raycastTarget = true;

            var panel = ValheimUi.CreatePanel(_reader.transform, 560f, 400f);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;

            string from = string.IsNullOrEmpty(entry.SenderName) ? "Alguém" : entry.SenderName;
            var title = ValheimUi.CreateLabel(panel, entry.Subject, 22, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -52f), new Vector2(-16f, -10f));

            var by = ValheimUi.CreateLabel(panel, "de " + from, 14, ValheimUi.Muted,
                TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)by.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -78f), new Vector2(-16f, -52f));

            var frame = ValheimUi.CreateInlay(panel, "Body");
            ValheimUi.Anchor(frame, Vector2.zero, Vector2.one, new Vector2(16f, 56f), new Vector2(-16f, -88f));
            var body = ValheimUi.CreateLabel(frame,
                string.IsNullOrEmpty(entry.Body) ? "(sem mensagem)" : entry.Body,
                16, ValheimUi.Beige, TextAlignmentOptions.TopLeft);
            ValheimUi.Stretch((RectTransform)body.transform, 10f, 10f);

            var back = ValheimUi.CreateButton(panel, "Voltar", 140f, 36f, 15);
            ValheimUi.Anchor((RectTransform)back.transform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(16f, 12f), new Vector2(156f, 48f));
            back.onClick.AddListener(CloseReader);

            var dismiss = ValheimUi.CreateButton(panel, "Excluir", 140f, 36f, 15);
            ValheimUi.Anchor((RectTransform)dismiss.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-156f, 12f), new Vector2(-16f, 48f));
            dismiss.onClick.AddListener(() =>
            {
                CloseReader();
                mailbox.RequestClaim(mailId);
                Say("Mensagem removida.");
            });
        }

        private void CloseReader()
        {
            _readingId = null;
            if (_reader != null) Object.Destroy(_reader);
            _reader = null;
        }
    }

    /// <summary>Write a letter to a player. The server resolves the name against the
    /// directory (anyone who has logged in) so offline recipients still work.</summary>
    internal sealed class MailComposeView : NpcViewBase
    {
        private MailboxNpc Mailbox => Npc as MailboxNpc;

        private TMP_InputField _target;
        private TMP_InputField _subject;
        private TMP_InputField _body;
        private RectTransform _suggestions;
        private TextMeshProUGUI _hint;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "Compose");
            ValheimUi.Stretch(frame, 0f, 0f);

            var title = ValheimUi.CreateLabel(frame, "Nova mensagem", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -6f));

            var targetLabel = ValheimUi.CreateLabel(frame, "Destinatário (digite para buscar)", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)targetLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -104f), new Vector2(-12f, -86f));
            _target = ValheimUi.CreateInputField(frame, "", 0f, 36f);
            ValheimUi.Anchor((RectTransform)_target.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -142f), new Vector2(-12f, -106f));

            var subjectLabel = ValheimUi.CreateLabel(frame, "Assunto", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)subjectLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -164f), new Vector2(-12f, -146f));
            _subject = ValheimUi.CreateInputField(frame, "", 0f, 36f);
            ValheimUi.Anchor((RectTransform)_subject.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -202f), new Vector2(-12f, -166f));

            var bodyLabel = ValheimUi.CreateLabel(frame, "Mensagem", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)bodyLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -224f), new Vector2(-12f, -206f));
            _body = ValheimUi.CreateInputField(frame, "", 0f, 90f, 15, multiline: true);
            ValheimUi.Anchor((RectTransform)_body.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -318f), new Vector2(-12f, -226f));

            var listArea = ValheimUi.CreateRect("Suggest", frame);
            ValheimUi.Anchor(listArea, Vector2.zero, Vector2.one, new Vector2(8f, 56f), new Vector2(-8f, -326f));
            _suggestions = ValheimUi.CreateScrollList(listArea, spacing: 3f);

            _hint = ValheimUi.CreateLabel(listArea, "", 14, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_hint.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -8f), new Vector2(-10f, 24f));

            var send = ValheimUi.CreateButton(frame, "Enviar", 220f, 42f, 16);
            ValheimUi.Anchor((RectTransform)send.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-238f, 8f), new Vector2(-14f, 50f));
            send.onClick.AddListener(() =>
            {
                Mailbox.RequestSend(_target.text, _subject.text, _body.text);
                Say("Enviando para o jogador...");
            });

            Mailbox?.RequestDirectory();
        }

        public override void Refresh()
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;

            if (!string.IsNullOrEmpty(mailbox.LastStatus))
            {
                Say(mailbox.LastStatus);
                mailbox.LastStatus = "";
            }

            var signature = RecipientSearch.Signature(mailbox, _target.text);
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(mailbox);
            }
        }

        private void Rebuild(MailboxNpc mailbox)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var matches = RecipientSearch.Find(mailbox, _target.text, out int total);
            _hint.text = RecipientSearch.Hint(mailbox, _target.text, matches.Count, total);
            _hint.gameObject.SetActive(_hint.text.Length > 0);

            foreach (var recipient in matches)
            {
                var row = Row(_suggestions, 36f);
                _rows.Add(row.gameObject);
                var pick = ValheimUi.CreateButton(row, recipient.Name, 0f, 34f, 14);
                Flexible(pick.gameObject);
                var name = recipient.Name;
                pick.onClick.AddListener(() => _target.text = name);
            }
        }
    }

    /// <summary>
    /// Send an item to another player via the mailbox. The attachment is taken directly from
    /// the sender's inventory and waits in the recipient's mailbox until collected.
    ///
    /// Non-VIP: 1 stack per letter (up to the item's own max stack size).
    /// VIP: up to VipMaxAttachments stacks per letter.
    /// The server re-validates both limits and hands the parcel back if it refuses; this UI
    /// is just first-line UX.
    /// </summary>
    internal sealed class MailParcelView : NpcViewBase
    {
        private MailboxNpc Mailbox => Npc as MailboxNpc;

        // inventory picker
        private RectTransform _inventory;
        private string _inventorySignature;
        private readonly List<GameObject> _inventoryRows = new List<GameObject>();
        private Image _selectedIcon;
        private TextMeshProUGUI _selectedLabel;
        private string _itemName = "";
        private int _itemQuality = 1;
        private int _itemMaxStack = 1;

        // form fields
        private TMP_InputField _target;
        private TMP_InputField _amount;
        private TMP_InputField _subject;
        private TMP_InputField _body;
        private TextMeshProUGUI _limitHint;
        private string _dirSignature;
        private RectTransform _suggestions;
        private readonly List<GameObject> _suggestionRows = new List<GameObject>();

        protected override void OnBuild()
        {
            // Two columns: left = inventory, right = form
            const float leftW = 280f;

            var left = ValheimUi.CreateInlay(Root, "Inv");
            ValheimUi.Anchor(left, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(leftW, 0f));

            var invHeader = ValheimUi.CreateLabel(left, "Inventário", 16, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)invHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -36f), new Vector2(-8f, -6f));

            var invArea = ValheimUi.CreateRect("InvArea", left);
            ValheimUi.Anchor(invArea, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -38f));
            _inventory = ValheimUi.CreateScrollList(invArea, spacing: 3f);

            // right pane
            var right = ValheimUi.CreateInlay(Root, "Form");
            ValheimUi.Anchor(right, Vector2.zero, Vector2.one, new Vector2(leftW + 8f, 0f), Vector2.zero);

            // selected item display
            var selRow = ValheimUi.CreateRect("SelRow", right);
            ValheimUi.Anchor(selRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -48f), new Vector2(-8f, -6f));
            var selLayout = selRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            selLayout.spacing = 8f;
            selLayout.childControlWidth = true;
            selLayout.childControlHeight = true;
            selLayout.childForceExpandWidth = false;
            selLayout.childAlignment = TextAnchor.MiddleLeft;
            _selectedIcon = ValheimUi.CreateItemIcon(selRow, null, 36f);
            _selectedLabel = ValheimUi.CreateLabel(selRow, "(nenhum item selecionado)", 15,
                ValheimUi.Muted, TextAlignmentOptions.Left);
            _selectedLabel.textWrappingMode = TextWrappingModes.NoWrap;

            // limit hint (changes with VIP status)
            _limitHint = ValheimUi.CreateLabel(right, "", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)_limitHint.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -68f), new Vector2(-8f, -50f));

            // destinatário
            var tLabel = ValheimUi.CreateLabel(right, "Destinatário (digite para buscar)", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)tLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -90f), new Vector2(-8f, -72f));
            _target = ValheimUi.CreateInputField(right, "", 0f, 34f);
            ValheimUi.Anchor((RectTransform)_target.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -126f), new Vector2(-8f, -92f));

            // quantidade
            var qLabel = ValheimUi.CreateLabel(right, "Quantidade", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)qLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -148f), new Vector2(-8f, -130f));
            _amount = ValheimUi.CreateInputField(right, "1", 0f, 34f);
            _amount.contentType = TMP_InputField.ContentType.IntegerNumber;
            ValheimUi.Anchor((RectTransform)_amount.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -184f), new Vector2(-8f, -150f));

            // assunto
            var sLabel = ValheimUi.CreateLabel(right, "Assunto", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)sLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -206f), new Vector2(-8f, -188f));
            _subject = ValheimUi.CreateInputField(right, "", 0f, 34f);
            ValheimUi.Anchor((RectTransform)_subject.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -242f), new Vector2(-8f, -208f));

            // mensagem (opcional)
            var bLabel = ValheimUi.CreateLabel(right, "Mensagem (opcional)", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)bLabel.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -264f), new Vector2(-8f, -246f));
            _body = ValheimUi.CreateInputField(right, "", 0f, 64f, 13, multiline: true);
            ValheimUi.Anchor((RectTransform)_body.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -332f), new Vector2(-8f, -266f));

            // sugestões de destinatário
            var sugArea = ValheimUi.CreateRect("Suggest", right);
            ValheimUi.Anchor(sugArea, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(8f, 54f), new Vector2(-8f, 0f + 332f - 334f + 54f));
            // posição dinâmica — pinnar na parte inferior
            ValheimUi.Anchor(sugArea, Vector2.zero, new Vector2(1f, 0f),
                new Vector2(8f, 54f), new Vector2(-8f, 140f));
            _suggestions = ValheimUi.CreateScrollList(sugArea, spacing: 2f);

            // botão enviar
            var send = ValheimUi.CreateButton(right, "Enviar pacote", 220f, 42f, 16);
            ValheimUi.Anchor((RectTransform)send.transform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-238f, 8f), new Vector2(-14f, 50f));
            send.onClick.AddListener(OnSend);

            Mailbox?.RequestDirectory();
        }

        public override void Refresh()
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;

            if (!string.IsNullOrEmpty(mailbox.LastStatus))
            {
                Say(mailbox.LastStatus);
                mailbox.LastStatus = "";
            }

            RebuildInventoryIfChanged();
            RebuildSuggestionsIfChanged(mailbox);
            UpdateLimitHint();
        }

        // ---- inventory ----

        private void RebuildInventoryIfChanged()
        {
            var items = Player.GetInventory()?.GetAllItems()
                ?.Where(i => i != null && !string.IsNullOrEmpty(i.m_dropPrefab?.name))
                .OrderBy(i => i.m_shared?.m_name ?? i.m_dropPrefab.name)
                .ToList();
            if (items == null) return;

            var sig = string.Join("|", items.Select(i =>
                $"{i.m_dropPrefab.name}:{i.m_quality}:{i.m_stack}"));
            if (sig == _inventorySignature) return;
            _inventorySignature = sig;

            foreach (var row in _inventoryRows) if (row != null) Object.Destroy(row);
            _inventoryRows.Clear();

            foreach (var item in items)
            {
                string prefab = item.m_dropPrefab.name;
                int quality = item.m_quality;
                int stack = item.m_stack;
                int maxStack = item.m_shared?.m_maxStackSize ?? 1;
                string name = item.m_shared?.m_name ?? prefab;

                var btn = ValheimUi.CreateButton(_inventory, "", 0f, 42f, 14);
                _inventoryRows.Add(btn.gameObject);

                var icon = ValheimUi.CreateItemIcon((RectTransform)btn.transform, prefab, 36f);
                var lbl = ValheimUi.CreateLabel((RectTransform)btn.transform,
                    $"{ValheimUi.Localize(name)} x{stack}",
                    14, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(lbl.gameObject);

                btn.onClick.AddListener(() =>
                {
                    _itemName = prefab;
                    _itemQuality = quality;
                    _itemMaxStack = maxStack;
                    var sprite = ValheimUi.FindItemIcon(prefab);
                    _selectedIcon.sprite = sprite;
                    _selectedIcon.enabled = sprite != null;
                    _selectedLabel.text = $"{ValheimUi.Localize(name)}  (q{quality}, {stack} no inv.)";
                    _selectedLabel.color = ValheimUi.Beige;
                    UpdateLimitHint();
                });
            }
        }

        // ---- suggestions ----

        private void RebuildSuggestionsIfChanged(MailboxNpc mailbox)
        {
            var sig = RecipientSearch.Signature(mailbox, _target.text);
            if (sig == _dirSignature) return;
            _dirSignature = sig;

            foreach (var row in _suggestionRows) if (row != null) Object.Destroy(row);
            _suggestionRows.Clear();

            foreach (var recipient in RecipientSearch.Find(mailbox, _target.text, out _))
            {
                var row = Row(_suggestions, 34f);
                _suggestionRows.Add(row.gameObject);
                var pick = ValheimUi.CreateButton(row, recipient.Name, 0f, 32f, 13);
                Flexible(pick.gameObject);
                var n = recipient.Name;
                pick.onClick.AddListener(() => _target.text = n);
            }
        }

        // ---- limit hint ----

        private void UpdateLimitHint()
        {
            bool isVip = VipList.VipListApi.IsLocalPlayerVip();
            int maxStacks = isVip
                ? System.Math.Max(MailDatabase.MaxAttachmentsNonVip, Plugin.VipMaxAttachments.Value)
                : MailDatabase.MaxAttachmentsNonVip;

            if (string.IsNullOrEmpty(_itemName))
            {
                _limitHint.text = isVip
                    ? $"<color=#c8b090>VIP: até {maxStacks} stack(s) por carta</color>"
                    : "1 stack por carta";
                return;
            }

            int cap = isVip
                ? (int)System.Math.Min((long)_itemMaxStack * maxStacks, int.MaxValue)
                : _itemMaxStack;
            _limitHint.text = isVip
                ? $"<color=#c8b090>VIP: até {cap} unid. ({maxStacks} stack(s))</color>"
                : $"Limite: {cap} unid. (1 stack)";
        }

        // ---- send ----

        private void OnSend()
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;

            if (string.IsNullOrEmpty(_itemName))
            {
                Say("Selecione um item no inventário.");
                return;
            }

            string target = (_target.text ?? "").Trim();
            if (target.Length == 0)
            {
                Say("Informe o destinatário.");
                return;
            }

            if (!int.TryParse(_amount.text, out int amount) || amount <= 0)
            {
                Say("Quantidade inválida.");
                return;
            }

            bool isVip = VipList.VipListApi.IsLocalPlayerVip();
            int maxStacks = isVip
                ? System.Math.Max(MailDatabase.MaxAttachmentsNonVip, Plugin.VipMaxAttachments.Value)
                : MailDatabase.MaxAttachmentsNonVip;
            long maxAmount = (long)_itemMaxStack * maxStacks;
            if (maxAmount > int.MaxValue) maxAmount = int.MaxValue;

            if (amount > (int)maxAmount)
            {
                Say(isVip
                    ? $"Máximo {maxAmount} unid. ({maxStacks} stack(s)) por carta."
                    : $"Não-VIP pode enviar no máximo {_itemMaxStack} unid. (1 stack) por carta.");
                return;
            }

            var inventory = Player.GetInventory();
            if (inventory == null || ItemNames.Count(inventory, _itemName, _itemQuality) < amount)
            {
                Say($"Você não tem {amount}x {ValheimUi.Localize(MarketView.DisplayName(_itemName))} no inventário.");
                return;
            }

            // Remove o item do inventário do cliente antes de enviar (mesmo padrão do MarketView)
            ItemNames.Remove(inventory, _itemName, amount, _itemQuality);
            _inventorySignature = null; // força rebuild do inventário

            string subject = (_subject.text ?? "").Trim();
            if (string.IsNullOrEmpty(subject))
                subject = $"Pacote de {Player.m_localPlayer?.GetPlayerName() ?? "alguém"}";

            mailbox.RequestSendWithAttachment(target, subject, _body.text ?? "",
                _itemName, _itemQuality, amount);
            Say($"Enviando {amount}x {ValheimUi.Localize(MarketView.DisplayName(_itemName))} para {target}...");

            // reset do item selecionado
            _itemName = "";
            _itemQuality = 1;
            _itemMaxStack = 1;
            _selectedLabel.text = "(nenhum item selecionado)";
            _selectedLabel.color = ValheimUi.Muted;
            _selectedIcon.sprite = null;
            _selectedIcon.enabled = false;
            _amount.text = "1";
        }
    }

    /// <summary>
    /// The recipient list both mail tabs show: every name the server knows, narrowed by what
    /// is typed in the recipient field. Capped on screen, not on the server -- the whole
    /// address book arrives, and typing finds anyone in it.
    /// </summary>
    internal static class RecipientSearch
    {
        private const int MaxShown = 60;

        internal static List<MailRecipient> Find(MailboxNpc mailbox, string typed, out int total)
        {
            string filter = (typed ?? "").Trim();
            var all = mailbox.CachedRecipients
                .Where(r => r != null && !string.IsNullOrEmpty(r.Name) &&
                            (filter.Length == 0 || r.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(r => filter.Length > 0 && !r.Name.StartsWith(filter, System.StringComparison.OrdinalIgnoreCase))
                .ThenBy(r => r.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            total = all.Count;
            return all.Take(MaxShown).ToList();
        }

        internal static string Signature(MailboxNpc mailbox, string typed) =>
            (typed ?? "").Trim() + "|" + mailbox.HasDirectoryOnce + "|" + mailbox.CachedRecipients.Count;

        internal static string Hint(MailboxNpc mailbox, string typed, int shown, int total)
        {
            if (!mailbox.HasDirectoryOnce) return "Carregando destinatários...";
            if (mailbox.CachedRecipients.Count == 0) return "Nenhum jogador conhecido ainda.";
            if (total == 0) return $"Ninguém com \"{(typed ?? "").Trim()}\" no nome.";
            return total > shown ? $"{shown} de {total} jogadores; digite para filtrar." : "";
        }
    }
}
