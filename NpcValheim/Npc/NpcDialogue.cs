using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using NpcValheim.Persistence;
using NpcValheim.UI;

namespace NpcValheim.Npc
{
    /// <summary>
    /// Packing, cleaning and the emote list for <see cref="DialogueSettings"/>. The ZDO holds
    /// one string so the server writes it in one go and every client reads it whole.
    /// </summary>
    internal static class NpcDialogue
    {
        internal const int MaxPhrases = 12;
        internal const int MaxPhraseLength = 160;

        private const string Version = "1";
        private const char Field = '\u001f';
        private const char Item = '\u001e';

        /// <summary>
        /// The player's one-shot emotes, as the game names them (Emote.cs triggers
        /// "emote_" + id on the player animator). Looping ones (sit, dance, rest...) are left
        /// out: they never end on their own, and the NPC would sit for the rest of the session.
        /// </summary>
        internal static readonly (string Id, string Label)[] Emotes =
        {
            ("", "Nenhuma"),
            ("wave", "Acenar"),
            ("bow", "Reverência"),
            ("point", "Apontar"),
            ("thumbsup", "Joinha"),
            ("cheer", "Comemorar"),
            ("nonono", "Não, não"),
            ("comehere", "Chamar"),
            ("laugh", "Rir"),
            ("shrug", "Dar de ombros"),
            ("flex", "Mostrar força"),
            ("challenge", "Desafiar"),
            ("blowkiss", "Mandar beijo"),
            ("toast", "Brindar"),
            ("roar", "Rugir"),
        };

        internal static string EmoteLabel(string id) =>
            Emotes.FirstOrDefault(e => e.Id == (id ?? "")).Label ?? "Nenhuma";

        internal static bool IsKnownEmote(string id) => Emotes.Any(e => e.Id == (id ?? ""));

        internal static DialogueSettings Clean(DialogueSettings source)
        {
            source ??= new DialogueSettings();
            return new DialogueSettings
            {
                Greet = CleanList(source.Greet),
                Talk = CleanList(source.Talk),
                Bye = CleanList(source.Bye),
                GreetEmote = IsKnownEmote(source.GreetEmote) ? source.GreetEmote ?? "" : "",
                TalkEmote = IsKnownEmote(source.TalkEmote) ? source.TalkEmote ?? "" : "",
                ByeEmote = IsKnownEmote(source.ByeEmote) ? source.ByeEmote ?? "" : "",
            };
        }

        private static List<string> CleanList(IEnumerable<string> phrases)
        {
            var result = new List<string>();
            if (phrases == null) return result;
            foreach (var raw in phrases)
            {
                // The separators are what keep the packed string apart; a phrase can't carry them.
                var text = (raw ?? "").Replace(Field, ' ').Replace(Item, ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
                if (text.Length == 0) continue;
                if (text.Length > MaxPhraseLength) text = text.Substring(0, MaxPhraseLength);
                result.Add(text);
                if (result.Count >= MaxPhrases) break;
            }
            return result;
        }

        internal static string Pack(DialogueSettings dialogue)
        {
            var d = Clean(dialogue);
            return string.Join(Field.ToString(), new[]
            {
                Version, d.GreetEmote, d.TalkEmote, d.ByeEmote,
                string.Join(Item.ToString(), d.Greet),
                string.Join(Item.ToString(), d.Talk),
                string.Join(Item.ToString(), d.Bye),
            });
        }

        internal static DialogueSettings Unpack(string packed)
        {
            var parts = (packed ?? "").Split(Field);
            if (parts.Length < 7 || parts[0] != Version) return new DialogueSettings();
            return Clean(new DialogueSettings
            {
                GreetEmote = parts[1],
                TalkEmote = parts[2],
                ByeEmote = parts[3],
                Greet = Split(parts[4]),
                Talk = Split(parts[5]),
                Bye = Split(parts[6]),
            });
        }

        private static List<string> Split(string joined) =>
            string.IsNullOrEmpty(joined) ? new List<string>() : joined.Split(Item).ToList();

        /// <summary>One line per phrase, as the admin edits them.</summary>
        internal static string ToLines(List<string> phrases) => phrases == null ? "" : string.Join("\n", phrases);

        internal static List<string> FromLines(string text) =>
            CleanList((text ?? "").Split('\n'));
    }

