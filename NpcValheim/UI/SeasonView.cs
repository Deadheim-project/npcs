using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Season Herald's screen: the season's progress, the 10-level reward track, the
    /// week's three challenges and the button that unlocks the paid levels.
    ///
    /// Everything shown is the server's answer to a status request sent when the tab opens
    /// and every few seconds after; the button only asks, and the server charges its own
    /// price (Season.ServeBuy).
    /// </summary>
    internal sealed class SeasonView : NpcViewBase
    {
        private const float PollSeconds = 5f;
        private const string Green = "#8fd18f";
        private const string Grey = "#9a9188";
        private const string Gold = "#e8c04a";

        private SeasonHeraldNpc Herald => Npc as SeasonHeraldNpc;

        private TextMeshProUGUI _head;
        private TextMeshProUGUI _intro;
        private RectTransform _list;
        private TextMeshProUGUI _empty;

        private int _seenRevision = -1;
        private int _seenMessage;
        private float _nextPoll;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            _head = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_head.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            var pane = ValheimUi.CreateInlay(Root, "Temporada");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            _intro = ValheimUi.CreateLabel(pane, "", 14, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_intro.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -50f), new Vector2(-16f, -6f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -54f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "Consultando a temporada...", 15,
                ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            _seenMessage = Season.MessageRevision;
        }

        public override void Refresh()
        {
            var herald = Herald;
            if (herald == null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                herald.RequestStatus();
            }

            if (Season.MessageRevision != _seenMessage)
            {
                _seenMessage = Season.MessageRevision;
                Say(Season.LastMessage);
            }

            if (Season.Revision == _seenRevision) return;
            _seenRevision = Season.Revision;
            Rebuild(herald);
        }

        private void Rebuild(SeasonHeraldNpc herald)
        {
            foreach (var row in _rows) if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();

            if (!Season.Known)
            {
                _empty.gameObject.SetActive(true);
                return;
            }

            if (Season.State == 3 || Season.State == 0)
            {
                _head.text = Season.Name;
                _intro.text = "";
                _empty.text = Season.State == 3
                    ? "O passe de temporada não está aberto neste servidor."
                    : "A temporada ainda não começou. Volte em breve.";
                _empty.gameObject.SetActive(true);
                return;
            }
            _empty.gameObject.SetActive(false);

            int level = Math.Min(10, Season.Points / Season.PerLevel);
            int max = 10 * Season.PerLevel;
            string deadcoins = Season.Balance >= 0 ? Season.Balance.ToString() : "?";
            _head.text = $"{Season.Name}   <color={Grey}>Nível</color> {level}/10   " +
                         $"<color={Grey}>Pontos</color> {Math.Min(Season.Points, max)}/{max}   " +
                         $"<color={Grey}>Deadcoins:</color> {deadcoins}";

            string left = "";
            if (Season.State == 1 && Season.EndUtc > DateTime.UtcNow)
            {
                var span = Season.EndUtc - DateTime.UtcNow;
                left = span.TotalDays >= 1 ? $" Termina em {Math.Ceiling(span.TotalDays - 0.5):0} dia(s)." : " Termina hoje.";
            }
            _intro.text = Season.State == 2
                ? "A temporada acabou: o progresso está congelado."
                : "Só cosméticos, sem vantagem em combate. Cumpra os desafios da semana para ganhar pontos." + left;

            AddLabelRow("Desafios da semana", ValheimUi.Orange, 18, 30f);
            if (Season.Challenges.Count == 0)
                AddLabelRow(Season.State == 2 ? "Temporada encerrada." : "Sem desafios nesta semana.", ValheimUi.Muted, 14, 24f);
            foreach (var c in Season.Challenges)
            {
                string state = c.Done
                    ? $"<color={Green}>Concluído (+{c.Points} pontos)</color>"
                    : $"{c.Progress}/{c.Goal}   <color={Grey}>+{c.Points} pontos</color>";
                AddLabelRow($"<b>{c.Label}</b>   {state}", ValheimUi.Beige, 16, 32f);
            }

            AddLabelRow("Trilha de recompensas", ValheimUi.Orange, 18, 34f);
            foreach (var pair in Season.RewardKeys.OrderBy(r => r.Key))
            {
                int l = pair.Key;
                bool free = l <= Season.FreeLevels;
                bool reached = level >= l;
                bool delivered = Season.ClaimedLevels.Contains(l);
                string tag = free ? $"<color={Green}>grátis</color>" : $"<color={Gold}>passe</color>";
                string status;
                if (delivered) status = $"<color={Green}>Entregue</color>";
                else if (reached && !free && !Season.Paid) status = $"<color={Gold}>Ganho: libera com o passe</color>";
                else if (reached) status = "Entrega em instantes";
                else status = $"<color={Grey}>Faltam {l * Season.PerLevel - Season.Points} pontos</color>";
                AddLabelRow($"<b>Nível {l}</b>  {tag}   {Season.DescribeKey(pair.Value)}   {status}", ValheimUi.Beige, 15, 30f);
            }

            if (Season.State == 1 && !Season.Paid)
            {
                var row = Row(_list, 52f);
                _rows.Add(row.gameObject);
                var info = ValheimUi.CreateLabel(row, "Passe: libera os níveis pagos, inclusive o que você já ganhou.",
                    14, ValheimUi.Muted, TextAlignmentOptions.Left);
                Flexible(info.gameObject);
                var buy = ValheimUi.CreateButton(row, $"Comprar: {Season.Price} Deadcoins", 220f, 44f, 14);
                ValheimUi.SetWidth(buy.gameObject, 220f);
                buy.onClick.AddListener(() => Buy(herald));
            }
            else if (Season.Paid)
            {
                AddLabelRow($"<color={Green}>Você tem o passe desta temporada.</color>", ValheimUi.Beige, 15, 30f);
            }
        }

        private void AddLabelRow(string text, Color color, int size, float height)
        {
            var row = Row(_list, height);
            _rows.Add(row.gameObject);
            var label = ValheimUi.CreateLabel(row, text, size, color, TextAlignmentOptions.Left);
            Flexible(label.gameObject);
        }

        private void Buy(SeasonHeraldNpc herald)
        {
            if (Season.Balance >= 0 && Season.Balance < Season.Price)
            {
                Say($"Você tem {Season.Balance} Deadcoins; o passe custa {Season.Price}.");
                return;
            }
            if (!Season.TryBeginPurchase()) return;
            if (herald == null || !herald.RequestBuy(Season.Price)) Say("Não foi possível falar com o Arauto agora.");
        }
    }
}
