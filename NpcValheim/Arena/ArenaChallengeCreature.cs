using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The creature of a challenge, on every machine. The server puts it in the world with its
    /// match on the ZDO; whichever client comes to own it reads that back, so it keeps the
    /// challenge's health after changing hands, drops nothing, and only the players of its own
    /// match, with the gates open, can hurt it.
    /// </summary>
    internal static class ArenaChallengeCreature
    {
        internal static readonly int ZdoMatch = "npcv_arenaCreature".GetStableHashCode();
        internal static readonly int ZdoHealth = "npcv_arenaCreatureHp".GetStableHashCode();

        internal static long MatchOf(ZDO zdo) => zdo?.GetLong(ZdoMatch, 0L) ?? 0L;

        internal static long MatchOf(Character character) =>
            character != null && !(character is Player) ? MatchOf(ArenaCombat.ZdoOf(character)) : 0L;

        /// <summary>Whether a cfg entry can be played: a creature the game knows, not a boss,
        /// with rewards the post can carry. Before the world is up there is nothing to check.</summary>
        /// <summary>A boss: m_boss, or a defeat key of the world's progression ("defeated_gdking"...).
        /// Plain creatures carry keys too (the Troll's "KilledTroll" only lets the troll raid
        /// come, and any troll killed anywhere sets it), so those do not count.</summary>
        internal static bool IsBoss(Character character) =>
            character != null && (character.m_boss ||
                (character.m_defeatSetGlobalKey ?? "").StartsWith("defeated_", StringComparison.OrdinalIgnoreCase));

        internal static bool Usable(ArenaChallengeDef def, out string why)
        {
            why = null;
            if (def.BeastId != null && ArenaBestiary.Find(def.BeastId) == null)
            {
                why = $"'{def.BeastId}' nao esta no bestiario";
                return false;
            }
            if (ZNetScene.instance == null) return true;
            var prefab = ZNetScene.instance.GetPrefab(def.Prefab);
            var character = prefab != null ? prefab.GetComponent<Character>() : null;
            if (character == null || character is Player || prefab.GetComponent<BaseAI>() == null)
            {
                why = $"'{def.Prefab}' nao e uma criatura do jogo";
                return false;
            }
            // A boss counts as defeated for whoever hit it (and Deadheim's boss bands read that),
            // and its key opens the world's progression.
            if (IsBoss(character))
            {
                why = $"'{def.Prefab}' e chefe: matar contaria como chefe derrotado";
                return false;
            }
            if (ObjectDB.instance != null)
                foreach (var reward in def.Rewards)
                {
                    int max = ItemSpawner.MaxDeliverableAmount(reward.Key);
                    if (max <= 0)
                    {
                        why = $"recompensa '{reward.Key}' nao e um item que o jogo conhece";
                        return false;
                    }
                    if (reward.Value > max)
                    {
                        why = $"recompensa {reward.Key}: {reward.Value} e mais do que uma entrega carrega ({max})";
                        return false;
                    }
                }
            return true;
        }

        // ------------------------------------------------------------------ server

        /// <summary>The challenge's creature: the game's, or the base of a creature of the
        /// Bestiário with its mark, its level and its health for <paramref name="players"/>.</summary>
        internal static ZDOID Spawn(long matchId, ArenaChallengeDef def, Vector3 at, float yaw, int players)
        {
            if (def.BeastId == null) return Create(def.Prefab, matchId, null, def.Level, def.HealthMultiplier, at, yaw, 0L);
            var beast = ArenaBestiary.Find(def.BeastId);
            if (beast == null)
            {
                Plugin.Log.LogError($"NpcValheim Arena: a criatura '{def.BeastId}' do desafio saiu do bestiario");
                return ZDOID.None;
            }
            return Create(beast.Base, matchId, beast, beast.Level, ArenaBestiary.HealthFactor(beast, players), at, yaw, 0L);
        }

        /// <summary>"Invocar perto de mim": a creature of the Bestiário outside any match, for the
        /// admin to look at and hit. It goes by itself at <paramref name="untilUtcTicks"/>.</summary>
        internal static ZDOID SpawnTest(ArenaBeastDef beast, Vector3 at, float yaw, long untilUtcTicks) =>
            Create(beast.Base, 0L, beast, beast.Level, ArenaBestiary.HealthFactor(beast, 1), at, yaw, untilUtcTicks);

        private static ZDOID Create(string prefabName, long matchId, ArenaBeastDef beast, int level, float health,
            Vector3 at, float yaw, long untilUtcTicks)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefabName) : null;
            if (prefab == null || prefab.GetComponent<Character>() == null) return ZDOID.None;

            GameObject go = null;
            try
            {
                go = UnityEngine.Object.Instantiate(prefab, at + Vector3.up * 0.5f, Quaternion.Euler(0f, yaw, 0f));
                var character = go.GetComponent<Character>();
                var zdo = go.GetComponent<ZNetView>().GetZDO();
                if (matchId != 0L) zdo.Set(ZdoMatch, matchId);
                zdo.Set(ZdoHealth, health);
                if (beast != null)
                {
                    zdo.Set(ArenaBeastBody.ZdoBeast, beast.Id);
                    if (untilUtcTicks > 0L) zdo.Set(ArenaBeastBody.ZdoUntil, untilUtcTicks);
                    // Character.Awake ran before the mark existed.
                    ArenaBeastBody.Apply(character);
                }
                // SetLevel runs SetupMaxHealth, which the patch below multiplies.
                character.SetLevel(level);
                character.SetHealth(character.GetMaxHealth());

                // A creature that leaves at dawn or with its raid would end the challenge as if
                // it had been killed.
                var monster = go.GetComponent<MonsterAI>();
                if (monster != null)
                {
                    monster.SetDespawnInDay(false);
                    monster.SetEventCreature(false);
                }
                go.GetComponent<BaseAI>()?.SetHuntPlayer(true);
                return zdo.m_uid;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"NpcValheim Arena: falha ao criar a criatura '{prefabName}'{(beast != null ? $" ({beast.Id})" : "")}: {e}");
                if (go != null) ZNetScene.instance.Destroy(go);
                return ZDOID.None;
            }
        }

        /// <summary>Health from 0 to 1 as the server's copy of the ZDO has it, or -1.</summary>
        internal static float Health(ZDOID id)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null) return -1f;
            float max = zdo.GetFloat(ZDOVars.s_maxHealth, 0f);
            if (max <= 0f) return -1f;
            return Mathf.Clamp01(zdo.GetFloat(ZDOVars.s_health, max) / max);
        }

        internal static void Despawn(ZDOID id)
        {
            var zdo = ZDOMan.instance?.GetZDO(id);
            if (zdo == null) return;
            var instance = ZNetScene.instance?.FindInstance(id);
            var nview = instance != null ? instance.GetComponent<ZNetView>() : null;
            if (nview != null && nview.IsValid())
            {
                nview.ClaimOwnership();
                ZNetScene.instance.Destroy(instance);
                return;
            }
            // DestroyZDO does nothing for an object someone else owns.
            zdo.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(zdo);
        }

        internal static void Sweep(IEnumerable<string> prefabs, Func<long, bool> live)
        {
            if (ZDOMan.instance == null) return;
            int removed = 0;
            var found = new List<ZDO>();
            foreach (var prefab in prefabs)
            {
                found.Clear();
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, found, ref index)) { }
                foreach (var zdo in found)
                {
                    long match = MatchOf(zdo);
                    if (match == 0L || live(match)) continue;
                    Despawn(zdo.m_uid);
                    removed++;
                }
            }
            if (removed > 0) Plugin.Log.LogInfo($"NpcValheim Arena: {removed} criatura(s) de desafio sem partida removida(s)");
        }


        // ------------------------------------------------------------------ every machine

        /// <summary>The level's health times the challenge's, again every time the game works
        /// the health out (a new owner, a level set). For a creature of the Bestiário the ZDO
        /// holds vidaMult x (1 + vidaPorJogadorExtra x (jogadores - 1)), worked out at spawn, so
        /// a new owner without the bestiary still keeps it.</summary>
        [HarmonyPatch(typeof(Character), "SetupMaxHealth")]
        private static class HealthPatch
        {
            private static void Postfix(Character __instance)
            {
                try
                {
                    var zdo = __instance is Player ? null : ArenaCombat.ZdoOf(__instance);
                    // A test creature of the Bestiário has no match; its health is set the same way.
                    if (MatchOf(zdo) == 0L && !ArenaBeastBody.IsBeast(zdo)) return;
                    float multiplier = zdo.GetFloat(ZdoHealth, 1f);
                    if (multiplier > 0f && Mathf.Abs(multiplier - 1f) > 0.001f)
                        __instance.SetMaxHealth(__instance.GetMaxHealthBase() * __instance.GetLevel() * multiplier);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: vida da criatura do desafio falhou: " + e.Message);
                }
            }
        }

        /// <summary>The reward is the post's; the creature itself drops nothing. Nor does a test
        /// creature of the Bestiário, or an admin could farm it.</summary>
        [HarmonyPatch(typeof(CharacterDrop), nameof(CharacterDrop.GenerateDropList))]
        private static class DropPatch
        {
            private static void Postfix(CharacterDrop __instance, List<KeyValuePair<GameObject, int>> __result)
            {
                if (__result == null) return;
                var character = __instance.GetComponent<Character>();
                if (MatchOf(character) != 0L || (character != null && !(character is Player) && Beastly(ArenaCombat.ZdoOf(character))))
                    __result.Clear();
            }
        }

        /// <summary>A creature of the Bestiário or one of its lacaios (a test creature's have no match).</summary>
        private static bool Beastly(ZDO zdo) =>
            ArenaBeastBody.IsBeast(zdo) || (zdo != null && !zdo.GetZDOID(ArenaBeastBrain.ZdoMinionOwner).IsNone());

        /// <summary>Whether a hit on the creature of <paramref name="match"/> may land: only from
        /// a player of that match with the gates open. No attacker (a fall, the water) lands;
        /// anything else -- an outsider, someone's tame, a hit from too far to tell -- does not.</summary>
        private static bool Allowed(long match, ZDOID attackerId)
        {
            if (attackerId.IsNone()) return true;
            var attacker = ArenaCombat.PlayerZdo(attackerId);
            return attacker != null && attacker.GetLong(ArenaClient.ZdoMatch, 0L) == match &&
                   attacker.GetInt(ArenaClient.ZdoPhase, 0) == ArenaClient.PhaseLive;
        }

        /// <summary>Victim side: RPC_Damage runs on the creature's owner, the decision that counts.</summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class VictimPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Character __instance, HitData hit)
            {
                try
                {
                    if (hit == null) return true;
                    long match = MatchOf(__instance);
                    return match == 0L || Allowed(match, hit.m_attacker);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: regra de dano da criatura falhou: " + e.Message);
                    return true;
                }
            }
        }

        /// <summary>Attacker side: someone outside the challenge swinging at its creature is
        /// stopped before the hit, and told why.</summary>
        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class AttackerPatch
        {
            private static float _nextMessage;

            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Character __instance, HitData hit)
            {
                try
                {
                    var local = Player.m_localPlayer;
                    if (hit == null || local == null || hit.GetAttacker() != local) return true;
                    long match = MatchOf(__instance);
                    var self = ArenaCombat.ZdoOf(local);
                    if (match == 0L || self == null || Allowed(match, self.m_uid)) return true;
                    if (Time.time >= _nextMessage)
                    {
                        _nextMessage = Time.time + 2f;
                        local.Message(MessageHud.MessageType.Center, self.GetLong(ArenaClient.ZdoMatch, 0L) == match
                            ? "O desafio ainda não começou." : "Essa criatura é de um desafio da arena.", 0, null);
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: checagem do atacante da criatura falhou: " + e.Message);
                    return true;
                }
            }
        }
    }
}
