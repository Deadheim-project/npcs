using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Integration;
using NpcValheim.Npc;

namespace NpcValheim.UI
{
    /// <summary>
    /// One tab of the Treinador: Estudo (the two buffs, study and protection) or Aulas (one row
    /// per skill, with the price of the next level). Same shape as the Mestre das Montarias --
    /// the wallet on top, one row per thing, a button per currency the server sells it for.
    ///
    /// What is shown is the server's: the prices and numbers arrive by ServerSync, the
    /// Deadcoins, this week's lessons and the lesson cap in the answer to a status request sent
    /// when the tab opens and every few seconds after. The buffs in place are read from
    /// Deadheim, the levels from the character. Buttons are a courtesy; the server checks the
    /// price, the cap and the week again on every click (Trainer.ServePurchase).
    /// </summary>
    internal sealed class TrainerView : NpcViewBase
    {
        private const float PollSeconds = 5f;
        private const string Grey = "#9a9188";
        private const string Green = "#8fd18f";

        private readonly bool _lessons;

        private TextMeshProUGUI _wallet;
        private TextMeshProUGUI _intro;
        private RectTransform _list;
        private TextMeshProUGUI _empty;
        private string _signature;
        private int _seenMessage;
        private float _nextPoll;
        private readonly List<GameObject> _rows = new List<GameObject>();

        public TrainerView(bool lessons)
        {
            _lessons = lessons;
        }

        private TrainerNpc Counter => Npc as TrainerNpc;

        protected override void OnBuild()
        {
            _wallet = ValheimUi.CreateLabel(Root, "", 18, ValheimUi.Yellow, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_wallet.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, -30f), new Vector2(0f, 0f));

            var pane = ValheimUi.CreateInlay(Root, "Trainer");
            ValheimUi.Anchor(pane, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -40f));

