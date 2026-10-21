using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The barber's UI: hair/beard selection, color pickers, preview on the player,
    /// and a confirm button that consumes one FichaBarbeiro.
    /// </summary>
    internal sealed class BarberView : NpcViewBase
    {
        private const float PollSeconds = 3f;
        private const string Grey = "#9a9188";

        private int _hairIndex, _beardIndex;
        private TextMeshProUGUI _hairText, _beardText;
        private Image _hairSwatch, _skinSwatch;
        private Slider _hairR, _hairG, _hairB;
        private Slider _skinR, _skinG, _skinB;
        private Button _confirmButton;
        private Button _cancelButton;
        private TextMeshProUGUI _tokenCount;
        private TextMeshProUGUI _statusLabel;

        private List<string> _availableHair = new List<string>();
        private List<string> _availableBeard = new List<string>();
        private List<Vector3> _hairPalette = new List<Vector3>();
        private List<Vector3> _skinPalette = new List<Vector3>();
        private float _nextPoll;
        private int _seenCatalog;
        private int _seenResult;
        private Vector3 _startHairColor, _startSkinColor;

        private BarberNpc Barber => Npc as BarberNpc;

        protected override void OnBuild()
        {
            var pane = ValheimUi.CreateInlay(Root, "Barber");
            ValheimUi.Stretch(pane, 0f, 0f);

            // Without a layout group every row piled up at the pane's center; the column
            // is what puts them one under the other.
            var column = pane.gameObject.AddComponent<VerticalLayoutGroup>();
            column.padding = new RectOffset(20, 20, 10, 10);
            column.spacing = 5f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.childAlignment = TextAnchor.UpperCenter;

            var header = ValheimUi.CreateLabel(pane, "Barbeiro", 20, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.SetHeight(header.gameObject, 28f);

            _tokenCount = ValheimUi.CreateLabel(pane, "", 16, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.SetHeight(_tokenCount.gameObject, 24f);

            _hairText = CreateStepper(pane, "Cabelo", () => Step(ref _hairIndex, _availableHair, -1),
                () => Step(ref _hairIndex, _availableHair, 1));
            _beardText = CreateStepper(pane, "Barba", () => Step(ref _beardIndex, _availableBeard, -1),
                () => Step(ref _beardIndex, _availableBeard, 1));

            AddTitle(pane, "Cor do cabelo");
            _hairR = CreateColorSlider(pane, "Vermelho");
            _hairG = CreateColorSlider(pane, "Verde");
            _hairB = CreateColorSlider(pane, "Azul");
            _hairSwatch = CreateSwatch(pane);

            if (Plugin.BarberAllowSkin?.Value ?? false)
            {
                AddTitle(pane, "Cor da pele");
                _skinR = CreateColorSlider(pane, "Vermelho");
                _skinG = CreateColorSlider(pane, "Verde");
                _skinB = CreateColorSlider(pane, "Azul");
                _skinSwatch = CreateSwatch(pane);
            }

            _statusLabel = ValheimUi.CreateLabel(pane, "", 15, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.SetHeight(_statusLabel.gameObject, 22f);

            var buttonRow = Row(pane, 44f, 16f);
            buttonRow.GetComponent<HorizontalLayoutGroup>().childAlignment = TextAnchor.MiddleCenter;
            _cancelButton = ValheimUi.CreateButton(buttonRow, "Cancelar", 120f, 40f, 16);
            _cancelButton.onClick.AddListener(OnCancel);
            _confirmButton = ValheimUi.CreateButton(buttonRow, "Confirmar (1 ficha)", 180f, 40f, 16);
            _confirmButton.onClick.AddListener(OnConfirm);

            // Start the sliders on the character's current colors, so "no change" is the default
            BarberClient.SaveOriginal();
            ResetSliders();

            _seenCatalog = NpcValheim.Npc.Barber.CatalogRevision;
            _seenResult = NpcValheim.Npc.Barber.ResultRevision;
            Barber?.RequestStatus();
            MovePanelAside();
        }

        // The window covers the middle of the screen, which is where the character is, and
        // the preview is the point of this screen. Park it at the left edge (it can still be
        // dragged by its title bar).
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
            // Closing the window without confirming drops the preview
            BarberClient.RevertPreview();
            base.Destroy();
        }

        private void ResetSliders()
        {
            if (Player == null) return;
            _startHairColor = BarberClient.HairColorOf(Player);
            _startSkinColor = BarberClient.SkinColorOf(Player);
            _hairR.SetValueWithoutNotify(_startHairColor.x);
            _hairG.SetValueWithoutNotify(_startHairColor.y);
            _hairB.SetValueWithoutNotify(_startHairColor.z);
            if (_skinR != null)
            {
                _skinR.SetValueWithoutNotify(_startSkinColor.x);
                _skinG.SetValueWithoutNotify(_startSkinColor.y);
                _skinB.SetValueWithoutNotify(_startSkinColor.z);
            }
        }

        public override void Refresh()
        {
            if (Barber == null || Player == null) return;

            // The catalog arrives once; ask again only while it is still missing
            if (_availableHair.Count == 0 && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                Barber.RequestStatus();
            }

            if (NpcValheim.Npc.Barber.CatalogRevision != _seenCatalog)
            {
                _seenCatalog = NpcValheim.Npc.Barber.CatalogRevision;
                UpdateCatalog(NpcValheim.Npc.Barber.LastCatalog);
            }
            if (NpcValheim.Npc.Barber.ResultRevision != _seenResult)
            {
                _seenResult = NpcValheim.Npc.Barber.ResultRevision;
                Say(NpcValheim.Npc.Barber.LastResult);
                // After a confirmed change the new look is the baseline
                ResetSliders();
                _hairIndex = _beardIndex = 0;
            }
            if (!NpcValheim.Npc.Barber.Enabled) _statusLabel.text = "O Barbeiro está fechado (desligado na config).";
            else if (!string.IsNullOrEmpty(Status)) _statusLabel.text = Status;

            int tokenCount = Player.GetInventory() != null
                ? ItemNames.Count(Player.GetInventory(), NpcValheim.Npc.Barber.TokenPrefab, -1) : 0;
            _tokenCount.text = $"<color={Grey}>Fichas do Barbeiro:</color> {tokenCount}";
            _confirmButton.interactable = !BarberClient.Awaiting;

            UpdatePreview();
        }

        // "hair=a,b|beard=c,d|hc=r,g,b;r,g,b|sc=...|skin=0|token=X"
        private void UpdateCatalog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            foreach (var part in message.Split('|'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq), value = part.Substring(eq + 1);

                if (key == "hair")
                {
                    _availableHair = new List<string> { "" }; // empty = no change
                    foreach (var n in value.Split(',')) if (n.Length > 0) _availableHair.Add(n);
                    _hairIndex = 0;
                }
                else if (key == "beard")
                {
                    _availableBeard = new List<string> { "" };
                    foreach (var n in value.Split(',')) if (n.Length > 0) _availableBeard.Add(n);
                    _beardIndex = 0;
                }
                else if (key == "hc") _hairPalette = NpcValheim.Npc.Barber.ParsePalette(value, ';');
                else if (key == "sc") _skinPalette = NpcValheim.Npc.Barber.ParsePalette(value, ';');
            }
        }

        /// <summary>With a palette, snap the slider color to the nearest entry (the server
        /// only accepts palette colors); without one the color stays free.</summary>
        private static Vector3 Snap(Vector3 color, List<Vector3> palette)
        {
            if (palette == null || palette.Count == 0) return color;
            Vector3 best = palette[0];
            float bestD = (best - color).sqrMagnitude;
            foreach (var c in palette)
            {
                float d = (c - color).sqrMagnitude;
                if (d < bestD) { best = c; bestD = d; }
            }
            return best;
        }

        private static string Fmt(Vector3 c) =>
            string.Format(CultureInfo.InvariantCulture, "{0:F2},{1:F2},{2:F2}", c.x, c.y, c.z);

        private static bool Same(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 0.0001f;

        private string SelectedHair() =>
            _hairIndex > 0 && _hairIndex < _availableHair.Count ? _availableHair[_hairIndex] : "";

        private string SelectedBeard() =>
            _beardIndex > 0 && _beardIndex < _availableBeard.Count ? _availableBeard[_beardIndex] : "";

        // Untouched sliders mean "keep the current color": snapping them to the palette would
        // recolor a character whose color is not in it just by opening the window.
        private static Vector3 Picked(Vector3 raw, Vector3 start, List<Vector3> palette) =>
            Same(raw, start) ? start : Snap(raw, palette);

        private Vector3 HairColor() =>
            Picked(new Vector3(_hairR.value, _hairG.value, _hairB.value), _startHairColor, _hairPalette);

        private Vector3 SkinColor() => _skinR == null ? _startSkinColor
            : Picked(new Vector3(_skinR.value, _skinG.value, _skinB.value), _startSkinColor, _skinPalette);

        // Colors travel only when they differ from the character's current ones
        private string HairColorText() => Same(HairColor(), _startHairColor) ? "" : Fmt(HairColor());
        private string SkinColorText() => _skinR == null || Same(SkinColor(), _startSkinColor) ? "" : Fmt(SkinColor());

        private void UpdatePreview()
        {
            if (_hairText == null || BarberClient.Awaiting) return;
            _hairText.text = SelectedHair().Length > 0 ? SelectedHair() : "(atual)";
            _beardText.text = SelectedBeard().Length > 0 ? SelectedBeard() : "(atual)";
            var hair = HairColor();
            _hairSwatch.color = new Color(hair.x, hair.y, hair.z, 1f);
            if (_skinSwatch != null)
            {
                var skin = SkinColor();
                _skinSwatch.color = new Color(skin.x, skin.y, skin.z, 1f);
            }
            BarberClient.ApplyPreview(SelectedHair(), SelectedBeard(), HairColorText(), SkinColorText());
        }

        private void OnConfirm()
        {
            string error = BarberClient.BeginApply(Barber, SelectedHair(), SelectedBeard(), HairColorText(), SkinColorText());
            Say(error ?? "Solicitando mudança...");
        }

        private void OnCancel()
        {
            if (BarberClient.Awaiting) return; // a grant is in flight; cancelling now would still charge
            BarberClient.RevertPreview();
            ResetSliders();
            _hairIndex = _beardIndex = 0;
            Say("Prévia cancelada.");
        }

        // Index 0 is "no change"; the others walk the catalog and wrap around.
        private void Step(ref int index, List<string> options, int delta)
        {
            if (options.Count == 0) return;
            index = ((index + delta) % options.Count + options.Count) % options.Count;
            UpdatePreview();
        }

        private static void AddTitle(Transform parent, string text)
        {
            var title = ValheimUi.CreateLabel(parent, text, 16, ValheimUi.Orange, TextAlignmentOptions.Left);
            ValheimUi.SetHeight(title.gameObject, 22f);
        }

        private static Image CreateSwatch(Transform parent)
        {
            var rect = ValheimUi.CreateRect("Swatch", parent);
            ValheimUi.SetHeight(rect.gameObject, 16f);
            var image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }

        private TextMeshProUGUI CreateStepper(Transform parent, string label,
            UnityEngine.Events.UnityAction previous, UnityEngine.Events.UnityAction next)
        {
            var row = Row(parent, 36f, 8f);
            var labelObj = ValheimUi.CreateLabel(row, label, 15, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(labelObj.gameObject, 90f);

            var left = ValheimUi.CreateButton(row, "<", 40f, 32f, 16);
            left.onClick.AddListener(previous);
            var value = ValheimUi.CreateLabel(row, "(atual)", 15, ValheimUi.Yellow, TextAlignmentOptions.Center);
            Flexible(value.gameObject);
            var right = ValheimUi.CreateButton(row, ">", 40f, 32f, 16);
            right.onClick.AddListener(next);
            return value;
        }

        private Slider CreateColorSlider(Transform parent, string label)
        {
            var row = Row(parent, 24f, 8f);
            var labelObj = ValheimUi.CreateLabel(row, label, 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(labelObj.gameObject, 90f);

            // The slider gets its own object: as a component of the row, the row's layout
            // group would also have laid out its background, fill and handle.
            var sliderRect = ValheimUi.CreateRect("Slider", row);
            Flexible(sliderRect.gameObject);
            var slider = sliderRect.gameObject.AddComponent<Slider>();
            slider.minValue = 0f;
            slider.maxValue = 1f;

            var bg = ValheimUi.CreateRect("Background", sliderRect);
            ValheimUi.Anchor(bg, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, -4f), new Vector2(0f, 4f));
            bg.gameObject.AddComponent<Image>().color = new Color(0.3f, 0.3f, 0.3f);

            var fillArea = ValheimUi.CreateRect("Fill Area", sliderRect);
            ValheimUi.Anchor(fillArea, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, -4f), new Vector2(0f, 4f));
            var fill = ValheimUi.CreateRect("Fill", fillArea);
            // The slider only moves the anchors; a new rect keeps its 100x100 default size
            // unless its offsets are zeroed, which is the orange block seen in the first run.
            ValheimUi.Stretch(fill, 0f, 0f);
            fill.gameObject.AddComponent<Image>().color = ValheimUi.Orange;
            slider.fillRect = fill;

            var handleArea = ValheimUi.CreateRect("Handle Slide Area", sliderRect);
            ValheimUi.Stretch(handleArea, 8f, 0f);
            var handle = ValheimUi.CreateRect("Handle", handleArea);
            handle.sizeDelta = new Vector2(16f, 22f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = Color.white;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;

            slider.SetValueWithoutNotify(0.5f);
            slider.onValueChanged.AddListener(_ => UpdatePreview());
            return slider;
        }
    }
}
