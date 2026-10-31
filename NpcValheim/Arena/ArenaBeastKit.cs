using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LiteDB;

namespace NpcValheim.Arena
{
    /// <summary>
    /// One attack of a creature of the Bestiário: an item the base already carries (its
    /// m_defaultItems, m_randomWeapon, m_randomSets...), named by the item's prefab, with new
    /// numbers. The animation and the hit stay the base's; only the numbers change, and only
    /// on that one creature (ArenaBeastBody.ApplyAttacks copies the item's shared data first).
    /// </summary>
    internal sealed class ArenaBeastAttack
    {
        public string Item = "";
        /// <summary>By <see cref="ArenaBestiary.DamageTypes"/>: blunt, slash, pierce, fire, frost, lightning, poison, spirit.</summary>
        public float[] Damage = new float[8];
        public float Range = 2f;
        public float Interval = 2f;
        public float Force = 30f;
        /// <summary>One of <see cref="ArenaBeastKit.HitEffects"/> by its name.</summary>
        public string HitEffect = ArenaBestiary.NoEffect;
    }

    /// <summary>One ability of a creature: its kind (stomp, charge...), when it goes off and its numbers.</summary>
    internal sealed class ArenaBeastAbility
    {
        public string Kind = "";
        public bool On = true;
        public string Trigger = "every";
        public float Value;
        /// <summary>The numeric parameters of the kind, every one present.</summary>
        public readonly Dictionary<string, float> Numbers = new Dictionary<string, float>(StringComparer.Ordinal);
        /// <summary>For the kinds with a damage type (tipoDano): blunt, fire...</summary>
        public string DamageType = "";
        /// <summary>For the summon: the creature's prefab.</summary>
        public string Prefab = "";

        public ArenaAbilitySpec Spec => ArenaBeastKit.Spec(Kind);

        public float N(string key) => Numbers.TryGetValue(key, out var v) ? v : Spec?.Param(key)?.Default ?? 0f;

        /// <summary>The motor runs it: switched on and one of the kinds of this phase.</summary>
        public bool Acts => On && (Spec?.Implemented ?? false);
    }

    internal enum ArenaParamKind { Number, Integer, DamageType, Prefab }

    internal sealed class ArenaParamSpec
    {
        public string Key;
        public string Label;
        public ArenaParamKind Kind;
        public float Default;
        public float Min;
        public float Max;
        public string DefaultText;
    }

    internal sealed class ArenaAbilitySpec
    {
        public string Key;
        public string Rune;
        public string Name;
        public string Category;
        /// <summary>Delivery phase (2 or 3). Phase 3 kinds can be saved and edited, the motor ignores them.</summary>
        public int Phase;
        public string What;
        public string Warn;
        public string React;
        public string Trigger;
        public float TriggerValue;
        public ArenaParamSpec[] Params;

        public bool Implemented => Phase <= 2;

        public ArenaParamSpec Param(string key) => Array.Find(Params, p => p.Key == key);
    }

    internal sealed class ArenaTriggerSpec
    {
        public string Key;
        public string Label;
        public string Tip;
        public float Default;
        public float Min;
        public float Max;
    }

    /// <summary>
    /// The catalogue of the prototype (docs/bestiario-arena.html: ABIL, TRIG, STATUS) and the
    /// parse of the "ataques" and "habilidades" of a creature. Pure: the checks run it.
    /// </summary>
    internal static class ArenaBeastKit
    {
        internal const int MaxAttacks = 12;
        internal const int MaxAbilities = 6;
        internal const int MaxMinionsAlive = 8;
        internal const int MaxPrefabLength = 64;
        /// <summary>Fúria do Tempo stops adding here (+200%).</summary>
        internal const float TimeRageCap = 200f;
        internal const float TimeRageStep = 10f;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>What a hit of an attack may leave on the victim: the name shown and the game's
        /// status effect (all of them in SEMan; "Sangrando" is not in the game).</summary>
        internal static readonly (string Name, string StatusEffect)[] HitEffects =
        {
            (ArenaBestiary.NoEffect, ""), ("Queimando", "Burning"), ("Envenenado", "Poison"), ("Congelado (lento)", "Frost"),
            ("Molhado", "Wet"), ("Eletrizado", "Lightning"), ("Enfumaçado", "Smoked"),
        };

