using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using NpcValheim.Arena;
using NpcValheim.Npc;
using Object = UnityEngine.Object;

namespace NpcValheim.UI
{
    /// <summary>
    /// What every arena page shares: a list rebuilt whenever the server sends a new state,
    /// a few labels that tick on their own between states (countdowns), and the server's last
    /// word on the status line. Forms with text fields are built once, outside the list, so
    /// a state arriving while someone types does not take the field away from them.
    /// </summary>
    internal abstract class ArenaViewBase : NpcViewBase
    {
        internal static readonly Color GoldColor = new Color(1f, 0.82f, 0.25f, 1f);
        internal static readonly Color GreenColor = new Color(0.45f, 0.86f, 0.42f, 1f);

        protected ArenaNpcBase ArenaNpc => Npc as ArenaNpcBase;
        protected RectTransform List;
        protected int Size;

        private readonly List<GameObject> _rows = new List<GameObject>();
        private readonly List<(TextMeshProUGUI Label, Func<string> Text)> _live = new List<(TextMeshProUGUI, Func<string>)>();
        private int _seenMessage;
        private string _signature;
        private float _nextData;
        private float _nextLive;

        protected void CreateList(Vector2 offsetMin, Vector2 offsetMax)
        {
            var pane = ValheimUi.CreateInlay(Root, "Pane");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, offsetMin, offsetMax);
            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Stretch(area, 4f, 4f);
            List = ValheimUi.CreateScrollList(area, spacing: 4f);
            _seenMessage = ArenaClient.MessageRevision;
            if (Size == 0) Size = Sizes().FirstOrDefault();
            ArenaClient.RequestData();
        }

        protected static bool SoloOn => ArenaConfig.Solo?.Value ?? true;

        protected static bool IsSolo(int size) => SoloOn && size == 1;

        protected static List<int> Brackets()
        {
            var list = ArenaSettingsParser.ParseBrackets(ArenaConfig.Brackets?.Value ?? "2,3,5", null);
            if (list.Count == 0 && !SoloOn) list = new List<int> { 2, 3, 5 };
            return ArenaSettingsParser.WithSolo(list, SoloOn);
        }

        /// <summary>The brackets this page offers; the Organizer leaves out the solo 1v1.</summary>
        protected virtual List<int> Sizes() => Brackets();

        protected virtual string Signature() =>
            $"{ArenaClient.Revision}:{ArenaClient.ResultRevision}:{Size}:{ArenaConfig.Brackets?.Value}:{SoloOn}";

        protected abstract void Rebuild();

        /// <summary>Called every few seconds while the page is open.</summary>
        protected virtual void Poll() { }

        public override void Refresh()
        {
            if (Time.unscaledTime >= _nextData)
            {
                _nextData = Time.unscaledTime + 5f;
                ArenaClient.RequestData();
                Poll();
            }

            if (ArenaClient.MessageRevision != _seenMessage)
            {
                _seenMessage = ArenaClient.MessageRevision;
                Say(ArenaClient.LastMessage);
            }

            string signature = Signature();
            if (signature != _signature)
            {
                _signature = signature;
                Clear();
                Rebuild();
            }

            if (Time.unscaledTime >= _nextLive)
            {
                _nextLive = Time.unscaledTime + 0.5f;
                foreach (var (label, text) in _live)
                    if (label != null) label.text = text();
            }
        }

        /// <summary>Forces the next Refresh to rebuild (a local choice changed what to show).</summary>
        protected void Invalidate() => _signature = null;

