using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;
// The server's per-quest payload is also called QuestView; alias it so this file can be the
// *screen* called QuestView without every reference needing a namespace prefix.
using QuestEntry = NpcValheim.Npc.QuestView;

namespace NpcValheim.UI
{
    /// <summary>
    /// The quest log, laid out the way a quest log is expected to be: titles down the left,
    /// the selected quest's detail filling the right, and its actions pinned to the bottom
    /// right of that detail pane.
    ///
    /// The left list is the only thing that rebuilds, and only when the server sends a new
    /// snapshot -- otherwise clicking "Aceitar" would rebuild the list under the cursor and
    /// lose the selection mid-click.
    /// </summary>
    internal sealed class QuestView : NpcViewBase
    {
        private QuestGiverNpc Giver => Npc as QuestGiverNpc;

        private RectTransform _list;
        private TextMeshProUGUI _listEmpty;

        // right-hand detail
        private TextMeshProUGUI _title;
        private TextMeshProUGUI _state;
        private TextMeshProUGUI _description;
        private TextMeshProUGUI _objective;
        private RectTransform _rewardRow;
        private TextMeshProUGUI _placeholder;
        private RectTransform _detailBody;

        private Button _accept;
        private Button _turnIn;
        private Button _abandon;

        private string _selectedId;
        private string _lastSignature;
        private readonly List<Button> _listButtons = new List<Button>();

        protected override void OnBuild()
        {
            const float listWidth = 300f;

            // ---- left: quest titles ----
            var listFrame = ValheimUi.CreateInlay(Root, "QuestList");
            ValheimUi.Anchor(listFrame, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(listWidth, 0f));

            var listHeader = ValheimUi.CreateLabel(listFrame, "Missões", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)listHeader.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(8f, -38f), new Vector2(-8f, -6f));

            var listArea = ValheimUi.CreateRect("ListArea", listFrame);
            ValheimUi.Anchor(listArea, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -40f));
            _list = ValheimUi.CreateScrollList(listArea, spacing: 3f);