        internal static string CanonicalHitEffect(string name) =>
            HitEffects.Select(e => e.Name).FirstOrDefault(n => string.Equals(n, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));

        internal static string StatusEffectOf(string hitEffect) =>
            HitEffects.FirstOrDefault(e => string.Equals(e.Name, hitEffect, StringComparison.OrdinalIgnoreCase)).StatusEffect ?? "";

        internal static readonly ArenaTriggerSpec[] Triggers =
        {
            new ArenaTriggerSpec { Key = "start", Label = "Início da luta", Tip = "Usa uma vez, assim que os portões abrem.", Default = 0, Min = 0, Max = 0 },
            new ArenaTriggerSpec { Key = "hp", Label = "Vida ≤ %", Tip = "Dispara quando a vida cai até o valor. Vira uma fase na barra.", Default = 50, Min = 1, Max = 99 },
            new ArenaTriggerSpec { Key = "every", Label = "A cada N s", Tip = "Repete com recarga, com ±20% de variação para não ficar previsível.", Default = 20, Min = 3, Max = 600 },
            new ArenaTriggerSpec { Key = "time", Label = "Tempo de luta ≥", Tip = "Dispara depois de tantos segundos de luta. Bom contra grupo que enrola.", Default = 300, Min = 1, Max = 3600 },
        };

        internal static ArenaTriggerSpec Trigger(string key) => Array.Find(Triggers, t => t.Key == key);

        private static ArenaParamSpec P(string key, string label, float value, float min, float max) =>
            new ArenaParamSpec { Key = key, Label = label, Kind = ArenaParamKind.Number, Default = value, Min = min, Max = max };

        private static ArenaParamSpec I(string key, string label, int value, int min, int max) =>
            new ArenaParamSpec { Key = key, Label = label, Kind = ArenaParamKind.Integer, Default = value, Min = min, Max = max };

        private static ArenaParamSpec Type(string value) =>
            new ArenaParamSpec { Key = "tipoDano", Label = "Tipo", Kind = ArenaParamKind.DamageType, DefaultText = value };

        private static ArenaParamSpec Prefab(string value) =>
            new ArenaParamSpec { Key = "prefab", Label = "Criatura", Kind = ArenaParamKind.Prefab, DefaultText = value };

