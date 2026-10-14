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
    /// The Caçador de Recompensas' board: who is wanted and for how much, a form to put a
    /// price on someone's head, and the player's own head with the buy-back price.
    ///
    /// Everything shown is Deadheim's server's: the board arrives in answer to a request sent
    /// when the tab opens and every few seconds after, the player's own pot with the PvP state
    /// Deadheim already syncs. The buttons only ask (DeadheimPvpApi); coins leave the bag on
    /// asking, and Deadheim gives back whatever its server did not keep.
    /// </summary>
    internal sealed class BountyView : NpcViewBase
    {
        private const float PollSeconds = 4f;
        private const string Grey = "#9a9188";
        private const string Gold = "#ffd700";
        private const string Red = "#ee6b57";

        private TextMeshProUGUI _head;
        private Button _pay;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private TMP_InputField _target;
        private TMP_InputField _amount;
        private TextMeshProUGUI _rules;
        private TextMeshProUGUI _closed;
        private GameObject _board;
        private GameObject _form;

        private float _nextPoll;
        private int _seenBoard = -1;
        private int _seenMessage;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            // --- the player's own head, across the top ---
            var top = ValheimUi.CreateRect("Head", Root);
            ValheimUi.Anchor(top, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -44f), Vector2.zero);
            _head = ValheimUi.CreateLabel(top, "", 16, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.Anchor((RectTransform)_head.transform, Vector2.zero, Vector2.one, new Vector2(6f, 0f), new Vector2(-190f, 0f));
            _pay = ValheimUi.CreateButton(top, "Pagar a minha", 180f, 38f, 14);
            ValheimUi.Anchor((RectTransform)_pay.transform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(-180f, -19f), new Vector2(0f, 19f));
            _pay.onClick.AddListener(PayOwn);

            // --- the board, on the left ---
            var board = ValheimUi.CreateInlay(Root, "Board");
            ValheimUi.Anchor(board, Vector2.zero, new Vector2(0.6f, 1f), Vector2.zero, new Vector2(-6f, -52f));
            _board = board.gameObject;
            var boardHeader = ValheimUi.CreateLabel(board, "Procurados", 18, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)boardHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));
            var area = ValheimUi.CreateRect("Area", board);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -40f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);
            _empty = ValheimUi.CreateLabel(area, "Nenhuma cabeça a prêmio. Por enquanto.", 15, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            // --- the form, on the right ---
            var form = ValheimUi.CreateInlay(Root, "Form");
            ValheimUi.Anchor(form, new Vector2(0.6f, 0f), Vector2.one, new Vector2(6f, 0f), new Vector2(0f, -52f));
            _form = form.gameObject;
            var formHeader = ValheimUi.CreateLabel(form, "Colocar a prêmio", 18, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)formHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            var column = ValheimUi.CreateRect("Column", form);
            ValheimUi.Anchor(column, Vector2.zero, Vector2.one, new Vector2(14f, 10f), new Vector2(-14f, -44f));
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            ValheimUi.SetHeight(Dim(column, "Jogador (nome exato):").gameObject, 20f);
            _target = ValheimUi.CreateInputField(column, "", 0f, 34f);
            ValheimUi.SetHeight(Dim(column, "Moedas:").gameObject, 20f);
            _amount = ValheimUi.CreateInputField(column, "", 0f, 34f);
            _amount.contentType = TMP_InputField.ContentType.IntegerNumber;
            var place = ValheimUi.CreateButton(column, "Colocar bounty", 0f, 40f, 15);
            Iconify(place, MarketplaceNpc.CoinPrefabName, 26f);
            place.onClick.AddListener(Place);
            _rules = ValheimUi.CreateLabel(column, "", 13, ValheimUi.Muted, TextAlignmentOptions.TopLeft);
            Flexible(_rules.gameObject).flexibleHeight = 1f;

            _closed = ValheimUi.CreateLabel(Root, "", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_closed.transform, Vector2.zero, Vector2.one, new Vector2(40f, 40f), new Vector2(-40f, -60f));

            _seenMessage = DeadheimPvpApi.BountyMessageRevision();
        }

        public override void Refresh()
        {
            string closed = !DeadheimPvpApi.IsAvailable ? DeadheimPvpApi.NotInstalled
                : !DeadheimPvpApi.Active() ? "O PvP está desligado neste servidor."
                : !DeadheimPvpApi.BountyEnabled() ? "A bounty está desligada neste servidor."
                : null;
            _closed.gameObject.SetActive(closed != null);
            _closed.text = closed ?? "";
            _board.SetActive(closed == null);
            _form.SetActive(closed == null);
            _head.transform.parent.gameObject.SetActive(closed == null);
            if (closed != null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                DeadheimPvpApi.RequestBountyBoard();
            }

            int message = DeadheimPvpApi.BountyMessageRevision();
            if (message != _seenMessage)
            {
                _seenMessage = message;
                Say(DeadheimPvpApi.LastBountyMessage());
                _nextPoll = 0f; // the board changed with it
            }

            RefreshHead();

            int revision = DeadheimPvpApi.BountyBoardRevision();
            if (revision != _seenBoard)
            {
                _seenBoard = revision;
                RebuildBoard();
            }

            int cap = DeadheimPvpApi.BountyDailyCap();
            _rules.text =
                $"Mínimo: <color={Gold}>{DeadheimPvpApi.BountyMinimum()}</color> moedas, tiradas da sua bolsa.\n" +
                $"Quem matar o alvo leva <color={Gold}>{DeadheimPvpApi.BountyKillerSharePercent():0}%</color>; o resto fica com a casa.\n" +
                $"O alvo é avisado e vira CAÇADO em {DeadheimPvpApi.FormatDuration(DeadheimPvpApi.BountyDelaySeconds())}: " +
                "aparece no mapa e perde a zona segura.\n" +
                (cap > 0 ? $"Teto por jogador: {cap} moedas por dia.\n" : "") +
                "Não vale em si mesmo, em PvE nem em quem acabou de sair de uma bounty.";
        }

        private void RefreshHead()
        {
            int coins = MarketplaceNpc.CoinsOf(Player);
            int pot = DeadheimPvpApi.LocalBountyPot();
            int cost = DeadheimPvpApi.LocalBuyoutCost();
            string head;
            if (pot > 0)
            {
                string state = DeadheimPvpApi.LocalHunted() ? $"<color={Red}>você está CAÇADO</color>"
                    : DeadheimPvpApi.LocalHuntPending() ? "você vira CAÇADO em breve"
                    : "";
                head = $"<color={Red}>Há {pot} moedas na sua cabeça</color>" + (state.Length > 0 ? " · " + state : "") +
                       (cost > 0 ? $" · encerrar custa <color={Gold}>{cost}</color>" : "");
            }
            else
            {
                double cooldown = DeadheimPvpApi.LocalBountyCooldownSeconds();
                head = cooldown > 0d
                    ? $"Sua cabeça está limpa. Ninguém pode pôr bounty em você por {DeadheimPvpApi.FormatDuration(cooldown)}."
                    : "Sua cabeça está limpa.";
            }
            _head.text = $"<color={Grey}>Na sua bolsa:</color> {coins} moedas   " + head;
            _pay.gameObject.SetActive(pot > 0 && cost > 0);
            _pay.interactable = coins >= cost;
        }

        private void RebuildBoard()
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var board = DeadheimPvpApi.BountyBoard();
            _empty.gameObject.SetActive(board.Length == 0);
            foreach (var entry in board)
            {
                if (entry == null || entry.Length < 5) continue;
                string target = entry[0];
                bool online = entry[3] == "1";
                bool hunted = entry[4] == "1";

                var row = Row(_list, 52f);
                _rows.Add(row.gameObject);
                var label = ValheimUi.CreateLabel(row,
                    $"<b>{target}</b>   <color={Gold}>{entry[1]}</color> moedas para quem matar\n" +
                    $"<size=13><color={(hunted ? Red : Grey)}>{entry[2]}</color>" +
                    $"<color={Grey}>{(online ? " · online" : " · offline, tempo parado")}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                var raise = ValheimUi.CreateButton(row, "Aumentar", 110f, 40f, 14);
                ValheimUi.SetWidth(raise.gameObject, 110f);
                raise.onClick.AddListener(() =>
                {
                    _target.text = target;
                    Say($"Escreva quantas moedas a mais na cabeça de {target}.");
                });
            }
        }

        private void Place()
        {
            string target = (_target.text ?? "").Trim();
            if (!int.TryParse((_amount.text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            {
                Say("Escreva quantas moedas.");
                return;
            }
            string refusal = DeadheimPvpApi.PlaceBounty(target, amount);
            if (refusal != null) { Say(refusal); return; }
            Say($"Colocando {amount} moedas na cabeça de {target}...");
            _amount.text = "";
        }

        private void PayOwn()
        {
            int cost = DeadheimPvpApi.LocalBuyoutCost();
            string refusal = DeadheimPvpApi.PayOwnBounty();
            Say(refusal ?? $"Pagando {cost} moedas para encerrar a sua bounty...");
        }
    }
}
