using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;

namespace NpcValheim.UI
{
    /// <summary>
    /// Admin tab of the Juiz de Guerra: the attributes of each side (switch cooldown, PvE
    /// title, skill gain and gathering rate for PvE and for PvP), one row each, saved one at a
    /// time.
    ///
    /// They are Deadheim's server config, not this NPC's: Salvar goes through Deadheim's own
    /// admin channel (the ESC menu's Servidor tab), whose server checks adminlist.txt on the
    /// real connection, writes its cfg and lets ServerSync hand the value to every player. So
    /// a change here is the same as editing the server's Detalhes.Deadheim.cfg, and applies to
    /// every Juiz de Guerra at once.
    /// </summary>
    internal sealed class WarJudgeAdminView : NpcViewBase
    {
        private sealed class Field
        {
            public string Key;
            public string Kind;
            public string Value;
            public TMP_InputField Input;
            public Button Toggle;
        }

        private RectTransform _list;
        private GameObject _pane;
        private TextMeshProUGUI _closed;
        private readonly List<Field> _fields = new List<Field>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private string _keys;
        private int _seenMessage;

        protected override void OnBuild()
        {
            var pane = ValheimUi.CreateInlay(Root, "Attributes");
            ValheimUi.Stretch(pane, 0f, 0f);
            _pane = pane.gameObject;
            var header = ValheimUi.CreateLabel(pane, "Atributos do PvE e do PvP (valem para o servidor inteiro)", 17,
                ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));
            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 6f), new Vector2(-6f, -42f));
            _list = ValheimUi.CreateScrollList(area, spacing: 6f);

            _closed = ValheimUi.CreateLabel(Root, "", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_closed.transform, Vector2.zero, Vector2.one, new Vector2(40f, 40f), new Vector2(-40f, -60f));

            _seenMessage = DeadheimPvpApi.SettingMessageRevision();
        }

        public override void Refresh()
        {
            string closed = !DeadheimPvpApi.IsAvailable ? DeadheimPvpApi.NotInstalled
                : !DeadheimPvpApi.LocalIsAdmin() ? "Só admins do servidor mudam estes atributos."
                : null;
            _closed.gameObject.SetActive(closed != null);
            _closed.text = closed ?? "";
            _pane.SetActive(closed == null);
            if (closed != null) return;

            int message = DeadheimPvpApi.SettingMessageRevision();
            if (message != _seenMessage)
            {
                _seenMessage = message;
                Say(DeadheimPvpApi.LastSettingMessage());
            }

            var settings = DeadheimPvpApi.PveSettings();
            string keys = string.Join("|", System.Array.ConvertAll(settings, s => s != null && s.Length > 0 ? s[0] : ""));
            if (keys != _keys)
            {
                _keys = keys;
                Rebuild(settings);
                return;
            }

            // The values move under the admin's feet when ServerSync delivers a change (theirs
            // or another admin's). A field being typed in is left alone.
            for (int i = 0; i < settings.Length && i < _fields.Count; i++)
            {
                var field = _fields[i];
                string value = settings[i][2];
                if (value == field.Value) continue;
                field.Value = value;
                if (field.Input != null && !field.Input.isFocused) field.Input.text = value;
                if (field.Toggle != null) PaintToggle(field);
            }
        }

        private void Rebuild(string[][] settings)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();
            _fields.Clear();

            foreach (var setting in settings)
            {
                if (setting == null || setting.Length < 5) continue;
                var field = new Field { Key = setting[0], Value = setting[2], Kind = setting[3] };
                _fields.Add(field);

                var row = Row(_list, 62f);
                _rows.Add(row.gameObject);
                var label = ValheimUi.CreateLabel(row, $"<b>{setting[1]}</b>\n<size=12><color=#9a9188>{setting[4]}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                if (field.Kind == "bool")
                {
                    field.Toggle = ValheimUi.CreateButton(row, "", 130f, 40f, 15);
                    ValheimUi.SetWidth(field.Toggle.gameObject, 130f);
                    PaintToggle(field);
                    field.Toggle.onClick.AddListener(() =>
                        Save(field, field.Value.Trim().ToLowerInvariant() == "true" ? "false" : "true"));
                    continue;
                }

                field.Input = ValheimUi.CreateInputField(row, field.Value, field.Kind == "text" ? 200f : 110f, 38f);
                if (field.Kind == "number") field.Input.contentType = TMP_InputField.ContentType.DecimalNumber;
                var save = ValheimUi.CreateButton(row, "Salvar", 96f, 38f, 14);
                ValheimUi.SetWidth(save.gameObject, 96f);
                save.onClick.AddListener(() => Save(field, field.Input.text));
            }
        }

        private static void PaintToggle(Field field)
        {
            bool on = (field.Value ?? "").Trim().ToLowerInvariant() == "true";
            field.Toggle.GetComponentInChildren<TextMeshProUGUI>().text = on ? "Ligado" : "Desligado";
        }

        private void Save(Field field, string value)
        {
            // The cfg wants a dot for decimals whatever the Windows language says.
            value = (value ?? "").Trim();
            if (field.Kind == "number") value = value.Replace(',', '.');
            string refusal = DeadheimPvpApi.SetPveSetting(field.Key, value);
            Say(refusal ?? $"Enviando {field.Key} = {value} ao servidor...");
        }
    }
}
