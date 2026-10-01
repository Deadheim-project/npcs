using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Arena;

namespace NpcValheim.UI
{
    /// <summary>
    /// The arena on screen while you play: the line WoW put at the top of the screen (queue
    /// time, "the arena is ready", the countdown, the two teams still standing) and the key
    /// that opens the arena panel from anywhere -- WoW's PvP frame, which is where the
    /// popups that need an answer (Entrar, Assinar, Aceitar) are answered.
    /// </summary>
    internal sealed class ArenaHud : MonoBehaviour
    {
        internal const string PanelTitle = "Arena";

        private static ArenaHud _instance;
        private GameObject _canvas;
        private TextMeshProUGUI _line;
        private float _nextRefresh;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_ArenaHud");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ArenaHud>();
        }

        internal static void TogglePanel()
        {
            if (UiRoot.StandaloneTitle == PanelTitle)
            {
                UiRoot.RequestClose();
                return;
            }
            if (UiInputBlocker.IsOpen || Player.m_localPlayer == null) return;
            ArenaClient.RequestData();
            UiRoot.OpenStandalone(PanelTitle, new List<(string, NpcViewBase)>
            {
                ("Arena", new ArenaStatusView()),
                ("Times", new ArenaTeamsView()),
                ("Ranking", new ArenaLadderView()),
            }, Player.m_localPlayer);
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Teardown();
                return;
            }

            if (ArenaConfig.PanelKey != null && Input.GetKeyDown(ArenaConfig.PanelKey.Value) && !Console.IsVisible() &&
                (Chat.instance == null || !Chat.instance.HasFocus()) && (TextInput.instance == null || !TextInput.IsVisible()))
                TogglePanel();

            if (_canvas == null) Build();
            if (_canvas == null) return;

            bool hud = !UiInputBlocker.IsOpen && (Menu.instance == null || !Menu.IsVisible());
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.25f;
                string text = Text();
                _line.text = text ?? "";
                hud &= !string.IsNullOrEmpty(text);
                if (_canvas.activeSelf != hud) _canvas.SetActive(hud);
            }
            else if (!hud && _canvas.activeSelf)
            {
                _canvas.SetActive(false);
            }
        }

        private static string Text()
        {
            var s = ArenaClient.Snapshot;
            var key = ArenaConfig.PanelKey?.Value.ToString() ?? "H";
            var match = s.Match;
            if (match != null)
            {
                int left = ArenaClient.SecondsLeft();
                string clock = $"{left / 60}:{left % 60:00}";
                string score = $"<color=#ffd140>{match.GoldName} {match.GoldAlive}</color>  ×  <color=#73db6b>{match.GreenAlive} {match.GreenName}</color>";
                switch (match.Phase)
                {
                    case "invited":
                        return $"<color=#ffe300>A arena está pronta!</color>  [{key}] Entrar  ·  {left}s";
                    case "prep":
                        return $"Preparação  ·  os portões abrem em {clock}";
                    case "live":
                        return match.Mine == "ko" ? $"{score}  ·  você foi derrotado" : $"{score}  ·  {clock}";
                    default:
                        string outcome = match.Winner == ArenaSide.None ? "Empate"
                            : match.Winner == match.Side ? "<color=#73db6b>Vitória!</color>" : "<color=#ee6b57>Derrota</color>";
                        return $"{outcome}  ·  placar em [{key}]  ·  volta em {clock}";
                }
            }

            var queue = s.Queue;
            if (queue != null)
            {
                if (queue.Status == "confirm") return "Arena: confirmando o grupo...";
                int waited = queue.Waited + Mathf.FloorToInt(Time.realtimeSinceStartup - ArenaClient.SnapshotAt);
                return $"Na fila: {ArenaRules.BracketName(queue.Size)} {(queue.Rated ? "Ranqueada" : "Escaramuça")}  ·  {waited / 60}:{waited % 60:00}";
            }

            if (s.SignRequests.Count > 0 || s.Invite != null)
                return $"Arena: pedido aguardando resposta  [{key}]";
            return null;
        }

        private void Build()
        {
            if (!ValheimUi.EnsureAssets()) return;
            _canvas = ValheimUi.CreateCanvas("NpcValheim_ArenaHud", 950);
            if (_canvas == null) return;

            var root = ValheimUi.CreateRect("Banner", _canvas.transform);
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = new Vector2(0f, -86f);
            root.sizeDelta = new Vector2(760f, 34f);

            var back = root.gameObject.AddComponent<Image>();
            back.color = new Color(0f, 0f, 0f, 0.45f);
            back.raycastTarget = false;

            _line = ValheimUi.CreateLabel(root, "", 19, ValheimUi.Beige, TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)_line.transform, 10f, 2f);
            _line.textWrappingMode = TextWrappingModes.NoWrap;
            _canvas.SetActive(false);
        }

        private void Teardown()
        {
            if (_canvas != null) Destroy(_canvas);
            _canvas = null;
            _line = null;
        }
    }
}
