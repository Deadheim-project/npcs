using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// Appearance editor. Categories down the left, the chosen category's options on the
    /// right -- the same shape as the quest log, so the window only ever teaches one layout.
    /// Armour and hand items show their real icons, which is the whole reason to pick from a
    /// list rather than type a prefab name.
    /// </summary>
    internal sealed class AppearanceView : NpcViewBase
    {
        private enum Category { Armor, Hands, Hair, Beard, Skin, HairColor, Model, Scale }

        private Category _category = Category.Armor;
        private ArmorSlot _armorSlot = ArmorSlot.Helmet;
        private HandSlot _handSlot = HandSlot.Right;

        private RectTransform _options;
        private RectTransform _subTabs;
        private RectTransform _colorEditor;
        private readonly List<GameObject> _optionRows = new List<GameObject>();
        private readonly List<GameObject> _subTabButtons = new List<GameObject>();

        private TMP_InputField _r, _g, _b, _scale;
        private Image _colorPreview;
        private string _builtKey;

        protected override void OnBuild()
        {
            const float sideWidth = 190f;

            var side = ValheimUi.CreateInlay(Root, "Categories");
            ValheimUi.Anchor(side, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(sideWidth, 0f));

            var sideList = ValheimUi.CreateScrollList(side, spacing: 4f);

            foreach (Category category in Enum.GetValues(typeof(Category)))
            {
                var captured = category;
                var button = ValheimUi.CreateButton(sideList, Label(category), 0f, 40f, 15);
                button.onClick.AddListener(() => { _category = captured; _builtKey = null; });
            }

            var right = ValheimUi.CreateInlay(Root, "Options");
            ValheimUi.Anchor(right, Vector2.zero, Vector2.one, new Vector2(sideWidth + 10f, 0f), Vector2.zero);

            _subTabs = ValheimUi.CreateRect("SubTabs", right);
            ValheimUi.Anchor(_subTabs, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -46f), new Vector2(-8f, -6f));
            var subLayout = _subTabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            subLayout.spacing = 6f;
            subLayout.childControlWidth = false;
            subLayout.childControlHeight = true;
            subLayout.childAlignment = TextAnchor.MiddleLeft;

            var area = ValheimUi.CreateRect("Area", right);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(5f, 5f), new Vector2(-5f, -50f));
            _options = ValheimUi.CreateScrollList(area, spacing: 3f);

            BuildColorEditor(right);
        }

        private static string Label(Category category) => category switch
        {
            Category.Armor => "Armadura",
            Category.Hands => "Mãos",
            Category.Hair => "Cabelo",
            Category.Beard => "Barba",
            Category.Skin => "Pele (RGB)",
            Category.HairColor => "Cabelo (RGB)",
            Category.Model => "Modelo",
            Category.Scale => "Tamanho",
            _ => category.ToString(),
        };

        public override void Refresh()
        {
            string key = $"{_category}:{_armorSlot}:{_handSlot}";
            if (key == _builtKey) return;
            _builtKey = key;
            Rebuild();
        }

        private void Rebuild()
        {
            foreach (var row in _optionRows) if (row != null) UnityEngine.Object.Destroy(row);
            _optionRows.Clear();
            foreach (var tab in _subTabButtons) if (tab != null) UnityEngine.Object.Destroy(tab);
            _subTabButtons.Clear();

            bool isColor = _category == Category.Skin || _category == Category.HairColor;
            _colorEditor.gameObject.SetActive(isColor || _category == Category.Scale);
            ((RectTransform)_options.parent).gameObject.SetActive(!isColor && _category != Category.Scale);

            switch (_category)
            {
                case Category.Armor: BuildArmor(); break;
                case Category.Hands: BuildHands(); break;
                case Category.Hair: BuildNamed(NpcBase.GetHairNames(), n => Npc.RequestSetHair(Player, n)); break;
                case Category.Beard: BuildNamed(NpcBase.GetBeardNames(), n => Npc.RequestSetBeard(Player, n)); break;
                case Category.Model: BuildModels(); break;
                case Category.Skin:
                case Category.Scale:
                case Category.HairColor: ConfigureEditor(); break;
            }
        }

        private void BuildArmor()
        {
            foreach (ArmorSlot slot in Enum.GetValues(typeof(ArmorSlot)))
            {
                var captured = slot;
                var label = slot switch
                {
                    ArmorSlot.Helmet => "Capacete",
                    ArmorSlot.Chest => "Peitoral",
                    ArmorSlot.Legs => "Pernas",
                    ArmorSlot.Shoulder => "Capa",
                    _ => slot.ToString(),
                };
                var button = ValheimUi.CreateButton(_subTabs, label, 110f, 34f, 14);
                Highlight(button, slot == _armorSlot);
                button.onClick.AddListener(() => { _armorSlot = captured; _builtKey = null; });
                _subTabButtons.Add(button.gameObject);
            }

            AddOption("(nenhum)", null, () => Npc.RequestSetArmor(Player, _armorSlot, ""));
            foreach (var name in NpcBase.GetArmorNamesForSlot(_armorSlot))
            {
                var captured = name;
                AddOption(ValheimUi.Localize(MarketView.DisplayName(name)), name,
                    () => Npc.RequestSetArmor(Player, _armorSlot, captured), name);
            }
        }

        private void BuildHands()
        {
            foreach (HandSlot slot in Enum.GetValues(typeof(HandSlot)))
            {
                var captured = slot;
                var button = ValheimUi.CreateButton(_subTabs,
                    slot == HandSlot.Right ? "Mão direita" : "Mão esquerda", 130f, 34f, 14);
                Highlight(button, slot == _handSlot);
                button.onClick.AddListener(() => { _handSlot = captured; _builtKey = null; });
                _subTabButtons.Add(button.gameObject);
            }

            AddOption("(nenhum)", null, () => Npc.RequestSetHandItem(Player, _handSlot, ""));
            foreach (var name in NpcBase.GetHandItemNames(_handSlot))
            {
                var captured = name;
                AddOption(ValheimUi.Localize(MarketView.DisplayName(name)), name,
                    () => Npc.RequestSetHandItem(Player, _handSlot, captured), name);
            }
        }

        private void BuildNamed(List<string> names, Action<string> apply)
        {
            AddOption("0 — (nenhum)", null, () => apply(""));
            for (int i = 0; i < names.Count; i++)
            {
                var captured = names[i];
                AddOption($"{i + 1} — {captured}", null, () => apply(captured));
            }
            if (names.Count == 0) Dim(_options, "(nenhuma opção encontrada no jogo)");
        }

        private void BuildModels()
        {
            int count = Npc.GetModelCount();
            for (int i = 0; i < count; i++)
            {
                int captured = i;
                AddOption(i == 0 ? "1 — Masculino" : i == 1 ? "2 — Feminino" : $"{i + 1}", null,
                    () => Npc.RequestSetModel(Player, captured));
            }
            if (count == 0) Dim(_options, "(nenhuma opção disponível)");
        }

        private void AddOption(string text, string iconPrefab, Action onClick, string subtitle = null)
        {
            var button = ValheimUi.CreateButton(_options, "", 0f, 42f, 15);
            _optionRows.Add(button.gameObject);

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            label.alignment = TextAlignmentOptions.Left;
            label.text = subtitle == null
                ? text
                : $"{text}  <size=12><color=#9a9188>{subtitle}</color></size>";

            if (iconPrefab != null) Iconify(button, iconPrefab);
            else ValheimUi.Anchor((RectTransform)label.transform, Vector2.zero, Vector2.one,
                new Vector2(12f, 2f), new Vector2(-8f, -2f));

            button.onClick.AddListener(() => onClick());
        }

        private static void Highlight(Button button, bool active)
        {
            var text = button.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) text.color = active ? ValheimUi.Yellow : ValheimUi.Orange;
            button.image.color = active ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
        }

        // ---- RGB / scale editor ----

        private void BuildColorEditor(Transform parent)
        {
            _colorEditor = ValheimUi.CreateRect("Editor", parent);
            ValheimUi.Anchor(_colorEditor, Vector2.zero, Vector2.one, new Vector2(20f, 20f), new Vector2(-20f, -56f));
            var column = _colorEditor.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 12f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.childAlignment = TextAnchor.UpperLeft;

            _editorTitle = Heading(_colorEditor, "");
            ValheimUi.SetHeight(_editorTitle.gameObject, 30f);

            _rgbRow = Row(_colorEditor, 40f);
            ValheimUi.CreateLabel(_rgbRow, "R", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            _r = ValheimUi.CreateInputField(_rgbRow, "255", 80f, 36f);
            ValheimUi.CreateLabel(_rgbRow, "G", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            _g = ValheimUi.CreateInputField(_rgbRow, "255", 80f, 36f);
            ValheimUi.CreateLabel(_rgbRow, "B", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            _b = ValheimUi.CreateInputField(_rgbRow, "255", 80f, 36f);

            var preview = ValheimUi.CreateRect("Preview", _colorEditor);
            ValheimUi.SetHeight(preview.gameObject, 54f);
            _colorPreview = preview.gameObject.AddComponent<Image>();
            _colorPreview.sprite = ValheimUi.FieldSprite;
            _colorPreview.type = Image.Type.Sliced;

            _scaleRow = Row(_colorEditor, 40f);
            ValheimUi.CreateLabel(_scaleRow, "Escala (0.5 a 2.0)", 16, ValheimUi.Beige, TextAlignmentOptions.Left);
            _scale = ValheimUi.CreateInputField(_scaleRow, "1", 100f, 36f);

            _apply = ValheimUi.CreateButton(_colorEditor, "Aplicar", 200f, 44f, 17);
            ValheimUi.SetHeight(_apply.gameObject, 44f);
            _apply.onClick.AddListener(OnApply);

            _colorEditor.gameObject.SetActive(false);
        }

        private TextMeshProUGUI _editorTitle;
        private RectTransform _rgbRow;
        private RectTransform _scaleRow;
        private Button _apply;

        private void ConfigureEditor()
        {
            bool scale = _category == Category.Scale;
            _rgbRow.gameObject.SetActive(!scale);
            _colorPreview.gameObject.SetActive(!scale);
            _scaleRow.gameObject.SetActive(scale);

            var profile = Npc.BuildProfile();
            if (scale)
            {
                _editorTitle.text = "Tamanho do modelo";
                _scale.text = Mathf.Clamp(profile.Scale, 0.5f, 2f).ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            var color = _category == Category.Skin ? profile.SkinColor : profile.HairColor;
            _editorTitle.text = _category == Category.Skin ? "Cor da pele" : "Cor do cabelo";
            if (color != null)
            {
                _r.text = Mathf.RoundToInt(Mathf.Clamp01(color.R) * 255f).ToString(CultureInfo.InvariantCulture);
                _g.text = Mathf.RoundToInt(Mathf.Clamp01(color.G) * 255f).ToString(CultureInfo.InvariantCulture);
                _b.text = Mathf.RoundToInt(Mathf.Clamp01(color.B) * 255f).ToString(CultureInfo.InvariantCulture);
            }
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            _colorPreview.color = TryParseRgb(out var color)
                ? new Color(color.x, color.y, color.z, 1f)
                : Color.gray;
        }

        private bool TryParseRgb(out Vector3 color)
        {
            color = Vector3.zero;
            if (!int.TryParse(_r.text, out int r) || r < 0 || r > 255) return false;
            if (!int.TryParse(_g.text, out int g) || g < 0 || g > 255) return false;
            if (!int.TryParse(_b.text, out int b) || b < 0 || b > 255) return false;
            color = new Vector3(r / 255f, g / 255f, b / 255f);
            return true;
        }

        private void OnApply()
        {
            if (_category == Category.Scale)
            {
                if (!float.TryParse(_scale.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                {
                    Say("Tamanho inválido. Use um número entre 0.5 e 2.0.");
                    return;
                }
                value = Mathf.Clamp(value, 0.5f, 2f);
                _scale.text = value.ToString("0.##", CultureInfo.InvariantCulture);
                Npc.RequestSetScale(Player, value);
                Say($"Tamanho atualizado para {_scale.text}x.");
                return;
            }

            if (!TryParseRgb(out var color))
            {
                Say("Use números inteiros entre 0 e 255 para R, G e B.");
                return;
            }

            UpdatePreview();
            if (_category == Category.Skin) Npc.RequestSetSkinColor(Player, color);
            else Npc.RequestSetHairColor(Player, color);
            Say(_editorTitle.text + " atualizada.");
        }
    }
}
