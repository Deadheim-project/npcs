using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.U2D;
using UnityEngine.UI;

namespace NpcValheim.UI
{
    /// <summary>
    /// A small, self-contained equivalent of Jötunn's GUIManager: it finds the game's own UI
    /// assets at runtime and hands back Unity UI widgets already dressed in them.
    ///
    /// The asset names here are not guesses -- they are the ones Jötunn itself uses
    /// (UIAtlas/IconAtlas, "woodpanel_trophys", "button", "text_field", the litpanel material,
    /// the Valheim TMP fonts and the gui sfx prefabs), so this matches what every
    /// Valheim-styled mod already looks like on screen.
    ///
    /// Why uGUI at all, when the panel used to be IMGUI: IMGUI needs a CPU-readable
    /// Texture2D, and the game's buttons/fields live inside a sprite atlas that does not
    /// survive that extraction -- which is exactly why they came out as white blocks before.
    /// An Image just references the atlas sprite and renders it natively, so the real look
    /// comes for free, together with 9-slicing, hover states and the click sounds.
    /// </summary>
    internal static class ValheimUi
    {
        public const int UILayer = 5;

        // Jötunn's palette, taken from the game's own UI.
        public static readonly Color Orange = new Color(1f, 0.631f, 0.235f, 1f);
        public static readonly Color Beige = new Color(0.8529f, 0.725f, 0.5331f, 1f);
        public static readonly Color Yellow = new Color(1f, 0.889f, 0f, 1f);

        /// <summary>The gold an MMO marks a quest with -- WoW's #FFD100. Bright enough to
        /// carry an outline at distance, which the mod's orange was not.</summary>
        public static readonly Color QuestGold = new Color(1f, 0.82f, 0f, 1f);

        /// <summary>Blue for a quest that comes back on a timer, grey for one that is here but
        /// not yet takeable. Same meanings the same colours carry in WoW.</summary>
        public static readonly Color QuestBlue = new Color(0.2f, 0.73f, 1f, 1f);
        public static readonly Color QuestLocked = new Color(0.58f, 0.56f, 0.53f, 1f);
        public static readonly Color Muted = new Color(0.62f, 0.58f, 0.50f, 1f);
        public static readonly Color Danger = new Color(0.93f, 0.42f, 0.34f, 1f);

        public static readonly ColorBlock ButtonColors = new ColorBlock
        {
            normalColor = new Color(0.824f, 0.824f, 0.824f, 1f),
            highlightedColor = new Color(1.3f, 1.3f, 1.3f, 1f),
            pressedColor = new Color(0.537f, 0.556f, 0.556f, 1f),
            selectedColor = new Color(0.824f, 0.824f, 0.824f, 1f),
            disabledColor = new Color(0.566f, 0.566f, 0.566f, 0.502f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f,
        };

        public static readonly ColorBlock ScrollHandleColors = new ColorBlock
        {
            normalColor = new Color(0.926f, 0.645f, 0.34f, 1f),
            highlightedColor = new Color(1f, 0.786f, 0.088f, 1f),
            pressedColor = new Color(0.838f, 0.647f, 0.03f, 1f),
            selectedColor = new Color(1f, 0.786f, 0.088f, 1f),
            disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f),
            colorMultiplier = 1f,
            fadeDuration = 0.1f,
        };

        public static TMP_FontAsset FontBody { get; private set; }
        public static TMP_FontAsset FontDisplay { get; private set; }
        public static Sprite PanelSprite { get; private set; }
        public static Sprite ButtonSprite { get; private set; }
        public static Sprite FieldSprite { get; private set; }
        public static Sprite ScrollHandleSprite { get; private set; }
        public static Sprite ScrollBackSprite { get; private set; }
        public static Material PanelMaterial { get; private set; }

        private static GameObject _sfxButton;
        private static GameObject _sfxSelect;

