using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Alfaiate's screen: one tab per armor slot, the unlocked pieces on the left (the one
    /// you wear) and the looks that fit it on the right (what the others see). Picks are only a
    /// preview on your own character until Aplicar, which sends them all at once and pays Coins
    /// per piece changed. Deadheim keeps the rules; this view only asks it what fits.
    /// </summary>
    internal sealed class TailorView : NpcViewBase
    {
        private const float PollSeconds = 3f;
        private const string Grey = "#9a9188";

        private static readonly string[] Slots = { "Helmet", "Chest", "Legs", "Shoulder" };
        private static readonly string[] SlotNames = { "Capacete", "Peito", "Calça", "Capa" };

        private readonly Dictionary<string, string> _pending = new Dictionary<string, string>();
        private readonly List<Button> _tabs = new List<Button>();
        private int _slot = 1;
        private string _real;

        private TextMeshProUGUI _info, _summary, _statusLabel, _leftTitle, _rightTitle;
        private RectTransform _leftList, _rightList;
        private Button _applyButton, _revertButton, _clearButton, _undoButton;

        private int _seenState = -1, _seenStatus = -1, _seenResult, _seenApplied;
        private float _nextPoll;
        private bool _dirty = true;

        private TailorNpc Tailor => Npc as TailorNpc;

        protected override void OnBuild()
        {
            var pane = ValheimUi.CreateInlay(Root, "Tailor");
            ValheimUi.Stretch(pane, 0f, 0f);

            var header = ValheimUi.CreateLabel(pane, "Alfaiate", 20, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -34f), new Vector2(-10f, -6f));

            _info = ValheimUi.CreateLabel(pane, "", 14, ValheimUi.Beige, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_info.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -78f), new Vector2(-10f, -36f));

            var tabs = ValheimUi.CreateRect("Tabs", pane);
            ValheimUi.Anchor(tabs, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(10f, -118f), new Vector2(-10f, -82f));
            var tabLayout = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            tabLayout.spacing = 6f;
            tabLayout.childControlWidth = true;
            tabLayout.childControlHeight = true;
            tabLayout.childForceExpandWidth = true;
            for (int i = 0; i < Slots.Length; i++)
            {
                int index = i;
                var tab = ValheimUi.CreateButton(tabs, SlotNames[i], 0f, 34f, 15);
                tab.onClick.AddListener(() => SelectSlot(index));
                _tabs.Add(tab);
            }

            _leftTitle = ColumnTitle(pane, 0f, 0.5f);
            _rightTitle = ColumnTitle(pane, 0.5f, 1f);
            _leftList = Column(pane, 0f, 0.5f);
            _rightList = Column(pane, 0.5f, 1f);

            _summary = ValheimUi.CreateLabel(pane, "", 15, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_summary.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(10f, 78f), new Vector2(-10f, 100f));
            _statusLabel = ValheimUi.CreateLabel(pane, "", 14, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_statusLabel.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(10f, 56f), new Vector2(-10f, 78f));

            var buttons = ValheimUi.CreateRect("Buttons", pane);
            ValheimUi.Anchor(buttons, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(10f, 8f), new Vector2(-10f, 50f));
            var buttonLayout = buttons.gameObject.AddComponent<HorizontalLayoutGroup>();
            buttonLayout.spacing = 8f;
            buttonLayout.childControlWidth = true;
            buttonLayout.childControlHeight = true;
            buttonLayout.childForceExpandWidth = true;
            _undoButton = ValheimUi.CreateButton(buttons, "Desfazer prévia", 0f, 40f, 14);
            _undoButton.onClick.AddListener(OnUndo);
            _revertButton = ValheimUi.CreateButton(buttons, "Voltar ao real", 0f, 40f, 14);
            _revertButton.onClick.AddListener(OnRevert);
            _clearButton = ValheimUi.CreateButton(buttons, "Limpar tudo", 0f, 40f, 14);
            _clearButton.onClick.AddListener(OnClearAll);
            _applyButton = ValheimUi.CreateButton(buttons, "Aplicar", 0f, 40f, 15);
            _applyButton.onClick.AddListener(OnApply);

            _seenResult = TailorClient.ResultRevision;
            _seenApplied = TailorClient.AppliedRevision;
            Tailor?.RequestStatus();
            MovePanelAside();
        }

        private static TextMeshProUGUI ColumnTitle(RectTransform pane, float from, float to)
        {
            var title = ValheimUi.CreateLabel(pane, "", 15, ValheimUi.Orange, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(from, 1f), new Vector2(to, 1f),
                new Vector2(14f, -146f), new Vector2(-6f, -122f));
            return title;
        }

        private static RectTransform Column(RectTransform pane, float from, float to)
        {
            var area = ValheimUi.CreateRect("Column", pane);
            ValheimUi.Anchor(area, new Vector2(from, 0f), new Vector2(to, 1f), new Vector2(8f, 104f), new Vector2(-4f, -148f));
            return ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        // The preview is on the character in the middle of the screen; park the window aside
        // (same as the Barbeiro).
        private void MovePanelAside()
        {
            var panel = Root != null && Root.parent != null ? Root.parent.parent as RectTransform : null;
            var canvas = panel != null ? panel.GetComponentInParent<Canvas>() : null;
            if (panel == null || canvas == null) return;
            float free = ((RectTransform)canvas.transform).rect.width - panel.rect.width;
            if (free > 0f) panel.anchoredPosition = new Vector2(-free / 2f + 16f, panel.anchoredPosition.y);
        }

        public override void Destroy()
        {
            // Closing without applying drops the preview
            DeadheimTransmogApi.ClearPreview();
            base.Destroy();
        }

        public override void Refresh()
        {
            if (Tailor == null || Player == null) return;

            if (TailorClient.StatusRevision == 0 && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                Tailor.RequestStatus();
            }

            int state = DeadheimTransmogApi.StateRevision();
            if (state != _seenState || TailorClient.StatusRevision != _seenStatus)
            {
                _seenState = state;
                _seenStatus = TailorClient.StatusRevision;
                _dirty = true;
            }
            if (TailorClient.AppliedRevision != _seenApplied)
            {
                _seenApplied = TailorClient.AppliedRevision;
                _pending.Clear();
                DeadheimTransmogApi.ClearPreview();
                Tailor.RequestStatus();
                _dirty = true;
            }
            if (TailorClient.ResultRevision != _seenResult)
            {
                _seenResult = TailorClient.ResultRevision;
                Say(TailorClient.LastResult);
            }

            if (_dirty)
            {
                _dirty = false;
                Rebuild();
            }

            _statusLabel.text = !NpcValheim.Npc.Tailor.Enabled ? "O Alfaiate está fechado (desligado na config)."
                : !DeadheimTransmogApi.IsAvailable ? DeadheimTransmogApi.NotInstalled
                : Status ?? "";
            bool busy = TailorClient.Awaiting;
            _applyButton.interactable = !busy && _pending.Count > 0 && CanBuy(out _);
            _undoButton.interactable = !busy && _pending.Count > 0;
            _revertButton.interactable = !busy && _real != null && DeadheimTransmogApi.LocalChosen().ContainsKey(_real);
            _clearButton.interactable = !busy && DeadheimTransmogApi.LocalChosen().Count > 0;
        }

        /// <summary>Whether a change may be bought at all; why not, otherwise. The server checks
        /// all of it again -- this only keeps the button honest.</summary>
        private bool CanBuy(out string why)
        {
            why = null;
            if (!NpcValheim.Npc.Tailor.Enabled || !DeadheimTransmogApi.IsAvailable) { why = "Fechado."; return false; }
            if (!DeadheimTransmogApi.IsEnabled()) { why = "Transmog desativado no servidor."; return false; }
            if (DeadheimTransmogApi.RequiresVip() && !DeadheimTransmogApi.IsLocalVip()) { why = "Exclusivo Aesir (VIP)."; return false; }
            if (TailorClient.MinRating > 0 && TailorClient.Rating >= 0 && TailorClient.Rating < TailorClient.MinRating)
            {
                why = $"Requer rating de arena {TailorClient.MinRating}.";
                return false;
            }
            return true;
        }

        private int Price() => _pending.Count * TailorClient.PricePerPiece;

        private void Rebuild()
        {
            var unlocked = DeadheimTransmogApi.LocalUnlocked();
            var chosen = DeadheimTransmogApi.LocalChosen();
            string slot = Slots[_slot];
            var pieces = unlocked.Where(p => DeadheimTransmogApi.SlotOf(p) == slot)
                .OrderBy(ItemNames.Display).ToList();
            string worn = Worn(slot);
            if (_real == null || !pieces.Contains(_real)) _real = worn != null && pieces.Contains(worn) ? worn : pieces.FirstOrDefault();

            for (int i = 0; i < _tabs.Count; i++)
            {
                var label = _tabs[i].GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = i == _slot ? ValheimUi.Yellow : ValheimUi.Orange;
            }

            Clear(_leftList);
            Clear(_rightList);
            _leftTitle.text = "Peça que você usa";
            _rightTitle.text = _real != null ? "Aparência (o que os outros veem)" : "";

            if (pieces.Count == 0)
                Dim(_leftList, "Nenhuma peça desbloqueada neste slot. Vista uma peça para desbloquear.");
            foreach (string piece in pieces)
            {
                string shown = ItemNames.Display(piece);
                string look = _pending.TryGetValue(piece, out string p) ? p : chosen.TryGetValue(piece, out string c) ? c : null;
                if (piece == worn) shown += $" <color={Grey}>(vestindo)</color>";
                if (look != null) shown += $"\n<size=12><color={Grey}>parece:</color> {ItemNames.Display(look)}" +
                                           (_pending.ContainsKey(piece) ? " <color=#ffd84a>(prévia)</color>" : "") + "</size>";
                var button = ItemButton(_leftList, piece, shown, piece == _real);
                button.onClick.AddListener(() => { _real = piece; _dirty = true; });
            }

            if (_real != null)
                foreach (string app in pieces)
                {
                    if (app == _real) continue;
                    string why = DeadheimTransmogApi.WhyIncompatible(_real, app);
                    bool current = _pending.TryGetValue(_real, out string p) ? p == app : chosen.TryGetValue(_real, out string c) && c == app;
                    string shown = ItemNames.Display(app) + (why != null ? $"\n<size=12><color={Grey}>{why}</color></size>" : "");
                    var button = ItemButton(_rightList, app, shown, current);
                    button.interactable = why == null;
                    button.onClick.AddListener(() => Pick(app));
                }

            int price = Price();
            bool canBuy = CanBuy(out string blocked);
            string rating = TailorClient.Rating >= 0 ? $"seu rating {TailorClient.Rating}" : "rating...";
            string need = TailorClient.MinRating > 0
                ? $"Requer rating de arena {TailorClient.MinRating}{(TailorClient.Bracket > 1 ? $" ({TailorClient.Bracket}v{TailorClient.Bracket}+)" : "")} ({rating})"
                : "Sem exigência de rating";
            _info.text = $"{need}  ·  {TailorClient.PricePerPiece} moedas por peça  ·  voltar ao real é grátis\n" +
                         $"<color={Grey}>Ativas: {chosen.Count}/{DeadheimTransmogApi.MaxChoices()}" +
                         (DeadheimTransmogApi.IsSuppressedHere() ? "  ·  aqui (arena/raid) ninguém vê o transmog" : "") + "</color>";
            _summary.text = _pending.Count == 0 ? "Escolha uma peça e uma aparência para ver a prévia."
                : $"{_pending.Count} peça(s) na prévia: {price} moedas (você tem {MarketplaceNpc.CoinsOf(Player)})" +
                  (canBuy ? "" : $"  ·  {blocked}");
            _applyButton.GetComponentInChildren<TextMeshProUGUI>().text = price > 0 ? $"Aplicar ({price})" : "Aplicar";
        }

        private void Pick(string app)
        {
            if (_real == null || TailorClient.Awaiting) return;
            var chosen = DeadheimTransmogApi.LocalChosen();
            // Picking what is already active (or clicking a preview again) drops the preview
            if ((chosen.TryGetValue(_real, out string cur) && cur == app) ||
                (_pending.TryGetValue(_real, out string pend) && pend == app))
                _pending.Remove(_real);
            else
                _pending[_real] = app;
            DeadheimTransmogApi.SetPreview(DeadheimTransmogApi.EncodePairs(_pending));
            _dirty = true;
        }

        private void OnApply()
        {
            if (!CanBuy(out string why)) { Say(why); return; }
            string error = TailorClient.BeginBuy(Tailor, DeadheimTransmogApi.EncodePairs(_pending), Price());
            Say(error ?? "Costurando...");
        }

        private void OnUndo()
        {
            if (TailorClient.Awaiting) return;
            _pending.Clear();
            DeadheimTransmogApi.ClearPreview();
            _dirty = true;
            Say("Prévia desfeita.");
        }

        private void OnRevert()
        {
            if (_real == null || TailorClient.Awaiting) return;
            _pending.Remove(_real);
            DeadheimTransmogApi.SetPreview(DeadheimTransmogApi.EncodePairs(_pending));
            if (!Tailor.RequestOff(_real)) Say("Não foi possível falar com o servidor.");
        }

        private void OnClearAll()
        {
            if (TailorClient.Awaiting) return;
            _pending.Clear();
            DeadheimTransmogApi.ClearPreview();
            if (!Tailor.RequestOff("*")) Say("Não foi possível falar com o servidor.");
        }

        private void SelectSlot(int index)
        {
            if (index == _slot) return;
            _slot = index;
            _real = null;
            _dirty = true;
        }

        /// <summary>The prefab the local player wears in that slot, or null.</summary>
        private string Worn(string slot)
        {
            var inv = Player != null ? Player.GetInventory() : null;
            if (inv == null) return null;
            foreach (var item in inv.GetEquippedItems())
                if (item?.m_dropPrefab != null && item.m_shared.m_itemType.ToString() == slot)
                    return item.m_dropPrefab.name;
            return null;
        }

        private static Button ItemButton(Transform parent, string prefab, string text, bool selected)
        {
            var button = ValheimUi.CreateButton(parent, text, 0f, 48f, 14);
            Iconify(button, prefab, 36f);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.alignment = TextAlignmentOptions.Left;
                label.textWrappingMode = TextWrappingModes.Normal;
                label.color = selected ? ValheimUi.Yellow : ValheimUi.Orange;
            }
            return button;
        }

        private static void Clear(RectTransform list)
        {
            for (int i = list.childCount - 1; i >= 0; i--) Object.Destroy(list.GetChild(i).gameObject);
        }
    }
}