        internal static readonly ArenaAbilitySpec[] Abilities =
        {
            new ArenaAbilitySpec { Key = "stomp", Rune = "ᚦ", Name = "Pisão Sísmico", Category = "Área", Phase = 2,
                What = "Bate no chão e causa dano e empurrão a todos dentro do raio.",
                Warn = "Um anel vermelho aparece no chão durante o preparo.", React = "Sair do anel ou rolar no último instante.",
                Trigger = "every", TriggerValue = 14,
                Params = new[] { P("dano", "Dano", 60, 0, 2000), P("raio", "Raio (m)", 6, 1, 20), P("empurrao", "Empurrão", 80, 0, 500), P("preparo", "Preparo (s)", 1.2f, 0.3f, 5) } },
            new ArenaAbilitySpec { Key = "charge", Rune = "ᚱ", Name = "Investida", Category = "Movimento", Phase = 2,
                What = "Corre em linha reta até o jogador mais distante e derruba quem estiver no caminho.",
                Warn = "Rugido e uma faixa no chão mostrando a linha.", React = "Sair da linha.",
                Trigger = "every", TriggerValue = 20,
                Params = new[] { P("dano", "Dano", 45, 0, 2000), P("distancia", "Distância (m)", 18, 3, 40), P("preparo", "Preparo (s)", 1, 0.3f, 5) } },
            new ArenaAbilitySpec { Key = "summon", Rune = "ᛗ", Name = "Invocar Lacaios", Category = "Invocação", Phase = 2,
                What = "Chama criaturas de apoio. Os lacaios são da partida e somem quando a luta acaba.",
                Warn = "Fumaça nos pontos de surgimento, 2 s antes.", React = "Matar os lacaios rápido ou afastar o chefe deles.",
                Trigger = "hp", TriggerValue = 60,
                Params = new[] { Prefab("Greydwarf_Elite"), I("qtd", "Quantidade", 2, 1, 8), I("nivel", "Nível", 1, 1, 3), I("maxVivos", "Máx. vivos", 4, 1, 8) } },
            new ArenaAbilitySpec { Key = "enrage", Rune = "ᛏ", Name = "Fúria", Category = "Fortalecer", Phase = 2,
                What = "Fica mais rápida e bate mais forte até o fim da luta.",
                Warn = "Brilho vermelho, rugido longo e FÚRIA na barra.", React = "Guardar defesa e cura para essa parte.",
                Trigger = "hp", TriggerValue = 30,
                Params = new[] { P("velocidade", "Velocidade ×", 1.35f, 1, 3), P("dano", "Dano ×", 1.25f, 1, 5) } },
            new ArenaAbilitySpec { Key = "zone", Rune = "ᚲ", Name = "Zona Perigosa", Category = "Área", Phase = 2,
                What = "Marca o chão embaixo de um jogador. Depois do aviso, vira poça de fogo, veneno ou gelo.",
                Warn = "Círculo pulsando sob o jogador.", React = "Sair do círculo e não lutar em cima da poça.",
                Trigger = "every", TriggerValue = 12,
                Params = new[] { Type("fire"), P("dps", "Dano/s", 15, 0, 500), P("raio", "Raio (m)", 3, 1, 20), P("duracao", "Duração (s)", 8, 1, 60), P("aviso", "Aviso (s)", 1.5f, 0.3f, 5) } },
            new ArenaAbilitySpec { Key = "timerage", Rune = "ᛊ", Name = "Fúria do Tempo", Category = "Fortalecer", Phase = 2,
                What = "Depois de um tempo de luta, o dano sobe a cada 10 s. Impede vencer só enrolando.",
                Warn = "Mensagem no centro da tela e a barra pisca.", React = "Derrubar antes.",
                Trigger = "time", TriggerValue = 300,
                Params = new[] { P("aumento", "+% dano/10 s", 10, 1, 100) } },
            new ArenaAbilitySpec { Key = "shield", Rune = "ᛒ", Name = "Escudo Rúnico", Category = "Defesa", Phase = 3,
                What = "Fica imune e faz surgir totens. O escudo cai quando todos os totens forem destruídos.",
                Warn = "Bolha de runas e totens brilhando.", React = "Dividir o grupo para quebrar os totens.",
                Trigger = "hp", TriggerValue = 50,
                Params = new[] { I("totens", "Totens", 3, 1, 6), P("vidaTotem", "Vida do totem", 400, 1, 20000) } },
            new ArenaAbilitySpec { Key = "rain", Rune = "ᚺ", Name = "Chuva de Projéteis", Category = "Área", Phase = 3,
                What = "Faz cair pedras ou meteoros em volta dos jogadores.",
                Warn = "Sombra no chão onde cada um vai cair.", React = "Andar entre as sombras.",
                Trigger = "every", TriggerValue = 25,
                Params = new[] { I("projeteis", "Projéteis", 8, 1, 30), P("dano", "Dano", 40, 0, 2000), P("raio", "Área (m)", 12, 1, 30), Type("blunt") } },
            new ArenaAbilitySpec { Key = "blink", Rune = "ᛉ", Name = "Piscar", Category = "Movimento", Phase = 3,
                What = "Some e reaparece atrás do jogador que mais causou dano.",
                Warn = "Fumaça no ponto de chegada, meio segundo antes.", React = "Quem bate mais fica pronto para virar e bloquear.",
                Trigger = "every", TriggerValue = 18,
                Params = new[] { P("alcance", "Alcance (m)", 15, 2, 40) } },
            new ArenaAbilitySpec { Key = "aura", Rune = "ᛟ", Name = "Aura", Category = "Área", Phase = 3,
                What = "Causa dano ou lentidão contínuos a quem fica perto.",
                Warn = "Partículas em volta da criatura o tempo todo.", React = "Bater e sair, sem ficar colado.",
                Trigger = "start", TriggerValue = 0,
                Params = new[] { Type("frost"), P("dps", "Dano/s", 6, 0, 500), P("raio", "Raio (m)", 3.5f, 1, 20), P("lentidao", "Lentidão %", 20, 0, 90) } },
            new ArenaAbilitySpec { Key = "regen", Rune = "ᛜ", Name = "Regeneração", Category = "Defesa", Phase = 3,
                What = "Se ninguém bater nela por um tempo, recupera vida aos poucos.",
                Warn = "Brilho verde e números de cura.", React = "Manter alguém sempre causando dano.",
                Trigger = "start", TriggerValue = 0,
                Params = new[] { P("semDano", "Sem dano (s)", 6, 1, 60), P("porSeg", "Cura %/s", 1.5f, 0.1f, 20) } },
            new ArenaAbilitySpec { Key = "thorns", Rune = "ᛞ", Name = "Espinhos", Category = "Defesa", Phase = 3,
                What = "Por alguns segundos, devolve parte do dano corpo a corpo.",
                Warn = "A pele brilha e faz som de metal.", React = "Parar de bater corpo a corpo ou trocar para arco e magia.",
                Trigger = "every", TriggerValue = 30,
                Params = new[] { P("reflete", "Devolve %", 40, 0, 200), P("duracao", "Duração (s)", 6, 1, 30) } },
            new ArenaAbilitySpec { Key = "drain", Rune = "ᚾ", Name = "Dreno de Vida", Category = "Fortalecer", Phase = 3,
                What = "Os golpes curam parte do dano causado.",
                Warn = "Rastro vermelho saindo do jogador atingido.", React = "Bloquear em vez de levar golpe.",
                Trigger = "start", TriggerValue = 0,
                Params = new[] { P("cura", "Cura % do dano", 30, 0, 200) } },
            new ArenaAbilitySpec { Key = "leap", Rune = "ᛇ", Name = "Salto Esmagador", Category = "Movimento", Phase = 3,
                What = "Pula em cima de um jogador e causa dano em área ao cair.",
                Warn = "Círculo marcando o ponto de queda.", React = "Sair do círculo.",
                Trigger = "every", TriggerValue = 22,
                Params = new[] { P("dano", "Dano", 70, 0, 2000), P("raio", "Raio (m)", 4, 1, 20), P("alcance", "Alcance (m)", 20, 3, 40) } },
            new ArenaAbilitySpec { Key = "roar", Rune = "ᚹ", Name = "Grito Aterrorizante", Category = "Controle", Phase = 3,
                What = "Quem está perto fica lento por alguns segundos.",
                Warn = "A criatura enche o peito antes de gritar.", React = "Se afastar antes do grito ou usar um Poder.",
                Trigger = "every", TriggerValue = 35,
                Params = new[] { P("raio", "Raio (m)", 10, 1, 20), P("lentidao", "Lentidão %", 40, 0, 90), P("duracao", "Duração (s)", 3, 0.5f, 15) } },
        };

