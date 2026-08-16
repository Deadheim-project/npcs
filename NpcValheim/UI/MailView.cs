using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

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
                Say("Recebendo tudo; os itens caem no chão aqui.");
            });

            Mailbox?.RequestMail();
        }

        public override void Refresh()
        {
            var mailbox = Mailbox;
            if (mailbox == null) return;

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
            MailHud.MirrorMailbox(mailbox);
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
                if (!string.IsNullOrEmpty(entry.HouseName))
                    from = string.IsNullOrEmpty(from) ? $"casa {entry.HouseName}" : $"{from} · casa {entry.HouseName}";

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
                        Say("Pedido enviado; o item cai no chão aqui.");
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
                MailHud.MirrorMailbox(mailbox);
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
            string via = string.IsNullOrEmpty(entry.HouseName) ? "" : $"  ·  casa {entry.HouseName}";
            var title = ValheimUi.CreateLabel(panel, entry.Subject, 22, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -52f), new Vector2(-16f, -10f));

            var by = ValheimUi.CreateLabel(panel, "de " + from + via, 14, ValheimUi.Muted,
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

    /// <summary>Write a letter to a player or to a house. The server resolves the name
    /// against the directory (anyone who has logged in) so offline recipients still work.</summary>
    internal sealed class MailComposeView : NpcViewBase
    {
        private MailboxNpc Mailbox => Npc as MailboxNpc;

        private bool _toHouse;
        private Button _playerTab;
        private Button _houseTab;
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

            var typeRow = ValheimUi.CreateRect("Type", frame);
            ValheimUi.Anchor(typeRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -84f), new Vector2(-12f, -42f));
            var typeLayout = typeRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            typeLayout.spacing = 8f;
            typeLayout.childControlWidth = true;
            typeLayout.childControlHeight = true;
            typeLayout.childForceExpandWidth = true;

            _playerTab = ValheimUi.CreateButton(typeRow, "Jogador", 0f, 36f, 15);
            _playerTab.onClick.AddListener(() => SetMode(false));
            _houseTab = ValheimUi.CreateButton(typeRow, "Casa", 0f, 36f, 15);
            _houseTab.onClick.AddListener(() => SetMode(true));

            var targetLabel = ValheimUi.CreateLabel(frame, "Destinatário", 13, ValheimUi.Muted, TextAlignmentOptions.Left);
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
                Mailbox.RequestSend(_toHouse, _target.text, _subject.text, _body.text);
                MailHud.RefreshSoon();
                Say(_toHouse ? "Enviando para a casa..." : "Enviando para o jogador...");
            });

            SetMode(false);
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

            var signature = (_toHouse ? "H|" : "P|") + string.Join("|",
                mailbox.CachedRecipients.Select(r => $"{r.IsHouse}:{r.Name}:{r.MemberCount}"));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(mailbox);
            }
        }

        private void SetMode(bool house)
        {
            _toHouse = house;
            _playerTab.image.color = house ? new Color(0.72f, 0.72f, 0.72f, 1f) : Color.white;
            _houseTab.image.color = house ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            _target.text = "";
            _signature = "";
        }

        private void Rebuild(MailboxNpc mailbox)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var matches = mailbox.CachedRecipients.Where(r => r.IsHouse == _toHouse).ToList();
            _hint.gameObject.SetActive(matches.Count == 0);
            _hint.text = mailbox.HasDirectoryOnce
                ? (_toHouse ? "Nenhuma casa ainda. Crie uma na aba Casa." : "Nenhum jogador conhecido ainda.")
                : "Carregando destinatários...";

            foreach (var recipient in matches)
            {
                var row = Row(_suggestions, 36f);
                _rows.Add(row.gameObject);
                string caption = recipient.IsHouse
                    ? $"{recipient.Name}  ({recipient.MemberCount})"
                    : recipient.Name;
                var pick = ValheimUi.CreateButton(row, caption, 0f, 34f, 14);
                Flexible(pick.gameObject);
                var name = recipient.Name;
                pick.onClick.AddListener(() => _target.text = name);
            }
        }
    }

    /// <summary>Create a house, invite members, and see which houses you belong to.</summary>
    internal sealed class HouseView : NpcViewBase
    {
        private MailboxNpc Mailbox => Npc as MailboxNpc;

        private TMP_InputField _houseName;
        private TMP_InputField _invite;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "House");
            ValheimUi.Stretch(frame, 0f, 0f);

            var title = ValheimUi.CreateLabel(frame, "Sua casa", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -6f));

            var createRow = ValheimUi.CreateRect("Create", frame);
            ValheimUi.Anchor(createRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -86f), new Vector2(-12f, -44f));
            var createLayout = createRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            createLayout.spacing = 8f;
            createLayout.childControlWidth = true;
            createLayout.childControlHeight = true;
            createLayout.childForceExpandWidth = false;

            _houseName = ValheimUi.CreateInputField(createRow, "", 0f, 36f);
            Flexible(_houseName.gameObject);
            var create = ValheimUi.CreateButton(createRow, "Criar casa", 150f, 36f, 15);
            create.onClick.AddListener(() =>
            {
                Mailbox.RequestCreateHouse(_houseName.text);
                Say("Criando casa...");
            });

            var inviteRow = ValheimUi.CreateRect("Invite", frame);
            ValheimUi.Anchor(inviteRow, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -130f), new Vector2(-12f, -88f));
            var inviteLayout = inviteRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            inviteLayout.spacing = 8f;
            inviteLayout.childControlWidth = true;
            inviteLayout.childControlHeight = true;
            inviteLayout.childForceExpandWidth = false;

            _invite = ValheimUi.CreateInputField(inviteRow, "", 0f, 36f);
            Flexible(_invite.gameObject);
            var invite = ValheimUi.CreateButton(inviteRow, "Convidar", 150f, 36f, 15);
            invite.onClick.AddListener(() =>
            {
                var house = Mailbox.CachedRecipients.FirstOrDefault(r => r.IsHouse && r.IsMine);
                if (house == null)
                {
                    Say("Crie ou entre numa casa primeiro.");
                    return;
                }
                Mailbox.RequestInvite(house.Name, _invite.text);
                Say("Convite enviado.");
            });

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 12f), new Vector2(-6f, -138f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "", 15, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -10f), new Vector2(-10f, 30f));

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

            var signature = string.Join("|", mailbox.CachedRecipients
                .Where(r => r.IsHouse)
                .Select(r => $"{r.Name}:{r.MemberCount}:{r.IsMine}"));
            if (signature == _signature) return;
            _signature = signature;
            Rebuild(mailbox);
        }

        private void Rebuild(MailboxNpc mailbox)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var houses = mailbox.CachedRecipients.Where(r => r.IsHouse).ToList();
            _empty.gameObject.SetActive(houses.Count == 0);
            _empty.text = mailbox.HasDirectoryOnce
                ? "Nenhuma casa no servidor. Crie a primeira acima."
                : "Carregando...";

            foreach (var house in houses)
            {
                var row = Row(_list, 40f);
                _rows.Add(row.gameObject);
                string mark = house.IsMine ? "você é membro" : "outra casa";
                var label = ValheimUi.CreateLabel(row,
                    $"{house.Name}  ·  {house.MemberCount} membro(s)\n<size=12><color=#9a9188>{mark}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);
            }
        }
    }
}
