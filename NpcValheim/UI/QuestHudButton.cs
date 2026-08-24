using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// A button on the HUD that opens the quest journal, with a count of what is in progress.
    ///
    /// Its own icon rather than one hung on another mod's bar: reaching into EpicMMO's UI
    /// would put this mod at the mercy of that one's next layout change, for a button that
    /// works perfectly well standing on its own. Where it sits is a config value, so it can be
    /// moved out of the way of whatever else a server has on screen.
    ///
    /// Drawn from the game's own button sprite and the same orange used by the "!" over a
    /// quest giver's head, so it reads as part of Valheim and as part of this mod at once.
    /// </summary>
    internal sealed class QuestHudButton : MonoBehaviour
    {
        private static QuestHudButton _instance;

        private GameObject _canvas;
        private TextMeshProUGUI _count;
        private TextMeshProUGUI _mark;
        private Image _background;
        private float _nextRefresh;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_QuestHudButton");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<QuestHudButton>();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null)
            {
                if (_canvas != null) Destroy(_canvas);
                _canvas = null;
                return;
            }

            if (!Plugin.ShowQuestButton.Value)
            {
                if (_canvas != null) Destroy(_canvas);
                _canvas = null;
                return;
            }

            if (_canvas == null) Build();
            if (_canvas == null) return;

            // Deliberately still shown while the inventory is open.
            //
            // It used to hide whenever InventoryGui was visible, which sounds tidy and made
            // the button impossible to use: Valheim only gives you a mouse cursor while the
            // inventory or a menu is open, so the one moment you could click it was the exact
            // moment it disappeared. It was never "behind the inventory" -- it was gone, and
            // what remains behind it is the tracker.
            //
            // The escape menu still hides it, because that one is a modal the game expects to
            // own the screen, and our own panels do too (UiInputBlocker) so a quest window
            // cannot be opened on top of a shop.
            bool hud = !UiInputBlocker.IsOpen &&
                       (Menu.instance == null || !Menu.IsVisible());
            if (_canvas.activeSelf != hud) _canvas.SetActive(hud);

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 1f;
                Refresh();
            }
        }

        private void Build()
        {
            if (!ValheimUi.EnsureAssets()) return;

            // Above the inventory, below our own panels (4900+) so it can never cover one.
            // At 1000 it drew underneath the inventory window, so on the one screen where it
            // is clickable it was also the one screen where it was covered.
            _canvas = ValheimUi.CreateCanvas("NpcValheim_QuestButton", 4800);
            if (_canvas == null) return;

            var button = ValheimUi.CreateButton(_canvas.transform, "", 64f, 64f, 16);
            var rect = (RectTransform)button.transform;

            // Anchored to the top-left and pushed by the configured offset: an absolute
            // position would land somewhere different on every resolution.
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(Plugin.QuestButtonX.Value, -Plugin.QuestButtonY.Value);
            rect.sizeDelta = new Vector2(64f, 64f);

            _background = button.GetComponent<Image>();
            button.onClick.AddListener(QuestJournal.Toggle);

            // The same glyph that floats over a quest giver's head, so the two teach each
            // other: what the "!" means out there is what this button opens. Bold, gold and
            // outlined -- at 30pt in the mod's orange it read as a small dark smudge against
            // the button's own dark wood.
            _mark = ValheimUi.CreateLabel(rect, "!", 44, ValheimUi.QuestGold,
                TextAlignmentOptions.Center, display: true);
            _mark.fontStyle = FontStyles.Bold;

            // 0.12, not 0.3. A TMP outline grows inward as well as outward, so on a glyph this
            // size a thick one closes over the face and the whole thing renders as a black
            // blob -- which is exactly what it looked like.
            _mark.fontMaterial.EnableKeyword("OUTLINE_ON");
            _mark.outlineColor = new Color32(0, 0, 0, 255);
            _mark.outlineWidth = 0.12f;
            ValheimUi.Stretch((RectTransform)_mark.transform, 0f, 0f);

            _count = ValheimUi.CreateLabel(rect, "", 17, ValheimUi.QuestGold, TextAlignmentOptions.BottomRight);
            ValheimUi.Anchor((RectTransform)_count.transform, Vector2.zero, Vector2.one,
                new Vector2(0f, 2f), new Vector2(-4f, 0f));
        }

        /// <summary>Shows how many quests are in progress, and dims when there are none, so
        /// the button reports state instead of only being a door.</summary>
        private void Refresh()
        {
            int active = 0;
            bool ready = false;

            foreach (var giver in FindObjectsByType<QuestGiverNpc>(FindObjectsSortMode.None))
            {
                if (giver == null || !giver.HasSyncedOnce) continue;
                foreach (var quest in giver.CachedQuests)
                {
                    if (quest.Status != QuestStatus.Active) continue;
                    active++;
                    if (QuestGiverNpc.CanCompleteNow(quest, Player.m_localPlayer)) ready = true;
                }
                break;   // one giver's snapshot already covers this player's quests
            }

            if (_count != null) _count.text = active > 0 ? active.ToString() : "";

            // The glyph carries the state, matching the marker over an NPC's head exactly:
            // "?" gold when something is ready to hand in, "!" gold while quests are running,
            // and a dimmed "!" when there is nothing on. The button frame stays as it is --
            // tinting the wood made it look broken rather than highlighted.
            if (_mark != null)
            {
                _mark.text = ready ? "?" : "!";
                _mark.color = active > 0 ? ValheimUi.QuestGold : ValheimUi.QuestLocked;
            }

            if (_background != null)
                _background.color = active > 0 ? Color.white : new Color(1f, 1f, 1f, 0.6f);
        }
    }
}
