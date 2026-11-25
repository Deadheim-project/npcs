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

        /// <summary>1v1 is always on: the arena is 1v1, 2v2, 3v3, FFA and PvE.</summary>
        protected static bool SoloOn => true;

        protected static bool IsSolo(int size) => SoloOn && size == 1;

        protected static List<int> Brackets()
        {
            return new ArenaTuning().ActiveBrackets;
        }

        /// <summary>The brackets this page offers; the Organizer leaves out the solo 1v1.</summary>
        protected virtual List<int> Sizes() => Brackets();

        protected virtual string Signature() =>
            $"{ArenaClient.Revision}:{ArenaClient.ResultRevision}:{Size}";

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
            var row = Row(List, FitHeight(text, size, height));
            _rows.Add(row.gameObject);
            var label = ValheimUi.CreateLabel(row, text, size, color ?? ValheimUi.Beige, align, display);
            Flexible(label.gameObject);
            return label;
        }

        /// <summary>
        /// A line is as tall as its text needs. A fixed height let a long line wrap into the one
        /// below it (the English texts run longer than the Portuguese ones), which is what made
        /// the join screen look piled up. The height asked for stays as the minimum.
        /// </summary>
        private float FitHeight(string text, int size, float minimum)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("<size", System.StringComparison.OrdinalIgnoreCase) >= 0) return minimum;
            float width = List is RectTransform rect && rect.rect.width > 120f ? rect.rect.width - 24f : 620f;
            // Average glyph width is about 0.5 of the font size; wide enough to be safe on the
            // cautious side, so a line never ends up shorter than its text.
            float perLine = Mathf.Max(12f, width / (size * 0.52f));
            int lines = 0;
            foreach (var part in text.Split('\n'))
                lines += Mathf.Max(1, Mathf.CeilToInt(StripTags(part).Length / perLine));
            return Mathf.Max(minimum, lines * (size + 6f) + 4f);
        }

        private static string StripTags(string text) =>
            System.Text.RegularExpressions.Regex.Replace(text ?? "", "<[^>]+>", "");

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
            if (!sent) Say(L.T("O pedido não chegou ao servidor.", "The request did not reach the server."));
        }

        protected static string Clock(int seconds)
        {
            seconds = Mathf.Max(0, seconds);
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        protected static string Signed(int value) => value > 0 ? "+" + value : value.ToString(CultureInfo.InvariantCulture);

        protected static string SideText(int side) =>
            side == ArenaSide.Gold ? L.T("<color=#ffd140>Ouro</color>", "<color=#ffd140>Gold</color>") : side == ArenaSide.Green ? L.T("<color=#73db6b>Verde</color>", "<color=#73db6b>Green</color>") : "-";

        protected static string Kind(bool rated) => rated ? L.T("Ranqueada", "Ranked") : L.T("Escaramuça", "Skirmish");

        /// <summary>A queue as people read it: "2v2 Ranqueada", "FFA", "2v2 Escaramuça balanceada".</summary>
        protected static string QueueName(int size, bool rated, bool balanced = false) =>
            (size == ArenaRules.FfaSize ? L.T("FFA (todos contra todos)", "FFA (free for all)") : $"{ArenaRules.BracketName(size)} {Kind(rated)}") +
            (balanced ? size == ArenaRules.FfaSize ? L.T(" balanceado", " balanced") : L.T(" balanceada", " balanced") : "");

        /// <summary>A class's name as the server sent the classes, or its id while they have not arrived.</summary>
        internal static string ClassName(string id) => L.Server(ArenaClasses.Find(id)?.Name ?? id ?? "");

        /// <summary>My personal rating in a team, from the snapshot.</summary>
        protected static int PersonalIn(ArenaTeamView team)
        {
            var me = team?.Members.FirstOrDefault(m => m.PlayerId == ArenaClient.Snapshot.PlayerId);
            return me?.Personal ?? 0;
        }

        /// <summary>My personal rating in the team's Arena balanceada, -1 while it is not apart.</summary>
        protected static int BalancedPersonalIn(ArenaTeamView team)
        {
            var me = team?.Members.FirstOrDefault(m => m.PlayerId == ArenaClient.Snapshot.PlayerId);
            return me?.BalancedPersonal ?? -1;
        }

        /// <summary>"  ·  balanceada 431 (#3)" when the balanced rating is apart, empty otherwise:
        /// next to the normal rating, so a player who only plays balanced sees what moves.</summary>
        protected static string BalancedTail(ArenaTeamView team)
        {
            if (team == null || team.BalancedRating < 0) return "";
            return team.BalancedRank > 0
                ? L.T("  ·  balanceada {0} (#{1})", "  -  balanced {0} (#{1})", team.BalancedRating, team.BalancedRank)
                : L.T("  ·  balanceada {0}", "  -  balanced {0}", team.BalancedRating);
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
                Heading(match.IsChallenge
                    ? L.T("Desafio: {0} · {1}", "Challenge: {0} - {1}", match.Challenge, match.Map)
                    : L.T("Partida: {0} · {1}", "Match: {0} - {1}", match.Map, QueueName(match.Size, match.Rated, match.Balanced)));
                if (match.Balanced)
                    Line(match.Phase == "invited"
                            ? L.T("Arena balanceada · você: <color=#ffe300>{0}</color>. Ao entrar, você luta com o kit da classe.", "Balanced arena - you: <color=#ffe300>{0}</color>. When you enter, you fight with the class kit.", ClassName(match.ClassId))
                            : L.T("Partida balanceada: você luta de <color=#ffe300>{0}</color>. Seu equipamento volta no fim.", "Balanced match: you fight as <color=#ffe300>{0}</color>. Your gear comes back at the end.", ClassName(match.ClassId)),
                        15, ValheimUi.Beige, 24f);
                switch (match.Phase)
                {
                    case "invited":
                        LiveLine(() => (match.IsChallenge ? L.T("O desafio está pronto!", "The challenge is ready!")
                                           : match.Size == ArenaRules.FfaSize ? L.T("O FFA está pronto! Cada um por si.", "The FFA is ready! Every man for himself.")
                                           : L.T("A arena está pronta! Você é do time {0}.", "The arena is ready! You are on team {0}.", SideText(match.Side))) +
                                       L.T(" Expira em {0}s.", " Expires in {0}s.", ArenaClient.SecondsLeft()), 16, ValheimUi.Yellow);
                        var row = ButtonRow();
                        Btn(row, L.T("Entrar", "Enter"), 160f, () =>
                        {
                            ArenaClient.RequestEnter();
                            Say(L.T("Entrando na arena...", "Entering the arena..."));
                        }, 16);
                        Cell(row, L.T("Na ranqueada, deixar expirar conta como derrota.", "In ranked, letting it expire counts as a defeat."), 13, ValheimUi.Muted);
                        break;
                    case "prep":
                        LiveLine(() => L.T("Preparação: os portões abrem em {0}.", "Preparation: the gates open in {0}.", Clock(ArenaClient.SecondsLeft())) +
                                       (match.IsChallenge ? L.T(" A criatura chega quando abrirem.", " The creature arrives when they open.")
                                           : match.Size == ArenaRules.FfaSize ? L.T(" Todos contra todos: vence o último de pé.", " Free for all: the last one standing wins.")
                                           : L.T(" Você é do time {0}.", " You are on team {0}.", SideText(match.Side))), 16, ValheimUi.Yellow);
                        Score(match);
                        LeaveButton(L.T("Sair da arena", "Leave the arena"));
                        break;
                    case "live":
                        Score(match);
                        LiveLine(() => (ArenaClient.SecondsLeft() < 0 ? L.T("Sem limite de tempo", "No time limit")
                                           : L.T("{0}: {1}", "{0}: {1}", match.IsChallenge ? L.T("Tempo restante", "Time left") : L.T("Tempo até o empate", "Time until draw"), Clock(ArenaClient.SecondsLeft()))) +
                                       (match.Mine == "ko"
                                           ? L.T("   ·   <color=#ee6b57>você foi derrotado</color> ([{0}] volta agora)", "   -   <color=#ee6b57>you were defeated</color> ([{0}] return now)", ArenaSpectate.LeaveKey)
                                           : ""), 15);
                        LeaveButton(match.Rated ? L.T("Abandonar (conta como derrota)", "Forfeit (counts as a defeat)") : L.T("Abandonar a partida", "Forfeit the match"));
                        break;
                    default:
                        LiveLine(() => L.T("Partida encerrada. Volta para onde estava em {0}.", "Match over. Back to where you were in {0}.", Clock(ArenaClient.SecondsLeft())), 15);
                        LeaveButton(L.T("Sair agora", "Leave now"));
                        break;
                }
                Gap();
            }

            var queue = s.Queue;
            if (queue != null)
            {
                Heading(L.T("Fila", "Queue"));
                if (queue.Status == "confirm")
                    Line(L.T("Confirmando o grupo...", "Confirming the group..."), 15, ValheimUi.Yellow);
                else
                {
                    int since = queue.Waited;
                    float at = ArenaClient.SnapshotAt;
                    LiveLine(() => L.T("Na fila: {0} · esperando {1}", "In queue: {0} - waiting {1}", QueueName(queue.Size, queue.Rated, queue.Balanced), Clock(since + Mathf.FloorToInt(Time.realtimeSinceStartup - at))), 15, ValheimUi.Yellow);
                }
                var row = ButtonRow();
                Btn(row, L.T("Sair da fila", "Leave queue"), 160f, () => Ask(ArenaWire.ActQueueLeave));
                Gap();
            }

            if (s.SignRequests.Count > 0 || s.Invite != null)
            {
                Heading(L.T("Pedidos para você", "Requests for you"));
                foreach (var req in s.SignRequests)
                {
                    var r = req;
                    var row = ButtonRow(40f);
                    Cell(row, L.T("{0} pede a sua assinatura na carta \"{1}\" ({2})", "{0} asks for your signature on the charter \"{1}\" ({2})", r.OwnerName, r.TeamName, ArenaRules.BracketName(r.Size)), 15);
                    Btn(row, L.T("Assinar", "Sign"), 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n1"));
                    Btn(row, L.T("Recusar", "Decline"), 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n0"));
                }
                if (s.Invite != null)
                {
                    var inv = s.Invite;
                    var row = ButtonRow(40f);
                    Cell(row, L.T("{0} convidou você para o time \"{1}\" ({2})", "{0} invited you to the team \"{1}\" ({2})", inv.From, inv.TeamName, ArenaRules.BracketName(inv.Size)), 15);
                    Btn(row, L.T("Aceitar", "Accept"), 110f, () => Ask(ArenaWire.ActTeamAnswer, $"{inv.TeamId}\n1"));
                    Btn(row, L.T("Recusar", "Decline"), 110f, () => Ask(ArenaWire.ActTeamAnswer, $"{inv.TeamId}\n0"));
                }
                Gap();
            }

            if (withResult && ArenaClient.LastResult != null) ResultBlock(ArenaClient.LastResult);

            if (ArenaClient.FeedLines.Count > 0 && match != null)
            {
                Heading(L.T("Na arena", "In the arena"));
                foreach (var line in ArenaClient.FeedLines) Line(line, 14, ValheimUi.Muted, 20f);
            }
        }

        private void Score(ArenaMatchView match)
        {
            if (match.Size == ArenaRules.FfaSize)
            {
                Line(L.T("Todos contra todos: {0}/{1} de pé", "Free for all: {0}/{1} alive", match.GoldAlive, match.GoldTotal), 17, ValheimUi.Beige, 28f);
                return;
            }
            Line(L.T("<color=#ffd140>{0}</color>  {1}/{2} de pé     ×     ", "<color=#ffd140>{0}</color>  {1}/{2} alive     x     ", match.GoldName, match.GoldAlive, match.GoldTotal) +
                 L.T("<color=#73db6b>{0}</color>  {1}/{2} de pé", "<color=#73db6b>{0}</color>  {1}/{2} alive", match.GreenName, match.GreenAlive, match.GreenTotal), 17, ValheimUi.Beige, 28f);
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
            bool ffa = !r.IsChallenge && r.Size == ArenaRules.FfaSize;
            string outcome = r.IsChallenge
                ? r.Winner == ArenaSide.Gold ? L.T("{0} foi derrotado!", "{0} was defeated!", r.Challenge) : r.Winner == ArenaSide.Green ? L.T("{0} venceu", "{0} won", r.Challenge) : L.T("O desafio terminou sem vencedor", "The challenge ended with no winner")
                : r.Winner == ArenaSide.None ? L.T("Empate", "Draw")
                : ffa ? L.T("Vitória de {0}", "Victory for {0}", r.Players.FirstOrDefault(p => p.Side == r.Winner)?.Name ?? "?")
                : L.T("Vitória do time {0}", "Victory for team {0}", SideText(r.Winner));
            Heading(r.IsChallenge ? L.T("Resultado · Desafio {0} · {1}", "Result - Challenge {0} - {1}", r.Challenge, r.Map) : L.T("Resultado · {0} · {1}", "Result - {0} - {1}", r.Map, QueueName(r.Size, r.Rated, r.Balanced)));
            Line($"{outcome}  ·  {Clock(r.Duration)}" + (r.Record ? "  ·  <color=#73db6b>novo recorde do desafio!</color>" : ""), 17, ValheimUi.Yellow, 28f);
            foreach (var side in r.Sides)
            {
                string rating = r.Rated
                    ? $"   rating {side.RatingBefore} → {side.RatingBefore + side.RatingChange} ({Signed(side.RatingChange)})   MMR {side.Mmr} ({Signed(side.MmrChange)})"
                    : "";
                Line($"{SideText(side.Side)}: {side.TeamName}{rating}", 15, ValheimUi.Beige, 26f);
            }
            var header = ButtonRow(24f);
            Cell(header, L.T("Jogador", "Player"), 13, ValheimUi.Muted);
            if (r.Balanced) Cell(header, L.T("Classe", "Class"), 13, ValheimUi.Muted, 150f);
            if (!ffa) Cell(header, L.T("Time", "Team"), 13, ValheimUi.Muted, 80f);
            Cell(header, L.T("Abates", "Kills"), 13, ValheimUi.Muted, 70f);
            Cell(header, L.T("Situação", "Status"), 13, ValheimUi.Muted, 110f);
            if (r.Rated) Cell(header, L.T("Pessoal", "Personal"), 13, ValheimUi.Muted, 130f);
            else if (r.IsChallenge) Cell(header, L.T("Rating PvE", "PvE Rating"), 13, ValheimUi.Muted, 130f);
            var players = ffa
                ? r.Players.OrderByDescending(p => p.Side == r.Winner).ThenByDescending(p => p.Kos)
                : r.Players.OrderBy(p => p.Side).ThenByDescending(p => p.Kos);
            foreach (var p in players)
            {
                var row = ButtonRow(24f);
                Cell(row, p.Name, 15);
                // A kit that diverged during the fight is marked for everyone to see.
                if (r.Balanced) Cell(row, p.Class + (p.KitDiverged ? L.T(" <color=#ee6b57>(kit divergiu)</color>", " <color=#ee6b57>(kit mismatch)</color>") : ""), 15, null, 150f);
                if (!ffa) Cell(row, SideText(p.Side), 15, null, 80f);
                Cell(row, p.Kos.ToString(CultureInfo.InvariantCulture), 15, null, 70f);
                Cell(row, L.Server(p.Status), 15, null, 110f);
                // A challenge sends the PvE rating in the personal rating's place.
                if (r.Rated || r.IsChallenge)
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
            Line(L.T("<color=#9a9188>Pontos de Arena:</color> {0}", "<color=#9a9188>Arena Points:</color> {0}", s.Points), 17, ValheimUi.Yellow, 28f);
            Line(L.T("<color=#9a9188>Rating PvE (desafios):</color> {0}", "<color=#9a9188>PvE Rating (challenges):</color> {0}", s.PveRating) +
                 (s.PveGames > 0 ? L.T("   <size=13><color=#9a9188>{0} vitória(s) em {1} desafio(s)</color></size>", "   <size=13><color=#9a9188>{0} win(s) in {1} challenge(s)</color></size>", s.PveWins, s.PveGames) : ""),
                15, ValheimUi.Beige, 24f);
            Gap();
            StatusBlocks(withResult: true);
            if (s.Match == null && s.Queue == null && s.SignRequests.Count == 0 && s.Invite == null && ArenaClient.LastResult == null)
            {
                Line(L.T("Nada pendente.", "Nothing pending."), 15, ValheimUi.Muted);
                Line(L.T("Para jogar: fale com o Mestre da Arena. No 1v1 basta entrar na fila; para 2v2 e 3v3, crie o time na aba Criar time.", "To play: talk to the Arena Master. In 1v1 just join the queue; for 2v2 and 3v3, create the team in the Create team tab."), 14, ValheimUi.Muted, 44f);
                if (SoloOn)
                    Line(L.T("No 1v1 não precisa de time: fale com o Mestre da Arena e entre na fila sozinho.", "1v1 needs no team: talk to the Arena Master and join the queue alone."), 14, ValheimUi.Muted, 22f);
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

            var hint = ValheimUi.CreateLabel(form, L.T("Convidar (capitão):", "Invite (captain):"), 15, ValheimUi.Muted, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(hint.gameObject, 150f);
            _invite = ValheimUi.CreateInputField(form, "", 0f, 34f);
            Flexible(_invite.gameObject);
            var send = ValheimUi.CreateButton(form, L.T("Convidar", "Invite"), 130f, 34f, 14);
            send.onClick.AddListener(() =>
            {
                var name = (_invite.text ?? "").Trim();
                if (name.Length == 0) { Say(L.T("Escreva o nome do jogador.", "Type the player name.")); return; }
                Ask(ArenaWire.ActTeamInvite, $"{Size}\n{name}");
                Say(L.T("Convidando {0} para o time {1}...", "Inviting {0} to the {1} team...", name, ArenaRules.BracketName(Size)));
            });

            CreateList(Vector2.zero, new Vector2(0f, -46f));
        }

        protected override void Rebuild()
        {
            BracketSelector(L.T("Time:", "Team:"));
            var s = ArenaClient.Snapshot;
            var team = s.TeamOf(Size);
            bool solo = IsSolo(Size);
            if (team == null)
            {
                if (solo)
                {
                    Line(L.T("Você ainda não jogou a 1v1 ranqueada.", "You have not played ranked 1v1 yet."), 16, ValheimUi.Muted, 28f);
                    Line(L.T("No 1v1 não tem time para montar: entre na fila sozinho com o Mestre da Arena e o seu rating começa ali.", "1v1 has no team to build: join the queue alone with the Arena Master and your rating starts there."), 14, ValheimUi.Muted, 40f);
                    return;
                }
                Line(L.T("Você não tem time {0}.", "You have no {0} team.", ArenaRules.BracketName(Size)), 16, ValheimUi.Muted, 28f);
                Line(L.T("Crie o time na aba Criar time: compre a carta e colete as assinaturas.", "Create the team in the Create team tab: buy the charter and collect the signatures."), 14, ValheimUi.Muted);
                return;
            }

            bool captain = team.CaptainId == s.PlayerId && !solo;
            Heading($"{team.Name} · {ArenaRules.BracketName(team.Size)}");
            Line(L.T("Rating {0}   ·   Posição #{1}   ·   MMR {2}", "Rating {0}   -   Rank #{1}   -   MMR {2}", team.Rating, team.Rank, team.Mmr), 16, ValheimUi.Yellow, 26f);
            if (team.BalancedRating >= 0)
                Line(team.BalancedRank > 0
                    ? L.T("Arena balanceada: rating {0}   ·   Posição #{1}  (ranking separado, aba Ranking)", "Balanced arena: rating {0}   -   Rank #{1}  (separate ladder, Ladder tab)", team.BalancedRating, team.BalancedRank)
                    : L.T("Arena balanceada: rating {0}  (ranking separado; começa na primeira ranqueada balanceada)", "Balanced arena: rating {0}  (separate ladder; starts at the first balanced ranked)", team.BalancedRating),
                    15, ValheimUi.Yellow, 24f);
            Line(L.T("Semana: {0} jogos, {1} vitórias   ·   Temporada: {2} jogos, {3} vitórias", "Week: {0} games, {1} wins   -   Season: {2} games, {3} wins", team.WeekGames, team.WeekWins, team.SeasonGames, team.SeasonWins), 14, ValheimUi.Beige);
            Gap();

            var header = ButtonRow(24f);
            Cell(header, L.T("Membro", "Member"), 13, ValheimUi.Muted);
            Cell(header, L.T("Pessoal", "Personal"), 13, ValheimUi.Muted, 80f);
            Cell(header, L.T("Semana", "Week"), 13, ValheimUi.Muted, 90f);
            Cell(header, L.T("Temporada", "Season"), 13, ValheimUi.Muted, 100f);
            if (captain) Cell(header, "", 13, null, 236f);

            foreach (var m in team.Members.OrderByDescending(m => m.PlayerId == team.CaptainId).ThenByDescending(m => m.Personal))
            {
                var member = m;
                var row = ButtonRow(36f);
                string mark = member.PlayerId == team.CaptainId ? L.T(" <color=#ffd140>(capitão)</color>", " <color=#ffd140>(captain)</color>") : "";
                string online = member.Online ? "<color=#73db6b>●</color> " : "<color=#6b6560>●</color> ";
                Cell(row, online + member.Name + mark, 15);
                Cell(row, member.Personal.ToString(CultureInfo.InvariantCulture), 15, null, 80f);
                Cell(row, $"{member.WeekGames}/{member.WeekWins}", 15, null, 90f);
                Cell(row, $"{member.SeasonGames}/{member.SeasonWins}", 15, null, 100f);
                if (captain)
                {
                    if (member.PlayerId != team.CaptainId)
                    {
                        Btn(row, L.T("Capitão", "Captain"), 110f, () => Ask(ArenaWire.ActTeamCaptain, $"{Size}\n{member.PlayerId}"));
                        Btn(row, L.T("Remover", "Remove"), 110f, () => Ask(ArenaWire.ActTeamKick, $"{Size}\n{member.PlayerId}"));
                    }
                    else
                    {
                        var pad = ValheimUi.CreateRect("Pad", row);
                        ValheimUi.SetWidth(pad.gameObject, 236f);
                    }
                }
            }
            Gap();
            Line(L.T("Pontos de Arena vêm de cada partida terminada dentro da arena (vitória vale mais que derrota).", "Arena Points come from every match finished inside the arena (a win is worth more than a loss)."), 13, ValheimUi.Muted, 22f);

            if (solo) return;
            var actions = ButtonRow();
            if (captain)
            {
                Btn(actions, L.T("Desfazer o time", "Disband the team"), 200f, () => Ask(ArenaWire.ActTeamDisband, Size.ToString()));
                if (team.Members.Count > 1)
                    Cell(actions, L.T("Para sair, passe a capitania antes.", "To leave, hand over the captaincy first."), 13, ValheimUi.Muted);
            }
            else
            {
                Btn(actions, L.T("Sair do time", "Leave the team"), 200f, () => Ask(ArenaWire.ActTeamLeave, Size.ToString()));
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
            _name = FormRow(L.T("Nome do time:", "Team name:"), -40f, L.T("Comprar carta", "Buy charter"), () =>
            {
                int cost = ArenaConfig.CostOf(Size);
                var name = (_name.text ?? "").Trim();
                if (name.Length < 2) { Say(L.T("Escreva o nome do time.", "Type the team name.")); return; }
                var player = Player.m_localPlayer;
                if (player == null) return;
                if (!MarketplaceNpc.TryPay(player, cost))
                {
                    Say(L.T("A carta {0} custa {1} moedas.", "The {0} charter costs {1} coins.", ArenaRules.BracketName(Size), cost));
                    return;
                }
                // Paid first, from the bag, as at the merchant: the server keeps what the
                // charter costs and sends back the rest, or all of it if it refuses.
                Ask(ArenaWire.ActCharterBuy, $"{Size}\n{name}\n{cost}");
                Say(L.T("Comprando a carta...", "Buying the charter..."));
            });
            _signer = FormRow(L.T("Pedir assinatura a:", "Ask signature from:"), -84f, L.T("Pedir", "Ask"), () =>
            {
                var name = (_signer.text ?? "").Trim();
                if (name.Length == 0) { Say(L.T("Escreva o nome do jogador.", "Type the player name.")); return; }
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
            BracketSelector(L.T("Carta:", "Charter:"));
            if (SoloOn) Line(L.T("O 1v1 não precisa de carta: entre na fila sozinho com o Mestre da Arena.", "1v1 needs no charter: join the queue alone with the Arena Master."), 13, ValheimUi.Muted, 22f);
            var s = ArenaClient.Snapshot;
            int cost = ArenaConfig.CostOf(Size);
            int required = ArenaRules.RequiredSignatures(Size, ArenaConfig.SignaturesRequired?.Value ?? -1);
            var team = s.TeamOf(Size);
            var charter = s.CharterOf(Size);

            if (team != null)
            {
                Line(L.T("Você já está no time \"{0}\" ({1}).", "You are already on the team \"{0}\" ({1}).", team.Name, ArenaRules.BracketName(Size)), 16, ValheimUi.Beige, 28f);
                Line(L.T("Um jogador tem no máximo um time de cada tamanho.", "A player can have at most one team of each size."), 14, ValheimUi.Muted);
            }
            else if (charter != null)
            {
                Heading(L.T("Carta \"{0}\" · {1}", "Charter \"{0}\" - {1}", charter.Name, ArenaRules.BracketName(Size)));
                Line(L.T("Assinaturas: {0}/{1}", "Signatures: {0}/{1}", charter.Signatures, charter.Required) +
                     (string.IsNullOrEmpty(charter.Signers) ? "" : $"  ({charter.Signers})"), 16,
                    charter.Signatures >= charter.Required ? ValheimUi.Yellow : ValheimUi.Beige, 26f);
                Line(L.T("Peça a assinatura acima: quem assina vira membro fundador do time.", "Ask for signatures above: whoever signs becomes a founding member of the team."), 14, ValheimUi.Muted);
                var row = ButtonRow();
                var register = Btn(row, L.T("Registrar o time", "Register the team"), 200f, () => Ask(ArenaWire.ActCharterTurnIn, Size.ToString()));
                register.interactable = charter.Signatures >= charter.Required;
                Btn(row, L.T("Rasgar a carta", "Tear up the charter"), 170f, () => Ask(ArenaWire.ActCharterAbandon, Size.ToString()));
            }
            else
            {
                Heading(L.T("Carta de time {0}", "{0} team charter", ArenaRules.BracketName(Size)));
                Line(L.T("Custa {0} moedas. Escreva o nome acima e clique Comprar carta.", "Costs {0} coins. Type the name above and click Buy charter.", cost), 15, ValheimUi.Beige);
                Line(L.T("Depois colete {0} assinatura(s) de outros jogadores e volte para registrar o time. ", "Then collect {0} signature(s) from other players and come back to register the team.", required) +
                     L.T("O time aceita até {0} membros; você é o capitão.", "The team takes up to {0} members; you are the captain.", ArenaRules.MaxMembers(Size)), 14, ValheimUi.Muted, 40f);
            }

            if (s.SignRequests.Count > 0)
            {
                Gap();
                Heading(L.T("Pedem a sua assinatura", "Signatures requested"));
                foreach (var req in s.SignRequests)
                {
                    var r = req;
                    var row = ButtonRow(40f);
                    Cell(row, $"{r.OwnerName}: \"{r.TeamName}\" ({ArenaRules.BracketName(r.Size)})", 15);
                    Btn(row, L.T("Assinar", "Sign"), 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n1"));
                    Btn(row, L.T("Recusar", "Decline"), 110f, () => Ask(ArenaWire.ActCharterSign, $"{r.Owner}\n{r.Size}\n0"));
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
            BracketSelector(L.T("Arena:", "Arena:"));
            var s = ArenaClient.Snapshot;
            var team = s.TeamOf(Size);
            var group = ArenaGroups.Members();
            bool grouped = group.Count > 1;

            bool solo = IsSolo(Size);
            Heading(L.T("Arena {0}", "{0} Arena", ArenaRules.BracketName(Size)));
            Line(solo
                    ? team != null ? L.T("Seu rating 1v1: {0}  ·  posição #{1}", "Your 1v1 rating: {0}  -  rank #{1}", team.Rating, team.Rank) + BalancedTail(team) : L.T("1v1: você entra sozinho, sem time. O rating começa na primeira ranqueada.", "1v1: you enter alone, without a team. Your rating starts at the first ranked match.")
                    : team != null
                        ? L.T("Seu time: \"{0}\"  ·  rating {1}  ·  seu pessoal {2}", "Your team: \"{0}\"  -  rating {1}  -  your personal {2}", team.Name, team.Rating, PersonalIn(team)) + BalancedTail(team)
                        : L.T("Você não tem time {0} (aba Criar time).", "You have no {0} team (Create team tab).", ArenaRules.BracketName(Size)), 15, ValheimUi.Beige);
            Line(!ArenaGroups.Installed
                    ? L.T("Sem o mod Groups: só dá para entrar sozinho.", "Without the Groups mod: you can only join alone.")
                    : grouped
                        ? L.T("Grupo ({0}): {1}", "Group ({0}): {1}", group.Count, string.Join(", ", group.Select(m => m.Name))) + (ArenaGroups.IsLeader() ? "" : L.T("  ·  só o líder entra na fila", "  -  only the leader joins the queue"))
                        : L.T("Sem grupo: forme o grupo (Groups) para a ranqueada.", "No group: form a group (Groups) for ranked."),
                14, ValheimUi.Muted);

            bool busy = s.Queue != null || s.Match != null;
            Gap(10f);
            bool balanced = BalancedBlock(team, solo);
            string tail = balanced ? L.T(" balanceada", " balanced") : "";
            bool classMissing = balanced && BalancedChoice.ClassId.Length == 0;
            Gap(10f);
            Heading(L.T("Entrar na fila", "Join the queue"));
            var row = ButtonRow(40f);
            var rated = Btn(row, L.T("Entrar: Ranqueada", "Join: Ranked") + tail, balanced ? 270f : 210f, () => Join(true), 15);
            rated.interactable = !busy && (team != null || solo) && !(solo && grouped) && !classMissing;
            var skirmish = Btn(row, L.T("Entrar: Escaramuça", "Join: Skirmish") + tail, balanced ? 270f : 210f, () => Join(false), 15);
            skirmish.interactable = !busy && (ArenaConfig.Skirmish?.Value ?? true) && !classMissing;
            Line(solo
                    ? L.T("1v1: entre sem grupo. Ranqueada vale rating; escaramuça, não.", "1v1: join without a group. Ranked counts for rating; skirmish does not.")
                    : L.T("Ranqueada: grupo de exatamente {0} do mesmo time, rating em jogo. Escaramuça: sozinho ou em grupo, sem rating.", "Ranked: group of exactly {0} from the same team, rating at stake. Skirmish: alone or in a group, no rating.", Size),
                13, ValheimUi.Muted, 22f);
            if (balanced && grouped)
                Line(L.T("Na balanceada, cada um do grupo escolhe a própria classe no painel dele antes de você entrar na fila.", "In balanced, each group member picks their own class on their own panel before you join the queue."), 13, ValheimUi.Muted, 22f);
            Gap();
            FfaBlock(busy, balanced, classMissing);
            if (!balanced) ChallengeBlock(busy, group.Count);
            StatusBlocks(withResult: true);
        }

        private const float ClassCardWidth = 150f;
        private const float BeltSlot = 46f;

        /// <summary>
        /// The "Arena balanceada" checkbox and, when it is on, the class cards and the chosen
        /// class: its description, armour, health and the 8 slots of its belt. Returns whether the
        /// queue buttons below go to the balanced queue.
        /// </summary>
        private bool BalancedBlock(ArenaTeamView team, bool solo)
        {
            if (!(ArenaConfig.BalancedEnabled?.Value ?? false)) return false;
            ArenaClassesNet.AskIfMissing();
            bool on = BalancedChoice.On;
            var check = ButtonRow(36f);
            var box = Btn(check, (on ? "[X]" : "[  ]") + L.T("  Arena balanceada", "  Balanced arena"), 230f, () =>
            {
                BalancedChoice.On = !BalancedChoice.On;
                Invalidate();
            }, 15);
            var boxLabel = box.GetComponentInChildren<TextMeshProUGUI>();
            if (boxLabel != null) boxLabel.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
            ValheimUi.AddTooltip(box.gameObject, L.T("Arena balanceada", "Balanced arena"),
                L.T("Fila separada em que o seu equipamento, a sua comida e as suas skills não contam: todos lutam com o kit de uma classe. ", "Separate queue where your gear, food and skills do not count: everyone fights with a class kit.") +
                L.T("O seu inventário não é tocado e o seu equipamento volta no fim da partida.", "Your inventory is not touched and your gear comes back at the end of the match."));
            Cell(check, on ? L.T("o seu equipamento não conta: você luta com o kit da classe", "your gear does not count: you fight with the class kit") : "", 13, ValheimUi.Muted);
            if (!on) return false;

            var classes = ArenaClasses.All.Where(c => c.On).ToList();
            if (classes.Count == 0)
            {
                Line(ArenaClasses.Loaded ? L.T("O servidor ainda não tem classes ligadas.", "The server has no classes enabled yet.") : L.T("Carregando as classes...", "Loading classes..."), 14, ValheimUi.Muted);
                return true;
            }
            string chosen = BalancedChoice.ClassId;
            var cards = ButtonRow(40f);
            foreach (var c in classes)
            {
                var cls = c;
                bool mine = string.Equals(cls.Id, chosen, StringComparison.OrdinalIgnoreCase);
                var card = Btn(cards, L.Server(cls.Name), ClassCardWidth, () =>
                {
                    BalancedChoice.ClassId = cls.Id;
                    Invalidate();
                }, 14);
                var label = card.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = mine ? ValheimUi.Yellow : ValheimUi.Orange;
                card.image.color = mine ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }
            var pick = classes.FirstOrDefault(c => string.Equals(c.Id, chosen, StringComparison.OrdinalIgnoreCase));
            if (pick == null)
            {
                Line(L.T("Escolha a sua classe para entrar na fila balanceada.", "Pick your class to join the balanced queue."), 15, ValheimUi.Yellow);
                return true;
            }

            if (pick.Description.Length > 0) Line(L.Server(pick.Description), 14, ValheimUi.Beige, 40f);
            string armor = string.Join(", ", pick.Armor().Where(a => a.Item != null).Select(a => ItemNames.Display(a.Item.Prefab)));
            Line(L.T("Vida {0:0} · Vigor {1:0}", "Health {0:0} - Stamina {1:0}", pick.Health, pick.Stamina) + (pick.Eitr > 0 ? L.T(" · Eitr {0:0}", " - Eitr {0:0}", pick.Eitr) : "") +
                 (armor.Length > 0 ? $"  ·  <color=#9a9188>{armor}</color>" : ""), 14, ValheimUi.Beige, 24f);
            string skills = string.Join(", ", pick.Skills.OrderByDescending(kv => kv.Value).Select(kv => $"{SkillName(kv.Key)} {kv.Value}"));
            Line(L.T("Skills: {0}", "Skills: {0}", skills.Length > 0 ? skills + L.T(" · as outras {0}", " - others {0}", pick.SkillLevel) : L.T("todas {0}", "all {0}", pick.SkillLevel)), 13, ValheimUi.Muted, 22f);

            Line(L.T("Cinto (teclas 1 a 8 na partida):", "Belt (keys 1 to 8 in the match):"), 13, ValheimUi.Muted, 20f);
            var belt = ButtonRow(BeltSlot + 4f);
            for (int i = 0; i < ArenaClasses.MaxBelt; i++)
            {
                var slot = ValheimUi.CreateInlay(belt, "Slot");
                ValheimUi.SetWidth(slot.gameObject, BeltSlot);
                ValheimUi.SetHeight(slot.gameObject, BeltSlot);
                if (i >= pick.Belt.Count) continue;
                var item = pick.Belt[i];
                var icon = ValheimUi.CreateItemIcon(slot, item.Prefab, BeltSlot - 8f);
                ValheimUi.AddTooltip(icon.gameObject, ItemNames.Display(item.Prefab) + (item.Quality > 1 ? L.T(" (nível {0})", " (level {0})", item.Quality) : ""),
                    L.T("Tecla {0}", "Key {0}", i + 1) + (pick.Start.Contains(item.Prefab, StringComparer.OrdinalIgnoreCase) ? L.T(" · começa equipado", " - starts equipped") : ""));
            }
            Fill(belt);

            if (team != null && team.BalancedRating >= 0 && !solo)
                Line(L.T("Rating balanceado do time: {0}", "Team balanced rating: {0}", team.BalancedRating), 14, ValheimUi.Beige, 22f);
            else if (team != null && team.BalancedRating >= 0)
                Line(L.T("Seu rating 1v1 balanceado: {0}", "Your balanced 1v1 rating: {0}", team.BalancedRating), 14, ValheimUi.Beige, 22f);
            return true;
        }

        private static string SkillName(string skill) =>
            Enum.TryParse(skill, true, out Skills.SkillType type) ? ValheimUi.Localize("$skill_" + type.ToString().ToLowerInvariant()) : skill;

        private static void Fill(RectTransform row) => Flexible(ValheimUi.CreateRect("Fill", row).gameObject);

        /// <summary>The free-for-all: always alone, even in a group; no team, no rating.</summary>
        private void FfaBlock(bool busy, bool balanced, bool classMissing)
        {
            if (!(ArenaConfig.FfaEnabled?.Value ?? false)) return;
            if (balanced && !(ArenaConfig.BalancedFfa?.Value ?? false)) return;
            int max = Mathf.Clamp(ArenaConfig.FfaMaxPlayers?.Value ?? 8, 2, 16);
            int min = Mathf.Clamp(ArenaConfig.FfaMinPlayers?.Value ?? 3, 2, max);
            Heading(balanced ? L.T("FFA balanceado (todos contra todos)", "Balanced FFA (free for all)") : L.T("FFA (todos contra todos)", "FFA (free for all)"));
            Line(L.T("Cada um por si, de {0} a {1} jogadores. Vence o último de pé. Você entra sozinho, mesmo em grupo. Sem rating.", "Every man for himself, {0} to {1} players. Last one standing wins. You join alone, even in a group. No rating.", min, max),
                14, ValheimUi.Muted);
            var row = ButtonRow(40f);
            var go = Btn(row, balanced ? L.T("Entrar: FFA balanceado", "Join: Balanced FFA") : L.T("Entrar: FFA", "Join: FFA"), balanced ? 270f : 210f, () =>
            {
                Ask(ArenaWire.ActFfaJoin, balanced ? "1\n" + BalancedChoice.ClassId : "");
                Say(balanced ? L.T("Entrando na fila do FFA balanceado...", "Joining the balanced FFA queue...") : L.T("Entrando na fila do FFA...", "Joining the FFA queue..."));
            }, 15);
            go.interactable = !busy && !classMissing;
            Gap();
        }

        /// <summary>The challenges the server offers: the group (up to MaxPlayers) against one
        /// creature, no queue, no rating. Read from the synced cfg.</summary>
        private void ChallengeBlock(bool busy, int groupSize)
        {
            if (!(ArenaConfig.ChallengeEnabled?.Value ?? false)) return;
            var challenges = ArenaConfig.ChallengeDefs(null, checkWorld: false);
            if (challenges.Count == 0) return;
            int max = Mathf.Clamp(ArenaConfig.ChallengeMaxPlayers?.Value ?? 3, 1, 10);

            Heading(L.T("Desafio", "Challenge"));
            Line(L.T("Você e o seu grupo (até {0}) contra uma criatura forte. Sem rating; a recompensa vai pelo correio.", "You and your group (up to {0}) against a strong creature. No rating; the reward goes by mail.", max), 14, ValheimUi.Muted);
            foreach (var c in challenges)
            {
                var def = c;
                var row = ButtonRow(40f);
                string stars = def.Level > 1 ? L.T(" (nível {0})", " (level {0})", def.Level) : "";
                string reward = def.Rewards.Count == 0 ? L.T("sem recompensa", "no reward")
                    : string.Join(", ", def.Rewards.Select(r => $"{r.Value}x {ItemNames.Display(r.Key)}"));
                Cell(row, $"{def.Name}{stars}  ·  {reward}", 15);
                var go = Btn(row, L.T("Desafiar", "Challenge"), 140f, () => JoinChallenge(def.Name), 15);
                go.interactable = !busy && groupSize <= max;
            }
            if (groupSize > max) Line(L.T("O seu grupo tem {0}: o desafio aceita até {1}.", "Your group has {0}: the challenge accepts up to {1}.", groupSize, max), 13, ValheimUi.Muted, 22f);
            Gap();
        }

        private void JoinChallenge(string name)
        {
            var group = ArenaGroups.Members();
            if (group.Count > 1 && !ArenaGroups.IsLeader())
            {
                Say(L.T("Só o líder do grupo pode chamar o desafio.", "Only the group leader can call the challenge."));
                return;
            }
            long me = ZDOMan.GetSessionID();
            string peers = string.Join(",", group.Where(m => m.Peer != me).Select(m => m.Peer.ToString(CultureInfo.InvariantCulture)));
            Ask(ArenaWire.ActChallengeJoin, $"{name}\n{peers}");
            Say(L.T("Chamando o desafio...", "Calling the challenge..."));
        }

        private void Join(bool rated)
        {
            var group = ArenaGroups.Members();
            if (group.Count > 1 && !ArenaGroups.IsLeader())
            {
                Say(L.T("Só o líder do grupo pode entrar na fila.", "Only the group leader can join the queue."));
                return;
            }
            long me = ZDOMan.GetSessionID();
            string peers = string.Join(",", group.Where(m => m.Peer != me).Select(m => m.Peer.ToString(CultureInfo.InvariantCulture)));
            bool balanced = (ArenaConfig.BalancedEnabled?.Value ?? false) && BalancedChoice.On;
            Ask(ArenaWire.ActQueueJoin, $"{Size}\n{(rated ? 1 : 0)}\n{peers}" + (balanced ? $"\n1\n{BalancedChoice.ClassId}" : ""));
            Say(balanced ? L.T("Entrando na fila balanceada...", "Joining the balanced queue...") : L.T("Entrando na fila...", "Joining the queue..."));
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

        /// <summary>The PvP brackets, the Arena balanceada's when its rating is apart, and the
        /// PvE rating of the challenges.</summary>
        protected override List<int> Sizes()
        {
            var sizes = new List<int>(Brackets());
            if ((ArenaConfig.BalancedEnabled?.Value ?? false) && ArenaConfig.BalancedRatingMode?.Value == ArenaConfig.BalancedRating.Separado)
                sizes.AddRange(Brackets().Select(ArenaRules.BalancedLadder));
            sizes.Add(ArenaRules.PveLadder);
            return sizes;
        }

        protected override void Rebuild()
        {
            BracketSelector(L.T("Ranking:", "Ranking:"));
            bool pve = Size == ArenaRules.PveLadder;
            if (!ArenaClient.Ladders.TryGetValue(Size, out var rows))
            {
                Line(L.T("Carregando...", "Loading..."), 15, ValheimUi.Muted);
                return;
            }
            if (pve)
                Line(L.T("Rating PvE: sobe ao vencer um desafio, mais quanto mais difícil ele for; desce ao perder. Separado do PvP.", "PvE Rating: goes up when you win a challenge, more the harder it is; goes down when you lose. Separate from PvP."), 13, ValheimUi.Muted, 22f);
            if (ArenaRules.IsBalancedLadder(Size))
                Line(L.T("Arena balanceada: o rating das ranqueadas com o kit das classes, separado do rating normal.", "Balanced arena: the rating of ranked matches with the class kits, separate from the normal rating."), 13, ValheimUi.Muted, 22f);
            if (rows.Count == 0)
            {
                Line(pve ? L.T("Ninguém jogou um desafio ainda.", "Nobody has played a challenge yet.")
                    : ArenaRules.IsBalancedLadder(Size) ? L.T("Ninguém jogou essa ranqueada balanceada ainda.", "Nobody has played this balanced ranked yet.")
                    : IsSolo(Size) ? L.T("Ninguém jogou a 1v1 ranqueada ainda.", "Nobody has played ranked 1v1 yet.") : L.T("Nenhum time {0} ainda.", "No {0} team yet.", ArenaRules.BracketName(Size)),
                    15, ValheimUi.Muted);
                if (pve) BestTimes();
                return;
            }
            var header = ButtonRow(24f);
            Cell(header, "#", 13, ValheimUi.Muted, 50f);
            Cell(header, pve || IsSolo(ArenaRules.BracketOfLadder(Size)) ? L.T("Jogador", "Player") : L.T("Time", "Team"), 13, ValheimUi.Muted);
            Cell(header, L.T("Rating", "Rating"), 13, ValheimUi.Muted, 90f);
            Cell(header, L.T("Jogos", "Games"), 13, ValheimUi.Muted, 80f);
            Cell(header, L.T("Vitórias", "Wins"), 13, ValheimUi.Muted, 90f);
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
            if (pve) BestTimes();
        }

        /// <summary>Each challenge's fastest wins.</summary>
        private void BestTimes()
        {
            Gap();
            Heading(L.T("Melhores tempos dos desafios", "Best challenge times"));
            if (ArenaClient.BestTimes.Count == 0)
            {
                Line(L.T("Nenhum desafio vencido ainda.", "No challenge won yet."), 14, ValheimUi.Muted);
                return;
            }
            foreach (var group in ArenaClient.BestTimes.GroupBy(b => b.Challenge))
            {
                Line(group.Key, 16, ValheimUi.Orange, 26f);
                foreach (var b in group.OrderBy(b => b.Place))
                {
                    var row = ButtonRow(24f);
                    var color = b.Place == 1 ? ValheimUi.Yellow : ValheimUi.Beige;
                    Cell(row, b.Place.ToString(CultureInfo.InvariantCulture) + "º", 14, color, 50f);
                    Cell(row, $"{(int)b.Seconds / 60}:{b.Seconds % 60f:00.0}", 14, color, 90f);
                    Cell(row, b.Names, 14, color);
                }
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
            var offers = ArenaSettingsParser.ParseOffers(ArenaConfig.VendorItems?.Value, null)
                .Where(o => ArenaConfig.MaxOfferAmount(o.Prefab) > 0).ToList();

            Line(L.T("<color=#9a9188>Seus Pontos de Arena:</color> {0}", "<color=#9a9188>Your Arena Points:</color> {0}", s.Points), 18, ValheimUi.Yellow, 30f);
            Line(offers.Any(o => o.IsDeadcoins)
                ? L.T("Os itens chegam pela Caixa Postal; Deadcoins vão direto para o seu saldo. Pontos de Arena vêm de cada partida.", "Items arrive via the Mailbox; Deadcoins go straight to your balance. Arena Points come from every match.")
                : L.T("Os itens chegam pela Caixa Postal. Pontos de Arena vêm de cada partida.", "Items arrive via the Mailbox. Arena Points come from every match."), 13, ValheimUi.Muted, 22f);
            Gap();

            if (offers.Count == 0)
            {
                Line(L.T("A loja da arena não tem nada à venda.", "The arena shop has nothing for sale."), 15, ValheimUi.Muted);
                return;
            }

            foreach (var offer in offers)
            {
                var o = offer;
                var row = ButtonRow(48f);
                ArenaConfig.OfferIcon(row, o.Prefab, 38f);
                string requirement = "";
                bool meets = true;
                if (o.Rating > 0)
                {
                    int best = BestRating(s, o.Bracket);
                    meets = best >= o.Rating;
                    string where = o.Bracket > 1 ? $" ({ArenaRules.BracketName(o.Bracket)}+)" : "";
                    requirement = L.T("   <color={0}>requer rating {1}{2}</color>", "   <color={0}>requires rating {1}{2}</color>", (meets ? "#73db6b" : "#ee6b57"), o.Rating, where);
                }
                if (o.PveRating > 0)
                {
                    bool pveOk = s.PveRating >= o.PveRating;
                    meets &= pveOk;
                    requirement += L.T("   <color={0}>requer rating PvE {1}</color>", "   <color={0}>requires PvE rating {1}</color>", (pveOk ? "#73db6b" : "#ee6b57"), o.PveRating);
                }
                Cell(row, L.T("{0}x {1}{2}\n<size=13><color=#9a9188>{3} pontos</color>{4}</size>", "{0}x {1}{2}\n<size=13><color=#9a9188>{3} points</color>{4}</size>", o.Amount, ArenaConfig.OfferName(o.Prefab), o.Quality > 1 ? L.T(" (nível {0})", " (level {0})", o.Quality) : "", o.Points, requirement), 15);
                var buy = Btn(row, L.T("Comprar", "Buy"), 120f, () => Buy(o));
                buy.interactable = meets && s.Points >= o.Points;
            }
        }

        /// <summary>Same rule the server checks: the lower of personal and team rating, best
        /// across my teams of that bracket or larger, the balanced rating counting too when it is
        /// apart (a server that does not send the balanced personal leaves it out).</summary>
        private static int BestRating(ArenaSnapshot s, int bracket)
        {
            int best = 0;
            foreach (var team in s.Teams.Where(t => t.Size >= bracket))
            {
                best = Math.Max(best, ArenaRules.PurchaseRating(PersonalIn(team), team.Rating));
                int balancedPersonal = BalancedPersonalIn(team);
                if (team.BalancedRating >= 0 && balancedPersonal >= 0)
                    best = Math.Max(best, ArenaRules.PurchaseRating(balancedPersonal, team.BalancedRating));
            }
            return best;
        }

        private void Buy(ArenaOffer offer)
        {
            if (Time.unscaledTime - _sentAt < 2f) { Say(L.T("Aguarde a compra anterior.", "Wait for the previous purchase.")); return; }
            _sentAt = Time.unscaledTime;
            Ask(ArenaWire.ActVendorBuy, $"{offer.Prefab}\n{offer.Points}");
            Say(L.T("Comprando {0}x {1}...", "Buying {0}x {1}...", offer.Amount, ArenaConfig.OfferName(offer.Prefab)));
        }
    }
}
