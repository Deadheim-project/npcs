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

        private TMP_Dropdown _hairDropdown;
        private TMP_Dropdown _beardDropdown;
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
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            // Header
            var header = ValheimUi.CreateLabel(pane, "Barbeiro", 20, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            // Token count
            _tokenCount = ValheimUi.CreateLabel(pane, "", 16, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_tokenCount.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -56f), new Vector2(-16f, -16f));

            // Hair section
            var hairSection = CreateSection(pane, "Cabelo", 70f);
            _hairDropdown = CreateDropdown(hairSection, "Estilo", new List<string> { "" });

            // Beard section
            var beardSection = CreateSection(pane, "Barba", 70f);
            _beardDropdown = CreateDropdown(beardSection, "Estilo", new List<string> { "" });

            // Hair color section
            var hairColorSection = CreateSection(pane, "Cor do Cabelo", 90f);
            _hairR = CreateColorSlider(hairColorSection, "Vermelho", 0f, 1f);
            _hairG = CreateColorSlider(hairColorSection, "Verde", 0f, 1f);
            _hairB = CreateColorSlider(hairColorSection, "Azul", 0f, 1f);

            // Skin color section (only if enabled)
            bool allowSkin = Plugin.BarberAllowSkin?.Value ?? false;
            if (allowSkin)
            {
                var skinColorSection = CreateSection(pane, "Cor da Pele", 90f);
                _skinR = CreateColorSlider(skinColorSection, "Vermelho", 0f, 1f);
                _skinG = CreateColorSlider(skinColorSection, "Verde", 0f, 1f);
                _skinB = CreateColorSlider(skinColorSection, "Azul", 0f, 1f);
            }

            // Buttons
            var buttonRow = Row(pane, 50f, 16f);
            ValheimUi.Anchor(buttonRow, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(24f, 24f), new Vector2(-24f, 100f));

            _cancelButton = ValheimUi.CreateButton(buttonRow.transform, "Cancelar", 120f, 40f, 16);
            _cancelButton.onClick.AddListener(OnCancel);

            _confirmButton = ValheimUi.CreateButton(buttonRow.transform, "Confirmar (1 ficha)", 140f, 40f, 16);
            _confirmButton.onClick.AddListener(OnConfirm);

            // Status label
            _statusLabel = ValheimUi.CreateLabel(pane, "", 15, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_statusLabel.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(16f, 10f), new Vector2(-16f, 80f));

            // Start the sliders on the character's current colors, so "no change" is the default
            BarberClient.SaveOriginal();
            ResetSliders();

            _seenCatalog = NpcValheim.Npc.Barber.CatalogRevision;
            _seenResult = NpcValheim.Npc.Barber.ResultRevision;
            Barber?.RequestStatus();
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
            _startHairColor = Player.m_hairColor;
            _startSkinColor = Player.m_skinColor;
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
                _hairDropdown.SetValueWithoutNotify(0);
                _beardDropdown.SetValueWithoutNotify(0);
            }
            if (!string.IsNullOrEmpty(Status)) _statusLabel.text = Status;

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
                    UpdateDropdown(_hairDropdown, _availableHair);
                }
                else if (key == "beard")
                {
                    _availableBeard = new List<string> { "" };
                    foreach (var n in value.Split(',')) if (n.Length > 0) _availableBeard.Add(n);
                    UpdateDropdown(_beardDropdown, _availableBeard);
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

        private void UpdateDropdown(TMP_Dropdown dropdown, List<string> options)
        {
            dropdown.ClearOptions();
            dropdown.AddOptions(options);
        }

        private string SelectedHair() =>
            _hairDropdown.value > 0 && _hairDropdown.value < _availableHair.Count ? _availableHair[_hairDropdown.value] : "";

        private string SelectedBeard() =>
            _beardDropdown.value > 0 && _beardDropdown.value < _availableBeard.Count ? _availableBeard[_beardDropdown.value] : "";

        private Vector3 HairColor() => Snap(new Vector3(_hairR.value, _hairG.value, _hairB.value), _hairPalette);

        private Vector3 SkinColor() => _skinR == null ? _startSkinColor
            : Snap(new Vector3(_skinR.value, _skinG.value, _skinB.value), _skinPalette);

        // Colors travel only when they differ from the character's current ones
        private string HairColorText() => Same(HairColor(), _startHairColor) ? "" : Fmt(HairColor());
        private string SkinColorText() => _skinR == null || Same(SkinColor(), _startSkinColor) ? "" : Fmt(SkinColor());

        private void UpdatePreview()
        {
            if (_hairDropdown == null || BarberClient.Awaiting) return;
            BarberClient.ApplyPreview(SelectedHair(), SelectedBeard(), HairColorText(), SkinColorText());
        }

        private void OnConfirm()
        {
            string error = BarberClient.BeginApply(Barber, SelectedHair(), SelectedBeard(), HairColorText(), SkinColorText());
            Say(error ?? "Solicitando mudança...");
        }

        private void OnCancel()
        {
            BarberClient.RevertPreview();
            ResetSliders();
            _hairDropdown.SetValueWithoutNotify(0);
            _beardDropdown.SetValueWithoutNotify(0);
            Say("Prévia cancelada.");
        }

        private RectTransform CreateSection(Transform parent, string title, float height)
        {
            var section = ValheimUi.CreateRect("Section", parent);
            ValheimUi.SetHeight(section.gameObject, height);
            return section;
        }

        private TMP_Dropdown CreateDropdown(Transform parent, string label, List<string> options)
        {
            var row = Row(parent, 40f, 8f);
            ValheimUi.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -32f), new Vector2(-12f, -8f));

            var labelObj = ValheimUi.CreateLabel(row, label, 15, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(labelObj.gameObject, 100f);

            var dropdown = row.gameObject.AddComponent<TMP_Dropdown>();
            dropdown.ClearOptions();
            dropdown.AddOptions(options);
            var dropdownRect = dropdown.GetComponent<RectTransform>();
            ValheimUi.Stretch(dropdownRect, 0f, 0f);

            dropdown.onValueChanged.AddListener(_ => UpdatePreview());

            return dropdown;
        }

        private Slider CreateColorSlider(Transform parent, string label, float min, float max)
        {
            var row = Row(parent, 30f, 8f);
            ValheimUi.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(12f, -28f), new Vector2(-12f, -8f));

            var labelObj = ValheimUi.CreateLabel(row, label, 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(labelObj.gameObject, 80f);

            var slider = row.gameObject.AddComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.SetValueWithoutNotify(0.5f);
            var sliderRect = slider.GetComponent<RectTransform>();
            ValheimUi.Stretch(sliderRect, 0f, 0f);

            // Create background
            var bg = new GameObject("Background");
            bg.transform.SetParent(slider.transform, false);
            var bgImage = bg.AddComponent<Image>();
            bgImage.color = new Color(0.3f, 0.3f, 0.3f);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.sizeDelta = Vector2.zero;

            // Create fill
            var fill = new GameObject("Fill");
            fill.transform.SetParent(slider.transform, false);
            var fillImage = fill.AddComponent<Image>();
            fillImage.color = ValheimUi.Orange;
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = new Vector2(0.5f, 1f);
            fillRect.sizeDelta = Vector2.zero;
            slider.fillRect = fillRect;

            // Create handle
            var handle = new GameObject("Handle");
            handle.transform.SetParent(slider.transform, false);
            var handleImage = handle.AddComponent<Image>();
            handleImage.color = Color.white;
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(20f, 20f);
            slider.handleRect = handleRect;

            slider.onValueChanged.AddListener(_ => UpdatePreview());

            return slider;
        }
    }
}
