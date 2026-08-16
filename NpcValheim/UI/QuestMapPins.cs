using System.Collections.Generic;
using UnityEngine;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// Puts a pin on the map for the two objectives that are about going somewhere: the place
    /// an Explore quest names, and the NPC a Talk quest names.
    ///
    /// Those are the only two where "where do I go" is the whole difficulty -- a Kill or a
    /// Gather tells you what to do and the world tells you where. A pin that lingers after the
    /// quest is done is worse than no pin, so they are reconciled against the active quest
    /// list every couple of seconds and removed as soon as the quest leaves it.
    ///
    /// Purely client-side and purely cosmetic: pins live in the player's own map data and
    /// nothing here can change quest state.
    /// </summary>
    internal sealed class QuestMapPins : MonoBehaviour
    {
        private static QuestMapPins _instance;

        /// <summary>Quest id -> the pin currently shown for it.</summary>
        private readonly Dictionary<string, Minimap.PinData> _pins = new Dictionary<string, Minimap.PinData>();
        private float _nextSync;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_QuestMapPins");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<QuestMapPins>();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || Minimap.instance == null) return;
            if (Time.time < _nextSync) return;
            _nextSync = Time.time + 2f;

            Sync();
        }

        private void Sync()
        {
            var wanted = new Dictionary<string, Vector3>();

            foreach (var giver in FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None))
            {
                if (giver == null || !giver.HasSyncedOnce) continue;

                foreach (var quest in giver.CachedQuests)
                {
                    if (quest.Status != QuestStatus.Active) continue;
                    if (quest.Counter >= quest.Goal) continue;   // done, just not handed in yet

                    if (quest.Objective == QuestObjectiveKind.Explore)
                    {
                        if (TryParsePlace(quest.Target, out var place)) wanted[quest.Id] = place;
                    }
                    else if (quest.Objective == QuestObjectiveKind.Talk)
                    {
                        if (TryFindNpc(quest.Target, out var where)) wanted[quest.Id] = where;
                    }
                }
            }

            // Remove pins for quests that are no longer asking for them -- finished,
            // abandoned, or the NPC walked out of the loaded world.
            var stale = new List<string>();
            foreach (var kv in _pins)
                if (!wanted.ContainsKey(kv.Key)) stale.Add(kv.Key);

            foreach (var questId in stale)
            {
                if (_pins[questId] != null) Minimap.instance.RemovePin(_pins[questId]);
                _pins.Remove(questId);
            }

            foreach (var kv in wanted)
            {
                if (_pins.ContainsKey(kv.Key)) continue;

                var name = NameOf(kv.Key);
                // Bosses' pin type is the one the game draws most prominently, which is what an
                // active objective wants to be.
                // The 6-argument overload takes a PlatformUserID, whose type lives in an
                // assembly this project does not reference -- calling it would drag Splatform
                // in for nothing. The short overload is the same pin.
                var pin = Minimap.instance.AddPin(kv.Value, Minimap.PinType.Boss, name, save: false, isChecked: false);
                _pins[kv.Key] = pin;
                Plugin.Log.LogInfo($"NpcValheim: map pin for '{name}' at {kv.Value}");
            }
        }

        /// <summary>The quest's own name, for the pin label.</summary>
        private static string NameOf(string questId)
        {
            foreach (var giver in FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None))
            {
                if (giver == null) continue;
                foreach (var quest in giver.CachedQuests)
                    if (quest.Id == questId) return quest.Name;
            }
            return questId;
        }

        /// <summary>Where an NPC with this name is standing, if one is loaded. A Talk target
        /// that is nowhere nearby simply has no pin, rather than a pin pointing at nothing.</summary>
        private static bool TryFindNpc(string npcName, out Vector3 position)
        {
            position = Vector3.zero;
            if (string.IsNullOrEmpty(npcName)) return false;

            foreach (var npc in FindObjectsByType<NpcBase>(FindObjectsSortMode.None))
            {
                if (npc == null) continue;
                if (!string.Equals(npc.GetHoverName(), npcName, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                position = npc.transform.position;
                return true;
            }
            return false;
        }

        /// <summary>Reads a "x,z" target from the quest yaml.</summary>
        private static bool TryParsePlace(string target, out Vector3 place)
        {
            place = Vector3.zero;
            if (string.IsNullOrEmpty(target)) return false;

            var parts = target.Split(',');
            if (parts.Length != 2) return false;

            var culture = System.Globalization.CultureInfo.InvariantCulture;
            var style = System.Globalization.NumberStyles.Float;
            if (!float.TryParse(parts[0].Trim(), style, culture, out float x)) return false;
            if (!float.TryParse(parts[1].Trim(), style, culture, out float z)) return false;

            place = new Vector3(x, 0f, z);
            return true;
        }
    }
}
