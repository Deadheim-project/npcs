using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Arena;
using NpcValheim.Npc;
using Object = UnityEngine.Object;

namespace NpcValheim.UI
{
    /// <summary>
    /// F8, Bestiário: the arena's own creatures, in the shape of the Aparência page -- the list
    /// down the left with + Nova, Duplicar and Excluir, and on the right the sub-pages Visual,
    /// Atributos, Resistências, Ataques and Habilidades as rows of fields, each with its "?".
    /// Salvar sends the creature
    /// to the server, which checks the sender is an admin and the numbers are in range, writes
    /// arena-bestiario.json and sends the whole bestiary back to everyone. The page edits copies:
    /// what the server has comes back on Desfazer.
    /// </summary>
    internal sealed class BestiaryAdminView : ArenaViewBase
    {
        private const float SideWidth = 190f;
        private const float LabelWidth = 190f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private enum Sub { Visual, Attributes, Resistances, Attacks, Abilities }

        private static readonly (Sub Sub, string Label)[] Subs =
        {
            (Sub.Visual, "Visual"), (Sub.Attributes, "Atributos"), (Sub.Resistances, "Resistências"),
            (Sub.Attacks, "Ataques"), (Sub.Abilities, "Habilidades"),
        };

        /// <summary>The prototype's TIPS (docs/bestiario-arena.html): title, text and, dimmed under
        /// it, the game's field it changes.</summary>
        private static readonly Dictionary<string, (string Title, string Text)> Tips = new Dictionary<string, (string, string)>
        {
            ["id"] = ("Id", "Como o desafio chama a criatura: bestiario:id. Letras minúsculas, números e _. Fica fixo depois de salvar."),
            ["base"] = ("Criatura base", "De onde vêm o corpo, as animações e os ataques. Chefes não entram na lista." + Game("prefab do jogo")),
            ["nome"] = ("Nome", "Aparece na barra de vida e no aviso de vitória."),
            ["titulo"] = ("Título", "Subtítulo embaixo do nome, na barra de chefe."),
            ["barra"] = ("Barra de chefe", "Mostra a barra grande no topo da tela. É só visual: não conta como chefe derrotado."),
            ["escala"] = ("Tamanho", "Multiplica o modelo e a colisão. Acima de 2× ela pode não passar pelos portões."),
            ["cor"] = ("Tingimento (RGB 0-255)", "Mistura uma cor na textura original. Força 0 deixa a cor original."),
            ["brilho"] = ("Brilho (RGB 0-255)", "Faz olhos, runas e rachaduras emitirem luz."),
            ["vfx"] = ("Efeito no corpo", "Partículas do próprio jogo que acompanham a criatura (as dos efeitos Queimando, Envenenado, Congelado...). Runas brilhando acende uma luz da cor do brilho."),
            ["nivel"] = ("Nível", "1 = sem estrela, 2 = uma, 3 = duas. Cada nível soma a vida base de novo, igual ao jogo."),
            ["vida"] = ("Multiplicador de vida", "Por cima do nível. Vida = base × nível × multiplicador."),
            ["grupo"] = ("Vida por jogador extra (%)", "Com 60%, uma dupla enfrenta 1,6× a vida do solo e um trio, 2,2×."),
            ["vel"] = ("Velocidade", "Multiplica andar e correr. Acima de 1,5× a animação começa a deslizar." + Game("m_walkSpeed / m_runSpeed")),
            ["giro"] = ("Giro", "Quão rápido ela se vira. Giro baixo deixa atacar pelas costas." + Game("m_turnSpeed")),
            ["atord"] = ("Atordoamento", "Quanto dano ela aguenta antes de cambalear. Maior = cambaleia menos." + Game("m_staggerDamageFactor")),
            ["peso"] = ("Resistência a empurrão (%)", "Reduz o empurrão de armas e poderes."),
            ["visao"] = ("Percepção (m)", "Distância em que vê e ouve os jogadores." + Game("m_viewRange / m_hearRange")),
            ["foge"] = ("Foge com pouca vida", "Em chefe isso só irrita, então vem desligado."),
            ["res"] = ("Resistência", "Quanto desse dano ela recebe. Imune = 0%, Muito fraco = 200%." + Game("m_damageModifiers")),
            ["atkDmg"] = ("Dano por tipo", "Os números do golpe. Pode misturar tipos, como metade corte e metade fogo. O nível da criatura multiplica, como no jogo."),
            ["atkRange"] = ("Alcance da IA (m)", "De quão longe ela tenta esse ataque." + Game("m_aiAttackRange")),
            ["atkInt"] = ("Intervalo (s)", "Tempo mínimo entre dois usos." + Game("m_aiAttackInterval")),
            ["atkForce"] = ("Empurrão", "Quanto o golpe joga o jogador para trás." + Game("m_attackForce")),
            ["atkSE"] = ("Efeito ao acertar", "Status aplicado em quem leva o golpe. Só os que existem no jogo." + Game("m_attackStatusEffect")),
            ["phases"] = ("Fases", "Cada habilidade com gatilho Vida ≤ % vira uma fase da luta, na ordem da vida."),
            ["save"] = ("Salvar", "Manda ao servidor. Vale a partir da próxima criatura criada."),
            ["spawn"] = ("Invocar perto de mim", "Cria a versão salva na sua frente, fora de partida, para testar. Ela some sozinha em 5 minutos e não dá drop."),
            ["undo"] = ("Desfazer", "Volta para o que está salvo no servidor."),
        };

