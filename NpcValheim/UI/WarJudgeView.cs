using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Juiz de Guerra's desk: which side the player is on, what each side gets (side by
    /// side, so the choice is made with the numbers in view), how long until the next switch,
    /// and the one button that switches.
    ///
    /// The numbers are Deadheim's server config as it reaches this client by ServerSync, so an
    /// admin's edit in the Atributos tab shows up here at once. The button only asks
    /// (DeadheimPvpApi); Deadheim's server decides and answers.
    /// </summary>
    internal sealed class WarJudgeView : NpcViewBase
    {
        private const string Grey = "#9a9188";
        private const string Green = "#9acd32";
        private const string Orange = "#ff8c00";
        private const string Red = "#ee6b57";

        private TextMeshProUGUI _state;
        private TextMeshProUGUI _pve;
        private TextMeshProUGUI _pvp;
        private TextMeshProUGUI _note;
        private Button _switch;
        private TextMeshProUGUI _closed;
        private GameObject _body;
        private int _seenMessage;

        protected override void OnBuild()
        {
            var body = ValheimUi.CreateRect("Body", Root);
            ValheimUi.Stretch(body, 0f, 0f);
            _body = body.gameObject;

            _state = ValheimUi.CreateLabel(body, "", 20, ValheimUi.Beige, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)_state.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -40f), Vector2.zero);

            _pve = Column(body, 0f, 0.5f, "PvE");
            _pvp = Column(body, 0.5f, 1f, "PvP");

            _note = ValheimUi.CreateLabel(body, "", 14, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_note.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(20f, 62f), new Vector2(-20f, 110f));

            _switch = ValheimUi.CreateButton(body, "", 300f, 48f, 18);
            ValheimUi.Anchor((RectTransform)_switch.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-150f, 6f), new Vector2(150f, 54f));
            _switch.onClick.AddListener(Switch);

            _closed = ValheimUi.CreateLabel(Root, "", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_closed.transform, Vector2.zero, Vector2.one, new Vector2(40f, 40f), new Vector2(-40f, -60f));

            _seenMessage = DeadheimPvpApi.PveMessageRevision();
        }

        private static TextMeshProUGUI Column(RectTransform parent, float from, float to, string title)
        {
            var pane = ValheimUi.CreateInlay(parent, title);
            ValheimUi.Anchor(pane, new Vector2(from, 0f), new Vector2(to, 1f), new Vector2(6f, 116f), new Vector2(-6f, -48f));
            var header = ValheimUi.CreateLabel(pane, title, 20, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));
            var text = ValheimUi.CreateLabel(pane, "", 15, ValheimUi.Beige, TextAlignmentOptions.TopLeft);
            ValheimUi.Anchor((RectTransform)text.transform, Vector2.zero, Vector2.one, new Vector2(16f, 8f), new Vector2(-16f, -42f));
            return text;
        }

        public override void Refresh()
        {
            string closed = !DeadheimPvpApi.IsAvailable ? DeadheimPvpApi.NotInstalled
                : !DeadheimPvpApi.Active() ? "O PvP está desligado neste servidor."
                : null;
            _closed.gameObject.SetActive(closed != null);
            _closed.text = closed ?? "";
            _body.SetActive(closed == null);
            if (closed != null) return;

            int message = DeadheimPvpApi.PveMessageRevision();
            if (message != _seenMessage)
            {
                _seenMessage = message;
                Say(DeadheimPvpApi.LastPveMessage());
            }

            bool pve = DeadheimPvpApi.LocalIsPve();
            string title = DeadheimPvpApi.PveTitle();
            _state.text = pve
                ? $"Você é <color={Green}>{title}</color> (PvE): não luta com jogadores."
                : $"Você está no <color={Orange}>PvP</color>: luta com jogadores fora das zonas seguras.";

            _pve.text =
                $"Título: <color={Green}>{title}</color>\n" +
                "Não fere nem é ferido por jogadores, em lugar nenhum (nem na arena ou no castelo).\n" +
                "Não pode receber bounty.\n\n" +
                $"Ganho de skill: <b>x{Num(DeadheimPvpApi.PveSkillMultiplier())}</b>\n" +
                $"Coleta: <b>{Rate(DeadheimPvpApi.PveResourceRate())}</b>";
            _pvp.text =
                "Luta com jogadores fora das zonas seguras: pode virar PK, ser caçado e saquear.\n" +
                "Pode receber e colocar bounty.\n\n" +
                $"Ganho de skill: <b>x{Num(DeadheimPvpApi.PvpSkillMultiplier())}</b>\n" +
                $"Coleta: <b>{Rate(DeadheimPvpApi.PvpResourceRate())}</b>";

            bool toPve = !pve;
            float hours = DeadheimPvpApi.PveSwitchCooldownHours();
            double wait = DeadheimPvpApi.LocalPveSwitchSeconds();
            string refusal = DeadheimPvpApi.PveSwitchRefusal(toPve);

            _switch.GetComponentInChildren<TextMeshProUGUI>().text = toPve ? $"Virar {title} (PvE)" : "Voltar ao PvP";
            _switch.interactable = refusal == null;
            _note.text =
                (refusal != null ? $"<color={Red}>{refusal}</color>\n" : "") +
                (hours > 0f
                    ? $"Depois de trocar, a próxima troca só em {DeadheimPvpApi.FormatDuration(hours * 3600d)}." +
                      (wait > 0d ? $" Faltam {DeadheimPvpApi.FormatDuration(wait)}." : "")
                    : "Sem espera entre trocas.");
        }

        private void Switch()
        {
            bool toPve = !DeadheimPvpApi.LocalIsPve();
            string refusal = DeadheimPvpApi.RequestPveSwitch(toPve);
            Say(refusal ?? (toPve ? "Pedindo o PvE ao servidor..." : "Pedindo a volta ao PvP ao servidor..."));
        }

        private static string Num(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static string Rate(float value) => value > 0f ? $"x{Num(value)}" : "a do mundo";
    }
}