        private static SpriteAtlas _uiAtlas;
        private static SpriteAtlas _iconAtlas;
        private static bool _loaded;

        /// <summary>Resolves every asset once the game's own UI exists. Returns false while
        /// the atlases are not loaded yet, so callers can just try again next frame instead of
        /// caching a half-built skin.</summary>
        public static bool EnsureAssets()
        {
            if (_loaded) return true;

            _uiAtlas = FindByName<SpriteAtlas>("UIAtlas");
            _iconAtlas = FindByName<SpriteAtlas>("IconAtlas");

            PanelSprite = GetSprite("woodpanel_trophys") ?? GetSprite("woodpanel_settings");
            ButtonSprite = GetSprite("button");
            FieldSprite = GetSprite("text_field");
            ScrollHandleSprite = GetSprite("UISprite");
            ScrollBackSprite = GetSprite("Background");
            PanelMaterial = FindByName<Material>("litpanel");

            FontBody = FindByName<TMP_FontAsset>("Valheim-AveriaSansLibre");
            FontDisplay = FindByName<TMP_FontAsset>("Valheim-Norse") ?? FontBody;

            _sfxButton = FindByName<GameObject>("sfx_gui_button");
            _sfxSelect = FindByName<GameObject>("sfx_gui_select");

            // The panel sprite and a font are the two things nothing can substitute for; the
            // rest degrade to a plain tint without making the window unusable.
            if (PanelSprite == null || FontBody == null) return false;

            _loaded = true;
            Plugin.Log.LogInfo(
                $"NpcValheim UI: panel='{PanelSprite.name}' button='{Name(ButtonSprite)}' " +
                $"field='{Name(FieldSprite)}' font='{FontBody.name}' display='{Name(FontDisplay)}' " +
                $"material={(PanelMaterial != null)} sfx={(_sfxButton != null)}");
            return true;
        }

        private static string Name(Object o) => o != null ? o.name : "none";

        public static Sprite GetSprite(string spriteName)
        {
            var fromUi = _uiAtlas != null ? _uiAtlas.GetSprite(spriteName) : null;
            if (fromUi != null) return fromUi;
            var fromIcons = _iconAtlas != null ? _iconAtlas.GetSprite(spriteName) : null;
            if (fromIcons != null) return fromIcons;
            return FindByName<Sprite>(spriteName);
        }

        /// <summary>Exact-name lookup over everything currently loaded. Exact matters: a
        /// substring search for "button" happily returns a building piece's icon.</summary>
        private static T FindByName<T>(string wanted) where T : Object =>
            Resources.FindObjectsOfTypeAll<T>()
                .FirstOrDefault(o => o != null && string.Equals(o.name, wanted, System.StringComparison.Ordinal));

        // ---------- construction ----------

        /// <summary>Our own canvas under the game's GUI root, so the window sorts above the
        /// HUD without us touching any of the game's own canvases.</summary>
        public static GameObject CreateCanvas(string name, int sortingOrder)
        {
            var parent = FindGuiRoot();
            if (parent == null) return null;

            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster)) { layer = UILayer };
            go.transform.SetParent(parent, false);

            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.TexCoord1 |
                                              AdditionalCanvasShaderChannels.Normal |
                                              AdditionalCanvasShaderChannels.Tangent;
            canvas.overrideSorting = true;
            canvas.sortingOrder = sortingOrder;

