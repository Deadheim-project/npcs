using System;
using System.Reflection;
using HarmonyLib;
using NpcValheim.Npc;
using UnityEngine;

namespace NpcValheim.Testing
{
    /// <summary>
    /// The tester's side of the test world, run on each player's own machine because skills
    /// and inventory live there: every spawn puts every skill at [Teste] SkillLevel, tops the
    /// purse up to StartCoins, and lands on the hub platform instead of the start temple, a bed
    /// or the logout spot.
    /// </summary>
    internal static class TestPlayer
    {
        // Skills.GetSkill compiles against the publicized assembly but is private in the real
        // one (see GameApi for the same trap), so it is reached by reflection.
        private static readonly MethodInfo GetSkill =
            AccessTools.Method(typeof(Skills), "GetSkill", new[] { typeof(Skills.SkillType) });

        internal static void OnLocalSpawn(Player player)
        {
            if (!TestWorldConfig.IsOn || player == null) return;

            int skills = RaiseSkills(player, TestWorldConfig.SkillLevel.Value);
            int coins = TopUpCoins(player, TestWorldConfig.StartCoins.Value);

            Plugin.Log.LogInfo($"NpcValheim Teste: {skills} skill(s) em {TestWorldConfig.SkillLevel.Value}, +{coins} moedas");
            player.Message(MessageHud.MessageType.Center,
                $"Modo de teste: skills em {TestWorldConfig.SkillLevel.Value}" +
                (coins > 0 ? $", +{coins} moedas" : ""));
        }

        private static int RaiseSkills(Player player, int level)
        {
            if (level <= 0 || GetSkill == null) return 0;
            var skills = player.GetSkills();
            if (skills?.m_skills == null) return 0;

            float target = Mathf.Clamp(level, 1, 100);
            int changed = 0;
            // Walks the definitions rather than the enum: that is every skill the game and the
            // other mods registered, and nothing GetSkill cannot build a Skill for.
            foreach (var def in skills.m_skills)
            {
                if (def == null) continue;
                try
                {
                    if (!(GetSkill.Invoke(skills, new object[] { def.m_skill }) is Skills.Skill skill)) continue;
                    skill.m_level = target;
                    skill.m_accumulator = 0f;
                    changed++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"NpcValheim Teste: skill {def.m_skill} nao mudou: {e.GetBaseException().Message}");
                }
            }
            return changed;
        }

        private static int TopUpCoins(Player player, int target)
        {
            if (target <= 0) return 0;
            int missing = target - MarketplaceNpc.CoinsOf(player);
            return missing > 0
                ? ItemSpawner.GiveToInventory(player, MarketplaceNpc.CoinPrefabName, missing, 1)
                : 0;
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class SpawnPatch
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) OnLocalSpawn(__instance);
            }
        }

        /// <summary>
        /// Every spawn lands on the hub. Same contract as the game's own branches: point the
        /// zone loader at the spot and answer true only once the area -- the floating floor
        /// included -- is instantiated, so nobody arrives 800 m up before the floor does.
        /// Only once the server has built the hub, which it announces with a global key:
        /// sending players to a platform that does not exist would drop them to their death,
        /// respawn them there, and drop them again.
        /// </summary>
        [HarmonyPatch(typeof(Game), "FindSpawnPoint")]
        private static class SpawnPointPatch
        {
            private static bool Prefix(ref Vector3 point, ref bool usedLogoutPoint, ref bool __result)
            {
                if (!TestWorldConfig.IsOn || !TestWorldConfig.SpawnOnHub.Value) return true;
                if (ZNet.instance == null || ZNetScene.instance == null || !TestWorld.IsBuilt()) return true;

                point = TestWorldConfig.HubSpawnPoint;
                usedLogoutPoint = false;
                ZNet.instance.SetReferencePosition(point);
                __result = ZNetScene.instance.IsAreaReady(point);
                return false;
            }
        }
    }
}