        private static string Game(string field) => $"\n<size=85%><color=#9e9480>{field}</color></size>";

        private List<ArenaBeastDef> _items = new List<ArenaBeastDef>();
        /// <summary>Changed here and not yet what the server has.</summary>
        private readonly HashSet<ArenaBeastDef> _dirty = new HashSet<ArenaBeastDef>();
        /// <summary>Made here (+ Nova, Duplicar) and never saved: only these may change their id.</summary>
        private readonly HashSet<ArenaBeastDef> _new = new HashSet<ArenaBeastDef>();
        private int _sel;
        private Sub _sub = Sub.Visual;
        /// <summary>Habilidades: the one being edited, and the kind chosen to add.</summary>
        private int _selAbility;
        private int _pickAbility;
        private int _seenRevision = -1;

        private RectTransform _sideList;
        private readonly List<GameObject> _sideRows = new List<GameObject>();
        private readonly List<(Sub Sub, Button Button)> _subButtons = new List<(Sub, Button)>();
        private Image _tintSwatch, _glowSwatch;

        private ArenaBeastDef Current => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;

        protected override void OnBuild()
        {
            var side = ValheimUi.CreateInlay(Root, "Creatures");
            ValheimUi.Anchor(side, Vector2.zero, new Vector2(0f, 1f), Vector2.zero, new Vector2(SideWidth, 0f));
            var sideArea = ValheimUi.CreateRect("Area", side);
            ValheimUi.Anchor(sideArea, Vector2.zero, Vector2.one, new Vector2(4f, 48f), new Vector2(-4f, -4f));
            _sideList = ValheimUi.CreateScrollList(sideArea, spacing: 4f);

            var foot = ValheimUi.CreateRect("Foot", side);
            ValheimUi.Anchor(foot, Vector2.zero, new Vector2(1f, 0f), new Vector2(6f, 6f), new Vector2(-6f, 42f));
            var footLayout = foot.gameObject.AddComponent<HorizontalLayoutGroup>();
            footLayout.spacing = 4f;
            footLayout.childControlWidth = false;
            footLayout.childControlHeight = true;
            footLayout.childAlignment = TextAnchor.MiddleCenter;
            Btn(foot, "+ Nova", 56f, New, 12);
            Btn(foot, "Duplicar", 56f, Duplicate, 12);
            Btn(foot, "Excluir", 56f, Remove, 12);

            // The right inlay is the arena page's list, with the sub-tabs above and the actions
            // below inside the same frame.
            CreateList(new Vector2(SideWidth + 10f, 0f), Vector2.zero);
            var area = (RectTransform)List.parent.parent;
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(5f, 52f), new Vector2(-5f, -50f));
            var pane = (RectTransform)area.parent;

            var tabs = ValheimUi.CreateRect("SubTabs", pane);
            ValheimUi.Anchor(tabs, new Vector2(0f, 1f), Vector2.one, new Vector2(8f, -46f), new Vector2(-8f, -6f));
            var tabsLayout = tabs.gameObject.AddComponent<HorizontalLayoutGroup>();
            tabsLayout.spacing = 6f;
            tabsLayout.childControlWidth = false;
            tabsLayout.childControlHeight = true;
            tabsLayout.childAlignment = TextAnchor.MiddleLeft;
            foreach (var (sub, label) in Subs)
            {
                var captured = sub;
                var button = ValheimUi.CreateButton(tabs, label, sub == Sub.Resistances ? 120f : 104f, 34f, 14);
                button.onClick.AddListener(() => { _sub = captured; Invalidate(); });
                _subButtons.Add((sub, button));
            }

            var bar = ValheimUi.CreateRect("Actions", pane);
            ValheimUi.Anchor(bar, Vector2.zero, new Vector2(1f, 0f), new Vector2(8f, 6f), new Vector2(-8f, 46f));
            var barLayout = bar.gameObject.AddComponent<HorizontalLayoutGroup>();
            barLayout.spacing = 8f;
            barLayout.childControlWidth = false;
            barLayout.childControlHeight = true;
            barLayout.childAlignment = TextAnchor.MiddleCenter;
            Tip(Btn(bar, "Salvar", 110f, Save, 15), "save");
            Tip(Btn(bar, "Invocar perto de mim", 190f, Summon, 15), "spawn");
            Tip(Btn(bar, "Desfazer", 110f, Undo, 15), "undo");
        }

