using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The form an admin fills to put an item on sale at an NPC (the Deadcoins counter, the
    /// Intendente da Arena): the item, then a few whole numbers, then Salvar. Built once
    /// outside the page's list, so a list rebuilt while someone types keeps their text.
    ///
    /// The item is the prefab name the cfg holds, but nobody remembers that "Madeira" is
    /// "Wood" or that the case matters, so <see cref="Resolve"/> accepts either name in any
    /// case and the line under the form says what the server will understand.
    /// </summary>
    internal sealed class OfferForm
    {
        internal TMP_InputField Item;
        internal readonly List<TMP_InputField> Numbers = new List<TMP_InputField>();
        internal TextMeshProUGUI Preview;

        /// <summary>A row of [Item: ____ label ___ label ___ ... Salvar] across the top of
        /// <paramref name="root"/>, and the preview line under it. 74 px tall in all.</summary>
        internal static OfferForm Build(RectTransform root, (string Label, string Value, float Width)[] numbers, Action onSave)
        {
            var form = new OfferForm();

            var row = ValheimUi.CreateRect("OfferForm", root);
            ValheimUi.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -40f), Vector2.zero);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;

            Label(row, "Item:", 46f);
            form.Item = ValheimUi.CreateInputField(row, "", 0f, 34f);
            var flex = form.Item.gameObject.GetComponent<LayoutElement>();
            flex.flexibleWidth = 1f;
            flex.minWidth = 140f;
            form.Item.onValueChanged.AddListener(_ => form.UpdatePreview());

            foreach (var (label, value, width) in numbers)
            {
                Label(row, label, label.Length * 9f + 6f);
                var field = ValheimUi.CreateInputField(row, value, width, 34f);
                field.contentType = TMP_InputField.ContentType.IntegerNumber;
                form.Numbers.Add(field);
            }

            var save = ValheimUi.CreateButton(row, "Salvar", 96f, 34f, 14);
            save.onClick.AddListener(() => onSave());

            form.Preview = ValheimUi.CreateLabel(root, "", 14, ValheimUi.Beige, TextAlignmentOptions.Left);
            form.Preview.textWrappingMode = TextWrappingModes.NoWrap;
            form.Preview.overflowMode = TextOverflowModes.Ellipsis;
            ValheimUi.Anchor((RectTransform)form.Preview.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(52f, -70f), new Vector2(0f, -44f));
            form.UpdatePreview();
            return form;
        }

        private static void Label(RectTransform row, string text, float width)
        {
            var label = ValheimUi.CreateLabel(row, text, 15, ValheimUi.Muted, TextAlignmentOptions.Right);
            ValheimUi.SetWidth(label.gameObject, width);
        }

        /// <summary>Puts an existing entry back in the form, for editing.</summary>
        internal void Load(string prefab, params int[] values)
        {
            Item.text = prefab ?? "";
            for (int i = 0; i < values.Length && i < Numbers.Count; i++)
                Numbers[i].text = values[i].ToString(CultureInfo.InvariantCulture);
            UpdatePreview();
        }

        /// <summary>The number in field <paramref name="index"/>, or null when it is not one.</summary>
        internal int? Number(int index)
        {
            string text = (Numbers[index].text ?? "").Trim();
            if (text.Length == 0) return 0;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : (int?)null;
        }

        private void UpdatePreview()
        {
            string typed = (Item.text ?? "").Trim();
            if (typed.Length == 0)
            {
                Preview.text = "<color=#9a9188>Nome do item como no jogo (Madeira) ou o prefab (Wood).</color>";
                return;
            }
            string prefab = Resolve(typed);
            Preview.text = prefab == null
                ? "<color=#ee6b57>Nenhum item com esse nome.</color>"
                : $"{ItemNames.Display(prefab)} <color=#9a9188>· prefab {prefab} · até {ItemSpawner.MaxDeliverableAmount(prefab)} por entrega</color>";
        }

        /// <summary>
        /// The prefab an admin means: the exact prefab name, else a prefab or a displayed name
        /// equal ignoring case. Null when nothing matches. Only a prefab name is ever sent, and
        /// the server checks it again.
        /// </summary>
        internal static string Resolve(string typed)
        {
            typed = (typed ?? "").Trim();
            if (typed.Length == 0 || ObjectDB.instance == null) return null;
            if (ItemSpawner.MaxDeliverableAmount(typed) > 0) return typed;

            string byDisplay = null;
            foreach (var go in ObjectDB.instance.m_items)
            {
                if (go == null || go.GetComponent<ItemDrop>() == null) continue;
                if (string.Equals(go.name, typed, StringComparison.OrdinalIgnoreCase)) return go.name;
                if (byDisplay == null && string.Equals(ItemNames.Display(go.name), typed, StringComparison.OrdinalIgnoreCase))
                    byDisplay = go.name;
            }
            return byDisplay;
        }
    }
}
