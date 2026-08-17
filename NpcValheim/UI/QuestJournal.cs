using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;
using QuestEntry = NpcValheim.Npc.QuestView;

namespace NpcValheim.UI
{
    /// <summary>
    /// The player's own quest log, opened with a key from anywhere in the world.
    ///
    /// Quest state is server-side, and a quest giver's RPCs are addressed to that NPC's
    /// ZNetView -- useless when you are nowhere near one. So this uses a routed RPC
    /// registered globally instead, which is the same channel the game itself uses for
    /// messages that belong to a player rather than to an object.
    /// </summary>
    internal sealed class QuestJournal : MonoBehaviour
    {
        private const string RpcRequest = "NpcValheim_JournalRequest";
        private const string RpcData = "NpcValheim_JournalData";

        private static QuestJournal _instance;
        private static bool _registered;

        private GameObject _canvas;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private TextMeshProUGUI _header;

        private List<QuestEntry> _quests = new List<QuestEntry>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private string _signature;
        private float _nextRefresh;
        private bool _open;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_QuestJournal");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<QuestJournal>();
        }

        /// <summary>Opens/closes the journal the way the key does, for the scripted demo.
        /// Injecting a keystroke isn't possible from script, and calling Open directly would
        /// skip the guards the key path applies.</summary>
        internal static void Toggle()
        {
            if (_instance == null || Player.m_localPlayer == null) return;
            if (_instance._open) _instance.Close(); else _instance.Open();
        }

        /// <summary>Registered once ZRoutedRpc exists, which is after the world loads rather
        /// than at plugin start.</summary>
        private static void TryRegister()
        {
            if (_registered || ZRoutedRpc.instance == null) return;
            _registered = true;

            ZRoutedRpc.instance.Register(RpcRequest, (Action<long>)OnRequest);
            ZRoutedRpc.instance.Register(RpcData, (Action<long, string>)OnData);
            Plugin.Log.LogInfo("NpcValheim: quest journal RPCs registered");
        }

        /// <summary>Server side: answer with this player's quests.</summary>
        private static void OnRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            long playerId = GameApi.GetPlayerId(sender);
            if (playerId == 0L) return;

            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, new object[] { QuestGiverNpc.PackFor(playerId) });
        }

        private static void OnData(long sender, string packed)
        {
            if (_instance == null) return;
            _instance._quests = QuestGiverNpc.UnpackPublic(packed);
        }

        private void Update()
        {
            TryRegister();

            if (Player.m_localPlayer == null)
            {
                if (_open) Close();
                return;
            }

            // Never while another panel owns the input, or the key would type into a field.
            if (!UiInputBlocker.IsOpen && Input.GetKeyDown(Plugin.QuestJournalKey.Value))
            {
                if (_open) Close(); else Open();
            }
            else if (_open && Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
            }

            if (!_open) return;

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 3f;
                // Target read through GameApi: the direct GetServerPeerID() call throws
                // MethodAccessException at runtime on this install, so in a host/solo game the
                // journal's refresh threw every three seconds and the list never filled.
                if (ZRoutedRpc.instance != null)
                    ZRoutedRpc.instance.InvokeRoutedRPC(Npc.GameApi.GetServerPeerId(),
                        RpcRequest, new object[0]);
            }

            Refresh();
        }

        private void LateUpdate()
        {
            if (!_open) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        private void Open()
        {
            if (!ValheimUi.EnsureAssets()) return;

            _canvas = ValheimUi.CreateCanvas("NpcValheim_Journal", 4900);
            if (_canvas == null) return;

            var panel = ValheimUi.CreatePanel(_canvas.transform, 720f, 560f);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;

            var titleBar = ValheimUi.CreateRect("TitleBar", panel);
            ValheimUi.Anchor(titleBar, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -56f), Vector2.zero);
            titleBar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            titleBar.gameObject.AddComponent<DragWindow>().Target = panel;

            _header = ValheimUi.CreateLabel(titleBar, "Diário de missões", 28, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)_header.transform, 60f, 8f);

            var close = ValheimUi.CreateButton(panel, "X", 36f, 36f, 18);
            ValheimUi.Anchor((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-52f, -52f), new Vector2(-16f, -16f));
            close.onClick.AddListener(Close);

            var frame = ValheimUi.CreateInlay(panel, "Quests");
            ValheimUi.Anchor(frame, Vector2.zero, Vector2.one, new Vector2(20f, 20f), new Vector2(-20f, -60f));

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(5f, 5f), new Vector2(-5f, -5f));
            _list = ValheimUi.CreateScrollList(area, spacing: 5f);

            _empty = ValheimUi.CreateLabel(area, "Carregando...", 16, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -70f), new Vector2(-14f, -16f));

            _open = true;
            _signature = null;
            _nextRefresh = 0f;
            UiInputBlocker.IsOpen = true;
        }

        private void Close()
        {
            _open = false;
            _rows.Clear();
            if (_canvas != null) Destroy(_canvas);
            _canvas = null;
            UiInputBlocker.IsOpen = false;

            if (Menu.IsVisible() || (InventoryGui.instance != null && InventoryGui.IsVisible())) return;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Refresh()
        {
            // Only what this player is actually doing -- a journal is not a catalogue of
            // every quest on the server.
            var active = _quests.Where(q => q.Status == QuestStatus.Active).ToList();

            var signature = string.Join("|", active.Select(q =>
                q.Id + ":" + string.Join(",", QuestTracker.Steps(q).Select(s => s.Counter))));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(active);
            }

            _empty.gameObject.SetActive(active.Count == 0);
            _empty.text = "Você não tem missões em andamento.\n\nProcure um NPC com <color=#ffa13c>!</color> sobre a cabeça.";
            _header.text = active.Count > 0 ? $"Diário de missões — {active.Count}" : "Diário de missões";
        }

        private void Rebuild(List<QuestEntry> active)
        {
            foreach (var row in _rows) if (row != null) Destroy(row);
            _rows.Clear();

            foreach (var quest in active)
            {
                var steps = QuestTracker.Steps(quest);

                var card = ValheimUi.CreateRect("Quest", _list);
                ValheimUi.SetHeight(card.gameObject, 66f + 20f * steps.Count);
                _rows.Add(card.gameObject);

                var column = card.gameObject.AddComponent<VerticalLayoutGroup>();
                column.spacing = 2f;
                column.padding = new RectOffset(10, 10, 6, 6);
                column.childControlWidth = true;
                column.childControlHeight = true;
                column.childForceExpandWidth = true;
                column.childForceExpandHeight = false;

                ValheimUi.CreateLabel(card, quest.Name, 18, ValheimUi.Orange,
                    TextAlignmentOptions.TopLeft, display: true);

                var player = Player.m_localPlayer;
                bool ready = QuestGiverNpc.CanCompleteNow(quest, player);

                foreach (var step in steps)
                    ValheimUi.CreateLabel(card,
                        step.IsDone(player)
                            ? $"<color=#6fbf5b>✔ {QuestGiverNpc.Describe(step)}</color>"
                            : $"{QuestGiverNpc.Describe(step)}   <color=#9a9188>{step.Progress(player)}/{step.Goal}</color>",
                        15, ValheimUi.Beige, TextAlignmentOptions.TopLeft);

                if (ready)
                    ValheimUi.CreateLabel(card, "<color=#ffe300>pronta para entregar</color>",
                        15, ValheimUi.Beige, TextAlignmentOptions.TopLeft);

                ValheimUi.CreateLabel(card, $"Recompensa: {quest.RewardText}", 14, ValheimUi.Muted,
                    TextAlignmentOptions.TopLeft);
            }
        }
    }
}