        /// <summary>Every few seconds while open: asks for the bestiary if it never came.</summary>
        protected override void Poll() => ArenaBestiaryNet.AskIfMissing();

        protected override string Signature() => $"{ArenaBestiary.Revision}:{_sel}:{_sub}:{_items.Count}:{_selAbility}:{_pickAbility}";

        // ------------------------------------------------------------------ the copies

        private static bool Same(ArenaBeastDef a, ArenaBeastDef b) =>
            a != null && b != null && ArenaBestiary.ToJson(a) == ArenaBestiary.ToJson(b);

        /// <summary>What the server sent, merged with what is being edited here: a creature
        /// changed on this page stays as it is until the server's copy matches it (the save
        /// went through) or Desfazer drops it; the rest follow the server.</summary>
        private void Sync()
        {
            if (_seenRevision == ArenaBestiary.Revision) return;
            _seenRevision = ArenaBestiary.Revision;
            string selected = Current?.Id;

            var merged = new List<ArenaBeastDef>();
            var dirty = new HashSet<ArenaBeastDef>();
            var fresh = new HashSet<ArenaBeastDef>();
            foreach (var server in ArenaBestiary.All)
            {
                var mine = _items.FirstOrDefault(i => !_new.Contains(i) && string.Equals(i.Id, server.Id, StringComparison.OrdinalIgnoreCase));
                if (mine != null && _dirty.Contains(mine) && !Same(mine, server))
                {
                    merged.Add(mine);
                    dirty.Add(mine);
                }
                else merged.Add(server.Clone());
            }
            // A new one the server now has as it is here was saved: the server's copy above is it.
            foreach (var mine in _items.Where(_new.Contains))
            {
                if (Same(mine, ArenaBestiary.Find(mine.Id))) continue;
                merged.Add(mine);
                dirty.Add(mine);
                fresh.Add(mine);
            }

            _items = merged;
            _dirty.Clear();
            _dirty.UnionWith(dirty);
            _new.Clear();
            _new.UnionWith(fresh);
            int at = _items.FindIndex(i => string.Equals(i.Id, selected, StringComparison.OrdinalIgnoreCase));
            _sel = at >= 0 ? at : Mathf.Clamp(_sel, 0, Mathf.Max(0, _items.Count - 1));
        }

        private void Touch() => _dirty.Add(Current);