            go.GetComponent<CanvasScaler>().referencePixelsPerUnit = 50;
            go.transform.SetAsLastSibling();
            return go;
        }

        private static Transform FindGuiRoot()
        {
            foreach (var root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name == "GuiRoot") return root.transform.Find("GUI");
                if (root.name == "_GameMain") return root.transform.Find("LoadingGUI");
            }
            return null;
        }

        public static RectTransform CreateRect(string name, Transform parent, bool active = true)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = UILayer };
            if (!active) go.SetActive(false);
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>The wooden window frame, 9-sliced exactly as the game slices it.</summary>
        public static RectTransform CreatePanel(Transform parent, float width, float height)
        {
            var rect = CreateRect("Panel", parent);
            rect.sizeDelta = new Vector2(width, height);

            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = PanelSprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 1f;
            if (PanelMaterial != null) image.material = PanelMaterial;
            image.color = Color.white;
            return rect;
        }

        /// <summary>A flat dark inlay used to separate a list or detail area from the wood.
        /// Uses the field sprite so the inner border matches the game's own sunken boxes.</summary>
        public static RectTransform CreateInlay(Transform parent, string name = "Inlay")
        {
            var rect = CreateRect(name, parent);

            // Two layers, like the game's own sunken boxes: a dark fill for contrast, then
            // the field sprite on top for its border. One layer alone reads as either
            // washed-out wood or a flat black rectangle.
            var fill = rect.gameObject.AddComponent<Image>();
            fill.color = new Color(0f, 0f, 0f, 0.62f);

            if (FieldSprite != null)
            {
                var frame = CreateRect("Frame", rect);
                Stretch(frame, 0f, 0f);
                var border = frame.gameObject.AddComponent<Image>();
                border.sprite = FieldSprite;
                border.type = Image.Type.Sliced;
                border.pixelsPerUnitMultiplier = 1f;
                border.color = new Color(1f, 1f, 1f, 0.85f);
                border.raycastTarget = false;
            }
            return rect;
        }

        public static TextMeshProUGUI CreateLabel(Transform parent, string text, int fontSize,
            Color color, TextAlignmentOptions alignment = TextAlignmentOptions.TopLeft, bool display = false)
        {
            // Built inactive on purpose. TextMeshProUGUI resolves a font in Awake, and
            // AddComponent on an *active* object runs Awake immediately -- before we can
            // assign ours. It then falls back to TMP's built-in LiberationSans SDF, which
            // Valheim does not ship, and every single label logs
            // "There is no Font Asset assigned". Deferring Awake until after the assignment
            // is what makes that go away, rather than just hiding the warning.
            var rect = CreateRect("Label", parent, active: false);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            label.font = display ? FontDisplay : FontBody;
            label.fontSize = fontSize;
            label.color = color;
            label.alignment = alignment;
            label.text = text ?? "";
            label.raycastTarget = false;
            label.overflowMode = TextOverflowModes.Overflow;
            rect.gameObject.SetActive(true);
            return label;
        }

        public static Button CreateButton(Transform parent, string text, float width, float height,
            int fontSize = 16)
        {
            var rect = CreateRect("Button", parent);
            rect.sizeDelta = new Vector2(width, height);

            var image = rect.gameObject.AddComponent<Image>();
            image.sprite = ButtonSprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = 1f;

            var button = rect.gameObject.AddComponent<Button>();
            button.image = image;
            button.colors = ButtonColors;
            AttachSfx(rect.gameObject);

            var label = CreateLabel(rect, text, fontSize, Orange, TextAlignmentOptions.Center);
            Stretch((RectTransform)label.transform, 6f, 2f);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Ellipsis;

            // A layout group ignores sizeDelta and asks the LayoutElement instead. Without
            // this every button inside a list collapses to a 1px sliver, which is exactly
            // what the first render did.
            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.flexibleHeight = 0f;
            if (height > 0f) { element.preferredHeight = height; element.minHeight = height; }
            if (width > 0f) { element.preferredWidth = width; element.minWidth = width; }
            return button;
        }

        /// <summary>Gives a widget the game's own click/hover sounds. ButtonSfx lives in
        /// assembly_guiutils, and it is what makes a custom button *sound* native too.</summary>
        public static void AttachSfx(GameObject go)
        {
            if (_sfxButton == null) return;
            var sfx = go.GetComponent<ButtonSfx>() ?? go.AddComponent<ButtonSfx>();
            sfx.m_sfxPrefab = _sfxButton;
            sfx.m_selectSfxPrefab = _sfxSelect;
        }

        public static TMP_InputField CreateInputField(Transform parent, string value, float width,
            float height, int fontSize = 15, bool multiline = false)
        {
            // Inactive while assembling, for the same reason CreateLabel is: TMP_InputField
            // resolves its font in Awake too.
            var rect = CreateRect("InputField", parent, active: false);
            rect.sizeDelta = new Vector2(width, height);

            var image = rect.gameObject.AddComponent<Image>();
            if (FieldSprite != null)
            {
                image.sprite = FieldSprite;
                image.type = Image.Type.Sliced;
                image.pixelsPerUnitMultiplier = 1f;
            }
            else
            {
                image.color = new Color(0f, 0f, 0f, 0.6f);
            }

            var viewport = CreateRect("TextArea", rect);
            Stretch(viewport, 8f, 4f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var text = CreateLabel(viewport, value ?? "", fontSize, Beige,
                multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.Left);
            Stretch((RectTransform)text.transform, 0f, 0f);
            text.textWrappingMode = multiline ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;

            var field = rect.gameObject.AddComponent<TMP_InputField>();
            field.textViewport = viewport;
            field.textComponent = text;
            field.text = value ?? "";
            field.fontAsset = FontBody;
            field.pointSize = fontSize;
            field.lineType = multiline
                ? TMP_InputField.LineType.MultiLineNewline
                : TMP_InputField.LineType.SingleLine;
            field.caretColor = Orange;
            field.selectionColor = new Color(1f, 0.631f, 0.235f, 0.35f);

            var element = rect.gameObject.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.minWidth = width;
            element.preferredHeight = height;
            element.minHeight = height;
            element.flexibleHeight = 0f;

            rect.gameObject.SetActive(true);
            return field;
        }

        /// <summary>A scrolling column. Returns the content transform, already set up with a
        /// vertical layout + size fitter so callers only add children.</summary>
        public static RectTransform CreateScrollList(Transform parent, float spacing = 4f,
            RectOffset padding = null)
        {
            const float barWidth = 12f;

            var viewport = CreateRect("Viewport", parent);
            // Leave room for the scrollbar rather than letting it expand the viewport:
            // AutoHideAndExpandViewport reflows the list every time the content crosses the
            // scroll threshold, which makes rows twitch while you read them.
            viewport.anchorMin = Vector2.zero;
            viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero;
            viewport.offsetMax = new Vector2(-(barWidth + 3f), 0f);
            viewport.gameObject.AddComponent<RectMask2D>();

            var content = CreateRect("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;

            // A fresh RectTransform starts at sizeDelta (100, 100). With the anchors above
            // that makes the content 100px WIDER than the viewport and, because the pivot is
            // centred, 50px of every row hangs off the left edge and gets masked away --
            // which is why list rows were losing the start of their text. The fitter drives
            // height, so only width has to be pinned here.
            content.sizeDelta = new Vector2(0f, 0f);

            var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.padding = padding ?? new RectOffset(6, 6, 6, 6);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.childAlignment = TextAnchor.UpperLeft;

            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.content = content;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 40f;

            AddScrollbar(scroll, viewport, barWidth);
            return content;
        }

        private static void AddScrollbar(ScrollRect scroll, RectTransform viewport, float barWidth)
        {
            var bar = CreateRect("Scrollbar", viewport.parent);
            bar.anchorMin = new Vector2(1f, 0f);
            bar.anchorMax = new Vector2(1f, 1f);
            bar.pivot = new Vector2(1f, 0.5f);
            bar.sizeDelta = new Vector2(barWidth, 0f);
            bar.anchoredPosition = Vector2.zero;

            var back = bar.gameObject.AddComponent<Image>();
            back.sprite = ScrollBackSprite;
            back.color = new Color(0f, 0f, 0f, 0.75f);
            back.pixelsPerUnitMultiplier = 1f;

            var slidingArea = CreateRect("SlidingArea", bar);
            Stretch(slidingArea, 0f, 0f);

            // Anchors are rewritten by Scrollbar every frame from its value/size, so the
            // handle must start neutral; stretching it here is what produced one giant blob.
            var handle = CreateRect("Handle", slidingArea);
            handle.anchorMin = Vector2.zero;
            handle.anchorMax = Vector2.one;
            handle.offsetMin = Vector2.zero;
            handle.offsetMax = Vector2.zero;
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.sprite = ScrollHandleSprite;
            handleImage.pixelsPerUnitMultiplier = 1f;

            var scrollbar = bar.gameObject.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = handle;
            scrollbar.targetGraphic = handleImage;
            scrollbar.transition = Selectable.Transition.ColorTint;
            scrollbar.colors = ScrollHandleColors;

            scroll.verticalScrollbar = scrollbar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        /// <summary>An item icon exactly as the inventory draws it.</summary>
        public static Image CreateItemIcon(Transform parent, string prefabName, float size)
        {
            var rect = CreateRect("Icon", parent);
            rect.sizeDelta = new Vector2(size, size);

            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.sprite = FindItemIcon(prefabName);
            image.enabled = image.sprite != null;

            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = size;
            layout.preferredHeight = size;
            layout.minWidth = size;
            return image;
        }

        private static readonly Dictionary<string, Sprite> IconCache = new Dictionary<string, Sprite>();

        public static Sprite FindItemIcon(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            if (IconCache.TryGetValue(prefabName, out var cached)) return cached;

            Sprite icon = null;
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData?.m_shared?.m_icons != null && drop.m_itemData.m_shared.m_icons.Length > 0)
                icon = drop.m_itemData.m_shared.m_icons[0];

            IconCache[prefabName] = icon;
            return icon;
        }

        public static string Localize(string key)
        {
            if (string.IsNullOrEmpty(key) || Localization.instance == null) return key ?? "";
            var text = Localization.instance.Localize(key);
            return string.IsNullOrEmpty(text) ? key : text;
        }

        // ---------- layout helpers ----------

        public static void Stretch(RectTransform rect, float horizontalInset, float verticalInset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(horizontalInset, verticalInset);
            rect.offsetMax = new Vector2(-horizontalInset, -verticalInset);
        }

        public static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 offsetMin, Vector2 offsetMax)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        public static LayoutElement SetHeight(GameObject go, float height)
        {
            var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;

            // flexibleHeight defaults to -1 ("unset"), which makes the parent fall back to
            // whatever the object's own LayoutGroup reports -- and a HorizontalLayoutGroup
            // with childForceExpandHeight reports "give me everything". That is how a 40px
            // row ended up 190px tall. Pinning it to 0 says: this row wants exactly its
            // preferred height and no share of the leftover space.
            element.flexibleHeight = 0f;
            return element;
        }

        /// <summary>The horizontal counterpart: a fixed-width cell inside a row that neither
        /// stretches nor gets squeezed by its flexible neighbours.</summary>
        public static LayoutElement SetWidth(GameObject go, float width)
        {
            var element = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.minWidth = width;
            element.flexibleWidth = 0f;
            return element;
        }
    }

    /// <summary>Drag-to-move for the window, from any point of its title bar.</summary>
    internal sealed class DragWindow : MonoBehaviour, IDragHandler, IBeginDragHandler
    {
        public RectTransform Target;
        private Vector2 _grabOffset;

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)Target.parent, eventData.position, eventData.pressEventCamera, out var local);
            _grabOffset = Target.anchoredPosition - local;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Target == null) return;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    (RectTransform)Target.parent, eventData.position, eventData.pressEventCamera, out var local))
                Target.anchoredPosition = local + _grabOffset;
        }
    }
}
