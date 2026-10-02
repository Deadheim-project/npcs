using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// One tab of the Mestre das Montarias: Treinamento (the riding skill levels) or Montarias
    /// (the mounts for sale). Same shape as the Deadcoins counter -- the wallet on top, one row
    /// per offer, one button per row -- with the state of each row read from Montarias: a level
    /// you know, the next one you can learn, one that needs the level before it; a mount you
    /// already have.
    ///
    /// Buttons are a courtesy. The server checks the order of levels, ownership, the price and
    /// the currency again on every click (MountTrainer.ServePurchase).
    /// </summary>
    internal sealed class MountTrainerView : NpcViewBase
    {
        private const float PollSeconds = 5f;
        private const string SkillIcon = "SaddleLox";

        private static readonly Dictionary<string, Sprite> MountIcons = new Dictionary<string, Sprite>();

        private readonly MountOfferKind _kind;

        private TextMeshProUGUI _wallet;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private string _signature;
        private int _seenMessage;
        private float _nextPoll;
        private int _offerCount;
        private readonly List<GameObject> _rows = new List<GameObject>();

        public MountTrainerView(MountOfferKind kind)
        {
            _kind = kind;
        }

        private MountTrainerNpc Trainer => Npc as MountTrainerNpc;

        protected override void OnBuild()
        {
            _wallet = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_wallet.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            var pane = ValheimUi.CreateInlay(Root, "Offers");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            var header = ValheimUi.CreateLabel(pane,
                _kind == MountOfferKind.Skill ? "Habilidade de Montaria" : "Montarias à venda",
                18, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -38f));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "", 15, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            _seenMessage = MountTrainer.MessageRevision;
            MontariasApi.RequestLocalState();
        }

        public override void Refresh()
        {
            var trainer = Trainer;
            if (trainer == null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                trainer.RequestService(MountTrainer.ActionBalance, "");
            }

            _wallet.text =
                $"<color=#9a9188>Coins na bolsa:</color> {MarketplaceNpc.CoinsOf(Player)}   " +
                (DeadcoinShop.Balance < 0
                    ? "<color=#9a9188>Deadcoins: consultando...</color>"
                    : $"<color=#9a9188>Deadcoins:</color> {DeadcoinShop.Balance}");

            if (MountTrainer.MessageRevision != _seenMessage)
            {
                _seenMessage = MountTrainer.MessageRevision;
                Say(MountTrainer.LastMessage);
            }

            // Redraw when the list, or what this player knows and owns, changes. Both arrive
            // from the server on their own (ServerSync, Montarias' state push).
            string raw = Plugin.MountTrainerOffers.Value ?? "";
            string signature = raw + "#" + MontariasApi.IsAvailable + "#" + MontariasApi.LocalKnown() + "#" +
                               MontariasApi.LocalRevision();
            if (signature != _signature)
            {
                _signature = signature;
                Rebuild(trainer);
            }
            _empty.gameObject.SetActive(_offerCount == 0);
        }

        private void Rebuild(MountTrainerNpc trainer)
        {
            foreach (var row in _rows) if (row != null) Object.Destroy(row);
            _rows.Clear();
            _offerCount = 0;

            if (!MontariasApi.IsAvailable)
            {
                _empty.text = "O mod Montarias não está instalado.";
                return;
            }
            _empty.text = _kind == MountOfferKind.Skill
                ? "Este mestre não ensina nada no momento."
                : "Este mestre não tem montarias à venda no momento.";

            bool known = MontariasApi.LocalKnown();
            int rank = MontariasApi.LocalRank();
            foreach (var offer in MountCatalog.Available(Plugin.MountTrainerOffers.Value ?? "", null))
            {
                if (offer.Kind != _kind) continue;
                _offerCount++;

                var row = Row(_list, 56f);
                _rows.Add(row.gameObject);

                if (offer.Kind == MountOfferKind.Skill) ValheimUi.CreateItemIcon(row, SkillIcon, 40f);
                else MountIcon(row, offer.Id, 40f);

                string detail = offer.Kind == MountOfferKind.Skill
                    ? $"velocidade das montarias {MontariasApi.RankSpeed(offer.Rank) * 100f:0}%"
                    : $"exige {MontariasApi.RankName(MontariasApi.MountRequiredRank(offer.Id))}";
                var label = ValheimUi.CreateLabel(row,
                    $"{offer.Title}\n<size=12><color=#9a9188>{detail} · {offer.Price} {offer.CurrencyName}</color></size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                string state = StateOf(offer, known, rank);
                if (state != null)
                {
                    var tag = ValheimUi.CreateLabel(row, state, 14, ValheimUi.Muted, TextAlignmentOptions.Right);
                    ValheimUi.SetWidth(tag.gameObject, 170f);
                    continue;
                }

                var buy = ValheimUi.CreateButton(row, offer.Kind == MountOfferKind.Skill ? "Aprender" : "Comprar", 120f, 36f, 14);
                buy.onClick.AddListener(() => Buy(trainer, offer));
            }
        }

        /// <summary>Why a row has no button, or null when it can be bought.</summary>
        private static string StateOf(MountOffer offer, bool known, int rank)
        {
            if (!known) return "Consultando...";
            if (offer.Kind == MountOfferKind.Skill)
            {
                if (rank >= offer.Rank) return "Aprendida";
                if (rank < offer.Rank - 1) return $"Requer {MontariasApi.RankName(offer.Rank - 1)}";
                return null;
            }
            return MontariasApi.LocalOwns(offer.Id) ? "Já é sua" : null;
        }

        private void Buy(MountTrainerNpc trainer, MountOffer offer)
        {
            // Courtesies only -- the server decides with its own numbers.
            if (offer.Currency == MountCurrency.Coins && MarketplaceNpc.CoinsOf(Player) < offer.Price)
            {
                Say($"Você tem {MarketplaceNpc.CoinsOf(Player)} Coins; custa {offer.Price}.");
                return;
            }
            if (offer.Currency == MountCurrency.Deadcoins && DeadcoinShop.Balance >= 0 && DeadcoinShop.Balance < offer.Price)
            {
                Say($"Você tem {DeadcoinShop.Balance} Deadcoins; custa {offer.Price}.");
                return;
            }
            if (!MountTrainer.TryBeginPurchase())
            {
                Say("Aguarde a compra anterior terminar.");
                return;
            }

            if (MountTrainer.RequestBuy(trainer, offer))
            {
                Say($"{(offer.Kind == MountOfferKind.Skill ? "Aprendendo" : "Comprando")} {offer.Title}...");
                return;
            }
            MountTrainer.EndPurchase();
            Say("O pedido não chegou ao servidor.");
        }

        /// <summary>The mount's own menu icon, from the Montarias plugin folder.</summary>
        private static void MountIcon(Transform parent, string mountId, float size)
        {
            var rect = ValheimUi.CreateRect("Icon", parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.sprite = LoadMountIcon(MontariasApi.MountIconPath(mountId));
            image.enabled = image.sprite != null;
            ValheimUi.SetWidth(rect.gameObject, size);
        }

        private static Sprite LoadMountIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (MountIcons.TryGetValue(path, out var cached)) return cached;

            Sprite sprite = null;
            if (File.Exists(path))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (GameApi.TryLoadImage(tex, File.ReadAllBytes(path)))
                {
                    tex.wrapMode = TextureWrapMode.Clamp;
                    sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            MountIcons[path] = sprite;
            return sprite;
        }
    }
}
