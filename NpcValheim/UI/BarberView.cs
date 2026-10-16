using System;
using System.Collections.Generic;
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
        private float _nextPoll;
        private int _seenMessage;

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

            // Request initial status
            Barber?.RequestStatus();
            BarberClient.SaveOriginal();
        }

        public override void Refresh()
        {
            if (Barber == null || Player == null) return;

            // Poll for updates
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                Barber.RequestStatus();
            }

            // Check for messages
            if (NpcValheim.Npc.Barber.MessageRevision != _seenMessage)
            {
                _seenMessage = NpcValheim.Npc.Barber.MessageRevision;
                UpdateHairList(NpcValheim.Npc.Barber.LastMessage);
            }
            else
            {
                // Check if we got an error response from the server
                if (!string.IsNullOrEmpty(Status) && Status.StartsWith("error:"))
                {
                    _statusLabel.text = Status.Substring(6);
                }
                else if (!string.IsNullOrEmpty(Status))
                {
                    _statusLabel.text = Status;
                }
            }

            // Update token count
            string tokenPrefab = Plugin.BarberTokenPrefab?.Value ?? "FichaBarbeiro";
            int tokenCount = Player.GetInventory() != null ? ItemNames.Count(Player.GetInventory(), tokenPrefab, -1) : 0;
            _tokenCount.text = $"<color={Grey}>Fichas do Barbeiro:</color> {tokenCount}";

            // Update preview
            UpdatePreview();
        }

        private void UpdateHairList(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            // Parse "hair:Hair1,Hair2|beard:Beard1,Beard2"
            var parts = message.Split('|');
            foreach (var part in parts)
            {
                var kv = part.Split(':');
                if (kv.Length != 2) continue;

                if (kv[0] == "hair")
                {
                    _availableHair.Clear();
                    _availableHair.Add(""); // Empty = no change
                    _availableHair.AddRange(kv[1].Split(','));
                    UpdateDropdown(_hairDropdown, _availableHair);
                }
                else if (kv[0] == "beard")
                {
                    _availableBeard.Clear();
                    _availableBeard.Add(""); // Empty = no change
                    _availableBeard.AddRange(kv[1].Split(','));
                    UpdateDropdown(_beardDropdown, _availableBeard);
                }
            }
        }

        private void UpdateDropdown(TMP_Dropdown dropdown, List<string> options)
        {
            dropdown.ClearOptions();
            dropdown.AddOptions(options);
        }

        private void UpdatePreview()
        {
            string hair = _hairDropdown.value > 0 && _hairDropdown.value < _availableHair.Count
                ? _availableHair[_hairDropdown.value]
                : "";
            string beard = _beardDropdown.value > 0 && _beardDropdown.value < _availableBeard.Count
                ? _availableBeard[_beardDropdown.value]
                : "";

            Vector3 hairColor = new Vector3(_hairR.value, _hairG.value, _hairB.value);
            Vector3 skinColor = Vector3.zero;
            if (_skinR != null)
                skinColor = new Vector3(_skinR.value, _skinG.value, _skinB.value);

            BarberClient.ApplyPreview(hair, beard, hairColor, skinColor);
        }

        private void OnConfirm()
        {
            string hair = _hairDropdown.value > 0 && _hairDropdown.value < _availableHair.Count
                ? _availableHair[_hairDropdown.value]
                : "";
            string beard = _beardDropdown.value > 0 && _beardDropdown.value < _availableBeard.Count
                ? _availableBeard[_beardDropdown.value]
                : "";

            string hairColor = $"{_hairR.value:F2},{_hairG.value:F2},{_hairB.value:F2}";
            string skinColor = "";
            if (_skinR != null)
                skinColor = $"{_skinR.value:F2},{_skinG.value:F2},{_skinB.value:F2}";

            Barber?.RequestApply(hair, beard, hairColor, skinColor);
            Say("Solicitando mudança...");
        }

        private void OnCancel()
        {
            BarberClient.RevertPreview();
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
            slider.value = 0.5f;
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