        private string UniqueId(string wanted)
        {
            wanted = ArenaBestiary.IdFrom(wanted);
            if (wanted.Length == 0) wanted = "nova";
            string id = wanted;
            for (int n = 2; _items.Any(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase)); n++)
            {
                string suffix = "_" + n;
                id = wanted.Substring(0, Math.Min(wanted.Length, ArenaBestiary.MaxIdLength - suffix.Length)) + suffix;
            }
            return id;
        }

        private void Add(ArenaBeastDef beast, string message)
        {
            _items.Add(beast);
            _dirty.Add(beast);
            _new.Add(beast);
            _sel = _items.Count - 1;
            _sub = Sub.Visual;
            Invalidate();
            Say(message);
        }

        private void New()
        {
            var beast = new ArenaBeastDef
            {
                Base = "Troll", Name = "Nova criatura", Level = 3, HealthMultiplier = 2f, HealthPerExtraPlayer = 0.6f,
            };
            foreach (var key in ArenaBestiary.DamageTypes) beast.Resistances[key] = "Normal";
            beast.Id = UniqueId("nova");
            Add(beast, "Nova criatura: escolha a base em Visual e clique Salvar.");
        }

        private void Duplicate()
        {
            if (Current == null) return;
            var copy = Current.Clone();
            copy.Id = UniqueId(Current.Id + "_copia");
            copy.Name = (Current.Name + " (cópia)").Substring(0, Math.Min(ArenaBestiary.MaxNameLength, Current.Name.Length + 8));
            Add(copy, "Cópia criada, ainda não salva.");
        }

        private void Remove()
        {
            var beast = Current;
            if (beast == null) return;
            if (_new.Contains(beast))
            {
                _items.Remove(beast);
                _dirty.Remove(beast);
                _new.Remove(beast);
                _sel = Mathf.Clamp(_sel - 1, 0, Mathf.Max(0, _items.Count - 1));
                Invalidate();
                Say($"Descartada: {beast.DisplayName} (não estava salva).");
                return;
            }
            Ask(ArenaWire.ActAdminBeastRemove, beast.Id);
            Say($"Excluindo {beast.DisplayName}...");
        }

        private void Save()
        {
            var beast = Current;
            if (beast == null) return;
            // The server checks the same way; checking here first saves the round trip.
            var problems = new List<string>();
            if (ArenaBestiary.ParseOne(ArenaBestiary.ToJson(beast), problems) == null)
            {
                Say("Não salvei: " + problems.FirstOrDefault() + ".");
                return;
            }
            if (_new.Contains(beast) && ArenaBestiary.Find(beast.Id) != null)
            {
                Say($"Já existe uma criatura com o id \"{beast.Id}\": troque o id antes de salvar.");
                return;
            }
            Ask(ArenaWire.ActAdminBeastSet, ArenaBestiary.ToJson(beast));
            Say($"Salvando {beast.DisplayName}...");
        }

        private void Undo()
        {
            var beast = Current;
            if (beast == null) return;
            var server = _new.Contains(beast) ? null : ArenaBestiary.Find(beast.Id);
            if (server == null)
            {
                Say("Ainda não foi salva: nada a desfazer.");
                return;
            }
            _items[_sel] = server.Clone();
            _dirty.Remove(beast);
            Invalidate();
            Say("Mudanças desfeitas.");
        }

        private void Summon()
        {
            var beast = Current;
            var player = Player.m_localPlayer;
            if (beast == null || player == null) return;
            if (_new.Contains(beast) || _dirty.Contains(beast))
            {
                Say("Salve antes de invocar: o servidor invoca a versão salva.");
                return;
            }
            var t = player.transform;
            var at = t.position + t.forward * (3f + 2f * beast.Scale);
            float yaw = t.eulerAngles.y + 180f;
            Ask(ArenaWire.ActAdminBeastSpawn, string.Join("\n", beast.Id,
                at.x.ToString("R", Inv), at.y.ToString("R", Inv), at.z.ToString("R", Inv), yaw.ToString("R", Inv)));
            Say($"Invocando {beast.DisplayName}...");
        }

        // ------------------------------------------------------------------ drawing

        protected override void Rebuild()
        {
            Sync();
            RebuildSide();
            foreach (var (sub, button) in _subButtons)
            {
                bool on = sub == _sub;
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null)
                {
                    label.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
                    if (sub == Sub.Abilities)
                        label.text = $"Habilidades ({Current?.Abilities.Count ?? 0})";
                }
                button.image.color = on ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
            }

            var beast = Current;
            if (beast == null)
            {
                Line(!ArenaBestiary.Loaded
                    ? "O bestiário ainda não chegou do servidor."
                    : "Nenhuma criatura. Clique + Nova.", 15, ValheimUi.Muted, 30f);
                return;
            }
            switch (_sub)
            {
                case Sub.Visual: Visual(beast); break;
                case Sub.Attributes: Attributes(beast); break;
                case Sub.Resistances: Resistances(beast); break;
                case Sub.Attacks: Attacks(beast); break;
                case Sub.Abilities: Abilities(beast); break;
            }
        }

        private void RebuildSide()
        {
            foreach (var row in _sideRows) if (row != null) Object.Destroy(row);
            _sideRows.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                int index = i;
                var beast = _items[i];
                string text = beast.DisplayName + (_dirty.Contains(beast) ? " <color=#ffe300>*</color>" : "");
                var button = ValheimUi.CreateButton(_sideList, text, 0f, 38f, 14);
                button.onClick.AddListener(() => { _sel = index; _selAbility = 0; Invalidate(); });
                bool on = i == _sel;
                var label = button.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
                button.image.color = on ? Color.white : new Color(0.72f, 0.72f, 0.72f, 1f);
                _sideRows.Add(button.gameObject);
            }
        }

        // One row: the label, the controls, the "?".
        private RectTransform Field(string label)
        {
            var row = ButtonRow();
            Cell(row, label, 15, ValheimUi.Beige, LabelWidth);
            return row;
        }

        private void End(RectTransform row, string tip)
        {
            if (tip != null && Tips.TryGetValue(tip, out var t)) Tip(Btn(row, "?", 30f, () => Say(t.Title + ": " + Plain(t.Text))), tip);
            Flexible(ValheimUi.CreateRect("Fill", row).gameObject);
        }

        private static string Plain(string text)
        {
            int cut = text.IndexOf('\n');
            return cut >= 0 ? text.Substring(0, cut) : text;
        }

        private static Button Tip(Button button, string key)
        {
            if (Tips.TryGetValue(key, out var t)) ValheimUi.AddTooltip(button.gameObject, t.Title, t.Text);
            return button;
        }

        private TMP_InputField Text(RectTransform row, string value, float width, Action<string> set)
        {
            var field = ValheimUi.CreateInputField(row, value ?? "", width, 34f);
            if (width <= 0f) Flexible(field.gameObject);
            field.onValueChanged.AddListener(v => { set(v); Touch(); });
            return field;
        }

        /// <summary>A number box: a value that reads as a number is taken at once (Salvar checks
        /// the range); one that does not is pointed out when the box is left.</summary>
        private TMP_InputField Number(RectTransform row, float value, float width, Action<float> set)
        {
            var field = ValheimUi.CreateInputField(row, F(value), width, 34f);
            field.onValueChanged.AddListener(v =>
            {
                if (!TryNumber(v, out float n)) return;
                set(n);
                Touch();
            });
            field.onEndEdit.AddListener(v => { if (!TryNumber(v, out _)) Say($"\"{v}\" não é um número."); });
            return field;
        }

        private static bool TryNumber(string text, out float value) =>
            float.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, Inv, out value) && !float.IsNaN(value);

        private static string F(float value) => value.ToString("0.##", Inv);

        private void Stepper(RectTransform row, string shown, float width, Action<int> step)
        {
            Btn(row, "<", 34f, () => { step(-1); Touch(); Invalidate(); });
            Cell(row, shown, 14, ValheimUi.Yellow, width, TextAlignmentOptions.Center);
            Btn(row, ">", 34f, () => { step(1); Touch(); Invalidate(); });
        }

        private void Toggle(RectTransform row, bool on, Action<bool> set)
        {
            var button = Btn(row, on ? "Ligado" : "Desligado", 110f, () => { set(!on); Touch(); Invalidate(); });
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.color = on ? ValheimUi.Yellow : ValheimUi.Orange;
        }

        private static T Step<T>(IList<T> list, T current, int delta, Func<T, T, bool> same)
        {
            int at = -1;
            for (int i = 0; i < list.Count; i++) if (same(list[i], current)) { at = i; break; }
            return list[((at < 0 ? 0 : at + delta) % list.Count + list.Count) % list.Count];
        }

        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>R, G, B, the swatch and the strength in %.</summary>
        private Image Rgb(RectTransform row, int[] rgb, float strength, Action<float> setStrength, Func<Image> swatch)
        {
            Image sample = null;
            for (int c = 0; c < 3; c++)
            {
                int channel = c;
                var field = ValheimUi.CreateInputField(row, rgb[c].ToString(Inv), 56f, 34f);
                field.onValueChanged.AddListener(v =>
                {
                    if (!int.TryParse(v.Trim(), NumberStyles.Integer, Inv, out int n)) return;
                    rgb[channel] = n;
                    Touch();
                    var s = swatch();
                    if (s != null) s.color = Swatch(rgb);
                });
            }
            var rect = ValheimUi.CreateRect("Swatch", row);
            ValheimUi.SetWidth(rect.gameObject, 34f);
            sample = rect.gameObject.AddComponent<Image>();
            sample.sprite = ValheimUi.FieldSprite;
            sample.type = Image.Type.Sliced;
            sample.color = Swatch(rgb);
            Cell(row, "Força %", 14, ValheimUi.Muted, 62f);
            Number(row, strength * 100f, 56f, v => setStrength(v / 100f));
            return sample;
        }

        private static Color Swatch(int[] c) =>
            new Color(Mathf.Clamp(c[0], 0, 255) / 255f, Mathf.Clamp(c[1], 0, 255) / 255f, Mathf.Clamp(c[2], 0, 255) / 255f, 1f);

        // ------------------------------------------------------------------ sub-pages

        private void Visual(ArenaBeastDef b)
        {
            Heading("Identidade");
            var row = Field("Id");
            if (_new.Contains(b))
                Text(row, b.Id, 240f, v => b.Id = v.Trim());
            else
                Cell(row, $"{b.Id}  <color=#9e9480>(no desafio: {ArenaBestiary.ChallengeValue(b)})</color>", 14, ValheimUi.Beige, 360f);
            End(row, "id");

            row = Field("Criatura base");
            Stepper(row, $"{NpcBody.DisplayName(b.Base)} ({b.Base})", 230f,
                d => b.Base = Step(ArenaBestiary.Bases, b.Base, d, Eq));
            End(row, "base");

            row = Field("Nome");
            Text(row, b.Name, 300f, v => b.Name = v);
            End(row, "nome");
            row = Field("Título");
            Text(row, b.Title, 300f, v => b.Title = v);
            End(row, "titulo");
            row = Field("Barra de chefe");
            Toggle(row, b.BossBar, v => b.BossBar = v);
            End(row, "barra");

            Heading("Aparência");
            row = Field("Tamanho");
            Number(row, b.Scale, 70f, v => b.Scale = v);
            Cell(row, "×", 14, ValheimUi.Beige, 16f);
            End(row, "escala");
            LiveLine(() => Current != null && Current.Scale > 2f ? "      <color=#ed6b57>Acima de 2×: pode prender nos portões.</color>" : "",
                13, ValheimUi.Muted, 18f);

            row = Field("Tingimento R G B");
            _tintSwatch = Rgb(row, b.Tint, b.TintStrength, v => b.TintStrength = v, () => _tintSwatch);
            End(row, "cor");
            row = Field("Brilho R G B");
            _glowSwatch = Rgb(row, b.Glow, b.GlowStrength, v => b.GlowStrength = v, () => _glowSwatch);
            End(row, "brilho");
            row = Field("Efeito no corpo");
            Stepper(row, b.Effect, 180f, d => b.Effect = Step(ArenaBestiary.Effects, b.Effect, d, Eq));
            End(row, "vfx");
        }

        private static float BaseHealth(string prefab)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            var character = go != null ? go.GetComponent<Character>() : null;
            return character != null ? character.m_health : 0f;
        }

        private static string Hp(float value) => Mathf.RoundToInt(value).ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));

        private void Attributes(ArenaBeastDef b)
        {
            Heading("Vida");
            var row = Field("Nível (1 a 3)");
            Number(row, b.Level, 70f, v => b.Level = Mathf.RoundToInt(v));
            End(row, "nivel");
            row = Field("Vida ×");
            Number(row, b.HealthMultiplier, 70f, v => b.HealthMultiplier = v);
            End(row, "vida");
            row = Field("Vida por jogador extra %");
            Number(row, b.HealthPerExtraPlayer * 100f, 70f, v => b.HealthPerExtraPlayer = v / 100f);
            End(row, "grupo");
            LiveLine(() =>
            {
                var c = Current;
                if (c == null) return "";
                float hp = BaseHealth(c.Base);
                if (hp <= 0f) return $"Base de {c.Base}: o jogo ainda não carregou o prefab.";
                return $"Base de {NpcBody.DisplayName(c.Base)}: {Hp(hp)}. Vida: solo <color=#ffe300>{Hp(ArenaBestiary.MaxHealth(hp, c, 1))}</color>" +
                       $" · dupla <color=#ffe300>{Hp(ArenaBestiary.MaxHealth(hp, c, 2))}</color>" +
                       $" · trio <color=#ffe300>{Hp(ArenaBestiary.MaxHealth(hp, c, 3))}</color>";
            }, 13, ValheimUi.Muted, 22f);

            Heading("Movimento e combate");
            row = Field("Velocidade ×");
            Number(row, b.Speed, 70f, v => b.Speed = v);
            End(row, "vel");
            row = Field("Giro ×");
            Number(row, b.Turn, 70f, v => b.Turn = v);
            End(row, "giro");
            row = Field("Atordoamento ×");
            Number(row, b.Stagger, 70f, v => b.Stagger = v);
            End(row, "atord");
            row = Field("Resistência a empurrão %");
            Number(row, b.PushResist * 100f, 70f, v => b.PushResist = v / 100f);
            End(row, "peso");
            row = Field("Percepção (m)");
            Number(row, b.Perception, 70f, v => b.Perception = v);
            End(row, "visao");
            row = Field("Foge com pouca vida");
            Toggle(row, b.FleeLowHealth, v => b.FleeLowHealth = v);
            End(row, "foge");
        }

        private void Resistances(ArenaBeastDef b)
        {
            Line("Quanto de cada dano ela recebe. Dê a cada criatura uma fraqueza clara, para o grupo escolher a arma certa.",
                13, ValheimUi.Muted, 40f);
            var names = ArenaBestiary.Modifiers.Select(m => m.Name).ToList();
            for (int i = 0; i < ArenaBestiary.DamageTypes.Length; i++)
            {
                string key = ArenaBestiary.DamageTypes[i];
                string current = b.Resistances.TryGetValue(key, out var m) ? m : "Normal";
                var row = ButtonRow();
                Cell(row, $"<color={ArenaBestiary.DamageColors[i]}>{ArenaBestiary.DamageLabels[i]}</color>", 15, ValheimUi.Beige, LabelWidth);
                Stepper(row, current, 180f, d => b.Resistances[key] = Step(names, current, d, Eq));
                float factor = ArenaBestiary.ModifierFactor(current);
                string color = factor < 1f ? "#73db6b" : factor > 1f ? "#ed6b57" : "#9e9480";
                Cell(row, $"<color={color}>{Mathf.RoundToInt(factor * 100f)}%</color>", 15, ValheimUi.Beige, 56f, TextAlignmentOptions.Right);
                End(row, "res");
            }
        }

        // ------------------------------------------------------------------ Ataques

        /// <summary>A "?" with a tooltip made here (abilities, triggers), not one of <see cref="Tips"/>.</summary>
        private void TipEnd(RectTransform row, string title, string text)
        {
            var button = Btn(row, "?", 30f, () => Say(title + ": " + Plain(text)));
            ValheimUi.AddTooltip(button.gameObject, title, text);
            Flexible(ValheimUi.CreateRect("Fill", row).gameObject);
        }

        /// <summary>The base's own numbers for one of its items, as a new attack to edit.</summary>
        private static ArenaBeastAttack FromGame(string item)
        {
            var attack = new ArenaBeastAttack { Item = item };
            var shared = ArenaBeastBody.AttackShared(item);
            if (shared == null) return attack;
            var d = shared.m_damages;
            attack.Damage = new[] { d.m_blunt, d.m_slash, d.m_pierce, d.m_fire, d.m_frost, d.m_lightning, d.m_poison, d.m_spirit };
            attack.Range = shared.m_aiAttackRange;
            attack.Interval = shared.m_aiAttackInterval;
            attack.Force = shared.m_attackForce;
            string status = shared.m_attackStatusEffect != null ? shared.m_attackStatusEffect.name : "";
            attack.HitEffect = ArenaBeastKit.HitEffects.FirstOrDefault(e => e.StatusEffect.Length > 0 &&
                string.Equals(e.StatusEffect, status, StringComparison.OrdinalIgnoreCase)).Name ?? ArenaBestiary.NoEffect;
            return attack;
        }

        private void Attacks(ArenaBeastDef b)
        {
            Line("Os ataques vêm da criatura base: dá para mudar os números, mas não criar animação nova. Para um golpe a mais, use uma habilidade.",
                13, ValheimUi.Muted, 40f);
            var items = ArenaBeastBody.AttackItems(b.Base);
            if (items == null)
            {
                Line($"O jogo ainda não carregou {b.Base}: abra de novo no mundo.", 14, ValheimUi.Muted, 24f);
                return;
            }
            if (items.Count == 0) Line($"{NpcBody.DisplayName(b.Base)} não tem ataques de item.", 14, ValheimUi.Muted, 24f);
            var foreign = b.Attacks.Where(a => !items.Contains(a.Item)).Select(a => a.Item).ToList();
            if (foreign.Count > 0)
                Line($"<color=#ed6b57>De outra base, descartados ao salvar: {string.Join(", ", foreign)}</color>", 13, ValheimUi.Muted, 22f);

            for (int i = 0; i < items.Count; i++)
            {
                string item = items[i];
                var own = b.Attacks.Find(a => string.Equals(a.Item, item, StringComparison.OrdinalIgnoreCase));
                var shown = own ?? FromGame(item);
                // The first change turns the game's numbers into this creature's.
                ArenaBeastAttack Edit()
                {
                    var mine = b.Attacks.Find(a => string.Equals(a.Item, item, StringComparison.OrdinalIgnoreCase));
                    if (mine != null) return mine;
                    b.Attacks.Add(shown);
                    return shown;
                }

                Heading($"{ArenaBeastBody.AttackName(item)}  <size=70%><color=#9e9480>ataque {i + 1} de {NpcBody.DisplayName(b.Base)}" +
                        (own == null ? " · números do jogo" : "") + "</color></size>");
                for (int half = 0; half < 2; half++)
                {
                    var row = Field(half == 0 ? "Dano por tipo" : "");
                    for (int t = half * 4; t < half * 4 + 4; t++)
                    {
                        int type = t;
                        Cell(row, $"<color={ArenaBestiary.DamageColors[t]}>{ArenaBestiary.DamageLabels[t].Substring(0, Math.Min(5, ArenaBestiary.DamageLabels[t].Length))}</color>", 13, ValheimUi.Beige, 48f);
                        Number(row, shown.Damage[t], 54f, v => Edit().Damage[type] = v);
                    }
                    if (half == 0) End(row, "atkDmg");
                    else Flexible(ValheimUi.CreateRect("Fill", row).gameObject);
                }
                var r = Field("Alcance da IA (m)");
                Number(r, shown.Range, 70f, v => Edit().Range = v);
                End(r, "atkRange");
                r = Field("Intervalo (s)");
                Number(r, shown.Interval, 70f, v => Edit().Interval = v);
                End(r, "atkInt");
                r = Field("Empurrão");
                Number(r, shown.Force, 70f, v => Edit().Force = v);
                End(r, "atkForce");
                r = Field("Efeito ao acertar");
                var names = ArenaBeastKit.HitEffects.Select(e => e.Name).ToList();
                Stepper(r, shown.HitEffect, 180f, d => Edit().HitEffect = Step(names, shown.HitEffect, d, Eq));
                if (own != null)
                    Btn(r, "Números do jogo", 150f, () => { b.Attacks.Remove(own); Touch(); Invalidate(); }, 13);
                End(r, "atkSE");
            }
        }

        // ------------------------------------------------------------------ Habilidades

        private static string AbilityTip(ArenaAbilitySpec s) =>
            $"{s.What}\n\n<color=#ffb02e>Aviso:</color> {s.Warn}\n<color=#ffb02e>Como reagir:</color> {s.React}" +
            Game($"{s.Category} · fase {s.Phase} da entrega" + (s.Implemented ? "" : " (ainda não age)"));

        private void Abilities(ArenaBeastDef b)
        {
            var row = Field("Fases pela vida");
            Cell(row, ArenaBeastKit.PhaseLine(b.Abilities), 14, ValheimUi.Yellow, 420f);
            End(row, "phases");

            Heading($"Habilidades ({b.Abilities.Count} de {ArenaBeastKit.MaxAbilities})");
            _selAbility = Mathf.Clamp(_selAbility, 0, Mathf.Max(0, b.Abilities.Count - 1));
            for (int i = 0; i < b.Abilities.Count; i++)
            {
                int index = i;
                var a = b.Abilities[i];
                var spec = a.Spec;
                var line = ButtonRow();
                Cell(line, $"<size=130%>{spec.Rune}</size>", 15, ValheimUi.Yellow, 34f, TextAlignmentOptions.Center);
                string off = a.On ? "" : "<color=#9e9480>[desligada]</color> ";
                string later = spec.Implemented ? "" : " <color=#9e9480>· Fase 3: ainda não age</color>";
                Cell(line, $"{off}<color=#ffb02e>{spec.Name}</color> · {ArenaBeastKit.When(a)}{later}", 14, ValheimUi.Beige, 400f);
                var tip = Btn(line, "?", 30f, () => Say(spec.Name + ": " + spec.What));
                ValheimUi.AddTooltip(tip.gameObject, $"{spec.Rune} {spec.Name}", AbilityTip(spec));
                Btn(line, index == _selAbility ? "Editando" : "Editar", 90f, () => { _selAbility = index; Invalidate(); });
                Btn(line, "Remover", 90f, () =>
                {
                    b.Abilities.RemoveAt(index);
                    _selAbility = Mathf.Clamp(_selAbility, 0, Mathf.Max(0, b.Abilities.Count - 1));
                    Touch();
                    Invalidate();
                });
                Flexible(ValheimUi.CreateRect("Fill", line).gameObject);
            }

            if (b.Abilities.Count < ArenaBeastKit.MaxAbilities)
            {
                var kinds = ArenaBeastKit.Abilities;
                _pickAbility = ((_pickAbility % kinds.Length) + kinds.Length) % kinds.Length;
                var pick = kinds[_pickAbility];
                var add = Field("Adicionar");
                Btn(add, "<", 34f, () => { _pickAbility--; Invalidate(); });
                Cell(add, $"{pick.Rune} {pick.Name}", 14, ValheimUi.Yellow, 200f, TextAlignmentOptions.Center);
                Btn(add, ">", 34f, () => { _pickAbility++; Invalidate(); });
                Btn(add, "Adicionar", 100f, () =>
                {
                    b.Abilities.Add(ArenaBeastKit.NewAbility(pick.Key));
                    _selAbility = b.Abilities.Count - 1;
                    Touch();
                    Invalidate();
                });
                TipEnd(add, $"{pick.Rune} {pick.Name}", AbilityTip(pick));
            }

            if (b.Abilities.Count == 0) return;
            var e = b.Abilities[_selAbility];
            var s = e.Spec;
            Heading($"Editando: {s.Rune} {s.Name}");
            Line(s.What + (s.Implemented ? "" : "  <color=#9e9480>(Fase 3: fica salva, mas ainda não age no jogo.)</color>"), 13, ValheimUi.Muted, 34f);

            var r = Field("Ligada");
            Toggle(r, e.On, v => e.On = v);
            TipEnd(r, $"{s.Rune} {s.Name}", AbilityTip(s));

            var trigger = ArenaBeastKit.Trigger(e.Trigger) ?? ArenaBeastKit.Triggers[0];
            r = Field("Gatilho");
            Stepper(r, trigger.Label, 180f, d =>
            {
                var next = Step(ArenaBeastKit.Triggers, trigger, d, (x, y) => x.Key == y.Key);
                e.Trigger = next.Key;
                e.Value = next.Default;
            });
            TipEnd(r, trigger.Label, trigger.Tip);
            if (e.Trigger != "start")
            {
                r = Field(e.Trigger == "hp" ? "Vida %" : "Segundos");
                Number(r, e.Value, 70f, v => e.Value = v);
                TipEnd(r, trigger.Label, trigger.Tip + string.Format(Inv, "\nDe {0:0.##} a {1:0.##}.", trigger.Min, trigger.Max));
            }

            foreach (var p in s.Params)
            {
                r = Field(p.Label);
                switch (p.Kind)
                {
                    case ArenaParamKind.DamageType:
                        int at = Array.IndexOf(ArenaBestiary.DamageTypes, e.DamageType);
                        string label = at >= 0 ? $"<color={ArenaBestiary.DamageColors[at]}>{ArenaBestiary.DamageLabels[at]}</color>" : e.DamageType;
                        Stepper(r, label, 180f, d => e.DamageType = Step(ArenaBestiary.DamageTypes, e.DamageType, d, Eq));
                        Flexible(ValheimUi.CreateRect("Fill", r).gameObject);
                        break;
                    case ArenaParamKind.Prefab:
                        Text(r, e.Prefab, 240f, v => e.Prefab = v.Trim());
                        TipEnd(r, p.Label, "Nome do prefab do jogo (como no campo Criatura do desafio). Chefes e jogadores são recusados ao salvar." + Game("prefab do jogo"));
                        break;
                    default:
                        string key = p.Key;
                        Number(r, e.N(key), 70f, v => e.Numbers[key] = v);
                        TipEnd(r, p.Label, string.Format(Inv, "De {0:0.##} a {1:0.##}{2}.", p.Min, p.Max, p.Kind == ArenaParamKind.Integer ? ", inteiro" : ""));
                        break;
                }
            }
            r = Field("Aviso ao jogador");
            Cell(r, s.Warn, 14, ValheimUi.Beige, 460f);
            r = Field("Como reagir");
            Cell(r, s.React, 14, ValheimUi.Beige, 460f);
        }
    }
}
