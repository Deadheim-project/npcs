using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>VIP-only, server-wide NPC directory opened with F7.</summary>
    internal sealed class VipNpcDirectory : MonoBehaviour
    {
        private const string RpcCatalogRequest = "NpcValheim_VipCatalogRequest";
        private const string RpcCatalogData = "NpcValheim_VipCatalogData";
        private const string RpcOpenRequest = "NpcValheim_VipOpenRequest";
        private const string RpcOpenReady = "NpcValheim_VipOpenReady";
        private const string RpcRelease = "NpcValheim_VipRelease";

        private static readonly Dictionary<long, NpcBase> ServerProxies = new Dictionary<long, NpcBase>();
        private static VipNpcDirectory _instance;
        private static ZRoutedRpc _registeredOn;

        private readonly List<CatalogEntry> _entries = new List<CatalogEntry>();
        private readonly List<GameObject> _rows = new List<GameObject>();
        private GameObject _canvas;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private string _pendingProfileId;
        private float _pendingUntil;

        internal static void EnsureCreated()
        {
            if (_instance != null) return;
            var go = new GameObject("NpcValheim_VipNpcDirectory");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<VipNpcDirectory>();
        }

        internal static void ReleaseRemoteProxy()
        {
            if (ZRoutedRpc.instance == null) return;
            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcRelease, new object[0]);
        }

        private static void TryRegister()
        {
            if (ZRoutedRpc.instance == null || _registeredOn == ZRoutedRpc.instance) return;
            _registeredOn = ZRoutedRpc.instance;
            ZRoutedRpc.instance.Register(RpcCatalogRequest, (Action<long>)OnCatalogRequest);
            ZRoutedRpc.instance.Register(RpcCatalogData, (Action<long, string>)OnCatalogData);
            ZRoutedRpc.instance.Register(RpcOpenRequest, (Action<long, string>)OnOpenRequest);
            ZRoutedRpc.instance.Register(RpcOpenReady, (Action<long, string>)OnOpenReady);
            ZRoutedRpc.instance.Register(RpcRelease, (Action<long>)OnRelease);
            Plugin.Log.LogInfo("NpcValheim: VIP directory RPCs registered");
        }

        private void Update()
        {
            TryRegister();

            if (Player.m_localPlayer == null)
            {
                Close();
                _pendingProfileId = null;
                return;
            }

            if (!UiInputBlocker.IsOpen && Input.GetKeyDown(Plugin.VipNpcMenuKey.Value) &&
                VipList.VipListApi.IsLocalPlayerVip())
            {
                Open();
            }
            else if (_canvas != null &&
                     (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(Plugin.VipNpcMenuKey.Value)))
            {
                Close();
            }

            if (!string.IsNullOrEmpty(_pendingProfileId))
            {
                foreach (var npc in FindObjectsByType<NpcBase>(FindObjectsSortMode.None))
                {
                    if (!npc.IsVipProxy || npc.ProfileId != _pendingProfileId) continue;
                    string opened = _pendingProfileId;
                    _pendingProfileId = null;
                    UiRoot.OpenVipRemote(npc, Player.m_localPlayer);
                    Plugin.Log.LogInfo($"NpcValheim: opened remote VIP NPC {opened}");
                    break;
                }

                if (!string.IsNullOrEmpty(_pendingProfileId) && Time.time > _pendingUntil)
                {
                    Player.m_localPlayer.Message(MessageHud.MessageType.Center,
                        "O NPC remoto não pôde ser carregado.", 0, null);
                    _pendingProfileId = null;
                    ReleaseRemoteProxy();
                }
            }
        }

        private void LateUpdate()
        {
            if (_canvas == null) return;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Open()
        {
            if (_canvas != null || !ValheimUi.EnsureAssets()) return;

            _entries.Clear();
            _canvas = ValheimUi.CreateCanvas("NpcValheim_VipDirectory", 5001);
            if (_canvas == null) return;

            var panel = ValheimUi.CreatePanel(_canvas.transform, 720f, 590f);
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;

            var titleBar = ValheimUi.CreateRect("TitleBar", panel);
            ValheimUi.Anchor(titleBar, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -58f), Vector2.zero);
            titleBar.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0f);
            titleBar.gameObject.AddComponent<DragWindow>().Target = panel;
            var title = ValheimUi.CreateLabel(titleBar, "Acesso VIP — NPCs", 28, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Stretch((RectTransform)title.transform, 56f, 8f);

            var close = ValheimUi.CreateButton(panel, "X", 36f, 36f, 18);
            ValheimUi.Anchor((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-52f, -52f), new Vector2(-16f, -16f));
            close.onClick.AddListener(Close);

            var hint = ValheimUi.CreateLabel(panel,
                "Abra os serviços de qualquer NPC do servidor. Admin e Aparência permanecem bloqueados.",
                15, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)hint.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(24f, -92f), new Vector2(-24f, -62f));

            var frame = ValheimUi.CreateInlay(panel, "NPCs");
            ValheimUi.Anchor(frame, Vector2.zero, Vector2.one, new Vector2(24f, 24f), new Vector2(-24f, -104f));
            _list = ValheimUi.CreateScrollList(frame, spacing: 5f);
            _empty = ValheimUi.CreateLabel(frame, "Carregando NPCs do servidor...", 17,
                ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Stretch((RectTransform)_empty.transform, 24f, 70f);

            UiInputBlocker.IsOpen = true;
            RequestCatalog();
        }

        private void Close()
        {
            foreach (var row in _rows) if (row != null) Destroy(row);
            _rows.Clear();
            if (_canvas != null) Destroy(_canvas);
            _canvas = null;
            _list = null;
            _empty = null;
            UiInputBlocker.IsOpen = UiRoot.IsOpen;
            if (!UiRoot.IsOpen && !Menu.IsVisible())
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private static bool SenderIsVip(long sender)
        {
            string platformId = GameApi.GetPlatformUserId(sender);
            if (VipList.VipListApi.IsVip(platformId)) return true;

            return ZNet.instance != null && ZNet.instance.IsServer() &&
                   sender == GameApi.LocalRpcSenderId() && VipList.VipListApi.IsLocalPlayerVip();
        }

        private void RequestCatalog()
        {
            if (ZRoutedRpc.instance != null)
                ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcCatalogRequest, new object[0]);
        }

        private static void OnCatalogRequest(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !SenderIsVip(sender)) return;
            string packed = PackCatalog(NpcConfigStore.ListInstances());
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcCatalogData, new object[] { packed });
        }

        private static void OnCatalogData(long sender, string packed)
        {
            if (_instance == null || !VipList.VipListApi.IsLocalPlayerVip()) return;
            _instance._entries.Clear();
            _instance._entries.AddRange(UnpackCatalog(packed));
            _instance.RebuildRows();
        }

        private void RebuildRows()
        {
            if (_list == null) return;
            foreach (var row in _rows) if (row != null) Destroy(row);
            _rows.Clear();

            foreach (var entry in _entries)
            {
                var row = ValheimUi.CreateRect("Npc", _list);
                var layout = row.gameObject.AddComponent<HorizontalLayoutGroup>();
                layout.spacing = 10f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;
                layout.childAlignment = TextAnchor.MiddleLeft;
                ValheimUi.SetHeight(row.gameObject, 54f);
                _rows.Add(row.gameObject);

                var label = ValheimUi.CreateLabel(row,
                    $"{entry.Name}\n<size=12><color=#9a9188>{TypeLabel(entry.Type)}</color></size>",
                    17, ValheimUi.Beige, TextAlignmentOptions.Left);
                var flexible = label.gameObject.AddComponent<LayoutElement>();
                flexible.flexibleWidth = 1f;

                var open = ValheimUi.CreateButton(row, "Abrir", 130f, 42f, 16);
                string profileId = entry.ProfileId;
                open.onClick.AddListener(() => RequestOpen(profileId));
            }

            if (_empty != null)
            {
                _empty.gameObject.SetActive(_entries.Count == 0);
                _empty.text = "Nenhum NPC persistido foi encontrado no servidor.";
            }
        }

        private void RequestOpen(string profileId)
        {
            if (string.IsNullOrEmpty(profileId) || ZRoutedRpc.instance == null) return;
            Close();
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "Carregando NPC remoto...", 0, null);
            ZRoutedRpc.instance.InvokeRoutedRPC(GameApi.GetServerPeerId(), RpcOpenRequest,
                new object[] { profileId });
        }

        private static void OnOpenRequest(long sender, string profileId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !SenderIsVip(sender)) return;

            var record = NpcConfigStore.ListInstances().FirstOrDefault(entry => entry.ProfileId == profileId);
            if (record?.Profile == null) return;

            ReleaseServerProxy(sender);
            string prefabName = PrefabFor(record.Profile.ForType);
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab == null)
            {
                Plugin.Log.LogWarning($"NpcValheim: no VIP proxy prefab for type '{record.Profile.ForType}'");
                return;
            }

            Vector3 at = GameApi.GetPlayerPosition(sender);
            at.y -= 200f;
            var go = Instantiate(prefab, at, Quaternion.identity);
            var npc = go.GetComponent<NpcBase>();
            if (npc == null)
            {
                Destroy(go);
                return;
            }

            npc.InitializeVipProxy(record.ProfileId, record.Profile);
            ServerProxies[sender] = npc;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RpcOpenReady, new object[] { record.ProfileId });
        }

        private static void OnOpenReady(long sender, string profileId)
        {
            if (_instance == null || !VipList.VipListApi.IsLocalPlayerVip()) return;
            _instance._pendingProfileId = profileId;
            _instance._pendingUntil = Time.time + 12f;
        }

        private static void OnRelease(long sender)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            ReleaseServerProxy(sender);
        }

        private static void ReleaseServerProxy(long sender)
        {
            if (!ServerProxies.TryGetValue(sender, out var npc)) return;
            ServerProxies.Remove(sender);
            if (npc != null) npc.DestroyVipProxy();
        }

        private static string PackCatalog(IEnumerable<NpcInstanceRecord> records)
        {
            return string.Join("\n", records.Select(record =>
                Encode(record.ProfileId) + ";" + Encode(record.Profile.ForType) + ";" +
                Encode(string.IsNullOrWhiteSpace(record.Profile.Name) ? "NPC" : record.Profile.Name)));
        }

        private static List<CatalogEntry> UnpackCatalog(string packed)
        {
            var result = new List<CatalogEntry>();
            if (string.IsNullOrEmpty(packed)) return result;
            foreach (string line in packed.Split('\n'))
            {
                string[] parts = line.Split(';');
                if (parts.Length != 3) continue;
                try
                {
                    result.Add(new CatalogEntry
                    {
                        ProfileId = Decode(parts[0]), Type = Decode(parts[1]), Name = Decode(parts[2])
                    });
                }
                catch (FormatException) { }
            }
            return result;
        }

        private static string Encode(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

        private static string Decode(string value) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));

        private static string PrefabFor(string type) => type switch
        {
            "Teleporter" => "NpcValheim_Teleporter",
            "Marketplace" => "NpcValheim_Marketplace",
            "Auction" => "NpcValheim_Auction",
            "Mailbox" => "NpcValheim_Mailbox",
            "QuestGiver" => "NpcValheim_QuestGiver",
            _ => string.Empty,
        };

        private static string TypeLabel(string type) => type switch
        {
            "Teleporter" => "Teleportador",
            "Marketplace" => "Mercador",
            "Auction" => "Leilão",
            "Mailbox" => "Correio",
            "QuestGiver" => "Missões",
            _ => type,
        };

        private sealed class CatalogEntry
        {
            public string ProfileId;
            public string Type;
            public string Name;
        }
    }
}