    /// <summary>
    /// The NPC noticing the local player, Haldor style: an emote and a speech bubble when the
    /// player comes within <see cref="GreetRange"/>, the same on leaving past
    /// <see cref="ByeRange"/>, and when the player talks to it (opens its window). While the
    /// player stays near, it also says one of its talk phrases now and then, as vanilla traders do.
    ///
    /// Purely local, again like vanilla: each client sees its own NPC greet its own player.
    /// The animator trigger is set on this client's copy only, so nothing goes on the wire and
    /// another player across the square does not see the NPC wave at someone else.
    /// </summary>
    internal sealed class NpcGreeter : MonoBehaviour
    {
        private const float GreetRange = 5f;
        private const float ByeRange = 7f;
        private const float GreetAgainAfter = 20f;
        private const float RandomTalkMin = 30f;
        private const float RandomTalkMax = 50f;
        private const float BubbleSeconds = 5f;
        private const float BubbleCullDistance = 20f;

        private NpcBase _npc;
        private bool _near;
        private float _nextGreet;
        private float _nextCheck;
        private float _nextRandomTalk;

        private void Awake() => _npc = GetComponent<NpcBase>();

        private void Update()
        {
            if (_npc == null || Time.time < _nextCheck) return;
            _nextCheck = Time.time + 0.25f;

            var player = Player.m_localPlayer;
            if (player == null) { _near = false; return; }

            float distance = Vector3.Distance(player.transform.position, transform.position);
            if (!_near && distance < GreetRange)
            {
                _near = true;
                _nextRandomTalk = Time.time + UnityEngine.Random.Range(RandomTalkMin, RandomTalkMax);
                if (Time.time >= _nextGreet)
                {
                    _nextGreet = Time.time + GreetAgainAfter;
                    var d = _npc.GetDialogue();
                    Say(d.Greet, d.GreetEmote);
                }
            }
            else if (_near && distance > ByeRange)
            {
                _near = false;
                var d = _npc.GetDialogue();
                Say(d.Bye, d.ByeEmote);
            }
            else if (_near && Time.time >= _nextRandomTalk)
            {
                _nextRandomTalk = Time.time + UnityEngine.Random.Range(RandomTalkMin, RandomTalkMax);
                // Not over its own open window: the player is already reading that.
                if (!UiRoot.IsOpen) Say(_npc.GetDialogue().Talk, "");
            }
        }

        /// <summary>The player opened this NPC's window.</summary>
        internal void OnTalk()
        {
            var d = _npc != null ? _npc.GetDialogue() : null;
            if (d == null) return;
            _nextRandomTalk = Time.time + UnityEngine.Random.Range(RandomTalkMin, RandomTalkMax);
            Say(d.Talk, d.TalkEmote);
        }

        private void Say(List<string> phrases, string emote)
        {
            if (phrases != null && phrases.Count > 0 && Chat.instance != null)
            {
                string text = phrases[UnityEngine.Random.Range(0, phrases.Count)];
                string name = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
                text = text.Replace("{jogador}", name).Replace("{player}", name);
                float height = _npc.NameplateHeight * Mathf.Max(0.01f, transform.localScale.y) + 0.35f;
                Chat.instance.SetNpcText(gameObject, Vector3.up * height, BubbleCullDistance, BubbleSeconds, "", text, false);
            }
            PlayEmote(emote);
        }

        /// <summary>
        /// Fires "emote_&lt;id&gt;" on whichever animator here knows it: the player rig, or a
        /// creature body's puppet if it happens to have one. Checked first, because Unity logs
        /// a warning for every trigger an animator has no parameter for.
        /// </summary>
        private void PlayEmote(string emote)
        {
            if (string.IsNullOrEmpty(emote)) return;
            string trigger = "emote_" + emote;
            foreach (var animator in GetComponentsInChildren<Animator>())
            {
                if (animator == null || !animator.isActiveAndEnabled || animator.runtimeAnimatorController == null) continue;
                if (!animator.parameters.Any(p => p.type == AnimatorControllerParameterType.Trigger && p.name == trigger)) continue;
                if (animator.parameters.Any(p => p.name == "emote_stop")) animator.ResetTrigger("emote_stop");
                animator.SetTrigger(trigger);
                return;
            }
        }
    }
}
