using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// Who may hurt whom during a match, and what happens instead of dying.
    ///
    /// Deadheim's arena zone already turns PvP on and lifts skill loss, PK and immunity. On
    /// top of it a match adds three things WoW's arenas had by being instances: nobody from
    /// outside can touch the players and they cannot touch anyone outside; teammates cannot
    /// hurt each other; and no one fights before the gates open or after the match is
    /// decided. All of it is read from the match keys each player publishes on their own ZDO.
    /// </summary>
    internal static class ArenaCombat
    {
        private static int _playerPrefab;

        /// <summary>Lazy, so the rule above can be checked outside the game, where the
        /// assembly that hashes names is not loaded.</summary>
        private static int PlayerPrefab => _playerPrefab != 0 ? _playerPrefab : _playerPrefab = "Player".GetStableHashCode();

        /// <summary>The rule itself, on plain values so the checks can run it.</summary>
        internal static bool Forbidden(long attackerMatch, int attackerSide, int attackerPhase,
            long victimMatch, int victimSide, int victimPhase)
        {
            if (attackerMatch == 0L && victimMatch == 0L) return false;
            if (attackerMatch != victimMatch) return true;
            if (attackerPhase != ArenaClient.PhaseLive || victimPhase != ArenaClient.PhaseLive) return true;
            return attackerSide == victimSide;
        }

        internal static bool Forbidden(ZDO attacker, ZDO victim)
        {
            if (attacker == null || victim == null) return false;
            return Forbidden(
                attacker.GetLong(ArenaClient.ZdoMatch, 0L), attacker.GetInt(ArenaClient.ZdoSide, 0), attacker.GetInt(ArenaClient.ZdoPhase, 0),
                victim.GetLong(ArenaClient.ZdoMatch, 0L), victim.GetInt(ArenaClient.ZdoSide, 0), victim.GetInt(ArenaClient.ZdoPhase, 0));
        }

        /// <summary>Opponents in the same running match: for Deadheim, never allies.</summary>
        internal static bool Opponents(ZDO a, ZDO b)
        {
            if (a == null || b == null) return false;
            long match = a.GetLong(ArenaClient.ZdoMatch, 0L);
            return match != 0L && match == b.GetLong(ArenaClient.ZdoMatch, 0L) &&
                   a.GetInt(ArenaClient.ZdoPhase, 0) == ArenaClient.PhaseLive &&
                   b.GetInt(ArenaClient.ZdoPhase, 0) == ArenaClient.PhaseLive &&
                   a.GetInt(ArenaClient.ZdoSide, 0) != b.GetInt(ArenaClient.ZdoSide, 0);
        }

        internal static ZDO ZdoOf(Character character)
        {
            var nview = character != null ? character.GetComponent<ZNetView>() : null;
            return nview != null && nview.IsValid() ? nview.GetZDO() : null;
        }

        /// <summary>The attacker's ZDO if it is a player, read from the hit -- the attacker may
        /// be too far away to be loaded here (an arrow), and that must not let the hit through.</summary>
        internal static ZDO PlayerZdo(ZDOID id)
        {
            if (id.IsNone() || ZDOMan.instance == null) return null;
            var zdo = ZDOMan.instance.GetZDO(id);
            return zdo != null && zdo.GetPrefab() == PlayerPrefab ? zdo : null;
        }

        /// <summary>Victim side: Character.RPC_Damage runs on the client that owns the victim,
        /// which for a player is their own. That is the decision that counts.</summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class VictimPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Character __instance, HitData hit)
            {
                try
                {
                    if (hit == null || !(__instance is Player victim) || victim != Player.m_localPlayer) return true;
                    var attacker = PlayerZdo(hit.m_attacker);
                    var self = ZdoOf(victim);
                    if (attacker == null || attacker == self) return true;
                    return !Forbidden(attacker, self);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: regra de dano falhou: " + e.Message);
                    return true;
                }
            }
        }

        /// <summary>Attacker side: stops the swing from landing at all, so there is no ghost
        /// hit, and says why.</summary>
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
                    if (hit == null || local == null || !(__instance is Player victim) || victim == local) return true;
                    if (hit.GetAttacker() != local) return true;
                    if (!Forbidden(ZdoOf(local), ZdoOf(victim))) return true;

                    if (Time.time >= _nextMessage)
                    {
                        _nextMessage = Time.time + 2f;
                        local.Message(MessageHud.MessageType.Center, Explain(ZdoOf(local), ZdoOf(victim)), 0, null);
                    }
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: checagem do atacante falhou: " + e.Message);
                    return true;
                }
            }
        }

        private static string Explain(ZDO me, ZDO them)
        {
            long mine = me?.GetLong(ArenaClient.ZdoMatch, 0L) ?? 0L;
            long theirs = them?.GetLong(ArenaClient.ZdoMatch, 0L) ?? 0L;
            if (mine != theirs) return mine != 0L ? "Você está numa partida de arena." : "Esse jogador está numa partida de arena.";
            if ((me?.GetInt(ArenaClient.ZdoPhase, 0) ?? 0) == ArenaClient.PhasePrep) return "A batalha ainda não começou.";
            if ((me?.GetInt(ArenaClient.ZdoSide, 0) ?? 0) == (them?.GetInt(ArenaClient.ZdoSide, 0) ?? 0)) return "Companheiro de time.";
            return "Fora de combate.";
        }

        /// <summary>
        /// Nobody dies in a match: the death check is replaced by a knockout for the local
        /// player while they are in one. It runs before the game marks them dead, so Deadheim's
        /// death handling (tombstone, skill loss, PvP record) never sees it.
        /// </summary>
        [HarmonyPatch(typeof(Character), "CheckDeath")]
        private static class KnockoutPatch
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Character __instance, HitData ___m_lastHit)
            {
                if (ArenaClient.Phase == ArenaClient.PhaseNone) return true;
                if (!(__instance is Player player) || player != Player.m_localPlayer) return true;
                if (player.IsDead() || player.GetHealth() > 0f) return true;
                try
                {
                    ArenaClient.KnockOut(player, ___m_lastHit);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError("NpcValheim Arena: nocaute falhou, a morte segue normal: " + e);
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class SpawnPatch
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                ArenaClient.OnLocalSpawned();
            }
        }

        /// <summary>
        /// Deadheim keeps guildmates and party members from hurting each other. In a match the
        /// teams are the arena's, not the guild's: two guildmates on opposite sides must be
        /// able to fight. Patched by name, so nothing breaks without Deadheim.
        /// </summary>
        [HarmonyPatch]
        private static class DeadheimAlliesPatch
        {
            private static MethodBase _target;

            private static bool Prepare()
            {
                if (_target != null) return true;
                Type rules = null;
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    rules = assembly.GetType("Deadheim.Pvp.PvpRules", false);
                    if (rules != null) break;
                }
                _target = rules?.GetMethod("AreAllies", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Player), typeof(Player), typeof(Vector3) }, null);
                return _target != null;
            }

            private static MethodBase TargetMethod() => _target;

            private static void Postfix(Player a, Player b, ref bool __result)
            {
                if (!__result) return;
                try
                {
                    if (Opponents(ZdoOf(a), ZdoOf(b))) __result = false;
                }
                catch
                {
                    // Leave Deadheim's answer alone.
                }
            }
        }
    }
}
