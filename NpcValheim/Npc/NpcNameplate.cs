using TMPro;
using UnityEngine;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim.Npc
{
    /// <summary>
    /// The floating name above an NPC, plus the MMO quest marker: <b>!</b> when this player
    /// has a quest they can pick up here, <b>?</b> when they have one ready to hand in.
    ///
    /// The marker is deliberately per-player, not per-NPC: whether there is anything to do
    /// here depends entirely on who is looking. The client already receives its own quest
    /// snapshot (QuestGiverNpc.CachedQuests), so the marker is read straight off that -- no
    /// extra state and no way for one player's progress to light up someone else's marker.
    ///
    /// Drawn on a world-space canvas that is re-aimed at the camera every frame rather than
    /// parented rigidly, so it stays upright and readable regardless of the NPC's rotation or
    /// the admin-configurable body scale.
    /// </summary>
    internal sealed class NpcNameplate : MonoBehaviour
    {
        private const float HeadHeight = 2.15f;
        private const float WorldScale = 0.0075f;
        private const float VisibleDistance = 40f;
        private const float FadeStart = 30f;

        private NpcBase _npc;
        private QuestGiverNpc _questGiver;
        private Transform _root;
        private TextMeshProUGUI _name;
        private TextMeshProUGUI _marker;
        private float _nextQuestPoll;
        private bool _loggedMarker;

        private void Awake()
        {
            _npc = GetComponent<NpcBase>();
            _questGiver = _npc as QuestGiverNpc;
        }

        private void LateUpdate()
        {
            if (_npc == null) return;

            var camera = Utils.GetMainCamera();
            var player = Player.m_localPlayer;
            if (camera == null || player == null)
            {
                if (_root != null) _root.gameObject.SetActive(false);
                return;
            }

            float distance = Vector3.Distance(player.transform.position, transform.position);
            if (distance > VisibleDistance)
            {
                if (_root != null) _root.gameObject.SetActive(false);
                return;
            }

            if (_root == null && !TryBuild()) return;
            _root.gameObject.SetActive(true);

            // Keep the plate the same size on screen no matter what scale the admin gave the
            // body -- a 2x NPC should not get 2x lettering.
            float bodyScale = Mathf.Max(0.01f, transform.localScale.y);
            float height = _npc != null ? _npc.NameplateHeight : HeadHeight;
            _root.position = transform.position + Vector3.up * (height * bodyScale);
            _root.rotation = camera.transform.rotation;

            // WorldScale is in world units per canvas unit. The canvas is 600 units wide, so
            // this is what keeps a name roughly a head's width rather than spanning the
            // screen -- the first attempt used 0.01 and the name was taller than the NPC.
            _root.localScale = Vector3.one * (WorldScale / bodyScale);

            float alpha = distance <= FadeStart
                ? 1f
                : Mathf.InverseLerp(VisibleDistance, FadeStart, distance);

            _name.text = _npc.GetHoverName();
            _name.alpha = alpha;

            UpdateMarker(alpha);
        }

        private void UpdateMarker(float alpha)
        {
            if (_questGiver == null)
            {
                _marker.gameObject.SetActive(false);
                return;
            }

            PollQuestsOccasionally();

            // Marker semantics lifted from WoW, because they are already in everyone's hands:
            //   ?  yellow  -- finished, go and hand it in
            //   !  orange  -- available now
            //   !  blue    -- available and repeats on a timer (their daily blue)
            //   !  grey    -- exists here, but something is still in the way
            // Priority runs top to bottom, so the marker always shows the most actionable
            // thing this NPC has for you rather than whichever quest happened to be first.
            string glyph = null;
            Color color = ValheimUi.QuestGold;
            int rank = 0;   // higher wins

            foreach (var quest in _questGiver.CachedQuests)
            {
                if (QuestGiverNpc.CanCompleteNow(quest, Player.m_localPlayer))
                {
                    glyph = "?";
                    color = ValheimUi.QuestGold;
                    break;                      // nothing outranks a payout
                }

                if (quest.Status == QuestStatus.NotStarted && !quest.Locked && rank < 3)
                {
                    glyph = "!";
                    color = quest.Repeats ? ValheimUi.QuestBlue : ValheimUi.QuestGold;
                    rank = 3;
                }
                else if (quest.Status == QuestStatus.NotStarted && quest.Locked && rank < 1)
                {
                    // Shown rather than hidden: "there is something here, come back later" is
                    // information, and an NPC with no marker at all reads as having nothing.
                    glyph = "!";
                    color = ValheimUi.QuestLocked;
                    rank = 1;
                }
            }

            if (!_loggedMarker)
            {
                _loggedMarker = true;
                Plugin.Log.LogInfo($"NpcValheim marker: '{_npc.GetHoverName()}' quests={_questGiver.CachedQuests.Count} " +
                                   $"synced={_questGiver.HasSyncedOnce} glyph={glyph ?? "(none)"}");
            }

            _marker.gameObject.SetActive(glyph != null);
            if (glyph == null) return;

            _marker.text = glyph;
            _marker.color = color;
            _marker.alpha = alpha;

            // A gentle bob, so the marker reads as an invitation rather than as scenery.
            var rect = (RectTransform)_marker.transform;
            rect.anchoredPosition = new Vector2(0f, 42f + Mathf.Sin(Time.time * 2.4f) * 3.5f);
        }

        /// <summary>The marker has to be right *before* anyone interacts, so the client asks
        /// for its quest snapshot on a slow timer instead of only when the panel opens.</summary>
        private void PollQuestsOccasionally()
        {
            if (Time.time < _nextQuestPoll) return;
            _nextQuestPoll = Time.time + (_questGiver.HasSyncedOnce ? 15f : 2f);
            _questGiver.RequestQuests();
        }

        private bool TryBuild()
        {
            if (!ValheimUi.EnsureAssets()) return false;

            var go = new GameObject("NpcValheim_Nameplate", typeof(RectTransform), typeof(Canvas));
            go.layer = ValheimUi.UILayer;
            go.transform.SetParent(transform, false);

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            var rect = (RectTransform)go.transform;
            rect.sizeDelta = new Vector2(600f, 120f);

            _name = ValheimUi.CreateLabel(rect, _npc.GetHoverName(), 26, ValheimUi.Beige,
                TextAlignmentOptions.Center);
            var nameRect = (RectTransform)_name.transform;
            nameRect.anchorMin = nameRect.anchorMax = new Vector2(0.5f, 0.5f);
            nameRect.sizeDelta = new Vector2(600f, 44f);
            nameRect.anchoredPosition = Vector2.zero;
            AddOutline(_name);

            // Big and gold, the way an MMO marks a quest. The old 54pt orange glyph read as a
            // dark speck from any distance -- and a marker you have to look for is a marker
            // that is not doing its job.
            _marker = ValheimUi.CreateLabel(rect, "!", 90, ValheimUi.QuestGold,
                TextAlignmentOptions.Center, display: true);
            _marker.fontStyle = FontStyles.Bold;
            var markerRect = (RectTransform)_marker.transform;
            markerRect.anchorMin = markerRect.anchorMax = new Vector2(0.5f, 0.5f);
            markerRect.sizeDelta = new Vector2(160f, 120f);
            // Thin: an outline eats into the glyph's face as it grows, and past ~0.2 a "!"
            // fills in solid black instead of reading as gold with a dark edge.
            AddOutline(_marker, 0.16f);
            _marker.gameObject.SetActive(false);

            _root = go.transform;
            return true;
        }

        /// <summary>Names float over grass, snow and night sky in turn; without an outline
        /// they vanish against half of them.</summary>
        /// <summary>A black outline so the glyph survives being drawn over snow, sky or fire.
        /// The marker asks for a thicker one than the name does -- it is meant to be read at a
        /// distance where the name is not.</summary>
        private static void AddOutline(TextMeshProUGUI label, float width = 0.14f)
        {
            label.fontMaterial.EnableKeyword("OUTLINE_ON");
            label.outlineColor = new Color32(0, 0, 0, 255);
            label.outlineWidth = width;
        }

        private void OnDestroy()
        {
            if (_root != null) Destroy(_root.gameObject);
        }
    }
}