        internal static ArenaAbilitySpec Spec(string key) => Array.Find(Abilities, a => a.Key == key);

        /// <summary>A new ability of a kind, with the prototype's numbers.</summary>
        internal static ArenaBeastAbility NewAbility(string kind)
        {
            var spec = Spec(kind);
            var ability = new ArenaBeastAbility { Kind = kind, Trigger = spec.Trigger, Value = spec.TriggerValue };
            foreach (var p in spec.Params)
            {
                if (p.Kind == ArenaParamKind.DamageType) ability.DamageType = p.DefaultText;
                else if (p.Kind == ArenaParamKind.Prefab) ability.Prefab = p.DefaultText;
                else ability.Numbers[p.Key] = p.Default;
            }
            return ability;
        }

        /// <summary>"vida ≤ 60%", "a cada 14 s"...</summary>
        internal static string When(ArenaBeastAbility a)
        {
            switch (a.Trigger)
            {
                case "hp": return string.Format(Inv, "vida ≤ {0:0}%", a.Value);
                case "every": return string.Format(Inv, "a cada {0:0.#} s", a.Value);
                case "time": return string.Format(Inv, "após {0:0} s", a.Value);
                default: return "início da luta";
            }
        }

        /// <summary>The phases along the health: "60% ᛗ Invocar Lacaios → 30% ᛏ Fúria".</summary>
        internal static string PhaseLine(IEnumerable<ArenaBeastAbility> abilities)
        {
            var hp = abilities.Where(a => a.On && a.Trigger == "hp" && a.Spec != null).OrderByDescending(a => a.Value).ToList();
            return hp.Count == 0 ? "nenhuma habilidade por vida"
                : string.Join(" → ", hp.Select(a => string.Format(Inv, "{0:0}% {1} {2}", a.Value, a.Spec.Rune, a.Spec.Name)));
        }

