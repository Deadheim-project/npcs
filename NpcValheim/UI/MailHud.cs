using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Valheim Post stamp in the top-right. It only shows +N for new letters —
    /// click or the configured key tells you to walk to a Caixa Postal to read them.
    /// </summary>
    internal sealed class MailHud : MonoBehaviour
    {
        private const string RpcRequest = "NpcValheim_MailHudRequest";
        private const string RpcData = "NpcValheim_MailHudData";
        private const float IconSize = 78f;
        private const float PollSeconds = 2f;

        private static MailHud _instance;
        private static bool _registered;
        private static bool _listening;

        private GameObject _hud;
        private RectTransform _iconRoot;
        private TextMeshProUGUI _badge;
        private GameObject _badgeBack;
        private Image _iconImage;
        private TextMeshProUGUI _hint;
        private KeyCode _shownHintKey;

        private GameObject _notice;
        private TextMeshProUGUI _noticeText;

        private List<MailEntry> _mail = new List<MailEntry>();
        private int _shownCount = -1;
        private float _nextPoll;
        private float _pulse;
        private bool _noticeOpen;
        private Vector2 _buffHome;
        private bool _haveBuffHome;
        private RectTransform _buffRoot;
        private MailboxNpc _box;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_MailHud");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MailHud>();
            if (!_listening)
            {
                _listening = true;
                MailboxNpc.MailSynced += ApplyMailboxMail;
            }
        }

        /// <summary>Ask again on the next frame — used after a send so the writer sees
        /// their own copy if they mailed themselves or a house they belong to.</summary>
        internal static void RefreshSoon()
        {
            if (_instance != null) _instance._nextPoll = 0f;
        }

        /// <summary>The Caixa Postal already fetched this list. The stamp shows it
        /// without sending another RPC that could wipe the box.</summary>
        internal static void MirrorMailbox(MailboxNpc mailbox)
        {
            if (mailbox != null) ApplyMailboxMail(mailbox.CachedMail);
        }

        private static void ApplyMailboxMail(List<MailEntry> mail)
        {
            if (_instance == null || mail == null) return;
            var alreadyRead = new HashSet<string>();
            foreach (var entry in _instance._mail)
            {
                if (entry != null && entry.Read && !string.IsNullOrEmpty(entry.Id))
                    alreadyRead.Add(entry.Id);
            }
            foreach (var entry in mail)
            {
                if (entry != null && alreadyRead.Contains(entry.Id))
                    entry.Read = true;
            }
            _instance._mail = mail;
        }

        private void PollServer()
        {
            _box = NearestMailbox();
            if (_box != null)
            {
                _box.RequestMail();
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null) return;
            SendToServer(RpcRequest, player.GetPlayerID(), player.GetPlayerName() ?? "");
        }

        private static MailboxNpc NearestMailbox()
        {
            var boxes = UnityEngine.Object.FindObjectsOfType<MailboxNpc>();
            if (boxes == null || boxes.Length == 0) return null;
            var player = Player.m_localPlayer;
            if (player == null) return boxes[0];
            MailboxNpc best = null;
            float bestDist = float.MaxValue;
            var pos = player.transform.position;
            foreach (var box in boxes)
            {
                if (box == null) continue;
                float d = (box.transform.position - pos).sqrMagnitude;
                if (d >= bestDist) continue;
                bestDist = d;
                best = box;
            }
            return best;
        }

        internal static void BindRpcs() => TryRegister();

        private static void TryRegister()
        {
            if (_registered || ZRoutedRpc.instance == null) return;
            _registered = true;
            ZRoutedRpc.instance.Register(RpcRequest, (Action<long, long, string>)OnRequest);
            ZRoutedRpc.instance.Register(RpcData, (Action<long, string>)OnData);
            Plugin.Log.LogInfo("NpcValheim: mail HUD RPCs registered");
        }

        /// <summary>
        /// From a client, Valheim addresses the dedicated/host with 0 — not GetServerPeerID(),
        /// which is a uid the dedicated process is not listening on as a peer.
        /// </summary>
        private static long ServerRpcTarget()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
                return ZRoutedRpc.instance != null ? ZRoutedRpc.instance.GetServerPeerID() : 0L;
            return 0L;
        }

        private static void SendToServer(string rpc, params object[] args)
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerRpcTarget(), rpc, args ?? new object[0]);
        }

        private static bool TryRecipient(long sender, long claimedId, out long playerId, out string name,
            string clientName = null)
        {
            playerId = 0L;
            GameApi.RememberIdentity(sender);
            name = GameApi.GetPlayerName(sender);
            if ((string.IsNullOrWhiteSpace(name) || name == "???") && !string.IsNullOrWhiteSpace(clientName))
                name = clientName.Trim();
            if (claimedId != 0L && !string.IsNullOrWhiteSpace(name) && name != "???")
                PlayerDirectory.Remember(claimedId, name, GameApi.CollectIds(sender));
            playerId = claimedId != 0L ? claimedId : GameApi.GetPlayerId(sender);
            return playerId != 0L;
        }

        private static void OnRequest(long sender, long claimedId, string clientName)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (!TryRecipient(sender, claimedId, out long playerId, out string name, clientName))
            {
                Plugin.Log.LogWarning($"NpcValheim: mail HUD request from peer {sender} had no character id");
                return;
            }
            var mail = MailDatabase.GetMail(playerId, name);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcData, new object[] { MailWire.Pack(mail) });
        }

        private static void OnData(long sender, string packed)
        {
            if (_instance == null) return;
            var mail = MailWire.Unpack(packed);
            if (mail.Count == 0 && _instance._mail.Count > 0) return;
            _instance._mail = mail;
        }

        private void Update()
        {
            TryRegister();

            if (Player.m_localPlayer == null || Hud.instance == null)
            {
                RestoreBuffSlot();
                if (_hud != null) _hud.SetActive(false);
                if (_iconRoot != null) _iconRoot.gameObject.SetActive(false);
                if (_noticeOpen) CloseNotice();
                return;
            }

            if (_iconRoot == null && !TryBuildHud()) return;

            if (!UiInputBlocker.IsOpen && Input.GetKeyDown(Plugin.MailHudKey.Value))
            {
                if (_noticeOpen) CloseNotice();
                else OpenNotice();
            }
            else if (_noticeOpen && Input.GetKeyDown(KeyCode.Escape))
            {
                CloseNotice();
            }

            if (Time.time >= _nextPoll)
            {
                _nextPoll = Time.time + PollSeconds;
                PollServer();
            }

            RefreshBadge();
            RefreshHint();
            if (_noticeOpen) RefreshNotice();
            TickPulse();
        }

        private void LateUpdate()
        {
            if (_iconRoot != null && Player.m_localPlayer != null)
                SnapToMinimap();
        }

        /// <summary>
        /// Occupy the first status-effect slot (closest to the minimap). Vanilla lays
        /// effects left from that slot; we shift the whole row one icon further left.
        /// </summary>
        private void SnapToMinimap()
        {
            var map = Minimap.instance;
            var small = map != null ? map.m_smallRoot : null;
            var hud = Hud.instance;
            var root = hud != null ? hud.m_statusEffectListRoot : null;
            if (_iconRoot == null || small == null || !small.activeInHierarchy || root == null)
            {
                RestoreBuffSlot();
                if (_iconRoot != null) _iconRoot.gameObject.SetActive(false);
                return;
            }

            if (!_haveBuffHome || _buffRoot != root)
            {
                _buffRoot = root;
                _buffHome = root.anchoredPosition;
                _haveBuffHome = true;
            }

            const float sealNudge = 18f;
            const float potionNudge = 6f;
            const float gap = 18f;
            float push = IconSize + gap;
            root.anchoredPosition = new Vector2(_buffHome.x - push + potionNudge, _buffHome.y);

            var parent = root.parent;
            if (parent != null && _iconRoot.parent != parent)
                _iconRoot.SetParent(parent, false);

            _iconRoot.anchorMin = root.anchorMin;
            _iconRoot.anchorMax = root.anchorMax;
            _iconRoot.pivot = root.pivot;
            _iconRoot.sizeDelta = new Vector2(IconSize, IconSize);
            _iconRoot.anchoredPosition = _buffHome + new Vector2(sealNudge, 0f);
            _iconRoot.localRotation = Quaternion.identity;
            _iconRoot.gameObject.SetActive(true);
            if (_hud != null) _hud.SetActive(false);
        }

        private void RestoreBuffSlot()
        {
            if (!_haveBuffHome || _buffRoot == null) return;
            _buffRoot.anchoredPosition = _buffHome;
        }

        private void OnDestroy()
        {
            RestoreBuffSlot();
            if (_instance == this) _instance = null;
        }

        private bool TryBuildHud()
        {
            if (!ValheimUi.EnsureAssets()) return false;

            _hud = ValheimUi.CreateCanvas("NpcValheim_MailHud", 4500);
            if (_hud == null) return false;

            _iconRoot = ValheimUi.CreateRect("Icon", _hud.transform);
            _iconRoot.sizeDelta = new Vector2(IconSize, IconSize);
            _hud.SetActive(false);

            _iconImage = _iconRoot.gameObject.AddComponent<Image>();
            _iconImage.sprite = LoadIcon();
            _iconImage.preserveAspect = true;
            _iconImage.color = Color.white;
            _iconImage.raycastTarget = true;

            var button = _iconRoot.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.ColorTint;
            button.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(1.15f, 1.15f, 1.15f, 1f),
                pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f),
                selectedColor = Color.white,
                disabledColor = Color.white,
                colorMultiplier = 1f,
                fadeDuration = 0.08f,
            };
            button.targetGraphic = _iconImage;
            button.onClick.AddListener(() =>
            {
                if (_noticeOpen) CloseNotice();
                else OpenNotice();
            });
            ValheimUi.AttachSfx(_iconRoot.gameObject);

            _hint = ValheimUi.CreateLabel(_iconRoot, HintText(), 13, ValheimUi.Beige,
                TextAlignmentOptions.Center);
            var hintRect = (RectTransform)_hint.transform;
            hintRect.anchorMin = hintRect.anchorMax = new Vector2(0.5f, 0f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.sizeDelta = new Vector2(160f, 22f);
            hintRect.anchoredPosition = new Vector2(0f, -2f);
            _shownHintKey = Plugin.MailHudKey.Value;

            var badge = ValheimUi.CreateRect("Badge", _iconRoot);
            badge.anchorMin = badge.anchorMax = new Vector2(1f, 1f);
            badge.pivot = new Vector2(0.5f, 0.5f);
            badge.sizeDelta = new Vector2(34f, 34f);
            badge.anchoredPosition = new Vector2(-4f, -4f);
            _badgeBack = badge.gameObject;

            var disc = badge.gameObject.AddComponent<Image>();
            disc.sprite = ValheimUi.ButtonSprite;
            disc.type = Image.Type.Sliced;
            disc.color = new Color(0.72f, 0.16f, 0.14f, 0.96f);

            _badge = ValheimUi.CreateLabel(badge, "+1", 16, Color.white, TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)_badge.transform, 1f, 1f);
            _badge.raycastTarget = false;
            _badgeBack.SetActive(false);
            return true;
        }

        private void RefreshHint()
        {
            if (_hint == null || Plugin.MailHudKey == null) return;
            var key = Plugin.MailHudKey.Value;
            if (key == _shownHintKey && !string.IsNullOrEmpty(_hint.text)) return;
            _shownHintKey = key;
            _hint.text = HintText();
        }

        private static string HintText()
        {
            var key = Plugin.MailHudKey != null ? Plugin.MailHudKey.Value : KeyCode.P;
            return "press \"" + FormatKey(key) + "\"";
        }

        private static string FormatKey(KeyCode key)
        {
            var name = key.ToString();
            if (name.Length == 1) return name.ToLowerInvariant();
            if (name.StartsWith("Alpha") && name.Length == 6) return name.Substring(5);
            return name;
        }

        private void RefreshBadge()
        {
            int count = _mail.Count(IsUnreadHudMail);
            if (count != _shownCount)
            {
                if (count > _shownCount && _shownCount >= 0)
                    _pulse = 0.45f;
                _shownCount = count;
            }

            bool any = count > 0;
            if (_badgeBack != null) _badgeBack.SetActive(any);
            if (_badge != null && any)
                _badge.text = count > 99 ? "+99" : "+" + count;
        }

        /// <summary>Letters from a player or a house — that is what the stamp is for.
        /// Auction parcels still appear in the mailbox, but they do not drive the +N badge.</summary>
        private static bool IsHudMail(MailEntry entry) =>
            entry != null && (entry.IsMessage || !string.IsNullOrEmpty(entry.HouseName));

        private static bool IsUnreadHudMail(MailEntry entry) =>
            IsHudMail(entry) && !entry.Read;

        private void TickPulse()
        {
            if (_iconRoot == null) return;
            if (_pulse > 0f)
            {
                _pulse -= Time.deltaTime;
                float t = Mathf.Clamp01(_pulse / 0.45f);
                float scale = 1f + 0.12f * Mathf.Sin((1f - t) * Mathf.PI);
                _iconRoot.localScale = Vector3.one * scale;
            }
            else
            {
                _iconRoot.localScale = Vector3.one;
            }
        }

        private void OpenNotice()
        {
            if (!ValheimUi.EnsureAssets()) return;
            CloseNotice();

            _notice = ValheimUi.CreateCanvas("NpcValheim_MailNotice", 4950);
            if (_notice == null) return;

            var panel = ValheimUi.CreatePanel(_notice.transform, 460f, 210f);
            panel.anchorMin = panel.anchorMax = new Vector2(1f, 1f);
            panel.pivot = new Vector2(1f, 1f);
            panel.anchoredPosition = new Vector2(-18f, -(IconSize + 28f));

            var title = ValheimUi.CreateLabel(panel, "Correio", 24, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)title.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -48f), new Vector2(-48f, -10f));

            var close = ValheimUi.CreateButton(panel, "X", 32f, 32f, 16);
            ValheimUi.Anchor((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-42f, -42f), new Vector2(-10f, -10f));
            close.onClick.AddListener(CloseNotice);

            var frame = ValheimUi.CreateInlay(panel, "Body");
            ValheimUi.Anchor(frame, Vector2.zero, Vector2.one, new Vector2(16f, 56f), new Vector2(-16f, -52f));
            _noticeText = ValheimUi.CreateLabel(frame, NoticeText(), 16, ValheimUi.Beige,
                TextAlignmentOptions.Center);
            ValheimUi.Stretch((RectTransform)_noticeText.transform, 12f, 12f);

            var ok = ValheimUi.CreateButton(panel, "Ok", 140f, 36f, 15);
            ValheimUi.Anchor((RectTransform)ok.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-70f, 12f), new Vector2(70f, 48f));
            ok.onClick.AddListener(CloseNotice);

            _noticeOpen = true;
        }

        private void CloseNotice()
        {
            _noticeOpen = false;
            _noticeText = null;
            if (_notice != null) Destroy(_notice);
            _notice = null;
        }

        private void RefreshNotice()
        {
            if (_noticeText != null) _noticeText.text = NoticeText();
        }

        private string NoticeText()
        {
            int n = _mail.Count(IsUnreadHudMail);
            if (n <= 0) return "Nenhuma mensagem nova.\nDirija-se aos correios para conferir sua caixa.";
            if (n == 1) return "Você tem 1 mensagem nova.\nDirija-se aos correios para recebê-la.";
            return "Você tem " + n + " mensagens novas.\nDirija-se aos correios para recebê-las.";
        }

        private static Sprite LoadIcon()
        {
            var path = IconPath();
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"NpcValheim: mail HUD icon missing at '{path}'");
                return ValheimUi.ButtonSprite;
            }

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!GameApi.TryLoadImage(tex, File.ReadAllBytes(path)))
            {
                Plugin.Log.LogWarning("NpcValheim: could not decode the mail HUD icon");
                return ValheimUi.ButtonSprite;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.name = "MailHudIcon";
            Prefabs.PecaMesh.PunchPureWhiteBackground(tex);
            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private static string IconPath()
        {
            var fromPlugins = Path.Combine(Paths.PluginPath, "NpcValheim", "Assets", "Mailbox", "Selo_Png.png");
            if (File.Exists(fromPlugins)) return fromPlugins;
            fromPlugins = Path.Combine(Paths.PluginPath, "NpcValheim", "Assets", "Mailbox", "hud-icon.png");
            if (File.Exists(fromPlugins)) return fromPlugins;
            var assemblyDir = Path.GetDirectoryName(typeof(MailHud).Assembly.Location);
            if (string.IsNullOrEmpty(assemblyDir)) return fromPlugins;
            var fromAssembly = Path.Combine(assemblyDir, "Assets", "Mailbox", "Selo_Png.png");
            return File.Exists(fromAssembly)
                ? fromAssembly
                : Path.Combine(assemblyDir, "Assets", "Mailbox", "hud-icon.png");
        }
    }
}