            var header = ValheimUi.CreateLabel(pane, _lessons ? "Aulas" : "Estudo e proteção", 18, ValheimUi.Orange,
                TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -36f), new Vector2(-6f, -6f));

            float introHeight = _lessons ? 66f : 46f;
            _intro = ValheimUi.CreateLabel(pane, "", 14, ValheimUi.Muted, TextAlignmentOptions.Center);
            ValheimUi.Anchor((RectTransform)_intro.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(16f, -38f - introHeight), new Vector2(-16f, -38f));

            var area = ValheimUi.CreateRect("Area", pane);
            ValheimUi.Anchor(area, Vector2.zero, Vector2.one, new Vector2(4f, 4f), new Vector2(-4f, -42f - introHeight));
            _list = ValheimUi.CreateScrollList(area, spacing: 4f);

            _empty = ValheimUi.CreateLabel(area, "", 15, ValheimUi.Muted, TextAlignmentOptions.Top);
            ValheimUi.Anchor((RectTransform)_empty.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -60f), new Vector2(-10f, -12f));

            // Messages from before this panel opened are not about anything on it.
            _seenMessage = Trainer.MessageRevision;
        }

        public override void Refresh()
        {
            var counter = Counter;
            if (counter == null || Player == null) return;

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollSeconds;
                counter.RequestStatus();
            }

            if (Trainer.MessageRevision != _seenMessage)
            {
                _seenMessage = Trainer.MessageRevision;
                Say(Trainer.LastMessage);
            }

            string deadcoins = Trainer.DeadcoinBalance >= 0 ? Trainer.DeadcoinBalance.ToString() : "?";
            _wallet.text = $"<color={Grey}>Na sua bolsa:</color> {MarketplaceNpc.CoinsOf(Player)} moedas" +
                           $"   <color={Grey}>Deadcoins:</color> {deadcoins}";

            var settings = TrainerSettings.Current();
            var prices = TrainerCatalog.Prices();
            if (_lessons) RefreshLessons(counter, settings, prices);
            else RefreshBuffs(counter, settings, prices);
        }

        // ------------------------------------------------------------------ Estudo

        private void RefreshBuffs(TrainerNpc counter, TrainerSettings settings, TrainerPrices prices)
        {
            bool deadheim = DeadheimTrainerApi.IsAvailable;
            double studyLeft = deadheim ? DeadheimTrainerApi.LocalStudySeconds() : 0d;
            int charges = deadheim ? DeadheimTrainerApi.LocalProtectionCharges() : 0;

            if (deadheim)
            {
                float gain = DeadheimTrainerApi.LocalSkillGain();
                float bonus = DeadheimTrainerApi.LocalStudySeconds() > 0d ? DeadheimTrainerApi.LocalStudyBonus() : settings.StudyBonus;
                _intro.text = $"Seu ganho de XP de skill hoje: x{gain:0.##} do normal. " +
                              $"Com o estudo, nas skills abaixo de {settings.StudyMaxSkill}: x{gain * (1f + bonus):0.##}.";
            }
            else _intro.text = DeadheimTrainerApi.NotInstalled;

            // The countdown changes every second; the rows only when something on them does.
            string signature = $"{Plugin.TrainerPrices?.Value}|{settings.StudyBonus}|{settings.StudyMinutes}|{settings.StudyMaxSkill}|" +
                               $"{settings.ProtectionFactor}|{settings.ProtectionPveOnly}|{deadheim}|{Math.Ceiling(studyLeft / 60d)}|{charges}";
            if (signature == _signature) return;
            _signature = signature;

            foreach (var row in _rows) if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();

            int sold = 0;
            if (prices.Study != null && settings.StudyBonus > 0f && settings.StudyMinutes > 0)
            {
                sold++;
                string state = studyLeft > 0d
                    ? $"<color={Green}>Ativo: faltam {FormatMinutes(studyLeft)}. Comprar de novo soma mais {settings.StudyMinutes} min.</color>"
                    : $"<color={Grey}>Por {settings.StudyMinutes} min de jogo; o tempo só corre com o jogo aberto.</color>";
                BuffRow(counter, TrainerCatalog.ItemStudy, prices.Study, "MeadTasty",
                    $"<b>Estudo</b>   +{settings.StudyBonus * 100f:0}% de XP nas skills abaixo de {settings.StudyMaxSkill}",
                    state, deadheim);
            }
            if (prices.Protection != null && settings.ProtectionFactor > 0f)
            {
                sold++;
                string when = settings.ProtectionPveOnly
                    ? "na próxima morte para monstro, queda ou afogamento. Morte para jogador e pena de PK pagam inteiro"
                    : "na próxima morte. A pena de PK paga inteiro";
                string state = charges > 0
                    ? $"<color={Green}>Ativa: cobre a próxima morte que valer.</color>"
                    : $"<color={Grey}>Arena e castelo não tiram skill, então não gastam a proteção.</color>";
                BuffRow(counter, TrainerCatalog.ItemProtection, charges > 0 ? null : prices.Protection, "ShieldWood",
                    $"<b>Proteção</b>   corta {settings.ProtectionFactor * 100f:0}% da perda de skill {when}.",
                    state, deadheim);
            }

            _empty.text = "O Treinador não vende estudo nem proteção no momento.";
            _empty.gameObject.SetActive(sold == 0);
        }

        /// <summary>One buff: icon, what it does, its state, and a button per currency. A null
        /// price leaves the buttons off (the buff is already in place).</summary>
        private void BuffRow(TrainerNpc counter, string item, TrainerPrice price, string icon, string title, string state,
            bool deadheim)
        {
            var row = Row(_list, 74f);
            _rows.Add(row.gameObject);
            ValheimUi.CreateItemIcon(row, icon, 44f);
            var label = ValheimUi.CreateLabel(row, $"{title}\n<size=13>{state}</size>", 15, ValheimUi.Beige,
                TextAlignmentOptions.Left);
            Flexible(label.gameObject);

            if (!deadheim || price == null) return;
            Buttons(row, price, method => Buy(counter, item, method, PriceIn(price, method), null, 0, Title(item)));
        }

        // ------------------------------------------------------------------- Aulas

        private void RefreshLessons(TrainerNpc counter, TrainerSettings settings, TrainerPrices prices)
        {
            var skills = Player.GetSkills();
            var defs = skills?.m_skills?.Where(d => d != null && d.m_skill != Skills.SkillType.None &&
                                                    d.m_skill != Skills.SkillType.All).ToList()
                       ?? new List<Skills.SkillDef>();
            string levels = string.Join(",", defs.Select(d => Mathf.FloorToInt(TrainerLessons.RawLevel(skills, d.m_skill))));

            string signature = $"{Plugin.TrainerPrices?.Value}|{Plugin.TrainerLessonCaps?.Value}|{settings.LessonMaxLevel}|" +
                               $"{settings.LessonsPerWeek}|{Trainer.Known}|{Trainer.StatusRevision}|{levels}";
            if (signature == _signature) return;
            _signature = signature;

            _intro.text = LessonIntro(settings);

            foreach (var row in _rows) if (row != null) UnityEngine.Object.Destroy(row);
            _rows.Clear();

            bool weekFull = Trainer.Known && Trainer.LessonsPerWeek > 0 && Trainer.LessonsUsed >= Trainer.LessonsPerWeek;
            foreach (var def in defs.OrderBy(d => TrainerLessons.DisplayName(d.m_skill.ToString()), StringComparer.CurrentCulture))
            {
                string skill = def.m_skill.ToString();
                int level = Mathf.FloorToInt(TrainerLessons.RawLevel(skills, def.m_skill));
                int target = level + 1;
                var band = prices.LessonTo(target);

                var row = Row(_list, 52f);
                _rows.Add(row.gameObject);
                SpriteIcon(row, def.m_icon, 36f);

                string price = band == null ? "" :
                    $"   <color={Grey}>aula para o {target}: {PriceText(band.Gold, band.Deadcoins)}</color>";
                var label = ValheimUi.CreateLabel(row,
                    $"<b>{TrainerLessons.DisplayName(skill)}</b>   nível {level}\n<size=12>{price.Trim()}</size>",
                    15, ValheimUi.Beige, TextAlignmentOptions.Left);
                Flexible(label.gameObject);

                string state = !Trainer.Known ? "Consultando..."
                    : level >= 100 ? "Nível máximo"
                    : target > Trainer.LessonCap ? "No seu teto"
                    : band == null ? $"Sem aula para o {target}"
                    : weekFull ? "Limite da semana"
                    : null;
                if (state != null)
                {
                    var tag = ValheimUi.CreateLabel(row, state, 14, ValheimUi.Muted, TextAlignmentOptions.Right);
                    ValheimUi.SetWidth(tag.gameObject, 170f);
                    continue;
                }

                var lesson = new TrainerPrice { Gold = band.Gold, Deadcoins = band.Deadcoins };
                Buttons(row, lesson, method => Buy(counter, TrainerCatalog.ItemLesson, method, PriceIn(lesson, method),
                    skill, level, $"a aula de {TrainerLessons.DisplayName(skill)}"));
            }

            _empty.text = "Este personagem não tem skills para treinar.";
            _empty.gameObject.SetActive(defs.Count == 0);
        }

        private static string LessonIntro(TrainerSettings settings)
        {
            if (!Trainer.Known) return "Consultando as suas aulas...";

            string from = string.IsNullOrEmpty(Trainer.LessonCapBoss)
                ? "sem passe de chefe"
                : $"passe de {TrainerCatalog.BossName(Trainer.LessonCapBoss)}";
            string week = Trainer.LessonsPerWeek > 0
                ? $"Nesta semana: {Trainer.LessonsUsed} de {Trainer.LessonsPerWeek} aulas (a semana vira na segunda-feira)."
                : "Sem limite de aulas por semana.";
            var steps = TrainerCatalog.Caps()
                .Select(c => $"{(c.Boss == null ? "sem passe" : TrainerCatalog.BossName(c.Boss))} {Math.Min(c.Level, settings.LessonMaxLevel)}");
            return $"Uma aula sobe 1 nível numa skill. Seu teto: nível {Trainer.LessonCap} ({from}). {week}\n" +
                   $"<size=12>Tetos pelo passe: {string.Join(" · ", steps)}. Nunca acima de {settings.LessonMaxLevel}.</size>";
        }

        // ------------------------------------------------------------------- comum

        private void Buttons(RectTransform row, TrainerPrice price, Action<string> buy)
        {
            if (price.Gold > 0)
            {
                var gold = ValheimUi.CreateButton(row, $"{price.Gold} moedas", 150f, 40f, 14);
                Iconify(gold, MarketplaceNpc.CoinPrefabName, 24f);
                gold.onClick.AddListener(() => buy(Trainer.MethodGold));
            }
            if (price.Deadcoins > 0)
            {
                var deadcoins = ValheimUi.CreateButton(row, $"{price.Deadcoins} Deadcoins", 150f, 40f, 14);
                deadcoins.onClick.AddListener(() => buy(Trainer.MethodDeadcoins));
            }
        }

        private void Buy(TrainerNpc counter, string item, string method, int price, string skill, int level, string what)
        {
            // Courtesies only -- the server decides with its own numbers.
            if (method == Trainer.MethodGold && MarketplaceNpc.CoinsOf(Player) < price)
            {
                Say($"Você tem {MarketplaceNpc.CoinsOf(Player)} moedas; {what} custa {price}.");
                return;
            }
            if (method == Trainer.MethodDeadcoins && Trainer.DeadcoinBalance >= 0 && Trainer.DeadcoinBalance < price)
            {
                Say($"Você tem {Trainer.DeadcoinBalance} Deadcoins; {what} custa {price}.");
                return;
            }
            if (!Trainer.TryBeginPurchase())
            {
                Say("Aguarde a compra anterior terminar.");
                return;
            }
            if (counter != null && counter.RequestBuy(item, method, price, skill, level))
            {
                Say($"Pedindo {what} ao Treinador...");
                return;
            }
            Trainer.EndPurchase();
            Say("O pedido não chegou ao servidor.");
        }

        private static int PriceIn(TrainerPrice price, string method) =>
            method == Trainer.MethodGold ? price.Gold : price.Deadcoins;

        private static string PriceText(int gold, int deadcoins)
        {
            if (gold > 0 && deadcoins > 0) return $"{gold} moedas ou {deadcoins} Deadcoins";
            return gold > 0 ? $"{gold} moedas" : $"{deadcoins} Deadcoins";
        }

        private static string Title(string item) => item == TrainerCatalog.ItemStudy ? "o estudo" : "a proteção";

        private static string FormatMinutes(double seconds)
        {
            int minutes = (int)Math.Ceiling(seconds / 60d);
            return minutes >= 60 ? $"{minutes / 60} h {minutes % 60:00} min" : $"{minutes} min";
        }

        /// <summary>A skill's own icon (SkillDef.m_icon), sized like an item icon.</summary>
        private static void SpriteIcon(Transform parent, Sprite sprite, float size)
        {
            var rect = ValheimUi.CreateRect("Icon", parent);
            rect.sizeDelta = new Vector2(size, size);
            var image = rect.gameObject.AddComponent<Image>();
            image.preserveAspect = true;
            image.raycastTarget = false;
            image.sprite = sprite;
            image.enabled = sprite != null;
            var layout = rect.gameObject.AddComponent<LayoutElement>();
            layout.preferredWidth = size;
            layout.preferredHeight = size;
            layout.minWidth = size;
        }
    }
}
