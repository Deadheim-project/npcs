using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NpcValheim.UI
{
    /// <summary>
    /// F8 for admins: the Deadheim admin panel (global message, go to / pull a player, buff and
    /// heal in an area) in the same window the NPCs use. The work is done by the Deadheim mod on
    /// the server (AdminPainel.cs there): these pages only send requests to DH_Admin_ToServer and
    /// show the answers that come back on DH_Admin_Status. The two mods share only those RPC
    /// names and the package layout, so neither needs the other to compile.
    ///
    /// The admin check here only decides whether the key does anything; the server checks the
    /// real sender against the adminlist on every request.
    /// </summary>
    internal sealed class AdminPanelShortcut : MonoBehaviour
    {
        internal const string PanelTitle = "Painel de Admin";
        private static AdminPanelShortcut _instance;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_AdminPanelShortcut");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<AdminPanelShortcut>();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || Plugin.AdminPanelKey == null || !Input.GetKeyDown(Plugin.AdminPanelKey.Value)) return;

            if (UiRoot.StandaloneTitle == PanelTitle)
            {
                UiRoot.RequestClose();
                return;
            }

            if (UiInputBlocker.IsOpen || UiInputBlocker.IsTyping || !AdminPanelNet.LocalIsAdmin()) return;

            AdminPanelNet.ClearStatus();
            UiRoot.OpenStandalone(PanelTitle, new List<(string, NpcViewBase)>
            {
                ("Mensagem", new AdminMessageView()),
                ("Jogadores", new AdminPlayersView()),
                ("Buffs", new AdminBuffView()),
            }, Player.m_localPlayer);
        }
    }

    /// <summary>The wire to Deadheim's AdminPainel. Package layouts must match OnToServer there.</summary>
    internal static class AdminPanelNet
    {
        private const string ToServer = "DH_Admin_ToServer";
        private const string StatusRpc = "DH_Admin_Status";
        private const string DeadheimGuid = "Detalhes.Deadheim";

        internal static string LastStatus { get; private set; }

        internal static void Register(ZRoutedRpc rpc)
        {
            rpc.Register(StatusRpc, (Action<long, string>)RPC_Status);
        }

        private static void RPC_Status(long sender, string text)
        {
            if (ZRoutedRpc.instance == null || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
            LastStatus = text;
        }

        internal static void ClearStatus() => LastStatus = null;

        internal static bool LocalIsAdmin() =>
            Plugin.LocalPlayerIsServerSyncAdmin ||
            (ZNet.instance != null && ZNet.instance.LocalPlayerIsAdminOrHost());

        /// <summary>A Deadheim [Admin] setting (synced from the server there), or the fallback
        /// when Deadheim is missing.</summary>
        internal static T DeadheimSetting<T>(string key, T fallback)
        {
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(DeadheimGuid, out var info) && info?.Instance != null &&
                info.Instance.Config.TryGetEntry<T>("Admin", key, out var entry))
                return entry.Value;
            return fallback;
        }

        private static bool Send(ZPackage pkg)
        {
            if (ZRoutedRpc.instance == null) return false;
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), ToServer, pkg);
            LastStatus = "Enviado ao servidor...";
            return true;
        }

        private static ZPackage Package(string op)
        {
            var pkg = new ZPackage();
            pkg.Write(op);
            return pkg;
        }

        internal static void Message(bool preview, string text, string color, float seconds, bool blink, bool sound)
        {
            var pkg = Package(preview ? "previa" : "msg");
            pkg.Write(text);
            pkg.Write(color);
            pkg.Write(seconds);
            pkg.Write(blink);
            pkg.Write(sound);
            Send(pkg);
        }

        internal static void GoTo(long peer)
        {
            var pkg = Package("ir");
            pkg.Write(peer);
            Send(pkg);
        }

        internal static void Pull(long peer)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            var pkg = Package("puxar");
            pkg.Write(peer);
            pkg.Write(player.transform.position + player.transform.forward * 2f + Vector3.up * 0.5f);
            Send(pkg);
        }

        internal static void Buff(string effect, float seconds, float radius, bool includeMe)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            var pkg = Package("buff");
            pkg.Write(effect);
            pkg.Write(seconds);
            pkg.Write(radius);
            pkg.Write(player.transform.position);
            pkg.Write(includeMe);
            Send(pkg);
        }

        internal static void Heal(float radius, bool includeMe)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;
            var pkg = Package("curar");
            pkg.Write(radius);
            pkg.Write(player.transform.position);
            pkg.Write(includeMe);
            Send(pkg);
        }
    }

    /// <summary>Shared bits of the three admin pages: a vertical column, option strips that
    /// mark the chosen button, and the status line fed by the server's answers.</summary>
    internal abstract class AdminPanelViewBase : NpcViewBase
    {
        private float _nextSend;

        public override void Refresh() => Say(AdminPanelNet.LastStatus ?? "");

        /// <summary>One click a second: a double click must not send the message twice.</summary>
        protected bool CanSend()
        {
            if (Time.unscaledTime < _nextSend) return false;
            _nextSend = Time.unscaledTime + 1f;
            return true;
        }

        protected RectTransform Column(Transform parent, float spacing = 8f)
        {
            var column = ValheimUi.CreateRect("Column", parent);
            ValheimUi.Stretch(column, 14f, 10f);
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;
            return column;
        }

        protected static TextMeshProUGUI Caption(Transform row, string text, float width)
        {
            var label = ValheimUi.CreateLabel(row, text, 16, ValheimUi.Beige, TextAlignmentOptions.Left);
            ValheimUi.SetWidth(label.gameObject, width);
            return label;
        }

        /// <summary>A row of buttons where one is the current choice (lit, like the active tab).</summary>
        protected static List<Button> Options(Transform row, IList<string> labels, float width, Action<int> onPick)
        {
            var buttons = new List<Button>();
            for (int i = 0; i < labels.Count; i++)
            {
                int index = i;
                var button = ValheimUi.CreateButton(row, labels[i], width, 36f, 15);
                button.onClick.AddListener(() => onPick(index));
                buttons.Add(button);
            }
            return buttons;
        }

        protected static void Mark(List<Button> buttons, int chosen)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var text = buttons[i].GetComponentInChildren<TextMeshProUGUI>();
                if (text != null) text.fontStyle = i == chosen ? FontStyles.Bold | FontStyles.Underline : FontStyles.Normal;
                buttons[i].image.color = i == chosen ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }
        }

        protected static void SetText(Button button, string text)
        {
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = text;
        }
    }

    /// <summary>Global message: text, color, how long it stays, blink and sound, with a
    /// preview only the admin sees.</summary>
    internal sealed class AdminMessageView : AdminPanelViewBase
    {
        private const int MaxLength = 200;

        private static readonly (string name, string hex)[] Colors =
        {
            ("Vermelho", "#FF4040"), ("Laranja", "#FF9A2E"), ("Dourado", "#FFD700"),
            ("Verde", "#40FF70"), ("Ciano", "#40E0FF"), ("Azul", "#4080FF"),
            ("Roxo", "#C060FF"), ("Rosa", "#FF60C0"), ("Branco", "#FFFFFF"),
        };
        private static readonly float[] Durations = { 5f, 8f, 12f, 20f, 30f };

        private TMP_InputField _text;
        private TMP_InputField _hex;
        private TextMeshProUGUI _count;
        private TextMeshProUGUI _sample;
        private List<Button> _colorButtons;
        private List<Button> _durationButtons;
        private Button _blinkButton, _soundButton, _sendButton, _previewButton;

        private string _color = "#FF4040";
        private int _duration = 1;
        private bool _blink = true;
        private bool _sound = true;

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "AdminMessage");
            ValheimUi.Stretch(frame, 0f, 0f);
            var column = Column(frame);

            var head = Row(column, 28f);
            Flexible(Heading(head, "Mensagem para todos os jogadores").gameObject);
            _count = Caption(head, "", 90f);
            _count.alignment = TextAlignmentOptions.Right;

            _text = ValheimUi.CreateInputField(column, "", 800f, 84f, 17, multiline: true);
            _text.characterLimit = MaxLength;

            var colors1 = Row(column, 38f);
            Caption(colors1, "Cor", 60f);
            var colors2 = Row(column, 38f);
            Caption(colors2, "", 60f);
            _colorButtons = new List<Button>();
            for (int i = 0; i < Colors.Length; i++)
            {
                int index = i;
                var button = ValheimUi.CreateButton(i < 5 ? colors1 : colors2,
                    $"<color={Colors[i].hex}>{Colors[i].name}</color>", 128f, 36f, 15);
                button.onClick.AddListener(() => { _color = Colors[index].hex; _hex.text = _color; });
                _colorButtons.Add(button);
            }

            var hexRow = Row(column, 38f);
            Caption(hexRow, "Hex", 60f);
            _hex = ValheimUi.CreateInputField(hexRow, _color, 120f, 36f);
            _hex.characterLimit = 7;
            _hex.onValueChanged.AddListener(v => _color = v);
            _sample = ValheimUi.CreateLabel(hexRow, "", 20, ValheimUi.Beige, TextAlignmentOptions.Left, display: true);
            Flexible(_sample.gameObject);

            var durationRow = Row(column, 38f);
            Caption(durationRow, "Tempo", 60f);
            _durationButtons = Options(durationRow, Durations.Select(d => $"{d:0} s").ToList(), 100f, i => _duration = i);

            var flags = Row(column, 38f);
            Caption(flags, "", 60f);
            _blinkButton = ValheimUi.CreateButton(flags, "", 170f, 36f, 15);
            _blinkButton.onClick.AddListener(() => _blink = !_blink);
            _soundButton = ValheimUi.CreateButton(flags, "", 170f, 36f, 15);
            _soundButton.onClick.AddListener(() => _sound = !_sound);

            Dim(column, "O texto aceita <noparse><b>negrito</b>, <i>itálico</i> e <color=#FF0000>cor</color></noparse>. " +
                        "A mensagem aparece em faixa grande no meio da tela de todos e no chat.");

            var actions = Row(column, 46f);
            Flexible(ValheimUi.CreateRect("Spacer", actions).gameObject);
            _previewButton = ValheimUi.CreateButton(actions, "Prévia (só eu)", 200f, 44f, 16);
            _previewButton.onClick.AddListener(() => Send(preview: true));
            _sendButton = ValheimUi.CreateButton(actions, "ENVIAR A TODOS", 240f, 44f, 18);
            _sendButton.onClick.AddListener(() => Send(preview: false));
        }

        private string ValidColor()
        {
            string c = (_color ?? "").Trim();
            if (c.Length == 6) c = "#" + c;
            return c.Length == 7 && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c.ToUpperInvariant() : null;
        }

        private void Send(bool preview)
        {
            string color = ValidColor();
            string text = (_text.text ?? "").Trim();
            if (color == null || text.Length == 0 || !CanSend()) return;
            AdminPanelNet.Message(preview, text, color, Durations[_duration], _blink, _sound);
        }

        public override void Refresh()
        {
            base.Refresh();
            string color = ValidColor();
            int length = (_text.text ?? "").Length;
            _count.text = $"{length}/{MaxLength}";
            _sample.text = color == null ? "<color=#EE6B57>cor inválida</color>" : $"<color={color}>■■■  Exemplo</color>";
            Mark(_colorButtons, Array.FindIndex(Colors, c => string.Equals(c.hex, color, StringComparison.OrdinalIgnoreCase)));
            Mark(_durationButtons, _duration);
            SetText(_blinkButton, _blink ? "Piscar: sim" : "Piscar: não");
            SetText(_soundButton, _sound ? "Som: sim" : "Som: não");
            bool ok = color != null && (_text.text ?? "").Trim().Length > 0;
            _sendButton.interactable = ok;
            _previewButton.interactable = ok;
        }
    }

    /// <summary>Everyone online but the admin, each with Ir até (the admin goes) and Puxar
    /// (the player comes).</summary>
    internal sealed class AdminPlayersView : AdminPanelViewBase
    {
        private RectTransform _list;
        private TextMeshProUGUI _header;
        private TextMeshProUGUI _empty;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "AdminPlayers");
            ValheimUi.Stretch(frame, 0f, 0f);

            _header = ValheimUi.CreateLabel(frame, "Jogadores online", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)_header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -6f));

            var hint = Dim(frame, "Ir até: você vai até o jogador.  Puxar: o jogador vem até você (vale mesmo caçado ou em combate).");
            ValheimUi.Anchor((RectTransform)hint.transform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(14f, 6f), new Vector2(-14f, 30f));

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 34f), new Vector2(-6f, -40f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "Ninguém online além de você.", 16, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -70f), new Vector2(-14f, -16f));
        }

        private static List<(long peer, string name)> Online()
        {
            var list = new List<(long, string)>();
            if (ZNet.instance == null) return list;
            long me = ZDOMan.GetSessionID();
            foreach (var info in ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID.IsNone() || info.m_characterID.UserID == me) continue;
                list.Add((info.m_characterID.UserID, info.m_name));
            }
            return list.OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private float _nextScan;

        public override void Refresh()
        {
            base.Refresh();
            // The player list only changes when someone joins or leaves: four looks a second, not one a frame.
            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 0.25f;
            var players = Online();
            string signature = string.Join("|", players.Select(p => p.peer + ":" + p.name));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(players);
            }
            _header.text = $"Jogadores online — {players.Count}";
            _empty.gameObject.SetActive(players.Count == 0);
        }

        private void Rebuild(List<(long peer, string name)> players)
        {
            foreach (var row in _rows) if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();

            foreach (var (peer, name) in players)
            {
                var row = Row(_list, 48f);
                _rows.Add(row.gameObject);
                Flexible(ValheimUi.CreateLabel(row, name, 17, ValheimUi.Beige, TextAlignmentOptions.Left).gameObject);

                var go = ValheimUi.CreateButton(row, "Ir até", 140f, 40f, 16);
                go.onClick.AddListener(() => { if (CanSend()) AdminPanelNet.GoTo(peer); });
                var pull = ValheimUi.CreateButton(row, "Puxar", 140f, 40f, 16);
                pull.onClick.AddListener(() => { if (CanSend()) AdminPanelNet.Pull(peer); });
            }
        }
    }

    /// <summary>Buff or heal everyone within a radius of the admin. The buttons are every
    /// beneficial StatusEffect the game's ObjectDB has (mods included), grouped and searchable,
    /// so nothing on the list can be missing. Debuffs and the effects the game uses for its own
    /// plumbing are left out; the server refuses the plumbing ones too (AdminPainel.EfeitoProibido).</summary>
    internal sealed class AdminBuffView : AdminPanelViewBase
    {
        private static readonly float[] Radii = { 10f, 25f, 50f, 100f, 250f };
        private static readonly (string label, float seconds)[] Durations =
        {
            ("Do efeito", 0f), ("5 min", 300f), ("10 min", 600f), ("30 min", 1800f), ("1 hora", 3600f),
        };

        // Debuffs that are plain SE_Stats, so their type alone does not give them away.
        private static readonly string[] DebuffNames =
        {
            "Encumbered", "Tared", "Cold", "Freezing", "Slimed", "SoftDeath", "Immobilized", "Corrupt",
            "Stagger", "Lightning", "Fear", "Puke", "Spirit", "Burning", "Poison", "Wet", "Smoked",
        };

        private static readonly string[] GroupOrder = { "Poderes dos chefes", "Poções e hidromel", "Outros", "Bônus de conjunto" };

        private List<Button> _radiusButtons;
        private List<Button> _durationButtons;
        private Button _includeButton;
        private TMP_InputField _search;
        private RectTransform _list;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private List<(string group, string name, string shown, Sprite icon)> _buffs;
        private string _builtFor;
        private float _maxRadius = 100f;
        private float _nextSettings;

        private int _radius = 1;
        private int _duration;
        private bool _includeMe = true;

        private float Radius => Mathf.Min(Radii[_radius], _maxRadius);
        private float Duration => Durations[_duration].seconds;

        protected override void OnBuild()
        {
            float defaultSeconds = AdminPanelNet.DeadheimSetting("BuffDuracaoPadrao", 600f);
            int closest = 0;
            for (int i = 1; i < Durations.Length; i++)
                if (Mathf.Abs(Durations[i].seconds - defaultSeconds) < Mathf.Abs(Durations[closest].seconds - defaultSeconds)) closest = i;
            _duration = closest;

            var frame = ValheimUi.CreateInlay(Root, "AdminBuffs");
            ValheimUi.Stretch(frame, 0f, 0f);

            var top = ValheimUi.CreateRect("Top", frame);
            ValheimUi.Anchor(top, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, -190f), new Vector2(0f, 0f));
            var column = Column(top, 6f);

            var radiusRow = Row(column, 38f);
            Caption(radiusRow, "Raio", 80f);
            _radiusButtons = Options(radiusRow, Radii.Select(r => $"{r:0} m").ToList(), 100f, i => _radius = i);

            var durationRow = Row(column, 38f);
            Caption(durationRow, "Duração", 80f);
            _durationButtons = Options(durationRow, Durations.Select(d => d.label).ToList(), 100f, i => _duration = i);

            var actions = Row(column, 44f);
            Caption(actions, "", 80f);
            _includeButton = ValheimUi.CreateButton(actions, "", 170f, 40f, 15);
            _includeButton.onClick.AddListener(() => _includeMe = !_includeMe);
            var heal = ValheimUi.CreateButton(actions, "Curar todos na área (vida, stamina e eitr)", 420f, 40f, 15);
            heal.onClick.AddListener(() => { if (CanSend()) AdminPanelNet.Heal(Radius, _includeMe); });

            var searchRow = Row(column, 38f);
            Caption(searchRow, "Buscar", 80f);
            _search = ValheimUi.CreateInputField(searchRow, "", 300f, 36f);
            _search.characterLimit = 40;
            Flexible(Dim(searchRow, "filtra pelo nome do efeito").gameObject);

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 6f), new Vector2(-6f, -196f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);
        }

        private static bool IsBuff(StatusEffect se)
        {
            if (se == null || se.m_icon == null || string.IsNullOrEmpty(se.m_name)) return false;
            if (se is SE_Burning || se is SE_Frost || se is SE_Poison || se is SE_Wet || se is SE_Smoke ||
                se is SE_Puke || se is SE_Spawn || se is SE_HealthUpgrade || se is SE_Harpooned) return false;
            if (se is SE_Stats stats && (stats.m_healthPerTick < 0f || stats.m_healthOverTime < 0f || stats.m_speedModifier < 0f))
                return false;
            string name = se.name ?? "";
            // Potion_frostresist and Potion_poisonresist carry a debuff word but are resistances.
            if (name.StartsWith("Potion_", StringComparison.Ordinal)) return true;
            return !DebuffNames.Any(d => name.IndexOf(d, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GroupOf(string name)
        {
            if (name.StartsWith("GP_", StringComparison.Ordinal)) return "Poderes dos chefes";
            if (name.StartsWith("Potion_", StringComparison.Ordinal) || name.StartsWith("Mead", StringComparison.OrdinalIgnoreCase))
                return "Poções e hidromel";
            if (name.StartsWith("SetEffect_", StringComparison.Ordinal)) return "Bônus de conjunto";
            return "Outros";
        }

        /// <summary>Read once per opening: ObjectDB does not change while the window is up.</summary>
        private static List<(string group, string name, string shown, Sprite icon)> CollectBuffs()
        {
            var list = new List<(string group, string name, string shown, Sprite icon)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (ObjectDB.instance == null) return list;
            foreach (var se in ObjectDB.instance.m_StatusEffects)
            {
                if (!IsBuff(se) || !seen.Add(se.name)) continue;
                string shown = ValheimUi.Localize(se.m_name);
                if (string.IsNullOrWhiteSpace(shown) || shown.StartsWith("[", StringComparison.Ordinal)) shown = se.name;
                list.Add((GroupOf(se.name), se.name, shown, se.m_icon));
            }
            return list
                .OrderBy(b => Array.IndexOf(GroupOrder, b.group))
                .ThenBy(b => b.shown, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void Rebuild(string filter)
        {
            foreach (var row in _rows) if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();

            string group = null;
            RectTransform line = null;
            int inLine = 0;
            int shownCount = 0;
            foreach (var (g, name, shown, icon) in _buffs)
            {
                if (filter.Length > 0 &&
                    shown.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (g != group)
                {
                    group = g;
                    var title = Heading(_list, g);
                    ValheimUi.SetHeight(title.gameObject, 28f);
                    _rows.Add(title.gameObject);
                    line = null;
                }
                if (line == null || inLine == 3)
                {
                    line = Row(_list, 44f);
                    _rows.Add(line.gameObject);
                    inLine = 0;
                }
                inLine++;
                shownCount++;

                var button = ValheimUi.CreateButton(line, shown, 270f, 42f, 15);
                var iconRect = ValheimUi.CreateRect("Icon", button.transform);
                ValheimUi.Anchor(iconRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(8f, -14f), new Vector2(36f, 14f));
                var image = iconRect.gameObject.AddComponent<Image>();
                image.sprite = icon;
                image.preserveAspect = true;
                image.raycastTarget = false;
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                    ValheimUi.Anchor((RectTransform)label.transform, Vector2.zero, Vector2.one, new Vector2(42f, 2f), new Vector2(-6f, -2f));

                string effect = name;
                button.onClick.AddListener(() => { if (CanSend()) AdminPanelNet.Buff(effect, Duration, Radius, _includeMe); });
            }

            if (shownCount == 0)
                _rows.Add(Dim(_list, _buffs.Count == 0 ? "O jogo ainda não carregou os efeitos." : "Nenhum efeito com esse nome.").gameObject);
        }

        public override void Refresh()
        {
            base.Refresh();

            // The Deadheim settings are a lookup through BepInEx: twice a second is plenty.
            if (Time.unscaledTime >= _nextSettings)
            {
                _nextSettings = Time.unscaledTime + 0.5f;
                _maxRadius = AdminPanelNet.DeadheimSetting("BuffRaioMaximo", 100f);
                if (_buffs == null || _buffs.Count == 0) { _buffs = CollectBuffs(); _builtFor = null; }
            }

            // Rebuilt only when the search text changes, never per frame.
            string filter = (_search.text ?? "").Trim();
            if (_buffs != null && filter != _builtFor)
            {
                _builtFor = filter;
                Rebuild(filter);
            }

            Mark(_radiusButtons, _radius);
            Mark(_durationButtons, _duration);
            for (int i = 0; i < Radii.Length; i++) _radiusButtons[i].interactable = Radii[i] <= _maxRadius || i == 0;
            SetText(_includeButton, _includeMe ? "Incluir eu: sim" : "Incluir eu: não");
        }
    }
}
