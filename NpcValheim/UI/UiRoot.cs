using UnityEngine;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// Owns the NPC window's lifetime: opens it when an NPC asks, closes it on Escape or when
    /// the NPC goes away, and keeps the mouse cursor free while it is up.
    ///
    /// The window itself is Unity UI built from the game's own assets (see ValheimUi). This
    /// used to be an IMGUI OnGUI panel; the visible difference is that atlas sprites, item
    /// icons, the game's fonts and its click sounds all work now, none of which IMGUI could
    /// reach without copying textures out of the atlas by hand.
    /// </summary>
    public class UiRoot : MonoBehaviour
    {
        private static UiRoot _instance;

        private NpcBase _npc;
        private Player _player;
        private NpcWindow _window;

        public static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_UiRoot");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<UiRoot>();
        }

        public static void Open(NpcBase npc, Player player)
        {
            if (_instance == null || npc == null || player == null) return;
            _instance.OpenInternal(npc, player);
        }

        public static void RequestClose() => _instance?.Close();

        /// <summary>Showcase-only: steps to the next tab so an off-screen capture can show
        /// each one without anyone clicking.</summary>
        internal static void ShowcaseCycleTab() => _instance?._window?.CycleTab();

        public static bool IsOpen => _instance != null && _instance._window != null;

        private void OpenInternal(NpcBase npc, Player player)
        {
            // Assets live in the game's UI scene; if they are not up yet there is nothing to
            // build a Valheim-looking window out of, and a half-styled one is worse than
            // waiting a frame.
            if (!ValheimUi.EnsureAssets())
            {
                Plugin.Log.LogWarning("NpcValheim: UI assets not ready yet, panel not opened");
                return;
            }

            Close();
            _npc = npc;
            _player = player;
            _window = new NpcWindow(npc, player, Close);

            if (!_window.Alive)
            {
                Plugin.Log.LogError("NpcValheim: could not find the game's GUI root, panel not opened");
                _window = null;
                return;
            }

            UiInputBlocker.IsOpen = true;
            _loggedCursorState = false;
        }

        private void Close()
        {
            _window?.Destroy();
            _window = null;
            _npc = null;
            UiInputBlocker.IsOpen = false;
            ReleaseCursor();
        }

        private void Update()
        {
            if (Player.m_localPlayer == null)
            {
                if (_window != null) Close();
                return;
            }

            // Interact() cannot safely build UI from inside the game's input handling, so it
            // raises a flag that we pick up here on the next frame.
            foreach (var npc in FindObjectsByType<NpcBase>(FindObjectsSortMode.None))
            {
                if (!npc.PanelOpenRequested) continue;
                npc.ConsumePanelOpenRequest();
                Open(npc, Player.m_localPlayer);
            }

            // The NPC can be destroyed or unloaded while the panel is open -- Unity's == null
            // is true for destroyed objects, so this catches it.
            if (_window != null && _npc == null) Close();
            if (_window != null && Input.GetKeyDown(KeyCode.Escape)) Close();

            _window?.Refresh(_npc);
            UiInputBlocker.IsOpen = _window != null;
        }

        /// <summary>Forces the mouse cursor visible while the panel is open.
        ///
        /// Patching Menu.IsVisible (see Patches/UiInputPatches.cs) is what tells the game to
        /// stop treating input as gameplay, but it is not enough on its own to get a cursor:
        /// the game re-asserts lockState every frame from its own update, so whatever we set
        /// earlier in the frame is overwritten. LateUpdate runs after those, so this is the
        /// last word on it.</summary>
        private void LateUpdate()
        {
            if (_window == null) return;

            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;

            if (!_loggedCursorState)
            {
                _loggedCursorState = true;
                Plugin.Log.LogInfo($"NpcValheim: panel open -- cursor visible={Cursor.visible} lockState={Cursor.lockState}");
            }
        }

        private bool _loggedCursorState;

        private void OnDestroy()
        {
            UiInputBlocker.IsOpen = false;
            ReleaseCursor();
        }

        /// <summary>Hands the cursor back to the game so closing our panel doesn't leave the
        /// player unable to turn the camera.</summary>
        private void ReleaseCursor()
        {
            _loggedCursorState = false;
            if (Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }
}