        private void Clear()
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();
            _live.Clear();
        }

        // ------------------------------------------------------------------ list builders

        protected TextMeshProUGUI Line(string text, int size = 15, Color? color = null, float height = 24f,
            bool display = false, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var row = Row(List, height);
            _rows.Add(row.gameObject);
            var label = ValheimUi.CreateLabel(row, text, size, color ?? ValheimUi.Beige, align, display);
            Flexible(label.gameObject);
            return label;
        }

        protected TextMeshProUGUI LiveLine(Func<string> text, int size = 15, Color? color = null, float height = 24f)
        {
            var label = Line(text(), size, color, height);
            _live.Add((label, text));
            return label;
        }

        protected void Heading(string text) => Line(text, 18, ValheimUi.Orange, 30f, display: true);

        protected void Gap(float height = 8f)
        {
            var row = Row(List, height);
            _rows.Add(row.gameObject);
        }

        protected RectTransform ButtonRow(float height = 38f)
        {
            var row = Row(List, height);
            _rows.Add(row.gameObject);
            return row;
        }

        protected static Button Btn(RectTransform row, string text, float width, UnityAction onClick, int fontSize = 14)
        {
            var button = ValheimUi.CreateButton(row, text, width, 34f, fontSize);
            button.onClick.AddListener(onClick);
            return button;
        }

        protected static TextMeshProUGUI Cell(RectTransform row, string text, int size = 15, Color? color = null,
            float width = 0f, TextAlignmentOptions align = TextAlignmentOptions.Left)
        {
            var label = ValheimUi.CreateLabel(row, text, size, color ?? ValheimUi.Beige, align);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;
            if (width > 0f) ValheimUi.SetWidth(label.gameObject, width);
            else Flexible(label.gameObject);
            return label;
        }

        /// <summary>The bracket buttons; the chosen one is lit.</summary>
        protected void BracketSelector(string prefix = "")
        {
            var row = ButtonRow();
            if (!string.IsNullOrEmpty(prefix)) Cell(row, prefix, 15, ValheimUi.Muted, 140f);
            foreach (int size in Sizes())
            {
                int chosen = size;
                var button = Btn(row, ArenaRules.BracketName(size), 90f, () =>
                {
                    Size = chosen;
                    Invalidate();
                    OnSizeChanged();
                });
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = size == Size ? ValheimUi.Yellow : ValheimUi.Orange;
                button.image.color = size == Size ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }
            var fill = ValheimUi.CreateRect("Fill", row);
            Flexible(fill.gameObject);
        }

        protected virtual void OnSizeChanged() { }

        /// <summary>A request through this page's NPC when there is one, through the global
        /// arena channel otherwise (the panel behind the key).</summary>
        protected void Ask(string action, string payload = "")
        {
            bool sent = ArenaNpc != null ? ArenaNpc.RequestArena(action, payload) : ArenaNet.Request(action, payload);
            if (!sent) Say("O pedido não chegou ao servidor.");
        }

        protected static string Clock(int seconds)
        {
            seconds = Mathf.Max(0, seconds);
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        protected static string Signed(int value) => value > 0 ? "+" + value : value.ToString(CultureInfo.InvariantCulture);

        protected static string SideText(int side) =>
            side == ArenaSide.Gold ? "<color=#ffd140>Ouro</color>" : side == ArenaSide.Green ? "<color=#73db6b>Verde</color>" : "-";

        protected static string Kind(bool rated) => rated ? "Ranqueada" : "Escaramuça";

        /// <summary>My personal rating in a team, from the snapshot.</summary>
        protected static int PersonalIn(ArenaTeamView team)
        {
            var me = team?.Members.FirstOrDefault(m => m.PlayerId == ArenaClient.Snapshot.PlayerId);
            return me?.Personal ?? 0;
        }

        // ------------------------------------------------------- shared blocks

        /// <summary>Invitations, the queue, the match and the last scoreboard -- what the
        /// panel's front page shows, and what the Battlemaster shows under its queue buttons.</summary>
        protected void StatusBlocks(bool withResult)
        {
            var s = ArenaClient.Snapshot;
            var match = s.Match;
            if (match != null)
            {
                Heading($"Partida: {match.Map} · {ArenaRules.BracketName(match.Size)} {Kind(match.Rated)}");
                switch (match.Phase)
                {
                    case "invited":
                        LiveLine(() => $"A arena está pronta! Você é do time {SideText(match.Side)}. Expira em {ArenaClient.SecondsLeft()}s.",
                            16, ValheimUi.Yellow);
                        var row = ButtonRow();
                        Btn(row, "Entrar", 160f, () =>
                        {
                            ArenaClient.RequestEnter();
                            Say("Entrando na arena...");
                        }, 16);
                        Cell(row, "Na ranqueada, deixar expirar conta como derrota.", 13, ValheimUi.Muted);
                        break;
                    case "prep":
                        LiveLine(() => $"Preparação: os portões abrem em {Clock(ArenaClient.SecondsLeft())}. Você é do time {SideText(match.Side)}.", 16, ValheimUi.Yellow);
                        Score(match);
                        LeaveButton("Sair da arena");
                        break;
                    case "live":
                        Score(match);
                        LiveLine(() => $"Tempo até o empate: {Clock(ArenaClient.SecondsLeft())}" +
                                       (match.Mine == "ko" ? "   ·   <color=#ee6b57>você foi derrotado</color>" : ""), 15);
                        LeaveButton(match.Rated ? "Abandonar (conta como derrota)" : "Abandonar a partida");
                        break;
                    default:
                        LiveLine(() => $"Partida encerrada. Volta para onde estava em {Clock(ArenaClient.SecondsLeft())}.", 15);
                        LeaveButton("Sair agora");
                        break;
                }
                Gap();
            }

            var queue = s.Queue;
            if (queue != null)
            {
                Heading("Fila");
                if (queue.Status == "confirm")
                    Line($"Confirmando o grupo para {ArenaRules.BracketName(queue.Size)} {Kind(queue.Rated)}...", 15, ValheimUi.Yellow);
                else
                {
                    int since = queue.Waited;
                    float at = ArenaClient.SnapshotAt;
                    LiveLine(() => $"Na fila: {ArenaRules.BracketName(queue.Size)} {Kind(queue.Rated)} · esperando {Clock(since + Mathf.FloorToInt(Time.realtimeSinceStartup - at))}", 15, ValheimUi.Yellow);
                }
                var row = ButtonRow();
                Btn(row, "Sair da fila", 160f, () => Ask(ArenaWire.ActQueueLeave));
                Gap();
            }

            if (s.SignRequests.Count > 0 || s.Invite != null)
            {
                Heading("Pedidos para você");
                foreach (var req in s.SignRequests)
                {
                    var r = req;
                    var row = ButtonRow(40f);
                    Cell(row, $"{r.OwnerName} pede a sua assinatura na carta \"{r.TeamName}\" ({ArenaRules.BracketName(r.Size)})", 15);
                    Btn(row, "Assinar", 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n1"));
                    Btn(row, "Recusar", 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n0"));
                }
                if (s.Invite != null)
                {
                    var inv = s.Invite;
                    var row = ButtonRow(40f);
                    Cell(row, $"{inv.From} convidou você para o time \"{inv.TeamName}\" ({ArenaRules.BracketName(inv.Size)})", 15);
                    Btn(row, "Aceitar", 110f, () => Ask(ArenaWire.ActTeamAnswer, $"{inv.TeamId}\n1"));
                    Btn(row, "Recusar", 110f, () => Ask(ArenaWire.ActTeamAnswer, $"{inv.TeamId}\n0"));
                }
                Gap();
            }

            if (withResult && ArenaClient.LastResult != null) ResultBlock(ArenaClient.LastResult);

            if (ArenaClient.FeedLines.Count > 0 && match != null)
            {
                Heading("Na arena");
                foreach (var line in ArenaClient.FeedLines) Line(line, 14, ValheimUi.Muted, 20f);
            }
        }

        private void Score(ArenaMatchView match)
        {
            Line($"<color=#ffd140>{match.GoldName}</color>  {match.GoldAlive}/{match.GoldTotal} de pé     ×     " +
                 $"<color=#73db6b>{match.GreenName}</color>  {match.GreenAlive}/{match.GreenTotal} de pé", 17, ValheimUi.Beige, 28f);
        }

        private void LeaveButton(string text)
        {
            var row = ButtonRow();
            Btn(row, text, 280f, () => ArenaClient.RequestLeave());
        }

        /// <summary>The end-of-match scoreboard, as WoW showed it: each team with its rating and
        /// the change, then every player.</summary>
        protected void ResultBlock(ArenaResult r)
        {
            string outcome = r.Winner == ArenaSide.None ? "Empate: o tempo acabou"
                : $"Vitória do time {SideText(r.Winner)}";
            Heading($"Resultado · {r.Map} · {ArenaRules.BracketName(r.Size)} {Kind(r.Rated)}");
            Line($"{outcome}  ·  {Clock(r.Duration)}", 17, ValheimUi.Yellow, 28f);
            foreach (var side in r.Sides)
            {
                string rating = r.Rated
                    ? $"   rating {side.RatingBefore} → {side.RatingBefore + side.RatingChange} ({Signed(side.RatingChange)})   MMR {side.Mmr} ({Signed(side.MmrChange)})"
                    : "";
                Line($"{SideText(side.Side)}: {side.TeamName}{rating}", 15, ValheimUi.Beige, 26f);
            }
            var header = ButtonRow(24f);
            Cell(header, "Jogador", 13, ValheimUi.Muted);
            Cell(header, "Time", 13, ValheimUi.Muted, 80f);
            Cell(header, "Abates", 13, ValheimUi.Muted, 70f);
            Cell(header, "Situação", 13, ValheimUi.Muted, 110f);
            if (r.Rated) Cell(header, "Pessoal", 13, ValheimUi.Muted, 130f);
            foreach (var p in r.Players.OrderBy(p => p.Side).ThenByDescending(p => p.Kos))
            {
                var row = ButtonRow(24f);
                Cell(row, p.Name, 15);
                Cell(row, SideText(p.Side), 15, null, 80f);
                Cell(row, p.Kos.ToString(CultureInfo.InvariantCulture), 15, null, 70f);
                Cell(row, p.Status, 15, null, 110f);
                if (r.Rated)
                    Cell(row, p.PersonalBefore >= 0 ? $"{p.PersonalBefore + p.PersonalChange} ({Signed(p.PersonalChange)})" : "-", 15, null, 130f);
            }
            Gap();
        }
    }

    /// <summary>The front page of the arena panel (key H): what needs an answer now.</summary>
    internal sealed class ArenaStatusView : ArenaViewBase
    {
        protected override void OnBuild() => CreateList(Vector2.zero, Vector2.zero);

        protected override void Rebuild()
        {
            var s = ArenaClient.Snapshot;
            Line($"<color=#9a9188>Pontos de Arena:</color> {s.Points}", 17, ValheimUi.Yellow, 28f);
            Gap();
            StatusBlocks(withResult: true);
            if (s.Match == null && s.Queue == null && s.SignRequests.Count == 0 && s.Invite == null && ArenaClient.LastResult == null)
            {
                Line("Nada pendente.", 15, ValheimUi.Muted);
                Line("Para jogar: compre a carta do time com o Organizador de Arena, junte o grupo (Groups) e entre na fila com o Mestre da Arena.", 14, ValheimUi.Muted, 44f);
                if (SoloOn)
                    Line("No 1v1 não precisa de time: fale com o Mestre da Arena e entre na fila sozinho.", 14, ValheimUi.Muted, 22f);
            }
        }
    }

    /// <summary>The team frame: each of my teams, its numbers and its members, and what a
    /// captain can do about them (WoW's /teaminvite, /teamremove, /teamcaptain, /teamquit,
    /// /teamdisband).</summary>
    internal sealed class ArenaTeamsView : ArenaViewBase
    {
        private TMP_InputField _invite;

        protected override void OnBuild()
        {
            var form = ValheimUi.CreateRect("Invite", Root);
            ValheimUi.Anchor(form, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -40f), Vector2.zero);
            var layout = form.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;

            var hint = ValheimUi.CreateLabel(form, "Convidar (capitão):", 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(hint.gameObject, 150f);
            _invite = ValheimUi.CreateInputField(form, "", 0f, 34f);
            Flexible(_invite.gameObject);
            var send = ValheimUi.CreateButton(form, "Convidar", 130f, 34f, 14);
            send.onClick.AddListener(() =>
            {
                var name = (_invite.text ?? "").Trim();
                if (name.Length == 0) { Say("Escreva o nome do jogador."); return; }
                Ask(ArenaWire.ActTeamInvite, $"{Size}\n{name}");
                Say($"Convidando {name} para o time {ArenaRules.BracketName(Size)}...");
            });

            CreateList(Vector2.zero, new Vector2(0f, -46f));
        }

        protected override void Rebuild()
        {
            BracketSelector("Time:");
            var s = ArenaClient.Snapshot;
            var team = s.TeamOf(Size);
            bool solo = IsSolo(Size);
            if (team == null)
            {
                if (solo)
                {
                    Line("Você ainda não jogou a 1v1 ranqueada.", 16, ValheimUi.Muted, 28f);
                    Line("No 1v1 não tem time para montar: entre na fila sozinho com o Mestre da Arena e o seu rating começa ali.", 14, ValheimUi.Muted, 40f);
                    return;
                }
                Line($"Você não tem time {ArenaRules.BracketName(Size)}.", 16, ValheimUi.Muted, 28f);
                Line("Compre a carta do time com o Organizador de Arena e colete as assinaturas.", 14, ValheimUi.Muted);
                return;
            }

            bool captain = team.CaptainId == s.PlayerId && !solo;
            Heading($"{team.Name} · {ArenaRules.BracketName(team.Size)}");
            Line($"Rating {team.Rating}   ·   Posição #{team.Rank}   ·   MMR {team.Mmr}", 16, ValheimUi.Yellow, 26f);
            Line($"Semana: {team.WeekGames} jogos, {team.WeekWins} vitórias   ·   Temporada: {team.SeasonGames} jogos, {team.SeasonWins} vitórias", 14, ValheimUi.Beige);
            Gap();

            var header = ButtonRow(24f);
            Cell(header, "Membro", 13, ValheimUi.Muted);
            Cell(header, "Pessoal", 13, ValheimUi.Muted, 80f);
            Cell(header, "Semana", 13, ValheimUi.Muted, 90f);
            Cell(header, "Temporada", 13, ValheimUi.Muted, 100f);
            if (captain) Cell(header, "", 13, null, 236f);

            foreach (var m in team.Members.OrderByDescending(m => m.PlayerId == team.CaptainId).ThenByDescending(m => m.Personal))
            {
                var member = m;
                var row = ButtonRow(36f);
                string mark = member.PlayerId == team.CaptainId ? " <color=#ffd140>(capitão)</color>" : "";
                string online = member.Online ? "<color=#73db6b>●</color> " : "<color=#6b6560>●</color> ";
                Cell(row, online + member.Name + mark, 15);
                Cell(row, member.Personal.ToString(CultureInfo.InvariantCulture), 15, null, 80f);
                Cell(row, $"{member.WeekGames}/{member.WeekWins}", 15, null, 90f);
                Cell(row, $"{member.SeasonGames}/{member.SeasonWins}", 15, null, 100f);
                if (captain)
                {
                    if (member.PlayerId != team.CaptainId)
                    {
                        Btn(row, "Capitão", 110f, () => Ask(ArenaWire.ActTeamCaptain, $"{Size}\n{member.PlayerId}"));
                        Btn(row, "Remover", 110f, () => Ask(ArenaWire.ActTeamKick, $"{Size}\n{member.PlayerId}"));
                    }
                    else
                    {
                        var pad = ValheimUi.CreateRect("Pad", row);
                        ValheimUi.SetWidth(pad.gameObject, 236f);
                    }
                }
            }
            Gap();
            Line($"Para receber Pontos de Arena: o time joga {ArenaConfig.GamesPerWeek?.Value ?? 10}+ partidas na semana e você joga " +
                 $"{ArenaConfig.ParticipationPercent?.Value ?? 30}% delas.", 13, ValheimUi.Muted, 22f);

            if (solo) return;
            var actions = ButtonRow();
            if (captain)
            {
                Btn(actions, "Desfazer o time", 200f, () => Ask(ArenaWire.ActTeamDisband, Size.ToString()));
                if (team.Members.Count > 1)
                    Cell(actions, "Para sair, passe a capitania antes.", 13, ValheimUi.Muted);
            }
            else
            {
                Btn(actions, "Sair do time", 200f, () => Ask(ArenaWire.ActTeamLeave, Size.ToString()));
            }
        }
    }

    /// <summary>The Organizer: buy a charter, get it signed, turn it in.</summary>
    internal sealed class ArenaCharterView : ArenaViewBase
    {
        private TMP_InputField _name;
        private TMP_InputField _signer;

        protected override void OnBuild()
        {
            _name = FormRow("Nome do time:", -40f, "Comprar carta", () =>
            {
                int cost = ArenaConfig.CostOf(Size);
                var name = (_name.text ?? "").Trim();
                if (name.Length < 2) { Say("Escreva o nome do time."); return; }
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (!MarketplaceNpc.TryPay(player, cost))
                {
                    Say($"A carta {ArenaRules.BracketName(Size)} custa {cost} moedas.");
                    return;
                }
                // Paid first, from the bag, as at the merchant: the server keeps what the
                // charter costs and sends back the rest, or all of it if it refuses.
                Ask(ArenaWire.ActCharterBuy, $"{Size}\n{name}\n{cost}");
                Say("Comprando a carta...");
            });
            _signer = FormRow("Pedir assinatura a:", -84f, "Pedir", () =>
            {
                var name = (_signer.text ?? "").Trim();
                if (name.Length == 0) { Say("Escreva o nome do jogador."); return; }
                Ask(ArenaWire.ActCharterOffer, $"{Size}\n{name}");
            });
            CreateList(Vector2.zero, new Vector2(0f, -90f));
        }

        protected override List<int> Sizes()
        {
            var sizes = Brackets().Where(size => !IsSolo(size)).ToList();
            return sizes.Count > 0 ? sizes : Brackets();
        }

        private TMP_InputField FormRow(string caption, float top, string button, UnityAction onClick)
        {
            var form = ValheimUi.CreateRect("Form", Root);
            ValheimUi.Anchor(form, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, top), new Vector2(0f, top + 40f));
            var layout = form.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            var hint = ValheimUi.CreateLabel(form, caption, 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(hint.gameObject, 160f);
            var field = ValheimUi.CreateInputField(form, "", 0f, 34f);
            Flexible(field.gameObject);
            var go = ValheimUi.CreateButton(form, button, 150f, 34f, 14);
            go.onClick.AddListener(onClick);
            return field;
        }

        protected override void Rebuild()
        {
            BracketSelector("Carta:");
            if (SoloOn) Line("O 1v1 não precisa de carta: entre na fila sozinho com o Mestre da Arena.", 13, ValheimUi.Muted, 22f);
            var s = ArenaClient.Snapshot;
            int cost = ArenaConfig.CostOf(Size);
            int required = ArenaRules.RequiredSignatures(Size, ArenaConfig.SignaturesRequired?.Value ?? -1);
            var team = s.TeamOf(Size);
            var charter = s.CharterOf(Size);

            if (team != null)
            {
                Line($"Você já está no time \"{team.Name}\" ({ArenaRules.BracketName(Size)}).", 16, ValheimUi.Beige, 28f);
                Line("Um jogador tem no máximo um time de cada tamanho.", 14, ValheimUi.Muted);
            }
            else if (charter != null)
            {
                Heading($"Carta \"{charter.Name}\" · {ArenaRules.BracketName(Size)}");
                Line($"Assinaturas: {charter.Signatures}/{charter.Required}" +
                     (string.IsNullOrEmpty(charter.Signers) ? "" : $"  ({charter.Signers})"), 16,
                    charter.Signatures >= charter.Required ? ValheimUi.Yellow : ValheimUi.Beige, 26f);
                Line("Peça a assinatura acima: quem assina vira membro fundador do time.", 14, ValheimUi.Muted);
                var row = ButtonRow();
                var register = Btn(row, "Registrar o time", 200f, () => Ask(ArenaWire.ActCharterTurnIn, Size.ToString()));
                register.interactable = charter.Signatures >= charter.Required;
                Btn(row, "Rasgar a carta", 170f, () => Ask(ArenaWire.ActCharterAbandon, Size.ToString()));
            }
            else
            {
                Heading($"Carta de time {ArenaRules.BracketName(Size)}");
                Line($"Custa {cost} moedas. Escreva o nome acima e clique Comprar carta.", 15, ValheimUi.Beige);
                Line($"Depois colete {required} assinatura(s) de outros jogadores e volte para registrar o time. " +
                     $"O time aceita até {ArenaRules.MaxMembers(Size)} membros; você é o capitão.", 14, ValheimUi.Muted, 40f);
            }

            if (s.SignRequests.Count > 0)
            {
                Gap();
                Heading("Pedem a sua assinatura");
                foreach (var req in s.SignRequests)
                {
                    var r = req;
                    var row = ButtonRow(40f);
                    Cell(row, $"{r.OwnerName}: \"{r.TeamName}\" ({ArenaRules.BracketName(r.Size)})", 15);
                    Btn(row, "Assinar", 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n1"));
                    Btn(row, "Recusar", 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n0"));
                }
            }
        }
    }

    /// <summary>The Battlemaster: join the queue, rated with the group or a skirmish.</summary>
    internal sealed class ArenaQueueView : ArenaViewBase
    {
        protected override void OnBuild() => CreateList(Vector2.zero, Vector2.zero);

        private string _group = "";
        private float _nextGroup;

        protected override string Signature()
        {
            if (Time.unscaledTime >= _nextGroup)
            {
                _nextGroup = Time.unscaledTime + 1f;
                _group = string.Join(",", ArenaGroups.Members().Select(m => m.Peer)) + ":" + ArenaGroups.IsLeader();
            }
            return base.Signature() + ":" + _group;
        }

        protected override void Rebuild()
        {
            BracketSelector("Arena:");
            var s = ArenaClient.Snapshot;
            var team = s.TeamOf(Size);
            var group = ArenaGroups.Members();
            bool grouped = group.Count > 1;

            bool solo = IsSolo(Size);
            Heading($"Arena {ArenaRules.BracketName(Size)}");
            Line(solo
                    ? team != null ? $"Seu rating 1v1: {team.Rating}  ·  posição #{team.Rank}" : "1v1: você entra sozinho, sem time. O rating começa na primeira ranqueada."
                    : team != null
                        ? $"Seu time: \"{team.Name}\"  ·  rating {team.Rating}  ·  seu pessoal {PersonalIn(team)}"
                        : $"Você não tem time {ArenaRules.BracketName(Size)} (Organizador de Arena).", 15, ValheimUi.Beige);
            Line(!ArenaGroups.Installed
                    ? "Sem o mod Groups: só dá para entrar sozinho."
                    : grouped
                        ? $"Grupo ({group.Count}): {string.Join(", ", group.Select(m => m.Name))}" + (ArenaGroups.IsLeader() ? "" : "  ·  só o líder entra na fila")
                        : "Sem grupo: forme o grupo (Groups) para a ranqueada.",
                14, ValheimUi.Muted);

            bool busy = s.Queue != null || s.Match != null;
            var row = ButtonRow(40f);
            var rated = Btn(row, "Entrar: Ranqueada", 210f, () => Join(true), 15);
            rated.interactable = !busy && (team != null || solo) && !(solo && grouped);
            var skirmish = Btn(row, "Entrar: Escaramuça", 210f, () => Join(false), 15);
            skirmish.interactable = !busy && (ArenaConfig.Skirmish?.Value ?? true);
            Line(solo
                    ? "1v1: entre sem grupo. Ranqueada vale rating; escaramuça, não."
                    : $"Ranqueada: grupo de exatamente {Size} do mesmo time, rating em jogo. Escaramuça: sozinho ou em grupo, sem rating.",
                13, ValheimUi.Muted, 22f);
            Gap();
            StatusBlocks(withResult: true);
        }

        private void Join(bool rated)
        {
            var group = ArenaGroups.Members();
            if (group.Count > 1 && !ArenaGroups.IsLeader())
            {
                Say("Só o líder do grupo pode entrar na fila.");
                return;
            }
            long me = ZDOMan.GetSessionID();
            string peers = string.Join(",", group.Where(m => m.Peer != me).Select(m => m.Peer.ToString(CultureInfo.InvariantCulture)));
            Ask(ArenaWire.ActQueueJoin, $"{Size}\n{(rated ? 1 : 0)}\n{peers}");
            Say("Entrando na fila...");
        }
    }

    /// <summary>The ladder: every team of a bracket by rating.</summary>
    internal sealed class ArenaLadderView : ArenaViewBase
    {
        protected override void OnBuild()
        {
            CreateList(Vector2.zero, Vector2.zero);
            ArenaClient.RequestLadder(Size);
        }

        protected override string Signature() => base.Signature() + ":" + ArenaClient.LadderRevision;

        protected override void Poll() => ArenaClient.RequestLadder(Size);

        protected override void OnSizeChanged() => ArenaClient.RequestLadder(Size);

        protected override void Rebuild()
        {
            BracketSelector("Ranking:");
            if (!ArenaClient.Ladders.TryGetValue(Size, out var rows))
            {
                Line("Carregando...", 15, ValheimUi.Muted);
                return;
            }
            if (rows.Count == 0)
            {
                Line(IsSolo(Size) ? "Ninguém jogou a 1v1 ranqueada ainda." : $"Nenhum time {ArenaRules.BracketName(Size)} ainda.", 15, ValheimUi.Muted);
                return;
            }
            var header = ButtonRow(24f);
            Cell(header, "#", 13, ValheimUi.Muted, 50f);
            Cell(header, IsSolo(Size) ? "Jogador" : "Time", 13, ValheimUi.Muted);
            Cell(header, "Rating", 13, ValheimUi.Muted, 90f);
            Cell(header, "Jogos", 13, ValheimUi.Muted, 80f);
            Cell(header, "Vitórias", 13, ValheimUi.Muted, 90f);
            Cell(header, "%", 13, ValheimUi.Muted, 60f);
            foreach (var r in rows)
            {
                var row = ButtonRow(26f);
                var color = r.Mine ? ValheimUi.Yellow : ValheimUi.Beige;
                Cell(row, r.Rank.ToString(CultureInfo.InvariantCulture), 15, color, 50f);
                Cell(row, r.Name, 15, color);
                Cell(row, r.Rating.ToString(CultureInfo.InvariantCulture), 15, color, 90f);
                Cell(row, r.Games.ToString(CultureInfo.InvariantCulture), 15, color, 80f);
                Cell(row, r.Wins.ToString(CultureInfo.InvariantCulture), 15, color, 90f);
                Cell(row, r.Games > 0 ? $"{100 * r.Wins / r.Games}" : "-", 15, color, 60f);
            }
        }
    }

    /// <summary>The arena vendor: Arena Points for goods, some behind a rating.</summary>
    internal sealed class ArenaVendorView : ArenaViewBase
    {
        private float _sentAt = -10f;

        protected override void OnBuild() => CreateList(Vector2.zero, Vector2.zero);

        protected override string Signature() => base.Signature() + ":" + ArenaConfig.VendorItems?.Value;

        protected override void Rebuild()
        {
            var s = ArenaClient.Snapshot;
            Line($"<color=#9a9188>Seus Pontos de Arena:</color> {s.Points}", 18, ValheimUi.Yellow, 30f);
            Line("Os itens chegam pela Caixa Postal. A distribuição de pontos é semanal.", 13, ValheimUi.Muted, 22f);
            Gap();

            var offers = ArenaSettingsParser.ParseOffers(ArenaConfig.VendorItems?.Value, null)
                .Where(o => ItemSpawner.MaxDeliverableAmount(o.Prefab) > 0).ToList();
            if (offers.Count == 0)
            {
                Line("O Intendente não tem nada à venda.", 15, ValheimUi.Muted);
                return;
            }

            foreach (var offer in offers)
            {
                var o = offer;
                var row = ButtonRow(48f);
                ValheimUi.CreateItemIcon(row, o.Prefab, 38f);
                string requirement = "";
                bool meets = true;
                if (o.Rating > 0)
                {
                    int best = BestRating(s, o.Bracket);
                    meets = best >= o.Rating;
                    string where = o.Bracket > 1 ? $" ({ArenaRules.BracketName(o.Bracket)}+)" : "";
                    requirement = $"   <color={(meets ? "#73db6b" : "#ee6b57")}>requer rating {o.Rating}{where}</color>";
                }
                Cell(row, $"{o.Amount}x {ItemNames.Display(o.Prefab)}\n<size=13><color=#9a9188>{o.Points} pontos</color>{requirement}</size>", 15);
                var buy = Btn(row, "Comprar", 120f, () => Buy(o));
                buy.interactable = meets && s.Points >= o.Points;
            }
        }

        /// <summary>Same rule the server checks: the lower of personal and team rating, best
        /// across my teams of that bracket or larger.</summary>
        private static int BestRating(ArenaSnapshot s, int bracket)
        {
            int best = 0;
            foreach (var team in s.Teams.Where(t => t.Size >= bracket))
                best = Math.Max(best, ArenaRules.PurchaseRating(PersonalIn(team), team.Rating));
            return best;
        }

        private void Buy(ArenaOffer offer)
        {
            if (Time.unscaledTime - _sentAt < 2f) { Say("Aguarde a compra anterior."); return; }
            _sentAt = Time.unscaledTime;
            Ask(ArenaWire.ActVendorBuy, $"{offer.Prefab}\n{offer.Points}");
            Say($"Comprando {offer.Amount}x {ItemNames.Display(offer.Prefab)}...");
        }
    }

    /// <summary>
    /// Admin page on the Intendente: what it hands out for Arena Points. It edits the
    /// server's "Arena - Intendente" Items line, which is saved and sent to every client, so
    /// the page and a hand-edited cfg are the same list.
    /// </summary>
    internal sealed class ArenaVendorAdminView : ArenaViewBase
    {
        private OfferForm _form;

        protected override void OnBuild()
        {
            _form = OfferForm.Build(Root,
                new[] { ("Qtd", "1", 70f), ("Pontos", "100", 80f), ("Rating", "0", 80f), ("Time", "0", 56f) }, Save);
            CreateList(Vector2.zero, new Vector2(0f, -76f));
        }

        protected override string Signature() => base.Signature() + ":" + ArenaConfig.VendorItems?.Value;

        private void Save()
        {
            string prefab = OfferForm.Resolve(_form.Item.text);
            if (prefab == null) { Say("Escolha um item que o jogo conhece."); return; }
            int? amount = _form.Number(0), points = _form.Number(1), rating = _form.Number(2), bracket = _form.Number(3);
            if (amount == null || amount <= 0 || points == null || points <= 0)
            {
                Say("Quantidade e pontos precisam ser números maiores que zero.");
                return;
            }
            if (rating == null || rating < 0 || bracket == null || bracket < 0 || bracket > 10)
            {
                Say("Rating a partir de 0; Time de 0 a 10.");
                return;
            }
            var offer = new ArenaOffer { Prefab = prefab, Amount = amount.Value, Points = points.Value, Rating = rating.Value, Bracket = bracket.Value };
            Ask(ArenaWire.ActAdminOfferSet, ArenaSettingsParser.FormatOffer(offer));
            Say($"Salvando {offer.Amount}x {ItemNames.Display(prefab)}...");
        }

        protected override void Rebuild()
        {
            Line("Rating = mínimo (o menor entre pessoal e do time) para comprar, 0 = sem requisito. " +
                 "Time = tamanho mínimo do time em que esse rating vale (0 = qualquer). Os itens vão pela Caixa Postal.",
                13, ValheimUi.Muted, 40f);

            var problems = new List<string>();
            var offers = ArenaSettingsParser.ParseOffers(ArenaConfig.VendorItems?.Value, problems);
            Heading($"Recompensas do Intendente ({offers.Count})");
            if (offers.Count == 0) Line("O Intendente não vende nada.", 15, ValheimUi.Muted);
            foreach (var offer in offers)
            {
                var o = offer;
                var row = ButtonRow(48f);
                ValheimUi.CreateItemIcon(row, o.Prefab, 38f);
                int max = ItemSpawner.MaxDeliverableAmount(o.Prefab);
                string fault = max <= 0 ? "   <color=#ee6b57>não é item do jogo: fora da venda</color>"
                    : o.Amount > max ? $"   <color=#ee6b57>mais que uma entrega ({max}): fora da venda</color>" : "";
                string requirement = o.Rating > 0
                    ? $" · rating {o.Rating}{(o.Bracket > 1 ? $" ({ArenaRules.BracketName(o.Bracket)}+)" : "")}"
                    : "";
                Cell(row, $"{o.Amount}x {ItemNames.Display(o.Prefab)}  <size=12><color=#9a9188>{o.Prefab}</color></size>\n" +
                          $"<size=13><color=#9a9188>{o.Points} pontos{requirement}</color>{fault}</size>", 15);
                Btn(row, "Editar", 90f, () => _form.Load(o.Prefab, o.Amount, o.Points, o.Rating, o.Bracket));
                Btn(row, "Remover", 100f, () => Ask(ArenaWire.ActAdminOfferRemove, o.Prefab));
            }
            foreach (var problem in problems) Line(problem, 13, ValheimUi.Danger, 22f);
        }
    }

    /// <summary>
    /// Admin page on the Battlemaster: builds the arenas -- where each team starts, and
    /// where the defeated watch from -- from where the admin stands, marks the arena area
    /// (Deadheim's ArenaZones), shows which arenas the server would refuse and why, and runs
    /// the weekly payout on demand. Arenas are saved in the server's Maps line, the same
    /// one the cfg holds.
    /// </summary>
    internal sealed class ArenaAdminView : ArenaViewBase
    {
        private TMP_InputField _grantName;
        private TMP_InputField _grantPoints;
        private TMP_InputField _zoneName;
        private TMP_InputField _zoneRadius;
        private TMP_InputField _mapName;

        // The arena being put together: each start is taken where the admin stands when the
        // button is pressed, and nothing reaches the server until Salvar.
        private Vector3? _gold, _green, _spectator;
        private float _goldYaw, _greenYaw;

        protected override void OnBuild()
        {
            var form = ValheimUi.CreateRect("Grant", Root);
            ValheimUi.Anchor(form, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -40f), Vector2.zero);
            var layout = form.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 8f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            var hint = ValheimUi.CreateLabel(form, "Dar pontos a:", 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(hint.gameObject, 120f);
            _grantName = ValheimUi.CreateInputField(form, "", 0f, 34f);
            Flexible(_grantName.gameObject);
            _grantPoints = ValheimUi.CreateInputField(form, "100", 90f, 34f);
            var grant = ValheimUi.CreateButton(form, "Dar", 90f, 34f, 14);
            grant.onClick.AddListener(() =>
            {
                if (!int.TryParse(_grantPoints.text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int points))
                {
                    Say("Quantidade inválida.");
                    return;
                }
                Ask(ArenaWire.ActAdminGrant, $"{_grantName.text.Trim()}\n{points}");
            });

            var area = ValheimUi.CreateRect("Area", Root);
            ValheimUi.Anchor(area, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -84f), new Vector2(0f, -44f));
            var areaLayout = area.gameObject.AddComponent<HorizontalLayoutGroup>();
            areaLayout.spacing = 8f;
            areaLayout.childControlWidth = true;
            areaLayout.childControlHeight = true;
            areaLayout.childForceExpandWidth = false;
            var areaHint = ValheimUi.CreateLabel(area, "Área (nome, raio):", 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(areaHint.gameObject, 120f);
            _zoneName = ValheimUi.CreateInputField(area, "", 0f, 34f);
            Flexible(_zoneName.gameObject);
            _zoneRadius = ValheimUi.CreateInputField(area, "40", 90f, 34f);
            var mark = ValheimUi.CreateButton(area, "Marcar aqui", 130f, 34f, 14);
            mark.onClick.AddListener(MarkZone);

            var arena = ValheimUi.CreateRect("Arena", Root);
            ValheimUi.Anchor(arena, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -128f), new Vector2(0f, -88f));
            var arenaLayout = arena.gameObject.AddComponent<HorizontalLayoutGroup>();
            arenaLayout.spacing = 8f;
            arenaLayout.childControlWidth = true;
            arenaLayout.childControlHeight = true;
            arenaLayout.childForceExpandWidth = false;
            var arenaHint = ValheimUi.CreateLabel(arena, "Arena (nome):", 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(arenaHint.gameObject, 120f);
            _mapName = ValheimUi.CreateInputField(arena, "", 0f, 34f);
            Flexible(_mapName.gameObject);
            ValheimUi.CreateButton(arena, "Ouro aqui", 104f, 34f, 14).onClick.AddListener(() => Capture(ArenaSide.Gold));
            ValheimUi.CreateButton(arena, "Verde aqui", 104f, 34f, 14).onClick.AddListener(() => Capture(ArenaSide.Green));
            ValheimUi.CreateButton(arena, "Espectador", 104f, 34f, 14).onClick.AddListener(() => Capture(ArenaSide.None));
            ValheimUi.CreateButton(arena, "Salvar", 90f, 34f, 14).onClick.AddListener(SaveMap);
            CreateList(Vector2.zero, new Vector2(0f, -134f));
        }

        /// <summary>Takes one point of the arena being built where the admin stands, facing
        /// where they face (the team starts looking that way). None = the spectator spot.</summary>
        private void Capture(int side)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            var at = player.transform.position;
            float yaw = player.transform.eulerAngles.y;
            if (side == ArenaSide.Gold) { _gold = at; _goldYaw = yaw; }
            else if (side == ArenaSide.Green) { _green = at; _greenYaw = yaw; }
            else _spectator = at;
            string what = side == ArenaSide.None ? "Espectador" : "Início " + ArenaSide.Name(side);
            Say($"{what}: {ArenaConfig.Describe(at, yaw)} -- {ZoneHere(player)}. Clique Salvar quando terminar.");
        }

        private void SaveMap()
        {
            string name = ArenaSettingsParser.CleanMapName(_mapName.text);
            if (name.Length == 0) { Say("Dê um nome à arena."); return; }
            if (_gold == null || _green == null)
            {
                Say("Marque os dois inícios: fique em cada um e clique Ouro aqui / Verde aqui.");
                return;
            }
            if (Vector3.Distance(_gold.Value, _green.Value) > 300f)
            {
                Say("Os dois inícios estão a mais de 300 m um do outro.");
                return;
            }
            var map = new ArenaMapDef
            {
                Name = name, Gold = _gold.Value, GoldYaw = _goldYaw, Green = _green.Value, GreenYaw = _greenYaw,
                HasSpectator = _spectator != null, Spectator = _spectator ?? Vector3.zero,
            };
            Ask(ArenaWire.ActAdminMapSet, ArenaSettingsParser.FormatMap(map));
            Say($"Salvando a arena \"{name}\"...");
        }

        /// <summary>Puts an existing arena in the form, so one point can be retaken and saved
        /// again under the same name.</summary>
        private void EditMap(ArenaMapDef map)
        {
            _mapName.text = map.Name;
            _gold = map.Gold;
            _goldYaw = map.GoldYaw;
            _green = map.Green;
            _greenYaw = map.GreenYaw;
            _spectator = map.HasSpectator ? map.Spectator : (Vector3?)null;
            Say($"Editando \"{map.Name}\": retome o ponto que quiser e clique Salvar.");
        }

        private string Draft()
        {
            string Point(Vector3? p, float yaw) => p == null ? "<color=#ee6b57>falta</color>" : ArenaConfig.Describe(p.Value, yaw);
            return $"Ouro {Point(_gold, _goldYaw)} · Verde {Point(_green, _greenYaw)} · Espectador " +
                   (_spectator == null ? "<color=#9a9188>nenhum</color>"
                       : string.Format(CultureInfo.InvariantCulture, "{0:0.##},{1:0.##},{2:0.##}", _spectator.Value.x, _spectator.Value.y, _spectator.Value.z));
        }

        /// <summary>Centres the named area on where the admin stands; the server takes the
        /// position from its own view of the player.</summary>
        private void MarkZone()
        {
            string name = ArenaSettingsParser.CleanZoneName(_zoneName.text);
            if (name.Length == 0) { Say("Dê um nome à área."); return; }
            if (!float.TryParse(_zoneRadius.text, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius) ||
                radius < ArenaSettingsParser.MinZoneRadius || radius > ArenaSettingsParser.MaxZoneRadius)
            {
                Say($"Raio entre {ArenaSettingsParser.MinZoneRadius:0} e {ArenaSettingsParser.MaxZoneRadius:0} m.");
                return;
            }
            Ask(ArenaWire.ActAdminZoneSet, $"{name}\n{radius.ToString(CultureInfo.InvariantCulture)}");
        }

        protected override string Signature() =>
            base.Signature() + ":" + ArenaConfig.Maps?.Value + ":" + ArenaDeadheim.ZonesText;

        private static string Here(Player player) =>
            ArenaConfig.Describe(player.transform.position, player.transform.eulerAngles.y);

        private static string ZoneHere(Player player)
        {
            if (!ArenaDeadheim.Installed) return "Deadheim não instalado";
            var at = player.transform.position;
            return ArenaDeadheim.IsArena(at)
                ? $"dentro da ArenaZones \"{ArenaDeadheim.ArenaName(at)}\""
                : "<color=#ee6b57>fora de todas as ArenaZones do Deadheim</color>";
        }

        protected override void Rebuild()
        {
            var player = Player.m_localPlayer;
            Heading("Posição atual");
            if (player != null)
            {
                LiveLine(() => Player.m_localPlayer != null
                    ? $"{Here(Player.m_localPlayer)}   ·   {ZoneHere(Player.m_localPlayer)}"
                    : "", 15);
                var row = ButtonRow();
                Btn(row, "Copiar posição", 160f, () =>
                {
                    if (Player.m_localPlayer == null) return;
                    string here = Here(Player.m_localPlayer);
                    GUIUtility.systemCopyBuffer = here;
                    Say($"Copiado: {here}");
                });
            }
            Gap();

            Heading("Montar arena");
            Line("Escreva o nome, fique no início de cada time (olhando para onde ele deve olhar) e clique Ouro aqui / " +
                 "Verde aqui; Espectador (opcional) é onde fica quem foi derrotado. Salvar com o mesmo nome muda a arena. " +
                 "Os dois inícios precisam estar dentro de uma área (abaixo).", 13, ValheimUi.Muted, 40f);
            LiveLine(Draft, 14, ValheimUi.Beige);
            var draftRow = ButtonRow();
            Btn(draftRow, "Sem espectador", 150f, () => { _spectator = null; Say("Espectador retirado do rascunho."); });
            Btn(draftRow, "Limpar", 100f, () =>
            {
                _gold = _green = _spectator = null;
                _mapName.text = "";
                Say("Rascunho limpo.");
            });
            Gap();

            var maps = ArenaConfig.Current.Maps;
            Heading("Área da arena");
            if (!ArenaDeadheim.Installed)
                Line("Sem o Deadheim não há área: a partida vale num raio em volta dos inícios.", 14, ValheimUi.Muted);
            else
            {
                Line("Dentro da área o PvP vale sempre, sem perda de skill; quem sai dela durante a partida fugiu. " +
                     "Fique no centro, escreva nome e raio acima e clique Marcar aqui (mesmo nome = muda a área).",
                    13, ValheimUi.Muted, 40f);
                var zones = ArenaDeadheim.Zones();
                if (zones.Count == 0) Line("Nenhuma área marcada.", 15, ValheimUi.Danger);
                foreach (var zone in zones)
                {
                    var inside = maps.Where(m => m.Area != null && string.Equals(m.Area.Name, zone.Name, StringComparison.OrdinalIgnoreCase))
                        .Select(m => m.Name).ToList();
                    var row = ButtonRow();
                    Cell(row, string.Format(CultureInfo.InvariantCulture, "{0}: centro {1:0},{2:0} · raio {3:0} m · {4}",
                        zone.Name, zone.X, zone.Z, zone.Radius,
                        inside.Count > 0 ? "arenas: " + string.Join(", ", inside) : "nenhuma arena dentro"), 14);
                    var picked = zone;
                    Btn(row, "Editar", 80f, () =>
                    {
                        _zoneName.text = picked.Name;
                        _zoneRadius.text = picked.Radius.ToString("0", CultureInfo.InvariantCulture);
                    });
                    Btn(row, "Remover", 90f, () => Ask(ArenaWire.ActAdminZoneRemove, picked.Name));
                }
            }
            Gap();

            Heading("Arenas configuradas");
            if (maps.Count == 0) Line("Nenhuma. Sem arena, ninguém entra na fila.", 15, ValheimUi.Danger);
            foreach (var map in maps)
            {
                var m = map;
                var row = ButtonRow();
                Cell(row, $"{m.Name}: Ouro {ArenaConfig.Describe(m.Gold, m.GoldYaw)} · Verde {ArenaConfig.Describe(m.Green, m.GreenYaw)}" +
                          (m.HasSpectator ? " · espectador" : "") +
                          (m.Area != null ? $" · área {m.Area.Name}" : ""), 14);
                Btn(row, "Editar", 80f, () => EditMap(m));
                Btn(row, "Remover", 90f, () => Ask(ArenaWire.ActAdminMapRemove, m.Name));
            }
            // A map the server refuses (outside every area, say) is not in the list above, but
            // it is still in the cfg; name it so it can be removed from here too.
            var refused = ArenaSettingsParser.ParseMaps(ArenaConfig.Maps?.Value, null)
                .Where(m => maps.All(ok => !string.Equals(ok.Name, m.Name, StringComparison.OrdinalIgnoreCase))).ToList();
            foreach (var map in refused)
            {
                var m = map;
                var row = ButtonRow();
                Cell(row, $"{m.Name}: <color=#ee6b57>recusada pelo servidor (veja abaixo)</color>", 14);
                Btn(row, "Editar", 80f, () => EditMap(m));
                Btn(row, "Remover", 90f, () => Ask(ArenaWire.ActAdminMapRemove, m.Name));
            }
            var problems = ArenaConfig.Problems();
            foreach (var problem in problems) Line(problem, 13, ValheimUi.Danger, 22f);
            Gap();

            Heading("Pontos de Arena");
            var actions = ButtonRow();
            Btn(actions, "Distribuir os pontos da semana agora", 360f, () => Ask(ArenaWire.ActAdminDistribute));
            Line("Paga a semana como a distribuição automática (10 jogos, 30%) e zera os jogos da semana.", 13, ValheimUi.Muted, 22f);
        }
    }
}
