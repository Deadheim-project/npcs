using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>One arena as the admin page edits it: a [Arena - Local N] block before it is
    /// saved. The points are what "Marcar aqui" took; null = not marked yet.</summary>
    internal sealed class ArenaDraft
    {
        public int Slot;
        public bool On = true;
        public string Name = "";
        /// <summary>The name it was saved under, so a rename replaces the block instead of
        /// taking a second one. Empty for a new arena.</summary>
        public string SavedName = "";
        public Vector3? Gold, Green, Spectator;
        public float GoldYaw, GreenYaw;
        /// <summary>Aligned with <see cref="ArenaHealth.Types"/>.</summary>
        public readonly bool[] Types = { true, true, true, true, true };

        public bool Serves(int type) => type >= 0 && type < Types.Length && Types[type];
        public bool ServesChallenge => Serves(ArenaHealth.PveType);

        public float? Distance => Gold != null && Green != null ? Vector3.Distance(Gold.Value, Green.Value) : (float?)null;

        public ArenaDraft Clone()
        {
            var copy = new ArenaDraft
            {
                Slot = Slot, On = On, Name = Name, SavedName = SavedName, Gold = Gold, Green = Green, Spectator = Spectator,
                GoldYaw = GoldYaw, GreenYaw = GreenYaw,
            };
            Array.Copy(Types, copy.Types, Types.Length);
            return copy;
        }

        /// <summary>The fields of one Local block, as the cfg has them.</summary>
        public static ArenaDraft FromSlot(int slot, bool on, string name, string gold, string green, string spectator, bool[] types)
        {
            var d = new ArenaDraft { Slot = slot, On = on, Name = (name ?? "").Trim() };
            d.SavedName = d.Name;
            if (ArenaSettingsParser.TryPoint(gold, true, out var g, out float gy)) { d.Gold = g; d.GoldYaw = gy; }
            if (ArenaSettingsParser.TryPoint(green, true, out var v, out float vy)) { d.Green = v; d.GreenYaw = vy; }
            if (ArenaSettingsParser.TryPoint(spectator, false, out var s, out _)) d.Spectator = s;
            for (int i = 0; i < d.Types.Length && types != null && i < types.Length; i++) d.Types[i] = types[i];
            return d;
        }

        /// <summary>The arena the server is sent; null while a start is missing.</summary>
        public ArenaMapDef ToMap()
        {
            if (Gold == null || Green == null) return null;
            var map = new ArenaMapDef
            {
                Name = ArenaSettingsParser.CleanMapName(Name), Gold = Gold.Value, GoldYaw = GoldYaw, Green = Green.Value, GreenYaw = GreenYaw,
                HasSpectator = Spectator != null, Spectator = Spectator ?? Vector3.zero,
            };
            // Every box marked is no restriction at all, as an old arena without uso= reads.
            if (Types.All(t => t)) return map;
            for (int size = 1; size <= 3; size++) if (Types[size - 1]) map.Sizes.Add(size);
            map.Ffa = Types[ArenaHealth.FfaType];
            map.Pve = Types[ArenaHealth.PveType];
            return map;
        }
    }

    /// <summary>One challenge as the admin page edits it (an entry of ArenaLayout.Challenges).</summary>
    internal sealed class ChallengeDraft
    {
        /// <summary>Stable: made by the page for a new challenge, kept by the server forever.</summary>
        public string Id = "";
        /// <summary>The server has it (false for a new one not saved yet).</summary>
        public bool Saved;
        public bool On;
        public string Name = "", Creature = "", Reward = "", Extras = "", Map = "";
        public int Level = 1, Amount;
        public float Health = 1f;
        public int Difficulty = ArenaLayout.DefaultDifficulty;

        public ChallengeDraft Clone() => (ChallengeDraft)MemberwiseClone();
    }

    /// <summary>What a check says to do about itself: the page turns it into a button.</summary>
    internal enum ArenaFix
    {
        None,
        Name,
        TurnOn,
        MarkGold,
        MarkGreen,
        AllTypes,
        MakeZone,
        /// <summary>Mark type <see cref="ArenaCheck.Type"/> on arena <see cref="ArenaCheck.Target"/>.</summary>
        ServeType,
        NewArena,
        OpenArena,
        PickArena,
        PickCreature,
        PickReward,
    }

    internal sealed class ArenaCheck
    {
        public bool Ok;
        public string Text;
        public ArenaFix Fix;
        /// <summary>The button's text when not ok.</summary>
        public string FixLabel;
        /// <summary>The arena the fix acts on, when it is not the one being checked.</summary>
        public string Target;
        public int Type = -1;

        public override string ToString() => (Ok ? "ok " : "x ") + Text;
    }

    /// <summary>
    /// "O que falta para funcionar": every arena and challenge as a list of checks, each with
    /// the fix the admin page offers for it. Pure: the page passes the drafts, the arena areas
    /// and two lookups into the game, so tools/checks/arena runs the same rules as the page.
    /// The server still checks everything again on save and on every rebuild.
    /// </summary>
    internal static class ArenaHealth
    {
        /// <summary>The boxes of a Local block, in the order of ArenaConfig.MapTypes.</summary>
        internal static readonly string[] Types = { "1v1", "2v2", "3v3", "ffa", "pve" };
        internal static readonly string[] TypeLabels = { "1v1", "2v2", "3v3", "Todos contra todos", "Desafio contra criatura" };
        internal const int FfaType = 3;
        internal const int PveType = 4;
        internal const float MaxStartDistance = 300f;
        /// <summary>Closer than this the two teams start on top of each other: the Green was
        /// marked without walking (found in the game, 30/10: 0 m and a ✔).</summary>
        internal const float MinStartDistance = 5f;

        /// <summary>The area made around an arena: centred between the starts, reaching this
        /// far past them.</summary>
        internal const float ZoneMargin = 25f;
        internal const float MinZoneAround = 30f;

        internal static string TypeLabel(int type) => type >= 0 && type < TypeLabels.Length ? TypeLabels[type] : "?";

        /// <summary>The type box of a PvP size (1 to 3), or -1.</summary>
        internal static int TypeOfSize(int size) => size >= 1 && size <= 3 ? size - 1 : size == ArenaRules.FfaSize ? FfaType : -1;

        private static ArenaCheck Ok(string text) => new ArenaCheck { Ok = true, Text = text };

        private static ArenaCheck No(string text, ArenaFix fix, string label, string target = null, int type = -1) =>
            new ArenaCheck { Text = text, Fix = fix, FixLabel = label, Target = target, Type = type };

        /// <summary>
        /// One arena. <paramref name="zones"/> are the Deadheim arena areas, or null without
        /// Deadheim (then a match is held within a radius of the starts and there is nothing to
        /// check). <paramref name="others"/> are the other arenas, for the name.
        /// </summary>
        internal static List<ArenaCheck> ForArena(ArenaDraft d, List<ArenaZone> zones, IEnumerable<ArenaDraft> others)
        {
            var result = new List<ArenaCheck>();
            string name = ArenaSettingsParser.CleanMapName(d.Name);
            if (name.Length == 0) result.Add(No("A arena não tem nome", ArenaFix.Name, "Dar nome"));
            else if (others != null && others.Any(o => o != d && string.Equals(ArenaSettingsParser.CleanMapName(o.Name), name, StringComparison.OrdinalIgnoreCase)))
                result.Add(No($"Outra arena já se chama \"{name}\"", ArenaFix.Name, "Trocar nome"));

            result.Add(d.Gold != null ? Ok("Início do time Ouro marcado") : No("Falta o início do time Ouro", ArenaFix.MarkGold, "Marcar Ouro aqui"));
            result.Add(d.Green != null ? Ok("Início do time Verde marcado") : No("Falta o início do time Verde", ArenaFix.MarkGreen, "Marcar Verde aqui"));
            var distance = d.Distance;
            if (distance != null && distance.Value > MaxStartDistance)
                result.Add(No(string.Format(CultureInfo.InvariantCulture, "Os inícios estão a {0:0} m um do outro (máximo {1:0} m)",
                    distance.Value, MaxStartDistance), ArenaFix.MarkGreen, "Remarcar Verde aqui"));
            if (distance != null && distance.Value < MinStartDistance)
                result.Add(No(string.Format(CultureInfo.InvariantCulture,
                    "Os inícios estão a só {0:0} m um do outro: vá até o outro lado e marque o Verde de novo", distance.Value),
                    ArenaFix.MarkGreen, "Remarcar Verde aqui"));

            var served = Enumerable.Range(0, Types.Length).Where(d.Serves).Select(TypeLabel).ToList();
            result.Add(served.Count > 0
                ? Ok(served.Count == Types.Length ? "Serve para todos os tipos de partida" : "Serve para " + string.Join(", ", served))
                : No("Não serve para nenhum tipo de partida", ArenaFix.AllTypes, "Marcar tudo"));

            if (zones != null && d.Gold != null && d.Green != null)
            {
                var zone = ArenaSettingsParser.ZoneHolding(zones, d.Gold.Value, d.Green.Value);
                result.Add(zone != null
                    ? Ok($"Dentro da área de PvP \"{zone.Name}\"")
                    : No("Fora de uma área de PvP: o servidor recusa a arena", ArenaFix.MakeZone, "Criar área em volta"));
            }

            result.Add(d.On ? Ok("Ligada") : No("Desligada: nenhuma fila usa", ArenaFix.TurnOn, "Ligar"));
            return result;
        }

        internal static bool Ready(ArenaDraft d, List<ArenaZone> zones, IEnumerable<ArenaDraft> others) =>
            ForArena(d, zones, others).All(c => c.Ok);

        /// <summary>
        /// One challenge, against the arenas that are <paramref name="ready"/> and all the
        /// arenas there are. <paramref name="creatureKnown"/> says whether the game (or the
        /// Bestiário, for bestiario:id) has the creature and why not; <paramref name="itemKnown"/>
        /// whether a reward is an item of the game.
        /// </summary>
        internal static List<ArenaCheck> ForChallenge(ChallengeDraft c, IList<ArenaDraft> all, IList<ArenaDraft> ready,
            Func<string, string> creatureProblem, Func<string, bool> itemKnown)
        {
            var result = new List<ArenaCheck>();
            string creature = (c.Creature ?? "").Trim();
            string why = creature.Length == 0 ? "Escolha a criatura" : creatureProblem?.Invoke(creature);
            result.Add(why == null ? Ok("A criatura existe") : No(why, ArenaFix.PickCreature, "Escolher criatura"));

            string map = (c.Map ?? "").Trim();
            if (map.Length > 0)
            {
                var arena = all.FirstOrDefault(a => Same(a.Name, map));
                if (arena == null)
                    result.Add(No($"A arena \"{map}\" não existe mais", ArenaFix.PickArena, "Escolher arena"));
                else if (!arena.ServesChallenge)
                    result.Add(No($"A arena \"{map}\" não está marcada para Desafio contra criatura", ArenaFix.ServeType,
                        "Marcar na arena", arena.Name, PveType));
                else if (!ready.Contains(arena))
                    result.Add(No($"A arena \"{map}\" ainda não funciona", ArenaFix.OpenArena, "Abrir a arena", arena.Name));
                else result.Add(Ok($"Acontece na arena \"{arena.Name}\""));
            }
            else
            {
                var pve = ready.Where(a => a.ServesChallenge).ToList();
                if (pve.Count > 0) result.Add(Ok("Acontece em qualquer arena de desafio livre (" + string.Join(", ", pve.Select(a => a.Name)) + ")"));
                else if (ready.Count > 0)
                    result.Add(No("Nenhuma arena pronta aceita Desafio contra criatura", ArenaFix.ServeType, "Marcar na arena", ready[0].Name, PveType));
                else if (all.Count > 0)
                    result.Add(No("Nenhuma arena pronta ainda", ArenaFix.OpenArena, "Abrir a arena", all[0].Name));
                else result.Add(No("Nenhuma arena criada ainda", ArenaFix.NewArena, "Criar arena"));
            }

            string reward = (c.Reward ?? "").Trim();
            string extras = (c.Extras ?? "").Trim();
            int before = result.Count;
            if (reward.Length > 0 && c.Amount > 0 && itemKnown != null && !itemKnown(reward))
                result.Add(No($"A recompensa \"{reward}\" não é um item do jogo", ArenaFix.PickReward, "Trocar recompensa"));
            foreach (var extra in extras.Split(new[] { ',', '|', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = extra.Split(':');
                string item = pair[0].Trim();
                bool shape = item.Length > 0 && pair.Length <= 2 &&
                             (pair.Length == 1 || (int.TryParse(pair[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0));
                if (!shape)
                    result.Add(No($"Extra \"{extra.Trim()}\" mal escrito (use item:quantidade)", ArenaFix.PickReward, "Corrigir extras"));
                else if (itemKnown != null && !itemKnown(item))
                    result.Add(No($"Extra \"{item}\" não é um item do jogo", ArenaFix.PickReward, "Corrigir extras"));
            }
            if (result.Count == before)
                result.Add(Ok((reward.Length > 0 && c.Amount > 0) || extras.Length > 0
                    ? "A recompensa vai pela Caixa Postal"
                    : "Sem recompensa em item (só os pontos de arena)"));

            result.Add(c.On ? Ok("Ligado") : No("Desligado: não aparece no Mestre da Arena", ArenaFix.TurnOn, "Ligar"));
            return result;
        }

        /// <summary>
        /// The server as a whole: each queue that is on needs at least one ready arena that
        /// takes it, or it refuses everyone. <paramref name="brackets"/> are the active sizes.
        /// </summary>
        internal static List<ArenaCheck> Overall(IList<ArenaDraft> all, IList<ArenaDraft> ready, IEnumerable<int> brackets,
            bool ffa, bool challenges)
        {
            var result = new List<ArenaCheck>();
            if (ready.Count == 0)
            {
                result.Add(all.Count == 0
                    ? No("Nenhuma arena criada: ninguém entra na fila", ArenaFix.NewArena, "Criar arena")
                    : No("Nenhuma arena pronta: ninguém entra na fila", ArenaFix.OpenArena, "Abrir a arena", all[0].Name));
                return result;
            }
            var wanted = brackets.Select(TypeOfSize).Where(t => t >= 0 && t < FfaType).ToList();
            if (ffa) wanted.Add(FfaType);
            if (challenges) wanted.Add(PveType);
            foreach (int type in wanted.Distinct())
            {
                result.Add(ready.Any(a => a.Serves(type))
                    ? Ok($"{TypeLabel(type)}: " + string.Join(", ", ready.Where(a => a.Serves(type)).Select(a => a.Name)))
                    : No($"Nenhuma arena pronta aceita {TypeLabel(type)}: essa fila recusa todo mundo", ArenaFix.ServeType,
                        "Marcar na arena " + ready[0].Name, ready[0].Name, type));
            }
            return result;
        }

        /// <summary>The area made around an arena: centred between the two starts, with
        /// <see cref="ZoneMargin"/> past each, never under <see cref="MinZoneAround"/>.</summary>
        internal static ArenaZone ZoneAround(string name, Vector3 gold, Vector3 green)
        {
            var centre = (gold + green) * 0.5f;
            float half = Vector2.Distance(new Vector2(gold.x, gold.z), new Vector2(green.x, green.z)) * 0.5f;
            float radius = Mathf.Clamp(Mathf.Ceil(half + ZoneMargin), MinZoneAround, ArenaSettingsParser.MaxZoneRadius);
            return new ArenaZone { Name = ArenaSettingsParser.CleanZoneName(name), X = centre.x, Z = centre.z, Radius = radius };
        }

        private static bool Same(string a, string b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
