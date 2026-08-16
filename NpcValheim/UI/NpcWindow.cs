using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The window itself: the wooden frame, the title, the tab strip and the status line,
    /// with one <see cref="NpcViewBase"/> per tab hosted inside it.
    ///
    /// Which tabs exist is decided by <see cref="NpcBase.CanLocalPlayerAdminister"/>, the same
    /// rule the server enforces on the RPCs -- a visitor gets the service tab and nothing else.
    /// </summary>
    internal sealed class NpcWindow
    {
        private const float Width = 940f;
        private const float Height = 640f;

        private readonly GameObject _canvas;
        private readonly RectTransform _panel;
        private readonly TextMeshProUGUI _title;
        private readonly TextMeshProUGUI _status;
        private readonly RectTransform _tabStrip;
        private readonly RectTransform _content;

        private readonly List<(Button button, NpcViewBase view, string label)> _tabs =
            new List<(Button, NpcViewBase, string)>();
        private int _active;

        public bool Alive => _canvas != null;

        public NpcWindow(NpcBase npc, Player player, System.Action onClose)
        {
            _canvas = ValheimUi.CreateCanvas("NpcValheim_Window", 5000);
            if (_canvas == null) return;

            _panel = ValheimUi.CreatePanel(_canvas.transform, Width, Height);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.anchoredPosition = Vector2.zero;

            // --- title bar (also the drag handle) ---
            var titleBar = ValheimUi.CreateRect("TitleBar", _panel);
            ValheimUi.Anchor(titleBar, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -58f), new Vector2(0f, 0f));
            titleBar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f); // raycast target only
            titleBar.gameObject.AddComponent<DragWindow>().Target = _panel;

            _title = ValheimUi.CreateLabel(titleBar, npc.GetHoverName(), 30, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)_title.transform, 60f, 10f);

            var close = ValheimUi.CreateButton(_panel, "X", 36f, 36f, 18);
            ValheimUi.Anchor((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-52f, -52f), new Vector2(-16f, -16f));
            close.onClick.AddListener(() => onClose?.Invoke());

            // --- tabs ---
            _tabStrip = ValheimUi.CreateRect("Tabs", _panel);
            ValheimUi.Anchor(_tabStrip, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -104f), new Vector2(-24f, -60f));

            var strip = _tabStrip.gameObject.AddComponent<HorizontalLayoutGroup>();
            strip.spacing = 8f;
            strip.childControlWidth = true;
            strip.childControlHeight = true;
            strip.childForceExpandWidth = false;
            strip.childAlignment = TextAnchor.MiddleLeft;

            // --- content + status ---
            _content = ValheimUi.CreateRect("Content", _panel);
            ValheimUi.Anchor(_content, Vector2.zero, Vector2.one,
                new Vector2(24f, 52f), new Vector2(-24f, -110f));

            _status = ValheimUi.CreateLabel(_panel, "", 15, ValheimUi.Yellow, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)_status.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(26f, 14f), new Vector2(-26f, 44f));

            BuildTabs(npc, player);
            SetActive(0);
        }

        private void BuildTabs(NpcBase npc, Player player)
        {
            // A marketplace does two distinct jobs -- trading with other players, and trading
            // with the merchant himself -- so it gets a tab for each rather than cramming
            // both into one screen.
            switch (npc)
            {
                case TeleporterNpc _:
                    AddTab("Teleportar", new TeleportView(), npc, player);
                    break;
                case MailboxNpc _:
                    AddTab("Correio", new MailView(), npc, player);
                    AddTab("Enviar", new MailComposeView(), npc, player);
                    AddTab("Casa", new HouseView(), npc, player);
                    break;
                case QuestGiverNpc _:
                    AddTab("Missões", new QuestView(), npc, player);
                    break;
                // A marketplace NPC decides for itself which side of the economy it runs. The
                // auction house and the merchant are different NPCs now, so nobody has to work
                // out who they are trading with from which tab happens to be open.
                case MarketplaceNpc market:
                    if (market.HasShop) AddTab("Loja", new ShopView(), npc, player);
                    if (market.HasAuction) AddTab("Leilão", new MarketView(), npc, player);
                    break;
                default:
                    AddTab("Loja", new ShopView(), npc, player);
                    break;
            }

            if (npc.CanLocalPlayerAdminister())
            {
                if (npc.ShowsAppearanceTab)
                    AddTab("Aparência", new AppearanceView(), npc, player);
                AddTab("Admin", new AdminView(), npc, player);
            }
        }

        private void AddTab(string label, NpcViewBase view, NpcBase npc, Player player)
        {
            var button = ValheimUi.CreateButton(_tabStrip, label, 150f, 40f, 16);
            var index = _tabs.Count;
            button.onClick.AddListener(() => SetActive(index));

            view.Build(_content, npc, player);
            view.SetVisible(false);
            _tabs.Add((button, view, label));
        }

        private void SetActive(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            _active = index;

            for (int i = 0; i < _tabs.Count; i++)
            {
                var (button, view, label) = _tabs[i];
                view.SetVisible(i == index);

                // Valheim marks the active tab by keeping it lit while the others dim, which
                // ColorBlock alone cannot express on a Button that is not "selected".
                var text = button.GetComponentInChildren<TextMeshProUGUI>();
                if (text != null) text.color = i == index ? ValheimUi.Yellow : ValheimUi.Orange;
                button.image.color = i == index ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }
        }

        /// <summary>Showcase-only: steps to the next visible tab.</summary>
        public void CycleTab() => SetActive((_active + 1) % Mathf.Max(1, _tabs.Count));

        public void Refresh(NpcBase npc)
        {
            if (_canvas == null) return;
            if (npc != null) _title.text = npc.GetHoverName();

            if (_active < _tabs.Count)
            {
                var view = _tabs[_active].view;
                view.Refresh();
                _status.text = view.Status ?? "";
            }
        }

        public void Destroy()
        {
            foreach (var (_, view, _) in _tabs) view.Destroy();
            _tabs.Clear();
            if (_canvas != null) Object.Destroy(_canvas);
        }
    }
}
