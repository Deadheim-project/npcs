using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NpcValheim.Arena
{
    /// <summary>
    /// Where the arena is, for the rules below: inside a Deadheim arena area (its PvP zone) or,
    /// without one, inside one of the arenas the server sent. Works on the server and on every
    /// client, since both have the zones and the arenas.
    /// </summary>
    internal static class ArenaArea
    {
        internal static bool Contains(Vector3 point)
        {
            if (ArenaDeadheim.Installed && ArenaDeadheim.IsArena(point)) return true;
            var maps = ArenaConfig.Current.Maps;
            for (int i = 0; i < maps.Count; i++)
                if (maps[i].Contains(point)) return true;
            return false;
        }
    }

    /// <summary>
    /// The arena is a place for the fight and nothing else: no creature of the game is born
    /// in it (the challenges' creatures are created directly, not by these spawners), its
    /// buildings take no damage, and whoever is in it spends no arrows and wears nothing out.
    /// Each patch fails open: an error lets the game do what it would have done.
    /// </summary>
    internal static class ArenaProtection
    {
        private static bool Guarded(Vector3 point)
        {
            try { return ArenaArea.Contains(point); }
            catch { return false; }
        }

        // ------------------------------------------------------------------ creatures

        [HarmonyPatch]
        private static class SpawnSystemPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(SpawnSystem), "IsSpawnPointGood");

            private static bool Prefix(ref Vector3 spawnPoint, ref bool __result)
            {
                if (!Guarded(spawnPoint)) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch]
        private static class CreatureSpawnerPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(CreatureSpawner), "Spawn");

            private static bool Prefix(CreatureSpawner __instance, ref ZNetView __result)
            {
                if (!Guarded(__instance.transform.position)) return true;
                __result = null;
                return false;
            }
        }

        [HarmonyPatch]
        private static class SpawnAreaPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(SpawnArea), "SpawnOne");

            private static bool Prefix(SpawnArea __instance, ref bool __result)
            {
                if (!Guarded(__instance.transform.position)) return true;
                __result = false;
                return false;
            }
        }

        // ------------------------------------------------------------------ buildings

        /// <summary>A hit on a building in the arena does nothing (no damage, no hit effect).</summary>
        [HarmonyPatch]
        private static class WearNTearHitPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WearNTear), "RPC_Damage");

            private static bool Prefix(WearNTear __instance) => !Guarded(__instance.transform.position);
        }

        /// <summary>Nor does any other damage: fire, rain, the sea.</summary>
        [HarmonyPatch]
        private static class WearNTearDamagePatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(WearNTear), nameof(WearNTear.ApplyDamage));

            private static bool Prefix(WearNTear __instance, ref bool __result)
            {
                if (!Guarded(__instance.transform.position)) return true;
                __result = false;
                return false;
            }
        }

        // ------------------------------------------------------------------ the local player

        /// <summary>Whether the local player is in the arena (its area, or a match).</summary>
        private static bool LocalGuarded(Character character)
        {
            if (character == null || !ReferenceEquals(character, Player.m_localPlayer)) return false;
            return ArenaClient.MatchId != 0L || Guarded(character.transform.position);
        }

        private static readonly AccessTools.FieldRef<Attack, Humanoid> AttackCharacter =
            AccessTools.FieldRefAccess<Attack, Humanoid>("m_character");

        [ThreadStatic] private static bool _freeAmmo;

        /// <summary>Attack.UseAmmo takes the arrow with Inventory.RemoveItem(item, 1): while it
        /// runs for the local player in the arena, that removal is skipped.</summary>
        [HarmonyPatch]
        private static class UseAmmoPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Attack), "UseAmmo");

            private static void Prefix(Attack __instance)
            {
                try { _freeAmmo = LocalGuarded(AttackCharacter(__instance)); }
                catch { _freeAmmo = false; }
            }

            private static Exception Finalizer(Exception __exception)
            {
                _freeAmmo = false;
                return __exception;
            }
        }

        [HarmonyPatch]
        private static class RemoveAmmoPatch
        {
            private static MethodBase TargetMethod() =>
                AccessTools.Method(typeof(Inventory), nameof(Inventory.RemoveItem), new[] { typeof(ItemDrop.ItemData), typeof(int) });

            private static bool Prefix(ItemDrop.ItemData item, ref bool __result)
            {
                if (!_freeAmmo || item == null) return true;
                __result = true;
                return false;
            }
        }

        /// <summary>Armour wears by the damage taken, not by Game.m_durabilityRate.</summary>
        [HarmonyPatch]
        private static class ArmorPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Player), "DamageArmorDurability");

            private static bool Prefix(Player __instance) => !LocalGuarded(__instance);
        }

        // Weapons, shields and tools wear by Game.m_durabilityRate: held at 0 while the local
        // player is in the arena, given back as it was (or as the world changed it) on leaving.
        private static bool _holding;
        private static float _savedRate = 1f;

        /// <summary>From the client tick (null player = logged out: give it back).</summary>
        internal static void Tick(Player player)
        {
            bool guard = player != null && LocalGuarded(player);
            if (guard)
            {
                if (Game.m_durabilityRate != 0f)
                {
                    _savedRate = Game.m_durabilityRate;
                    Game.m_durabilityRate = 0f;
                }
                _holding = true;
            }
            else if (_holding)
            {
                _holding = false;
                if (Game.m_durabilityRate == 0f) Game.m_durabilityRate = _savedRate;
            }
        }
    }
}
