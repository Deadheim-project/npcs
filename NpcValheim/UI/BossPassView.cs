using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The boss pass counter: one row per boss in [BossPass] Bosses, saying whether this
    /// character holds the pass and how it was won, with the two ways to pay for the ones it
    /// does not.
    ///
    /// Everything shown is the server's: the list and the prices arrive by ServerSync, the
    /// passes and the Deadcoins balance in the answer to a status request sent when the tab
    /// opens and every few seconds after, so a boss killed elsewhere or an admin's edit of
    /// bosspass.txt shows up while the panel is open. The buttons only ask; the server
    /// charges its own price (BossPass.ServePurchase).
    /// </summary>
    internal sealed class BossPassView : NpcViewBase
    {
        private const float PollSeconds = 5f;
        private const string Green = "#8fd18f";
        private const string Grey = "#9a9188";

        private BossPassNpc Counter => Npc as BossPassNpc;

        private TextMeshProUGUI _balance;
        private TextMeshProUGUI _intro;
        private RectTransform _list;
        private TextMeshProUGUI _empty;

        private string _signature;
        private int _seenMessage;
        private float _nextPoll;
        private readonly List<GameObject> _rows = new List<GameObject>();
        private static readonly Dictionary<string, string> Trophies = new Dictionary<string, string>();

        protected override void OnBuild()
        {
            _balance = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_balance.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            var pane = ValheimUi.CreateInlay(Root, "Passes");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            var header = ValheimUi.CreateLabel(pane, "Passes de boss", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            _intro = ValheimUi.CreateLabel(pane, "", 14, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_intro.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -80f), new Vector2(-16f, -38f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -84f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "Este servidor não tem passes de boss configurados.", 15,
                ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            // Messages from before this panel opened are not about anything on it.
            _seenMessage = BossPass.MessageRevision;
        }

        public override void Refresh()
        {
            var counter = Counter;
            if (counter == null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                counter.RequestStatus();
            }

            if (BossPass.MessageRevision != _seenMessage)
            {
                _seenMessage = BossPass.MessageRevision;
                Say(BossPass.LastMessage);
            }

            var entries = BossPassCatalog.Current();
            string deadcoins = BossPass.DeadcoinBalance >= 0 ? BossPass.DeadcoinBalance.ToString() : "?";
            string held = BossPass.Known ? entries.Count(e => BossPass.HasLocalPass(e.Boss)).ToString() : "?";
            _balance.text = $"<color={Grey}>Na sua bolsa:</color> {MarketplaceNpc.CoinsOf(Player)} moedas" +
                            $"   <color={Grey}>Deadcoins:</color> {deadcoins}" +
                            $"   <color={Grey}>Passes:</color> {held} de {entries.Count}";

            // The list and prices come by ServerSync, the passes in the status answer; either
            // changing redraws the rows. A balance change only touches the line above.
            float radius = Plugin.BossPassKillRadius?.Value ?? 60f;
            string signature = $"{Plugin.BossPassBosses?.Value}|{BossPass.LocalPassSignature}|{radius}";
            if (signature == _signature) return;
            _signature = signature;

            _intro.text = "Cada passe libera, para este personagem, a loja do bioma do boss. É de graça para " +
                          $"quem derrota o boss: vale acertar um golpe ou estar a até {radius:0} m quando ele morre.";
            Rebuild(counter, entries);
            _empty.gameObject.SetActive(entries.Count == 0);
        }

        private void Rebuild(BossPassNpc counter, List<BossPassEntry> entries)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            foreach (var entry in entries)
            {
                var row = Row(_list, 58f);
                _rows.Add(row.gameObject);

                ValheimUi.CreateItemIcon(row, TrophyOf(entry.Boss), 44f);
                var label = ValheimUi.CreateLabel(row,
                    $"<b>{entry.Name}</b>   <color={Grey}>{BossPassCatalog.ShopName(entry)}</color>\n" +
                    $"<size=13>{Describe(entry)}</size>",
                    16, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                if (!BossPass.Known || BossPass.HasLocalPass(entry.Boss)) continue;

                if (entry.Gold > 0)
                {
                    var gold = ValheimUi.CreateButton(row, $"{entry.Gold} moedas", 170f, 44f, 14);
                    ValheimUi.SetWidth(gold.gameObject, 170f);
                    Iconify(gold, MarketplaceNpc.CoinPrefabName, 26f);
                    gold.onClick.AddListener(() => Pay(counter, entry, BossPass.MethodGold));
                }
                if (entry.Deadcoins > 0)
                {
                    var deadcoins = ValheimUi.CreateButton(row, $"{entry.Deadcoins} Deadcoins", 170f, 44f, 14);
                    ValheimUi.SetWidth(deadcoins.gameObject, 170f);
                    deadcoins.onClick.AddListener(() => Pay(counter, entry, BossPass.MethodDeadcoins));
                }
            }
        }

        private static string Describe(BossPassEntry entry)
        {
            if (!BossPass.Known) return $"<color={Grey}>Consultando o seu passe...</color>";
            switch (BossPass.LocalPassKind(entry.Boss))
            {
                case BossPassLedger.KindKill:
                    return $"<color={Green}>Passe conquistado em combate</color>";
                case BossPassLedger.KindGold:
                    return $"<color={Green}>Passe comprado com moedas</color>";
                case BossPassLedger.KindDeadcoins:
                    return $"<color={Green}>Passe comprado com Deadcoins</color>";
                case null:
                    return entry.Gold > 0 || entry.Deadcoins > 0
                        ? $"Sem passe: derrote {entry.Name} ou pague uma vez"
                        : $"Sem passe: este só se ganha derrotando {entry.Name}";
                default:
                    return $"<color={Green}>Passe concedido por um admin</color>";
            }
        }

        private void Pay(BossPassNpc counter, BossPassEntry entry, string method)
        {
            string said = BossPass.BeginPurchase(entry, method,
                price => counter != null && counter.RequestPass(entry.Boss, method, price));
            if (said != null) Say(said);
        }

        /// <summary>The trophy the boss drops, read off its own prefab so a modded boss gets
        /// its own too. Null (no icon) when it drops none.</summary>
        private static string TrophyOf(string boss)
        {
            if (Trophies.TryGetValue(boss, out var cached)) return cached;
            string trophy = null;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(boss) : null;
            var drops = prefab != null ? prefab.GetComponent<CharacterDrop>() : null;
            if (drops?.m_drops != null)
                trophy = drops.m_drops
                    .Select(d => d?.m_prefab != null ? d.m_prefab.name : null)
                    .FirstOrDefault(n => n != null && n.StartsWith("Trophy", System.StringComparison.Ordinal));
            if (ZNetScene.instance != null) Trophies[boss] = trophy;
            return trophy;
        }
    }
}
