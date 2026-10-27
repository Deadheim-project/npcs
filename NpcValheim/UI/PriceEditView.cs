using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>Admin tab "Preços": one row per price of this NPC, a number box and Salvar.
    /// The values come from the synced cfg, so a saved edit shows up here by itself.</summary>
    internal sealed class PriceEditView : NpcViewBase
    {
        private RectTransform _list;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();
        // What each box showed when built, to tell an admin's unsaved number from the cfg's.
        private readonly List<(string id, TMP_InputField input, string shown)> _inputs =
            new List<(string, TMP_InputField, string)>();

        protected override void OnBuild()
        {
            var pane = ValheimUi.CreateInlay(Root, "Prices");
            ValheimUi.Stretch(pane, 0f, 0f);
            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Stretch(area, 4f, 4f);
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        public override void Refresh()
        {
            var fields = PriceEditor.FieldsFor(Npc);
            var sb = new System.Text.StringBuilder();
            foreach (var f in fields) sb.Append(f.Config).Append('|').Append(f.Entry).Append('|').Append(f.Key).Append('=').Append(f.Value).Append('\n');
            string signature = sb.ToString();
            if (signature == _signature) return;
            // Do not rebuild under the admin's fingers while a box is being typed in.
            if (_signature != null && UiInputBlocker.IsTyping) return;
            _signature = signature;
            Rebuild(fields);
        }

        /// <summary>A field's place in the list without its current number, so a box keeps
        /// its typed value when a save elsewhere in the same entry changes that entry's text.</summary>
        private static string FieldId(PriceField f)
        {
            var sb = new System.Text.StringBuilder(f.Config).Append('|').Append(f.Key).Append('|');
            foreach (var part in (f.Entry ?? "").Split(';'))
            {
                int eq = part.IndexOf('=');
                string key = (eq > 0 ? part.Substring(0, eq) : part).Trim().ToLowerInvariant();
                if (key == "price" || key == "gold" || key == "deadcoins") continue;
                sb.Append(part.Trim()).Append(';');
            }
            return sb.ToString();
        }

        private void Rebuild(List<PriceField> fields)
        {
            // The boss pass has two prices in one entry: saving Coins rebuilt every row from
            // the synced cfg and wiped the Deadcoins number the admin had already typed. Keep
            // any box whose text differs from what it was built with.
            var typed = new Dictionary<string, string>();
            foreach (var (id, input, shown) in _inputs)
                if (input != null && input.text != shown) typed[id] = input.text;
            _inputs.Clear();

            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var hint = Row(_list, 26f);
            _rows.Add(hint.gameObject);
            Flexible(ValheimUi.CreateLabel(hint,
                "Preços deste NPC. Mude o número e clique em Salvar; vale para todos na hora. 0 em Coins/Deadcoins = não vende nessa moeda.",
                13, ValheimUi.Muted, TextAlignmentOptions.Left).gameObject);

            foreach (var field in fields)
            {
                var f = field;
                var row = Row(_list, 40f);
                _rows.Add(row.gameObject);
                Flexible(ValheimUi.CreateLabel(row, f.Label, 15, ValheimUi.Beige, TextAlignmentOptions.Left).gameObject);
                string shown = f.Value.ToString(CultureInfo.InvariantCulture);
                string id = FieldId(f);
                var input = ValheimUi.CreateInputField(row, typed.TryGetValue(id, out var pending) ? pending : shown, 110f, 34f);
                input.contentType = TMP_InputField.ContentType.IntegerNumber;
                _inputs.Add((id, input, shown));
                var save = ValheimUi.CreateButton(row, "Salvar", 96f, 34f, 14);
                save.onClick.AddListener(() =>
                {
                    if (!int.TryParse(input.text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value < 0 ||
                        (value == 0 && !f.ZeroAllowed))
                    {
                        Say(f.ZeroAllowed ? "Digite um número, 0 ou maior." : "Digite um número maior que zero.");
                        return;
                    }
                    Say(PriceEditor.Request(f, value) ? "Salvando preço..." : "Não foi possível enviar ao servidor.");
                });
            }
            if (fields.Count == 0)
            {
                var empty = Row(_list, 26f);
                _rows.Add(empty.gameObject);
                Flexible(ValheimUi.CreateLabel(empty, "Este NPC não tem preços.", 15, ValheimUi.Muted, TextAlignmentOptions.Left).gameObject);
            }
        }
    }
}
