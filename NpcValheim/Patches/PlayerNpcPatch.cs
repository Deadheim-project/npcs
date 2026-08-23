using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// Our NPCs are clones of the "Player" prefab (to get the real player body model, so
    /// hair/beard/skin/gender customization works exactly like character creation does).
    /// Player.Update/FixedUpdate/LateUpdate drive input, camera and movement, none of which
    /// makes sense for a static shopkeeper -- skip them entirely for anything tagged with
    /// NpcMarker, while Awake/Start (one-time setup, incl. the visual model) still run
    /// normally, same as for every other player you see online who isn't you.
    /// </summary>
    [HarmonyPatch(typeof(Player))]
    internal static class PlayerNpcPatch
    {
        private const BindingFlags AnyStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static FieldInfo _playerRegistry;

        /// <summary>
        /// Player.Awake registers every Player component in Player.s_players. Our visual
        /// clones need Awake so the body and VisEquipment are initialized, but they must not
        /// remain in that gameplay registry: vanilla uses it for proximity, targeting,
        /// events, noise and broadcasts intended for real players.
        ///
        /// Use reflection because s_players is private in the game assembly and publicizer
        /// coverage differs between client/server installs. The public GetAllPlayers list is
        /// retained as a compatibility fallback for game builds that rename the field.
        /// </summary>
        [HarmonyPatch(nameof(Player.Awake)), HarmonyPostfix]
        private static void Awake(Player __instance)
        {
            if (!IsOurs(__instance)) return;

            try
            {
                _playerRegistry ??= typeof(Player).GetField("s_players", AnyStatic);
                if (_playerRegistry?.GetValue(null) is IList players)
                {
                    RemoveEvery(players, __instance);
                    return;
                }

                // GetAllPlayers currently returns the backing List<Player>. This fallback is
                // deliberately second: if a future build returns a copy, touching it is safe
                // but ineffective, and the warning below makes that visible in the log.
                var publicPlayers = Player.GetAllPlayers();
                if (publicPlayers != null)
                {
                    while (publicPlayers.Remove(__instance)) { }
                    if (!publicPlayers.Contains(__instance)) return;
                }

                Plugin.Log?.LogWarning(
                    "NpcValheim: could not remove an NPC clone from Player.s_players");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogWarning(
                    $"NpcValheim: failed to remove an NPC clone from Player.s_players: {e.Message}");
            }
        }

        [HarmonyPatch(nameof(Player.Update)), HarmonyPrefix]
        private static bool Update(Player __instance) => !IsOurs(__instance);

        [HarmonyPatch(nameof(Player.FixedUpdate)), HarmonyPrefix]
        private static bool FixedUpdate(Player __instance) => !IsOurs(__instance);

        [HarmonyPatch(nameof(Player.LateUpdate)), HarmonyPrefix]
        private static bool LateUpdate(Player __instance) => !IsOurs(__instance);

        /// <summary>
        /// Hands the crosshair prompt back to the NPC.
        ///
        /// Player implements Hoverable too, and being a clone it sits *before* NpcBase in the
        /// component order -- so the game's GetComponentInParent&lt;Hoverable&gt;() picked the
        /// Player, whose hover text is empty for anyone who isn't you. The result was an NPC
        /// you could interact with but that showed nothing at all when you looked at it.
        /// Measured, not guessed: the suite prints every Hoverable on the root and which one
        /// the collider resolves to.
        ///
        /// Patching here rather than reordering components: component order isn't something
        /// Unity lets you set reliably, and stripping Player would take the body model with it.
        /// </summary>
        [HarmonyPatch(nameof(Player.GetHoverText)), HarmonyPrefix]
        private static bool GetHoverText(Player __instance, ref string __result)
        {
            var npc = Npc(__instance);
            if (npc == null) return true;
            __result = npc.GetHoverText();
            return false;
        }

        [HarmonyPatch(nameof(Player.GetHoverName)), HarmonyPrefix]
        private static bool GetHoverName(Player __instance, ref string __result)
        {
            var npc = Npc(__instance);
            if (npc == null) return true;
            __result = npc.GetHoverName();
            return false;
        }

        private static NpcBase Npc(Player instance) =>
            instance != null ? instance.GetComponent<NpcBase>() : null;

        private static void RemoveEvery(IList players, Player instance)
        {
            while (players.Contains(instance)) players.Remove(instance);
        }

        private static bool IsOurs(Player instance) =>
            instance != null && instance.GetComponent<NpcMarker>() != null;
    }

    /// <summary>Service NPCs are world fixtures, not combatants. Until their visual root no
    /// longer derives from Player, stop vanilla damage/death from deleting a configured NPC
    /// without running the authorized removal and snapshot cleanup flow.</summary>
    [HarmonyPatch(typeof(Character))]
    internal static class ServiceNpcDamagePatch
    {
        [HarmonyPatch(nameof(Character.Damage)), HarmonyPrefix]
        private static bool Damage(Character __instance) => !IsServiceNpc(__instance);

        [HarmonyPatch(nameof(Character.OnDeath)), HarmonyPrefix]
        private static bool OnDeath(Character __instance) => !IsServiceNpc(__instance);

        private static bool IsServiceNpc(Character character) =>
            character != null && character.GetComponent<NpcMarker>() != null;
    }
}
