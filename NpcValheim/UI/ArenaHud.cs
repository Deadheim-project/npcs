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
        private GameObject _balancedPanel;
        private TextMeshProUGUI _balanced;
        private GameObject _spectatePanel;
        private TextMeshProUGUI _spectate;
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
            if (UiInputBlocker.IsOpen || UiInputBlocker.IsTyping || Player.m_localPlayer == null) return;
            ArenaClient.RequestData();
            var pages = new List<(string, NpcViewBase)>
            {
                ("Arena", new ArenaStatusView()),
                ("Times", new ArenaTeamsView()),
                ("Ranking", new ArenaLadderView()),
            };
            // The Aesir (VIP) queues from here, without the Mestre da Arena; the server checks the VIP again.
            if (VipList.VipListApi.IsLocalPlayerVip()) pages.Insert(1, ("Fila", new ArenaQueueView()));
            UiRoot.OpenStandalone(PanelTitle, pages, Player.m_localPlayer);
        }

        private void Update()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Teardown();
                return;
            }

            if (ArenaConfig.PanelKey != null && ArenaConfig.PanelKey.Value != KeyCode.None &&
                Input.GetKeyDown(ArenaConfig.PanelKey.Value) && !UiInputBlocker.IsTyping)
                TogglePanel();

            ArenaSpectate.Update();

            if (_canvas == null) Build();
            if (_canvas == null) return;

            bool hud = !UiInputBlocker.IsOpen && (Menu.instance == null || !Menu.IsVisible());
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + 0.25f;
                string text = Text();
                _line.text = text ?? "";
                string watching = SpectateText();
                _spectate.text = watching ?? "";
                if (_spectatePanel.activeSelf != (watching != null)) _spectatePanel.SetActive(watching != null);
                string balanced = BalancedText();
                _balanced.text = balanced ?? "";
                if (_balancedPanel.activeSelf != (balanced != null)) _balancedPanel.SetActive(balanced != null);
                hud &= !string.IsNullOrEmpty(text) || watching != null || balanced != null;
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
            var key = ArenaConfig.PanelHint;
            var match = s.Match;
            if (match != null)
            {
                int left = ArenaClient.SecondsLeft();
                string clock = left < 0 ? "sem limite de tempo" : $"{left / 60}:{left % 60:00}";
                string score = match.Size == ArenaRules.FfaSize && !match.IsChallenge
                    ? $"FFA  {match.GoldAlive}/{match.GoldTotal} de pé"
                    : $"<color=#ffd140>{match.GoldName} {match.GoldAlive}</color>  ×  <color=#73db6b>{match.GreenAlive} {match.GreenName}</color>";
                switch (match.Phase)
                {
                    case "invited":
                        return $"<color=#ffe300>{(match.IsChallenge ? "O desafio está pronto!" : "A arena está pronta!")}</color>  {key} Entrar  ·  {left}s";
                    case "prep":
                        return $"Preparação  ·  os portões abrem em {clock}";
                    case "live":
                        return match.Mine == "ko" ? $"{score}  ·  você foi derrotado" : $"{score}  ·  {clock}";
                    default:
                        string outcome = match.Winner == ArenaSide.None ? match.IsChallenge ? "Desafio falhou" : "Empate"
                            : match.Winner == match.Side ? "<color=#73db6b>Vitória!</color>" : "<color=#ee6b57>Derrota</color>";
                        return $"{outcome}  ·  placar em {key}  ·  volta em {clock}";
                }
            }

            var queue = s.Queue;
            if (queue != null)
            {
                if (queue.Status == "confirm") return "Arena: confirmando o grupo...";
                int waited = queue.Waited + Mathf.FloorToInt(Time.realtimeSinceStartup - ArenaClient.SnapshotAt);
                string kind = (queue.Size == ArenaRules.FfaSize ? "" : queue.Rated ? " Ranqueada" : " Escaramuça") +
                              (queue.Balanced ? queue.Size == ArenaRules.FfaSize ? " balanceado" : " balanceada" : "");
                return $"Na fila: {ArenaRules.BracketName(queue.Size)}{kind}  ·  {waited / 60}:{waited % 60:00}";
            }

            if (s.SignRequests.Count > 0 || s.Invite != null)
                return $"Arena: pedido aguardando resposta  {key}";
            return null;
        }

        /// <summary>The band under the line in a balanced match: the class, and that the player's
        /// own gear comes back at the end. Null outside one.</summary>
        private static string BalancedText()
        {
            var match = ArenaClient.Snapshot.Match;
            if (ArenaKit.Active && ArenaKit.Class != null)
                return $"Partida balanceada: <color=#ffe300>{ArenaKit.Class.Name}</color>  ·  teclas 1 a {ArenaKit.Belt.Count} trocam a arma  ·  seu equipamento volta no fim";
            if (match != null && match.Balanced && match.Phase != "invited" && match.Phase != "ended")
                return $"Partida balanceada: <color=#ffe300>{ArenaViewBase.ClassName(match.ClassId)}</color>  ·  vestindo o kit da classe...";
            return null;
        }

        /// <summary>The panel of someone knocked out and watching (ArenaSpectate), or null.</summary>
        private static string SpectateText()
        {
            if (!ArenaSpectate.Active) return null;
            var match = ArenaClient.Snapshot.Match;
            string by = ArenaSpectate.By.Length > 0 ? $" por {ArenaSpectate.By}" : "";
            string standing = match == null ? ""
                : match.IsChallenge ? $"\nDe pé: {match.GoldAlive} de {match.GoldTotal} do seu grupo contra {match.GreenName}"
                : match.Size == ArenaRules.FfaSize ? $"\nDe pé: {match.GoldAlive} de {match.GoldTotal}"
                : $"\nDe pé: <color=#ffd140>{match.GoldName} {match.GoldAlive}</color>  ×  <color=#73db6b>{match.GreenAlive} {match.GreenName}</color>";
            string leave = ArenaSpectate.LeaveAsked ? "Voltando..." : $"[{ArenaSpectate.LeaveKey}] Sair agora";
            return $"<size=26><color=#ee6b57>VOCÊ FOI DERROTADO</color></size>{by}\n" +
                   "Assistindo à partida com a câmera livre: WASD e mouse, Espaço sobe, Ctrl desce\n" +
                   $"Quando a partida acabar, você volta para onde estava.{standing}\n{leave}";
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

            var band = ValheimUi.CreateRect("Balanced", _canvas.transform);
            band.anchorMin = band.anchorMax = new Vector2(0.5f, 1f);
            band.pivot = new Vector2(0.5f, 1f);
            band.anchoredPosition = new Vector2(0f, -122f);
            band.sizeDelta = new Vector2(760f, 28f);
            var bandBack = band.gameObject.AddComponent<Image>();
            bandBack.color = new Color(0.08f, 0.05f, 0.01f, 0.78f);
            bandBack.raycastTarget = false;
            // The plain font: the display one does not read against a bright sky (found in the game, 03/11).
            _balanced = ValheimUi.CreateLabel(band, "", 16, ValheimUi.Beige, TextAlignmentOptions.Center);
            ValheimUi.Stretch((RectTransform)_balanced.transform, 10f, 2f);
            _balanced.textWrappingMode = TextWrappingModes.NoWrap;
            _balancedPanel = band.gameObject;
            _balancedPanel.SetActive(false);

            var panel = ValheimUi.CreateRect("Spectate", _canvas.transform);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = new Vector2(0f, 220f);
            panel.sizeDelta = new Vector2(760f, 150f);
            var panelBack = panel.gameObject.AddComponent<Image>();
            panelBack.color = new Color(0f, 0f, 0f, 0.55f);
            panelBack.raycastTarget = false;
            _spectate = ValheimUi.CreateLabel(panel, "", 18, ValheimUi.Beige, TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)_spectate.transform, 12f, 8f);
            _spectatePanel = panel.gameObject;
            _spectatePanel.SetActive(false);
            _canvas.SetActive(false);
        }

        private void Teardown()
        {
            if (_canvas != null) Destroy(_canvas);
            _canvas = null;
            _line = null;
            _balancedPanel = null;
            _balanced = null;
            _spectatePanel = null;
            _spectate = null;
        }
    }
}
