using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// The destination list. A teleporter is a travel hub, not a single doorway, so this is a
    /// list of named places with a Viajar button each -- the same list/detail idiom the rest
    /// of the window uses.
    /// </summary>
    internal sealed class TeleportView : NpcViewBase
    {
        private TeleporterNpc Teleporter => Npc as TeleporterNpc;

        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private TextMeshProUGUI _header;
        private string _signature;
        private readonly List<GameObject> _rows = new List<GameObject>();

        protected override void OnBuild()
        {
            var frame = ValheimUi.CreateInlay(Root, "Destinations");
            ValheimUi.Stretch(frame, 0f, 0f);

            _header = ValheimUi.CreateLabel(frame, "Destinos", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)_header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -38f), new Vector2(-10f, -6f));

            var area = ValheimUi.CreateRect("Area", frame);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(6f, 6f), new Vector2(-6f, -40f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "", 16, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(14f, -70f), new Vector2(-14f, -16f));
        }

        public override void Refresh()
        {
            var teleporter = Teleporter;
            if (teleporter == null) return;

            var destinations = teleporter.GetDestinations();

            // Affordability is part of the signature, not just the price: the row has to go
            // from "can't pay" to "can pay" the moment the player picks up the last coin,
            // and nothing else would tell it to redraw.
            var signature = string.Join("|", destinations.Select(d =>
                $"{d.Id}:{d.Name}:{teleporter.CostOf(d)}:{teleporter.CostItemOf(d)}:{CanPay(teleporter, d)}"));
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(teleporter, destinations);
            }

            bool any = destinations.Count > 0;
            _empty.gameObject.SetActive(!any);
            _header.text = any ? $"Destinos — {destinations.Count}" : "Destinos";

            // The "ask an admin" half of this stays hidden from visitors: pointing someone at
            // a tab they cannot open is noise.
            _empty.text = teleporter.CanLocalPlayerAdminister()
                ? "Nenhum destino cadastrado ainda.\nUse a aba Admin para adicionar destinos."
                : "Nenhum destino cadastrado ainda.";
        }

        /// <summary>Whether the player is carrying this route's fare. A free route is always
        /// payable.</summary>
        private bool CanPay(TeleporterNpc teleporter, TeleportDestination destination)
        {
            int cost = teleporter.CostOf(destination);
            string item = teleporter.CostItemOf(destination);
            if (cost <= 0 || string.IsNullOrEmpty(item)) return true;

            var player = Player;
            return player != null && ItemNames.Count(player.GetInventory(), item, -1) >= cost;
        }

        private void Rebuild(TeleporterNpc teleporter, List<TeleportDestination> destinations)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();

            var player = Player;

            foreach (var destination in destinations)
            {
                var row = Row(_list, 56f);
                _rows.Add(row.gameObject);

                float distance = player != null
                    ? Vector3.Distance(player.transform.position, destination.Position)
                    : 0f;

                // The fare belongs on the row, not only inside TryTeleport: charging someone
                // and only then telling them the price is how you get a player who thinks the
                // teleporter ate their coins.
                int cost = teleporter.CostOf(destination);
                string costItem = teleporter.CostItemOf(destination);
                bool free = cost <= 0 || string.IsNullOrEmpty(costItem);
                bool affordable = CanPay(teleporter, destination);

                var label = ValheimUi.CreateLabel(row,
                    $"{destination.Name}\n<size=12><color=#9a9188>{distance:0} m daqui</color></size>",
                    16, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                // The fare gets its own little group instead of riding along in the label's
                // second line: a row-level icon lines up with the *row*, so next to a two-line
                // label it sat beside the wrong line entirely.
                var fare = Row(row, 40f, spacing: 4f);
                ValheimUi.SetWidth(fare.gameObject, 96f);

                if (free)
                {
                    var freeLabel = ValheimUi.CreateLabel(fare, "grátis", 14, ValheimUi.Muted,
                        TextAlignmentOptions.Right);
                    Flexible(freeLabel.gameObject);
                }
                else
                {
                    var amount = ValheimUi.CreateLabel(fare, cost.ToString(), 16,
                        affordable ? ValheimUi.Yellow : ValheimUi.Danger, TextAlignmentOptions.Right);
                    Flexible(amount.gameObject);
                    ValheimUi.CreateItemIcon(fare, costItem, 26f);
                }

                var id = destination.Id;
                var go = ValheimUi.CreateButton(row, "Viajar", 130f, 42f, 16);

                // Left clickable when unaffordable on purpose: TryTeleport answers with the
                // exact shortfall, which is more use than a dead button.
                go.onClick.AddListener(() =>
                {
                    Teleporter?.TryTeleport(Player, id);
                });
            }
        }
    }
}
