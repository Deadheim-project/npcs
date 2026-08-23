using System;
using System.Globalization;
using UnityEngine;

namespace NpcValheim.Persistence
{
    /// <summary>Rules shared by authoring, server-side progress and the client view.  Explore
    /// stores its arrival radius in Amount, but it is always one completed arrival rather than
    /// a counter that has to reach that radius.</summary>
    public static class QuestProgressRules
    {
        public const float MaxWorldCoordinate = 1000000f;
        public const int MaxExploreRadius = 10000;

        public static int Goal(QuestObjective objective) =>
            objective != null && objective.Kind == QuestObjectiveKind.Explore ? 1 : Math.Max(1, objective?.Amount ?? 1);

        public static int ExploreRadius(QuestObjective objective) =>
            objective != null && objective.Kind == QuestObjectiveKind.Explore
                ? Math.Max(1, objective.Amount)
                : 0;

        public static bool TryParseExploreTarget(string target, out Vector2 place)
        {
            place = Vector2.zero;
            var parts = (target ?? "").Split(',');
            if (parts.Length != 2 ||
                !float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z) ||
                !IsFinite(x) || !IsFinite(z) || Mathf.Abs(x) > MaxWorldCoordinate || Mathf.Abs(z) > MaxWorldCoordinate)
                return false;

            place = new Vector2(x, z);
            return true;
        }

        /// <summary>Validates shape everywhere. When game prefabs are available it also
        /// rejects misspelled item/creature ids, so a quest cannot be published with an
        /// objective that no player can ever finish.</summary>
        public static bool Validate(QuestDefinition quest, out string error)
        {
            error = null;
            if (quest == null) { error = "Quest vazia"; return false; }
            var steps = quest.Steps();
            if (steps == null || steps.Count == 0) { error = "A quest precisa de um objetivo"; return false; }

            foreach (var step in steps)
            {
                if (!ValidateObjective(step, out error)) return false;
            }

            foreach (var reward in quest.Rewards?.Items ?? new System.Collections.Generic.List<QuestItemReward>())
            {
                if (reward == null || string.IsNullOrWhiteSpace(reward.ItemName) || reward.Amount < 1)
                {
                    error = "Recompensa de item inválida";
                    return false;
                }
                if (ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(reward.ItemName.Trim()) == null)
                {
                    error = $"Item de recompensa inexistente: {reward.ItemName}";
                    return false;
                }
            }
            return true;
        }

        public static bool ValidateObjective(QuestObjective step, out string error)
        {
            error = null;
            if (step == null || !Enum.IsDefined(typeof(QuestObjectiveKind), step.Kind))
            {
                error = "Tipo de objetivo inválido";
                return false;
            }

            string target = (step.Target ?? "").Trim();
            if (target.Length == 0 || target.Length > 128)
            {
                error = "Alvo obrigatório e limitado a 128 caracteres";
                return false;
            }

            if (step.Kind == QuestObjectiveKind.Explore)
            {
                if (step.Amount < 1 || step.Amount > MaxExploreRadius || !TryParseExploreTarget(target, out _))
                {
                    error = "Explore usa alvo 'x,z' válido e raio entre 1 e 10000 metros";
                    return false;
                }
                return true;
            }

            if (step.Amount < 1 || step.Amount > 100000)
            {
                error = "Quantidade inválida";
                return false;
            }

            if (step.Kind == QuestObjectiveKind.Talk) return true;

            if (step.Kind == QuestObjectiveKind.Kill && ZNetScene.instance != null)
            {
                var prefab = ZNetScene.instance.GetPrefab(target);
                if (prefab == null || prefab.GetComponent<Character>() == null)
                {
                    error = $"Criatura inexistente: {target}";
                    return false;
                }
            }
            else if ((step.Kind == QuestObjectiveKind.Collect || step.Kind == QuestObjectiveKind.Gather) &&
                     ObjectDB.instance != null && ObjectDB.instance.GetItemPrefab(target) == null)
            {
                error = $"Item inexistente: {target}";
                return false;
            }

            return true;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
