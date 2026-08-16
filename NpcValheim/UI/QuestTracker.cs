using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;
// UI.QuestView is the panel; Npc.QuestView is the data it draws. Same name, different jobs.
using QuestEntry = NpcValheim.Npc.QuestView;

namespace NpcValheim.UI
{
    /// <summary>
    /// The objective tracker: what you are doing, on screen, while you play.
    ///
    /// Borrowed straight from WoW, and for the reason that makes it work there -- a quest you
    /// have to open a menu to remember is a quest you forget. The journal behind J is the
    /// place to read and manage; this is the place to glance. So it shows only what is in
    /// progress, only the line that matters (objective and count), and gets out of the way
    /// whenever a real panel is open.
    ///
    /// It also follows WoW in what it does with a finished objective: the line turns green and
    /// gains a tick rather than vanishing, so the moment of completing something is visible
    /// instead of the entry silently disappearing.
    /// </summary>
    internal sealed class QuestTracker : MonoBehaviour
    {
        private static QuestTracker _instance;

        private GameObject _canvas;
        private RectTransform _list;
        private TextMeshProUGUI _header;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private string _signature;
        private float _nextRefresh;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_QuestTracker");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<QuestTracker>();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null || !Plugin.ShowQuestTracker.Value)
            {
                Teardown();
                return;
            }

            if (_canvas == null) Build();
            if (_canvas == null) return;

            // Same rule as the rest of the HUD: gone while a full-screen panel is up.
            bool hud = !UiInputBlocker.IsOpen &&
                       (InventoryGui.instance == null || !InventoryGui.IsVisible()) &&
                       (Menu.instance == null || !Menu.IsVisible());
            if (_canvas.activeSelf != hud) _canvas.SetActive(hud);
            if (!hud) return;

            if (Time.time < _nextRefresh) return;
            _nextRefresh = Time.time + 1f;
            Refresh();
        }

        private void Teardown()
        {
            if (_canvas != null) Destroy(_canvas);
            _canvas = null;
            _rows.Clear();
            _signature = null;
        }

        private void Build()
        {
            if (!ValheimUi.EnsureAssets()) return;

            _canvas = ValheimUi.CreateCanvas("NpcValheim_Tracker", 900);
            if (_canvas == null) return;

            // Right edge, like WoW's, and deliberately with no panel background: a solid box
            // sitting over the world all session is far heavier than the text needs.
            var root = ValheimUi.CreateRect("Tracker", _canvas.transform);
            root.anchorMin = root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(1f, 1f);
            root.anchoredPosition = new Vector2(-Plugin.QuestTrackerX.Value, -Plugin.QuestTrackerY.Value);
            root.sizeDelta = new Vector2(320f, 400f);

            _header = ValheimUi.CreateLabel(root, "Missões", 18, ValheimUi.Orange,
                TextAlignmentOptions.Right, display: true);
            ValheimUi.Anchor((RectTransform)_header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -26f), Vector2.zero);
            AddShadow(_header);

            var area = ValheimUi.CreateRect("Area", root);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(0f, 0f), new Vector2(0f, -30f));

            var column = area.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 8f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.childAlignment = TextAnchor.UpperRight;
            _list = area;
        }

        private void Refresh()
        {
            var quests = ActiveQuests();

            var signature = string.Join("|", quests.Select(q => $"{q.Id}:{Progress(q)}:{q.Goal}"));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(quests);
            }

            _header.gameObject.SetActive(quests.Count > 0);
        }

        /// <summary>This player's quests in progress. Capped, because a tracker that fills the
        /// screen has stopped being a glance.</summary>
        private static List<QuestEntry> ActiveQuests()
        {
            foreach (var giver in FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None))
            {
                if (giver == null || !giver.HasSyncedOnce) continue;
                return giver.CachedQuests
                    .Where(q => q.Status == QuestStatus.Active)
                    .Take(Mathf.Max(1, Plugin.QuestTrackerMax.Value))
                    .ToList();
            }
            return new List<QuestEntry>();
        }

        /// <summary>Where the player stands, counted the way this objective is counted --
        /// Collect reads the bag, everything else reads the server's counter.</summary>
        private static int Progress(QuestEntry quest)
        {
            if (quest.Objective != QuestObjectiveKind.Collect) return quest.Counter;
            var player = Player.m_localPlayer;
            return player != null ? ItemNames.Count(player.GetInventory(), quest.Target, -1) : 0;
        }

        private void Rebuild(List<QuestEntry> quests)
        {
            foreach (var row in _rows) if (row != null) Destroy(row);
            _rows.Clear();

            foreach (var quest in quests)
            {
                var entry = ValheimUi.CreateRect("Quest", _list);
                ValheimUi.SetHeight(entry.gameObject, 44f);
                _rows.Add(entry.gameObject);

                var column = entry.gameObject.AddComponent<VerticalLayoutGroup>();
                column.spacing = 0f;
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;

                var title = ValheimUi.CreateLabel(entry, quest.Name, 15, ValheimUi.Beige,
                    TextAlignmentOptions.Right, display: true);
                AddShadow(title);

                int now = Progress(quest);
                bool done = now >= quest.Goal;

                // Green with a tick when finished, exactly like the tracker this is modelled
                // on -- the completion is the moment worth showing.
                var line = ValheimUi.CreateLabel(entry,
                    done ? $"<color=#6fbf5b>✔ {quest.ObjectiveText}  {now}/{quest.Goal}</color>"
                         : $"<color=#c9c1b4>{quest.ObjectiveText}  {now}/{quest.Goal}</color>",
                    13, ValheimUi.Muted, TextAlignmentOptions.Right);
                AddShadow(line);
            }
        }

        /// <summary>A drop shadow, because this text sits directly on the world: over snow or
        /// a bright sky, unshadowed beige is unreadable.</summary>
        private static void AddShadow(TextMeshProUGUI label)
        {
            var shadow = label.gameObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1.5f, -1.5f);
        }
    }
}

