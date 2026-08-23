using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace NpcValheim.Persistence
{
    /// <summary>Rules shared by quest authoring, progress and UI. Explore's Amount is a
    /// radius; reaching the target is one completion, never N completions where N is radius.</summary>
    public static class QuestProgressRules
    {
        public const float MaxWorldCoordinate = 1000000f;
        public const int MaxExploreRadius = 10000;

        public static int Goal(QuestObjective objective) =>
            objective != null && objective.Kind == QuestObjectiveKind.Explore ? 1 : Math.Max(1, objective?.Amount ?? 1);

        public static int ExploreRadius(QuestObjective objective) =>
            objective != null && objective.Kind == QuestObjectiveKind.Explore ? Math.Max(1, objective.Amount) : 0;

        public static bool TryParseExploreTarget(string target, out Vector2 place)
        {
            place = Vector2.zero;
            if (!TryParseExploreCoordinates(target, out var x, out var z)) return false;
            place = new Vector2(x, z);
            return true;
        }

        public static bool Validate(QuestDefinition quest, out string error)
        {
            error = null;
            if (quest == null) { error = "Quest vazia"; return false; }
            var steps = quest.Steps();
            if (steps == null || steps.Count == 0) { error = "A quest precisa de um objetivo"; return false; }
            for (int i = 0; i < steps.Count; i++)
                if (!ValidateObjective(steps[i], out error)) return false;

            var rewards = quest.Rewards?.Items;
            if (rewards == null) return true;
            for (int i = 0; i < rewards.Count; i++)
            {
                var reward = rewards[i];
                if (reward == null || string.IsNullOrWhiteSpace(reward.ItemName) || reward.Amount < 1)
                {
                    error = "Recompensa de item inválida";
                    return false;
                }
                if (HasItemCatalog() && !ItemExists(reward.ItemName.Trim()))
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
            if (step == null || !IsKnownKind(step.Kind)) { error = "Tipo de objetivo inválido"; return false; }
            string target = (step.Target ?? "").Trim();
            if (target.Length == 0 || target.Length > 128)
            {
                error = "Alvo obrigatório e limitado a 128 caracteres";
                return false;
            }

            if (step.Kind == QuestObjectiveKind.Explore)
            {
                if (step.Amount < 1 || step.Amount > MaxExploreRadius || !TryParseExploreCoordinates(target, out _, out _))
                {
                    error = "Explore usa alvo 'x,z' válido e raio entre 1 e 10000 metros";
                    return false;
                }
                return true;
            }

            if (step.Amount < 1 || step.Amount > 100000) { error = "Quantidade inválida"; return false; }
            if (step.Kind == QuestObjectiveKind.Talk) return true;

            if (step.Kind == QuestObjectiveKind.Kill && HasPrefabCatalog() && !IsCharacterPrefab(target))
            {
                error = $"Criatura inexistente: {target}";
                return false;
            }
            if ((step.Kind == QuestObjectiveKind.Collect || step.Kind == QuestObjectiveKind.Gather) &&
                HasItemCatalog() && !ItemExists(target))
            {
                error = $"Item inexistente: {target}";
                return false;
            }
            return true;
        }

        private static bool IsKnownKind(QuestObjectiveKind kind) =>
            kind == QuestObjectiveKind.Kill || kind == QuestObjectiveKind.Collect ||
            kind == QuestObjectiveKind.Gather || kind == QuestObjectiveKind.Talk ||
            kind == QuestObjectiveKind.Explore;

        private static bool TryParseExploreCoordinates(string target, out float x, out float z)
        {
            x = z = 0f;
            var parts = (target ?? "").Split(',');
            return parts.Length == 2 &&
                   float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
                   float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z) &&
                   IsFinite(x) && IsFinite(z) && Math.Abs(x) <= MaxWorldCoordinate && Math.Abs(z) <= MaxWorldCoordinate;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        // Reflection is intentional: publicizer coverage is not consistent between local and
        // dedicated game assemblies. Validation must never turn an access mismatch into every
        // YAML quest disappearing at startup.
        private static object StaticInstance(string typeName)
        {
            var type = Type.GetType(typeName + ", assembly_valheim") ??
                       Type.GetType(typeName + ", Assembly-CSharp") ??
                       Type.GetType(typeName);
            return type?.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        }

        private static object InvokeCatalog(object instance, string method, string name) =>
            instance?.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(string) }, null)?.Invoke(instance, new object[] { name });

        private static bool HasItemCatalog()
        {
            try { return InvokeCatalog(StaticInstance("ObjectDB"), "GetItemPrefab", "Wood") != null; }
            catch { return false; }
        }

        private static bool ItemExists(string name)
        {
            try { return InvokeCatalog(StaticInstance("ObjectDB"), "GetItemPrefab", name) != null; }
            catch { return false; }
        }

        private static bool HasPrefabCatalog()
        {
            try { return InvokeCatalog(StaticInstance("ZNetScene"), "GetPrefab", "Player") != null; }
            catch { return false; }
        }

        private static bool IsCharacterPrefab(string name)
        {
            try
            {
                var prefab = InvokeCatalog(StaticInstance("ZNetScene"), "GetPrefab", name);
                var characterType = Type.GetType("Character, assembly_valheim") ??
                                    Type.GetType("Character, Assembly-CSharp") ??
                                    Type.GetType("Character");
                var getComponent = prefab?.GetType().GetMethod("GetComponent", new[] { typeof(Type) });
                return characterType != null && getComponent?.Invoke(prefab, new object[] { characterType }) != null;
            }
            catch { return false; }
        }
    }
}
