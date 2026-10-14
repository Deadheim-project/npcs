using TMPro;
using UnityEngine;
using UnityEngine.UI;
using NpcValheim.Npc;
using NpcValheim.Persistence;

namespace NpcValheim.UI
{
    /// <summary>
    /// Admin tab "Falas": what this NPC does when a player comes near, talks to it (opens this
    /// window) and walks away -- one emote and a list of phrases for each moment, one phrase per
    /// line, picked at random. While a player stays near, the NPC also says a "falar" phrase
    /// now and then, as vanilla traders do.
    ///
    /// Saved like the name: the server checks the admin and writes the NPC's ZDO, so every
    /// player sees the new lines, and the instance yaml (and any template saved from it) keeps
    /// them under "dialogue:".
    /// </summary>
    internal sealed class DialogueView : NpcViewBase
    {
        private sealed class Moment
        {
            public int Emote;
            public Button EmoteButton;
            public TMP_InputField Phrases;
        }

        private Moment _greet;
        private Moment _talk;
        private Moment _bye;
        private string _loaded;

        protected override void OnBuild()
        {
            var hint = Dim(Root, "Uma frase por linha; o NPC sorteia uma. {jogador} vira o nome de quem está ouvindo. " +
                                 "A animação é a do emote do jogador; em corpo de criatura pode não existir.");
            ValheimUi.Anchor((RectTransform)hint.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(4f, -40f), new Vector2(-4f, 0f));

            _greet = Column(0f, 1f / 3f, "Ao chegar perto");
            _talk = Column(1f / 3f, 2f / 3f, "Ao falar");
            _bye = Column(2f / 3f, 1f, "Ao se afastar");

            var save = ValheimUi.CreateButton(Root, "Salvar falas", 200f, 42f, 16);
            ValheimUi.Anchor((RectTransform)save.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(-210f, 0f), new Vector2(-10f, 42f));
            save.onClick.AddListener(Save);

            var reset = ValheimUi.CreateButton(Root, "Usar o padrão", 200f, 42f, 16);
            ValheimUi.Anchor((RectTransform)reset.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(10f, 0f), new Vector2(210f, 42f));
            reset.onClick.AddListener(() =>
            {
                Load(Npc.TypeDefaultDialogue);
                Say("Padrão deste tipo de NPC carregado. Clique em Salvar falas para valer.");
            });
        }

        private Moment Column(float from, float to, string title)
        {
            var pane = ValheimUi.CreateInlay(Root, title);
            ValheimUi.Anchor(pane, new Vector2(from, 0f), new Vector2(to, 1f), new Vector2(4f, 52f), new Vector2(-4f, -46f));
            var header = ValheimUi.CreateLabel(pane, title, 17, ValheimUi.Orange, TextAlignmentOptions.Center, display: true);
            ValheimUi.Anchor((RectTransform)header.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(6f, -34f), new Vector2(-6f, -6f));

            var moment = new Moment();
            moment.EmoteButton = ValheimUi.CreateButton(pane, "", 0f, 36f, 14);
            ValheimUi.Anchor((RectTransform)moment.EmoteButton.transform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(10f, -76f), new Vector2(-10f, -40f));
            moment.EmoteButton.onClick.AddListener(() =>
            {
                moment.Emote = (moment.Emote + 1) % NpcDialogue.Emotes.Length;
                PaintEmote(moment);
            });

            moment.Phrases = ValheimUi.CreateInputField(pane, "", 0f, 0f, 14, multiline: true);
            ValheimUi.Anchor((RectTransform)moment.Phrases.transform, Vector2.zero, Vector2.one,
                new Vector2(10f, 10f), new Vector2(-10f, -84f));
            moment.Phrases.characterLimit = NpcDialogue.MaxPhrases * (NpcDialogue.MaxPhraseLength + 1);
            PaintEmote(moment);
            return moment;
        }

        private static void PaintEmote(Moment moment)
        {
            var label = moment.EmoteButton.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = "Animação: " + NpcDialogue.Emotes[moment.Emote].Label;
        }

        public override void Refresh()
        {
            // Loaded once per change on the NPC, not every frame: the admin is typing here.
            var current = Npc.GetDialogue();
            string packed = NpcDialogue.Pack(current);
            if (packed == _loaded) return;
            _loaded = packed;
            Load(current);
        }

        private void Load(DialogueSettings dialogue)
        {
            Fill(_greet, dialogue.GreetEmote, dialogue.Greet);
            Fill(_talk, dialogue.TalkEmote, dialogue.Talk);
            Fill(_bye, dialogue.ByeEmote, dialogue.Bye);
        }

        private static void Fill(Moment moment, string emote, System.Collections.Generic.List<string> phrases)
        {
            moment.Emote = System.Math.Max(0, System.Array.FindIndex(NpcDialogue.Emotes, e => e.Id == (emote ?? "")));
            PaintEmote(moment);
            moment.Phrases.text = NpcDialogue.ToLines(phrases);
        }

        private void Save()
        {
            var dialogue = new DialogueSettings
            {
                GreetEmote = NpcDialogue.Emotes[_greet.Emote].Id,
                TalkEmote = NpcDialogue.Emotes[_talk.Emote].Id,
                ByeEmote = NpcDialogue.Emotes[_bye.Emote].Id,
                Greet = NpcDialogue.FromLines(_greet.Phrases.text),
                Talk = NpcDialogue.FromLines(_talk.Phrases.text),
                Bye = NpcDialogue.FromLines(_bye.Phrases.text),
            };
            Npc.RequestSetDialogue(Player, dialogue);
            Say("Falas enviadas ao servidor.");
        }
    }
}
