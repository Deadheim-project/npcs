using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// One page of the NPC window. Views build their widgets once in <see cref="Build"/> and
    /// then only mutate them in <see cref="Refresh"/> -- rebuilding the hierarchy every frame
    /// (which is what the old IMGUI panel effectively did) would drop keyboard focus out of
    /// input fields and reset scroll positions while the player is reading.
    /// </summary>
    internal abstract class NpcViewBase
    {
        protected NpcBase Npc;
        protected Player Player;
        protected RectTransform Root;

        /// <summary>Shown in the window's status line; set by views after an action.</summary>
        public string Status { get; protected set; }

        public GameObject GameObject => Root != null ? Root.gameObject : null;

        public void Build(Transform parent, NpcBase npc, Player player)
        {
            Npc = npc;
            Player = player;
            Root = ValheimUi.CreateRect(GetType().Name, parent);
            ValheimUi.Stretch(Root, 0f, 0f);
            OnBuild();
        }

        protected abstract void OnBuild();

        /// <summary>Called every frame the view is visible. Keep it cheap: it runs on the
        /// widgets that already exist.</summary>
        public virtual void Refresh() { }

        public void SetVisible(bool visible)
        {
            if (Root != null && Root.gameObject.activeSelf != visible)
                Root.gameObject.SetActive(visible);
        }

        public void Destroy()
        {
            if (Root != null) Object.Destroy(Root.gameObject);
            Root = null;
        }

        protected void Say(string message) => Status = message;

        // ---- small shared builders ----

        protected static TextMeshProUGUI Heading(Transform parent, string text) =>
            ValheimUi.CreateLabel(parent, text, 20, ValheimUi.Orange, TextAlignmentOptions.Left, display: true);

        protected static TextMeshProUGUI Body(Transform parent, string text) =>
            ValheimUi.CreateLabel(parent, text, 16, ValheimUi.Beige, TextAlignmentOptions.TopLeft);

        protected static TextMeshProUGUI Dim(Transform parent, string text) =>
            ValheimUi.CreateLabel(parent, text, 15, ValheimUi.Muted, TextAlignmentOptions.TopLeft);

        /// <summary>A horizontal strip inside a vertical layout list.</summary>
        protected static RectTransform Row(Transform parent, float height, float spacing = 8f)
        {
            var row = ValheimUi.CreateRect("Row", parent);
            var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = true;
            layout.childAlignment = TextAnchor.MiddleLeft;
            ValheimUi.SetHeight(row.gameObject, height);
            return row;
        }

        /// <summary>Puts an item icon at the left edge of a button and moves its label clear
        /// of it. Padding the text with spaces instead (the first attempt) left the icon
        /// sitting on top of the first few letters.</summary>
        protected static void Iconify(Button button, string prefabName, float iconSize = 32f)
        {
            var icon = ValheimUi.CreateItemIcon(button.transform, prefabName, iconSize);
            var iconRect = (RectTransform)icon.transform;
            ValheimUi.Anchor(iconRect, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(8f, -iconSize / 2f), new Vector2(8f + iconSize, iconSize / 2f));

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
                ValheimUi.Anchor((RectTransform)label.transform, Vector2.zero, Vector2.one,
                    new Vector2(iconSize + 16f, 2f), new Vector2(-8f, -2f));
        }

        protected static LayoutElement Flexible(GameObject go, float weight = 1f)
        {
            var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            element.flexibleWidth = weight;
            return element;
        }
    }
}
