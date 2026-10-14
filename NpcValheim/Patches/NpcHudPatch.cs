using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.Patches
{
    /// <summary>
    /// The name over an NPC is the game's own: EnemyHud draws it with the plate it uses for
    /// other players (same font, outline, size on screen and 10 m range), because the NPCs are
    /// Player clones and it already treats them as players.
    ///
    /// Two things are taken off that plate. The health bar -- an NPC is not fighting anything,
    /// and a green bar over a shopkeeper reads as a target. And the head height: the player
    /// plate sits over the player rig's head, which a creature body or an admin-scaled NPC
    /// does not have where the rig thinks, so the plate goes to the NPC's NameplateHeight.
    ///
    /// The quest marker (! and ?) is not the game's and is still drawn by NpcNameplate.
    ///
    /// EnemyHud.m_huds and its HudData are private in the assembly the game loads. Read
    /// directly (they only look public in the publicized one we compile against), Mono refused
    /// to compile these postfixes: FieldAccessException on every EnemyHud.LateUpdate, which also
    /// stopped the game's own HUD update. Same trap as QuestKillTracker's m_lastHit, same cure:
    /// reach them by name at runtime.
    /// </summary>
    [HarmonyPatch(typeof(EnemyHud))]
    internal static class NpcHudPatch
    {
        private static readonly FieldInfo HudsField = AccessTools.Field(typeof(EnemyHud), "m_huds");
        private static readonly FieldInfo GuiField = GuiFieldOf(HudsField);
        private static bool _warned;

        private static FieldInfo GuiFieldOf(FieldInfo huds)
        {
            var args = huds != null && huds.FieldType.IsGenericType ? huds.FieldType.GetGenericArguments() : null;
            return args != null && args.Length == 2 ? AccessTools.Field(args[1], "m_gui") : null;
        }

        private static IDictionary Huds(EnemyHud hud)
        {
            if (HudsField != null && GuiField != null) return HudsField.GetValue(hud) as IDictionary;
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning("NpcValheim: EnemyHud.m_huds/m_gui not found; NPC names keep the game's plate");
            }
            return null;
        }

        private static GameObject Gui(object hudData) => hudData != null ? GuiField.GetValue(hudData) as GameObject : null;

        [HarmonyPatch(nameof(EnemyHud.ShowHud)), HarmonyPostfix]
        private static void ShowHud(EnemyHud __instance, Character c)
        {
            if (c == null || c.GetComponent<NpcMarker>() == null) return;
            var huds = Huds(__instance);
            if (huds == null || !huds.Contains(c)) return;
            var gui = Gui(huds[c]);
            if (gui == null) return;
            var health = gui.transform.Find("Health");
            if (health != null) health.gameObject.SetActive(false);
        }

        [HarmonyPatch(nameof(EnemyHud.UpdateHuds)), HarmonyPostfix]
        private static void UpdateHuds(EnemyHud __instance)
        {
            var camera = Utils.GetMainCamera();
            if (camera == null) return;
            var huds = Huds(__instance);
            if (huds == null) return;
            foreach (DictionaryEntry pair in huds)
            {
                var character = pair.Key as Character;
                var gui = Gui(pair.Value);
                if (character == null || gui == null || !gui.activeSelf) continue;
                var npc = character.GetComponent<NpcBase>();
                if (npc == null) continue;

                float scale = Mathf.Max(0.01f, character.transform.localScale.y);
                Vector3 top = character.transform.position + Vector3.up * (npc.NameplateHeight * scale);
                Vector3 screen = camera.WorldToScreenPointScaled(top);
                if (screen.z > 0f) gui.transform.position = screen;
                else gui.SetActive(false);
            }
        }
    }
}