            _listEmpty = ValheimUi.CreateLabel(listArea, "Carregando...", 15, ValheimUi.Muted,
                TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_listEmpty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -10f));

            // ---- right: the selected quest ----
            var detail = ValheimUi.CreateInlay(Root, "QuestDetail");
            ValheimUi.Anchor(detail, Vector2.zero, Vector2.one, new Vector2(listWidth + 10f, 0f), Vector2.zero);

            _placeholder = ValheimUi.CreateLabel(detail, "Escolha uma missão à esquerda.", 17,
                ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Stretch((RectTransform)_placeholder.transform, 20f, 20f);

            _detailBody = ValheimUi.CreateRect("Body", detail);
            ValheimUi.Anchor(_detailBody, Vector2.zero, Vector2.one, new Vector2(18f, 58f), new Vector2(-18f, -14f));

            var column = _detailBody.gameObject.AddComponent<VerticalLayoutGroup>();
            column.spacing = 10f;
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.childAlignment = TextAnchor.UpperLeft;

            _title = ValheimUi.CreateLabel(_detailBody, "", 26, ValheimUi.Orange,
                TextAlignmentOptions.TopLeft, display: true);
            _state = Dim(_detailBody, "");
            _description = Body(_detailBody, "");
            _objective = Body(_detailBody, "");

            var rewardHeading = ValheimUi.CreateLabel(_detailBody, "Recompensa", 17, ValheimUi.Orange,
                TextAlignmentOptions.TopLeft, display: true);
            ValheimUi.SetHeight(rewardHeading.gameObject, 24f);

            _rewardRow = ValheimUi.CreateRect("Rewards", _detailBody);
            var rewardLayout = _rewardRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            rewardLayout.spacing = 14f;
            rewardLayout.childControlWidth = true;
            rewardLayout.childControlHeight = true;
            rewardLayout.childForceExpandWidth = false;
            rewardLayout.childForceExpandHeight = false;
            rewardLayout.childAlignment = TextAnchor.MiddleLeft;
            ValheimUi.SetHeight(_rewardRow.gameObject, 46f);

            // ---- actions, bottom-right of the detail pane ----
            var actions = ValheimUi.CreateRect("Actions", detail);
            ValheimUi.Anchor(actions, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-430f, 10f), new Vector2(-16f, 52f));

            var actionLayout = actions.gameObject.AddComponent<HorizontalLayoutGroup>();
            actionLayout.spacing = 10f;
            actionLayout.childControlWidth = false;
            actionLayout.childControlHeight = true;
            actionLayout.childForceExpandWidth = false;
            actionLayout.childAlignment = TextAnchor.MiddleRight;

            _abandon = ValheimUi.CreateButton(actions, "Abandonar", 130f, 40f);
            _turnIn = ValheimUi.CreateButton(actions, "Entregar", 130f, 40f);
            _accept = ValheimUi.CreateButton(actions, "Aceitar", 130f, 40f);

            _accept.onClick.AddListener(OnAccept);
            _turnIn.onClick.AddListener(OnTurnIn);
            _abandon.onClick.AddListener(OnAbandon);

            Giver?.RequestQuests();
        }

        public override void Refresh()
        {
            var giver = Giver;
            if (giver == null) return;

            // Only rebuild the list when the data actually changed.
            var signature = Signature(giver.CachedQuests);
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                RebuildList(giver.CachedQuests);
            }

            _listEmpty.gameObject.SetActive(giver.CachedQuests.Count == 0);
            _listEmpty.text = giver.HasSyncedOnce
                ? "Nenhuma missão disponível.\n\nO admin define as missões em\nnpcs/quests/*.yaml"
                : "Carregando...";

            UpdateDetail(Selected());
        }

        private void RebuildList(List<QuestEntry> quests)
        {
            foreach (var button in _listButtons)
                if (button != null) Object.Destroy(button.gameObject);
            _listButtons.Clear();

            foreach (var quest in quests)
            {
                var entry = ValheimUi.CreateButton(_list, "", 0f, 52f, 16);
                var label = entry.GetComponentInChildren<TextMeshProUGUI>();
                label.alignment = TextAlignmentOptions.Left;
                label.textWrappingMode = TextWrappingModes.Normal;
                label.fontSize = 16;
                label.text = $"{quest.Name}\n<size=12><color=#9a9188>{ShortState(quest)}</color></size>";
                label.color = quest.Locked ? ValheimUi.Muted : ValheimUi.Orange;

                var captured = quest.Id;
                entry.onClick.AddListener(() => _selectedId = captured);
                _listButtons.Add(entry);
            }

            if (_selectedId == null && quests.Count > 0) _selectedId = quests[0].Id;
        }

        /// <summary>
        /// How far along the player actually is.
        ///
        /// The two objectives are counted in different places and the panel used to show only
        /// one of them: a Kill quest accumulates a counter on the server, but a Collect quest
        /// has no counter at all -- the items sit in the player's bag until hand-in. Reading
        /// Counter for both meant a Collect quest showed 0/20 with twenty planks in the bag,
        /// which reads as "the mod is broken" even though hand-in worked.
        /// </summary>
        private int Progress(QuestEntry quest)
        {
            if (quest.Objective == QuestObjectiveKind.Kill) return quest.Counter;
            var player = Player;
            return player != null ? ItemNames.Count(player.GetInventory(), quest.Target, -1) : 0;
        }

        private string ShortState(QuestEntry quest) => quest.Status switch
        {
            QuestStatus.Active => $"em andamento — {Progress(quest)}/{quest.Goal}",
            QuestStatus.Completed => "concluída",
            _ => quest.Locked ? quest.LockReason : "disponível",
        };

        private void UpdateDetail(QuestEntry quest)
        {
            bool has = quest != null;
            _placeholder.gameObject.SetActive(!has);
            _detailBody.gameObject.SetActive(has);
            _accept.gameObject.SetActive(has && quest.Status == QuestStatus.NotStarted && !quest.Locked);
            _turnIn.gameObject.SetActive(has && quest.Status == QuestStatus.Active);
            _abandon.gameObject.SetActive(has && quest.Status == QuestStatus.Active);
            if (!has) return;

            _title.text = quest.Name;
            _state.text = ShortState(quest);
            _description.text = string.IsNullOrEmpty(quest.Description) ? "" : quest.Description;
            _objective.text = $"<color=#ffa13c>Objetivo:</color> {DescribeObjective(quest)}";

            // Grey the button out when the player genuinely cannot finish yet, instead of
            // letting them click and be refused.
            _turnIn.interactable = QuestGiverNpc.CanCompleteNow(quest, Player);

            if (_rewardsShownFor != quest.Id)
            {
                _rewardsShownFor = quest.Id;
                BuildRewards(quest);
            }
        }

        /// <summary>Draws each reward as the game draws an item: its real icon with the
        /// amount over it. Coins and XP get the same treatment, using the Coins item icon
        /// and a plain label for experience.</summary>
        private void BuildRewards(QuestEntry quest)
        {
            for (int i = _rewardRow.childCount - 1; i >= 0; i--)
                Object.Destroy(_rewardRow.GetChild(i).gameObject);

            if (quest.RewardCoins > 0)
                AddReward(MarketplaceNpc.CoinPrefabName, quest.RewardCoins, null);

            foreach (var item in quest.RewardItems)
                AddReward(item.ItemName, item.Amount, null);

            if (quest.RewardExperience > 0)
                AddReward(null, 0, $"{quest.RewardExperience} XP");

            if (_rewardRow.childCount == 0)
                Dim(_rewardRow, "(sem recompensa)");
        }

        private void AddReward(string prefabName, int amount, string textOnly)
        {
            var cell = ValheimUi.CreateRect("Reward", _rewardRow);
            var layout = cell.gameObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = false;
            layout.childAlignment = TextAnchor.MiddleLeft;

            if (textOnly != null)
            {
                var xp = ValheimUi.CreateLabel(cell, textOnly, 16, ValheimUi.Yellow, TextAlignmentOptions.Left);
                xp.textWrappingMode = TextWrappingModes.NoWrap;
                return;
            }

            ValheimUi.CreateItemIcon(cell, prefabName, 40f);
            var label = ValheimUi.CreateLabel(cell,
                $"{amount}x {ValheimUi.Localize(DisplayName(prefabName))}", 16, ValheimUi.Beige,
                TextAlignmentOptions.Left);
            label.textWrappingMode = TextWrappingModes.NoWrap;
        }

        /// <summary>Rebuilt from the structured objective rather than the server's prose, so
        /// the item shows the name the player reads in their inventory.</summary>
        private static string DescribeObjective(QuestEntry quest)
        {
            string target = ValheimUi.Localize(DisplayName(quest.Target));
            return quest.Objective == QuestObjectiveKind.Kill
                ? $"Matar {quest.Goal}x {target}"
                : $"Entregar {quest.Goal}x {target}";
        }

        private string _rewardsShownFor;

        // ---- actions ----

        private void OnAccept()
        {
            var quest = Selected();
            if (quest == null) return;
            Giver.RequestAccept(quest.Id);
            Say($"Missão aceita: {quest.Name}");
        }

        private void OnAbandon()
        {
            var quest = Selected();
            if (quest == null) return;
            Giver.RequestAbandon(quest.Id);
            Say("Missão abandonada.");
        }

        /// <summary>Collect quests hand the items over from the client, because a server
        /// cannot reach into a remote inventory -- same split the sell form uses.</summary>
        private void OnTurnIn()
        {
            var quest = Selected();
            if (quest == null) return;

            if (quest.Objective == QuestObjectiveKind.Collect)
            {
                var inventory = Player.GetInventory();
                if (ItemNames.Count(inventory, quest.Target, -1) < quest.Goal)
                {
                    Say($"Você precisa de {quest.Goal}x {ValheimUi.Localize(DisplayName(quest.Target))}.");
                    return;
                }
                ItemNames.Remove(inventory, quest.Target, quest.Goal, -1);
            }
            else if (quest.Counter < quest.Goal)
            {
                Say($"Progresso insuficiente ({quest.Counter}/{quest.Goal}).");
                return;
            }

            Giver.RequestTurnIn(quest.Id);
            Say("Missão entregue; a recompensa vai para a sua bolsa.");
        }

        private QuestEntry Selected()
        {
            var giver = Giver;
            if (giver == null || _selectedId == null) return null;
            foreach (var quest in giver.CachedQuests)
                if (quest.Id == _selectedId) return quest;
            return null;
        }

        private static string DisplayName(string prefabName)
        {
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var shared = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared : null;
            return shared != null ? shared.m_name : prefabName;
        }

        private static string Signature(List<QuestEntry> quests)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var quest in quests)
                sb.Append(quest.Id).Append(':').Append((int)quest.Status).Append(':')
                  .Append(quest.Counter).Append('|');
            return sb.ToString();
        }
    }
}
