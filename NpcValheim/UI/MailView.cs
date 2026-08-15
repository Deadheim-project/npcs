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

            var signature = string.Join("|", mailbox.CachedMail.Select(m => m.Id));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(mailbox);
            }

            bool any = mailbox.CachedMail.Count > 0;
            _empty.gameObject.SetActive(!any);
            _empty.text = mailbox.HasSyncedOnce ? "Sua caixa está vazia." : "Carregando...";
            _claimAll.gameObject.SetActive(any);
            _header.text = any ? $"Correio — {mailbox.CachedMail.Count} item(ns)" : "Correio";
        }

        private void Rebuild(MailboxNpc mailbox)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            foreach (var entry in mailbox.CachedMail)
            {
                var row = Row(_list, 48f);
                _rows.Add(row.gameObject);

                string iconPrefab = entry.IsCoins ? MarketplaceNpc.CoinPrefabName : entry.ItemName;
                ValheimUi.CreateItemIcon(row, iconPrefab, 40f);

                string what = entry.IsCoins
                    ? $"{entry.Coins} moedas"
                    : $"{ValheimUi.Localize(MarketView.DisplayName(entry.ItemName))} x{entry.Amount}";

                var label = ValheimUi.CreateLabel(row,
                    $"{what}\n<size=12><color=#9a9188>{entry.Subject}</color></size>",
                    16, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var id = entry.Id;
                var claim = ValheimUi.CreateButton(row, "Receber", 120f, 38f, 15);
                claim.onClick.AddListener(() =>
                {
                    mailbox.RequestClaim(id);
                    Say("Pedido enviado; o item cai no chão aqui.");
                });
            }
        }
    }
}
