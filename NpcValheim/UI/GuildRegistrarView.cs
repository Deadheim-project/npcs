using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Registrar's desk. Founding hands over to Guilds' own creation form (name,
    /// description, badge, color), so a guild made here is the same as one made before.
    /// </summary>
    internal sealed class GuildRegistrarView : NpcViewBase
    {
        private TextMeshProUGUI _text;
        private Button _found;
        private string _signature;

        protected override void OnBuild()
        {
            var pane = ValheimUi.CreateInlay(Root, "Registrar");
            ValheimUi.Stretch(pane, 0f, 0f);

            var header = ValheimUi.CreateLabel(pane, "Registro de Guildas", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            _text = ValheimUi.CreateLabel(pane, "", 16, ValheimUi.Beige, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_text.transform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(30f, 90f), new Vector2(-30f, -56f));

            _found = ValheimUi.CreateButton(pane, "Fundar guilda", 240f, 46f, 18);
            ValheimUi.Anchor((RectTransform)_found.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-120f, 24f), new Vector2(120f, 70f));
            _found.onClick.AddListener(Found);
        }

        public override void Refresh()
        {
            bool available = GuildsBridge.IsAvailable;
            string guild = available ? GuildsBridge.OwnGuildName() : null;
            string price = available ? GuildsBridge.PriceText() : null;
            bool canPay = GuildsBridge.CanPay(Player);
            string signature = available + ":" + guild + ":" + price + ":" + canPay;
            if (signature == _signature) return;
            _signature = signature;

            if (!available)
                _text.text = "O registro de guildas está fechado: o mod Guilds não está carregado.";
            else if (guild != null)
                _text.text = $"Você já pertence à guilda <color=#ffe300>{guild}</color>.\n\n" +
                             "Para fundar outra, saia dela primeiro (tecla G).";
            else
                _text.text = "Toda guilda de Deadheim é fundada aqui.\n\n" +
                             "Escolha o nome, a descrição, o brasão e a cor. Você será o líder, e " +
                             "os outros jogadores poderão pedir para entrar pela tecla G.\n\n" +
                             (price == null ? "<color=#73db6b>Sem custo.</color>"
                                 : $"Custo: <color={(canPay ? "#ffe300" : "#ee6b57")}>{price}</color>" +
                                   (canPay ? "" : "\n<color=#ee6b57>Você não tem o suficiente.</color>"));

            _found.gameObject.SetActive(available && guild == null);
            _found.interactable = canPay;
        }

        private void Found()
        {
            if (GuildsBridge.OwnGuildName() != null) { Say("Você já pertence a uma guilda."); return; }
            if (!GuildsBridge.CanPay(Player)) { Say($"Fundar uma guilda custa {GuildsBridge.PriceText()}."); return; }
            UiRoot.RequestClose();
            if (!GuildsBridge.OpenCreateForm())
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Não foi possível abrir o registro de guildas.", 0, null);
        }
    }
}
