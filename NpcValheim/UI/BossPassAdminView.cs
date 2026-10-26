using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// Admin page of the Mestre dos Passes: which bosses have a pass, what each one is called,
    /// which shop it opens and what it costs, plus the kill radius and the Deadcoins gate. It
    /// edits the server's [BossPass] settings -- saved to the cfg and sent to every client by
    /// ServerSync -- so the counter, the Mercador locks and the cfg never disagree.
    /// </summary>
    internal sealed class BossPassAdminView : NpcViewBase
    {
        private const float FormHeight = 196f;

        private TMP_InputField _boss, _name, _shop, _gold, _deadcoins, _radius;
        private Button _gate;
        private RectTransform _list;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        private BossPassNpc Counter => Npc as BossPassNpc;

        protected override void OnBuild()
        {
            var form = ValheimUi.CreateRect("Form", Root);
            ValheimUi.Anchor(form, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -FormHeight), Vector2.zero);
            var column = form.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 6f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;

            var first = Row(form, 36f, 6f);
            Caption(first, "Boss", 50f);
            _boss = Field(first, 150f, "gd_king");
            Caption(first, "Nome", 50f);
            _name = Field(first, 150f, "Ancião");
            Caption(first, "Loja", 44f);
            _shop = Field(first, 0f, "Loja do Pântano");
            Flexible(_shop.gameObject);

            var second = Row(form, 36f, 6f);
            Caption(second, "Moedas", 70f);
            _gold = Number(second, "0");
            Caption(second, "Deadcoins", 90f);
            _deadcoins = Number(second, "0");
            Flexible(ValheimUi.CreateRect("Spacer", second).gameObject);
            var save = ValheimUi.CreateButton(second, "Salvar", 110f, 34f, 14);
            save.onClick.AddListener(Save);

            Dim(form, "Boss é o prefab da criatura (gd_king, Bonemass, Dragon, GoblinKing, SeekerQueen, Fader). " +
                      "Preço 0 = esse jeito de pagar não é oferecido; os dois em 0 = só matando o boss.");

            var options = Row(form, 36f, 6f);
            Caption(options, "Raio da morte (m)", 150f);
            _radius = Number(options, "60", decimals: true);
            var radiusSave = ValheimUi.CreateButton(options, "Salvar raio", 120f, 34f, 14);
            radiusSave.onClick.AddListener(() => Counter?.RequestSetOption(BossPass.OptionRadius, _radius.text));
            Flexible(ValheimUi.CreateRect("Spacer", options).gameObject);
            _gate = ValheimUi.CreateButton(options, "", 420f, 34f, 14);
            ValheimUi.SetWidth(_gate.gameObject, 420f);
            _gate.onClick.AddListener(() =>
            {
                bool on = Plugin.BossPassDeadcoinsAfterFirstKill?.Value ?? true;
                Counter?.RequestSetOption(BossPass.OptionDeadcoinsAfterFirstKill, on ? "false" : "true");
            });

            var pane = ValheimUi.CreateInlay(Root, "Bosses");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -(FormHeight + 6f)));
            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Stretch(area, 4f, 4f);
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        private static void Caption(RectTransform row, string text, float width)
        {
            var label = ValheimUi.CreateLabel(row, text, 15, ValheimUi.Muted, TextAlignmentOptions.Right);
            ValheimUi.SetWidth(label.gameObject, width);
        }

        private static TMP_InputField Field(RectTransform row, float width, string hint)
        {
            var field = ValheimUi.CreateInputField(row, "", width, 34f);
            if (width <= 0f) field.gameObject.GetComponent<LayoutElement>().minWidth = 140f;
            field.characterLimit = 48;
            return field;
        }

        private static TMP_InputField Number(RectTransform row, string value, bool decimals = false)
        {
            var field = ValheimUi.CreateInputField(row, value, 90f, 34f);
            field.contentType = decimals ? TMP_InputField.ContentType.DecimalNumber : TMP_InputField.ContentType.IntegerNumber;
            return field;
        }

        private static int? Whole(TMP_InputField field)
        {
            string text = (field.text ?? "").Trim();
            if (text.Length == 0) return 0;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
                ? value
                : (int?)null;
        }

        private void Save()
        {
            string boss = (_boss.text ?? "").Trim();
            if (!BossPassCatalog.IsCleanId(boss)) { Say("Informe o prefab do boss, sem espaços (ex.: gd_king)."); return; }
            int? gold = Whole(_gold), deadcoins = Whole(_deadcoins);
            if (gold == null || deadcoins == null) { Say("Moedas e Deadcoins precisam ser números inteiros, 0 ou mais."); return; }

            Counter?.RequestSetEntry(boss, _name.text, _shop.text, gold.Value, deadcoins.Value);
            Say($"Salvando o passe de {(string.IsNullOrWhiteSpace(_name.text) ? boss : _name.text.Trim())}...");
        }

        private void Load(BossPassEntry entry)
        {
            _boss.text = entry.Boss;
            _name.text = entry.Name ?? "";
            _shop.text = entry.Shop ?? "";
            _gold.text = entry.Gold.ToString(CultureInfo.InvariantCulture);
            _deadcoins.text = entry.Deadcoins.ToString(CultureInfo.InvariantCulture);
        }

        public override void Refresh()
        {
            bool gate = Plugin.BossPassDeadcoinsAfterFirstKill?.Value ?? true;
            var gateLabel = _gate.GetComponentInChildren<TextMeshProUGUI>();
            if (gateLabel != null)
                gateLabel.text = gate ? "Deadcoins só após a 1ª derrota do boss: LIGADO" : "Deadcoins só após a 1ª derrota do boss: DESLIGADO";

            // The radius is what the server has, until the admin starts typing a new one.
            if (!_radius.isFocused)
                _radius.text = (Plugin.BossPassKillRadius?.Value ?? 60f).ToString("0.##", CultureInfo.InvariantCulture);

            string raw = Plugin.BossPassBosses?.Value ?? "";
            if (raw == _signature) return;
            _signature = raw;
            Rebuild(raw);
        }

        private void Rebuild(string raw)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var problems = new List<string>();
            var entries = BossPassCatalog.Parse(raw, problems);
            Add(Row(_list, 26f), $"Passes ({entries.Count}) · Editar põe o boss no formulário acima; Salvar com o mesmo boss muda os dados.",
                13, ValheimUi.Muted);

            foreach (var entry in entries)
            {
                var e = entry;
                var row = Row(_list, 50f);
                _rows.Add(row.gameObject);
                string gold = e.Gold > 0 ? $"{e.Gold} moedas" : "sem moedas";
                string dead = e.Deadcoins > 0 ? $"{e.Deadcoins} Deadcoins" : "sem Deadcoins";
                var label = ValheimUi.CreateLabel(row,
                    $"<b>{e.Name}</b>  <size=12><color=#9a9188>{e.Boss}</color></size>\n" +
                    $"<size=12><color=#9a9188>{BossPassCatalog.ShopName(e)} · {gold} · {dead}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);
                var edit = ValheimUi.CreateButton(row, "Editar", 90f, 34f, 14);
                edit.onClick.AddListener(() => Load(e));
                var remove = ValheimUi.CreateButton(row, "Remover", 100f, 34f, 14);
                remove.onClick.AddListener(() =>
                {
                    Counter?.RequestRemoveEntry(e.Boss);
                    Say($"Retirando o passe de {e.Name}...");
                });
            }
            if (entries.Count == 0) Add(Row(_list, 26f), "Nenhum boss com passe.", 15, ValheimUi.Muted);
            foreach (var problem in problems) Add(Row(_list, 22f), "cfg: " + problem, 13, ValheimUi.Danger);
        }

        private void Add(RectTransform row, string text, int size, Color color)
        {
            _rows.Add(row.gameObject);
            Flexible(ValheimUi.CreateLabel(row, text, size, color, TextAlignmentOptions.Left).gameObject);
        }
    }
}