        internal static bool ValidPrefabName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= MaxPrefabLength &&
            name.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-');

        // ------------------------------------------------------------------ JSON

        internal static BsonDocument ToBson(ArenaBeastAttack a)
        {
            var damage = new BsonDocument();
            for (int i = 0; i < ArenaBestiary.DamageTypes.Length; i++)
                if (a.Damage[i] > 0f) damage[ArenaBestiary.DamageTypes[i]] = ArenaBestiary.N(a.Damage[i]);
            return new BsonDocument
            {
                ["item"] = a.Item,
                ["dano"] = damage,
                ["alcance"] = ArenaBestiary.N(a.Range),
                ["intervalo"] = ArenaBestiary.N(a.Interval),
                ["empurrao"] = ArenaBestiary.N(a.Force),
                ["efeito"] = a.HitEffect,
            };
        }

        internal static BsonDocument ToBson(ArenaBeastAbility a)
        {
            var doc = new BsonDocument
            {
                ["tipo"] = a.Kind,
                ["ligada"] = a.On,
                ["gatilho"] = a.Trigger,
                ["valor"] = ArenaBestiary.N(a.Value),
            };
            var spec = a.Spec;
            if (spec == null) return doc;
            foreach (var p in spec.Params)
            {
                if (p.Kind == ArenaParamKind.DamageType) doc[p.Key] = a.DamageType;
                else if (p.Kind == ArenaParamKind.Prefab) doc[p.Key] = a.Prefab;
                else doc[p.Key] = ArenaBestiary.N(a.N(p.Key));
            }
            return doc;
        }

