using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;

namespace NpcValheim.UI
{
    /// <summary>
    /// The Registrar's desk. Founding hands over to Guilds' own creation form (name,
    /// description, badge, color), so a guild made here is the same as one made before.
    ///
    /// It is also the door to Guilds' own window (members, applications, ranks, joining a
    /// guild): with [Guildas] SomentePeloNpc on, Guilds' key opens nothing and this button is
    /// the only way in.
    /// </summary>
    internal sealed class GuildRegistrarView : NpcViewBase
    {
        private TextMeshProUGUI _text;
        private Button _found;
        private Button _open;
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
            _found.onClick.AddListener(Found);

            _open = ValheimUi.CreateButton(pane, "", 240f, 46f, 18);
            _open.onClick.AddListener(Open);
        }

        public override void Refresh()
        {
            bool available = GuildsBridge.IsAvailable;
            string guild = available ? GuildsBridge.OwnGuildName() : null;
            string price = available ? GuildsBridge.PriceText() : null;
            bool canPay = GuildsBridge.CanPay(Player);
            bool npcOnly = GuildsBridge.WindowNpcOnly;
            string signature = available + ":" + guild + ":" + price + ":" + canPay + ":" + npcOnly;
            if (signature == _signature) return;
            _signature = signature;

            string where = npcOnly ? "aqui no Registrador" : "aqui ou pela tecla do Guilds";
            if (!available)
                _text.text = "O registro de guildas está fechado: o mod Guilds não está carregado.";
            else if (guild != null)
                _text.text = $"Você pertence à guilda <color=#ffe300>{guild}</color>.\n\n" +
                             $"Membros, pedidos de entrada e cargos ficam na janela da guilda, {where}.\n" +
                             "Para fundar outra, saia desta primeiro.";
            else
                _text.text = "Toda guilda de Deadheim é fundada aqui.\n\n" +
                             "Escolha o nome, a descrição, o brasão e a cor. Você será o líder, e " +
                             $"os outros jogadores poderão pedir para entrar, {where}.\n\n" +
                             (price == null ? "<color=#73db6b>Sem custo.</color>"
                                 : $"Custo: <color={(canPay ? "#ffe300" : "#ee6b57")}>{price}</color>" +
                                   (canPay ? "" : "\n<color=#ee6b57>Você não tem o suficiente.</color>"));

            bool founding = available && guild == null;
            _found.gameObject.SetActive(founding);
            _found.interactable = canPay;
            _open.gameObject.SetActive(available);
            _open.GetComponentInChildren<TextMeshProUGUI>().text = guild != null ? "Abrir a minha guilda" : "Procurar guildas";

            // Side by side when both show, centred when only one does.
            Place(_found, founding ? -250f : -120f);
            Place(_open, founding ? 10f : -120f);
        }

        private static void Place(Button button, float left) =>
            ValheimUi.Anchor((RectTransform)button.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(left, 24f), new Vector2(left + 240f, 70f));

        private void Found()
        {
            if (GuildsBridge.OwnGuildName() != null) { Say("Você já pertence a uma guilda."); return; }
            if (!GuildsBridge.CanPay(Player)) { Say($"Fundar uma guilda custa {GuildsBridge.PriceText()}."); return; }
            UiRoot.RequestClose();
            if (!GuildsBridge.OpenCreateForm())
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Não foi possível abrir o registro de guildas.", 0, null);
        }

        private void Open()
        {
            UiRoot.RequestClose();
            if (!GuildsBridge.OpenGuildWindow())
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "Não foi possível abrir a janela de guildas.", 0, null);
        }
    }
}