        /// <summary>The attacks. One without "item" (the first format, with made-up names) or a
        /// hit effect the game lacks is dropped or reset with a warning; numbers out of range
        /// refuse the creature, like every other field.</summary>
        internal static List<ArenaBeastAttack> ParseAttacks(BsonValue value, Action<string> bad, Action<string> warn)
        {
            var result = new List<ArenaBeastAttack>();
            if (value == null || value.IsNull) return result;
            if (!value.IsArray)
            {
                bad("\"ataques\" precisa ser uma lista");
                return result;
            }
            var items = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var entry in value.AsArray)
            {
                n++;
                if (!entry.IsDocument)
                {
                    bad($"ataque {n}: não é um objeto");
                    continue;
                }
                var d = entry.AsDocument;
                string item = ArenaBestiary.Str(d["item"]).Trim();
                if (item.Length == 0)
                {
                    string old = ArenaBestiary.Str(d["nome"]).Trim();
                    warn($"ataque {n}{(old.Length > 0 ? $" (\"{old}\")" : "")} sem \"item\": descartado");
                    continue;
                }
                string where = $"ataque {item}";
                if (!ValidPrefabName(item))
                {
                    bad($"{where}: item precisa ser o nome de um prefab");
                    continue;
                }
                if (!items.Add(item))
                {
                    bad($"{where}: repetido");
                    continue;
                }
                var a = new ArenaBeastAttack { Item = item };
                void Bad(string what) => bad($"{where}: {what}");
                var dmg = d["dano"];
                if (!dmg.IsNull && !dmg.IsDocument) Bad("\"dano\" precisa ser um objeto por tipo");
                else if (dmg.IsDocument)
                {
                    foreach (var key in dmg.AsDocument.Keys)
                    {
                        int at = Array.IndexOf(ArenaBestiary.DamageTypes, key);
                        if (at < 0) { Bad($"tipo de dano \"{key}\" não existe"); continue; }
                        a.Damage[at] = ArenaBestiary.Num(dmg.AsDocument[key], 0f, 0f, 2000f, "dano de " + key, Bad);
                    }
                }
                a.Range = ArenaBestiary.Num(d["alcance"], 2f, 0.5f, 60f, "alcance da IA", Bad);
                a.Interval = ArenaBestiary.Num(d["intervalo"], 2f, 0f, 60f, "intervalo", Bad);
                a.Force = ArenaBestiary.Num(d["empurrao"], 30f, 0f, 1000f, "empurrão", Bad);
                string effect = d["efeito"].IsNull ? ArenaBestiary.Str(d["status"]) : ArenaBestiary.Str(d["efeito"]);
                if (effect.Trim().Length == 0) effect = ArenaBestiary.NoEffect;
                a.HitEffect = CanonicalHitEffect(effect);
                if (a.HitEffect == null)
                {
                    warn($"{where}: efeito ao acertar \"{effect}\" não existe no jogo, ficou Nenhum");
                    a.HitEffect = ArenaBestiary.NoEffect;
                }
                if (result.Count >= MaxAttacks)
                {
                    bad($"no máximo {MaxAttacks} ataques");
                    break;
                }
                result.Add(a);
            }
            return result;
        }

        internal static List<ArenaBeastAbility> ParseAbilities(BsonValue value, Action<string> bad, Action<string> warn)
        {
            var result = new List<ArenaBeastAbility>();
            if (value == null || value.IsNull) return result;
            if (!value.IsArray)
            {
                bad("\"habilidades\" precisa ser uma lista");
                return result;
            }
            if (value.AsArray.Count > MaxAbilities) bad($"no máximo {MaxAbilities} habilidades");
            int n = 0;
            foreach (var entry in value.AsArray)
            {
                n++;
                if (!entry.IsDocument)
                {
                    bad($"habilidade {n}: não é um objeto");
                    continue;
                }
                var d = entry.AsDocument;
                string kind = ArenaBestiary.Str(d["tipo"]).Trim();
                var spec = Spec(kind);
                if (spec == null)
                {
                    bad($"habilidade {n}: tipo \"{kind}\" não existe");
                    continue;
                }
                string where = $"habilidade {n} ({spec.Name})";
                void Bad(string what) => bad($"{where}: {what}");
                var a = new ArenaBeastAbility { Kind = spec.Key, On = ArenaBestiary.Bool(d["ligada"], true) };

                string trigger = d["gatilho"].IsNull ? spec.Trigger : ArenaBestiary.Str(d["gatilho"]).Trim();
                var t = Trigger(trigger);
                if (t == null)
                {
                    Bad($"gatilho \"{trigger}\" não existe (start, hp, every, time)");
                    t = Trigger(spec.Trigger);
                }
                a.Trigger = t.Key;
                a.Value = t.Key == "start" ? 0f
                    : ArenaBestiary.Num(d["valor"], t.Key == spec.Trigger ? spec.TriggerValue : t.Default, t.Min, t.Max,
                        t.Key == "hp" ? "vida do gatilho (%)" : "segundos do gatilho", Bad);

                foreach (var p in spec.Params)
                {
                    var raw = d[p.Key];
                    switch (p.Kind)
                    {
                        case ArenaParamKind.DamageType:
                            string type = raw.IsNull ? p.DefaultText : ArenaBestiary.Str(raw).Trim();
                            if (Array.IndexOf(ArenaBestiary.DamageTypes, type) < 0)
                            {
                                Bad($"tipo de dano \"{type}\" não existe");
                                type = p.DefaultText;
                            }
                            a.DamageType = type;
                            break;
                        case ArenaParamKind.Prefab:
                            string prefab = raw.IsNull ? p.DefaultText : ArenaBestiary.Str(raw).Trim();
                            if (!ValidPrefabName(prefab)) Bad($"criatura \"{prefab}\" precisa ser o nome de um prefab");
                            a.Prefab = prefab;
                            break;
                        default:
                            float v = ArenaBestiary.Num(raw, p.Default, p.Min, p.Max, p.Label.ToLowerInvariant(), Bad);
                            if (p.Kind == ArenaParamKind.Integer && !raw.IsNull && raw.IsNumber &&
                                Math.Abs(raw.AsDouble - Math.Round(raw.AsDouble)) > 0.0001)
                                Bad($"{p.Label.ToLowerInvariant()} precisa ser inteiro");
                            a.Numbers[p.Key] = p.Kind == ArenaParamKind.Integer ? (float)Math.Round(v) : v;
                            break;
                    }
                }
                var known = new HashSet<string>(spec.Params.Select(p => p.Key)) { "tipo", "ligada", "gatilho", "valor" };
                foreach (var key in d.Keys)
                    if (!known.Contains(key)) warn($"{where}: \"{key}\" não é parâmetro desta habilidade e foi ignorado");
                if (spec.Key == "summon" && a.N("qtd") > a.N("maxVivos"))
                    Bad("quantidade maior que o máximo de vivos");
                result.Add(a);
            }
            return result;
        }
    }
}
