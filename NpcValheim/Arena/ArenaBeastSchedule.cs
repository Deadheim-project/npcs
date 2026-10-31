using System;
using System.Collections.Generic;

namespace NpcValheim.Arena
{
    /// <summary>
    /// Where the abilities of one creature stand. The owner keeps it on the creature's ZDO
    /// (see <see cref="Write"/>), so a new owner reads it back and goes on from there. Times
    /// are milliseconds of ZNet's clock (ZNet.GetTimeSeconds), the same on every machine.
    /// </summary>
    internal sealed class ArenaBeastScheduleState
    {
        internal const string KeyStart = "npcv_bStart";
        internal const string KeyBusy = "npcv_bBusy";
        internal const string KeyUsed = "npcv_bUsed";
        internal const string KeyNext = "npcv_bNext";

        /// <summary>When the fight began: the gates opening (the creature comes in with them),
        /// or the spawn of a test creature. 0 until the first tick.</summary>
        public long StartMs;
        /// <summary>Until when the ability under way holds the others back.</summary>
        public long BusyUntilMs;
        /// <summary>Abilities by index that go off once (hp, start, time) and already did.</summary>
        public int UsedMask;
        /// <summary>For "every": when it may go off next. 0 = not drawn yet.</summary>
        public readonly long[] NextMs = new long[ArenaBeastKit.MaxAbilities];

        public bool Used(int index) => (UsedMask & (1 << index)) != 0;

        internal void Write(Action<string, long> set)
        {
            set(KeyStart, StartMs);
            set(KeyBusy, BusyUntilMs);
            set(KeyUsed, UsedMask);
            for (int i = 0; i < NextMs.Length; i++) set(KeyNext + i, NextMs[i]);
        }

        internal static ArenaBeastScheduleState Read(Func<string, long> get)
        {
            var s = new ArenaBeastScheduleState
            {
                StartMs = get(KeyStart),
                BusyUntilMs = get(KeyBusy),
                UsedMask = (int)get(KeyUsed),
            };
            for (int i = 0; i < s.NextMs.Length; i++) s.NextMs[i] = get(KeyNext + i);
            return s;
        }

        internal ArenaBeastScheduleState Copy() => Read(Snapshot());

        private Func<string, long> Snapshot()
        {
            var values = new Dictionary<string, long>();
            Write((k, v) => values[k] = v);
            return k => values.TryGetValue(k, out var v) ? v : 0L;
        }
    }

    /// <summary>
    /// What goes off now, as plain logic: in go the abilities, the clock, the health and the
    /// state; out comes the index of the one to use (or -1) and the state after it. The owner's
    /// ArenaBeastBrain asks this four times a second and does the rest.
    ///
    ///   hp     once, when the health comes down to the value (%).
    ///   every  again and again, the value in seconds apart, ±20%; the first one value after the start.
    ///   time   once, value seconds after the start.
    ///   start  once, at the start.
    /// Only one at a time: whatever is due while another one is under way waits for it to
    /// end. Health first (a phase must not be skipped), then start, time, every.
    /// </summary>
    internal static class ArenaBeastSchedule
    {
        internal const double Spread = 0.2;

        internal static int Decide(IReadOnlyList<ArenaBeastAbility> abilities, ArenaBeastScheduleState s, long nowMs,
            float healthPercent, Func<double> random)
        {
            int count = Math.Min(abilities.Count, ArenaBeastKit.MaxAbilities);
            if (s.StartMs <= 0L) s.StartMs = nowMs;
            for (int i = 0; i < count; i++)
                if (abilities[i].Acts && abilities[i].Trigger == "every" && s.NextMs[i] <= 0L)
                    s.NextMs[i] = s.StartMs + Cooldown(abilities[i].Value, random);
            if (nowMs < s.BusyUntilMs) return -1;

            foreach (var trigger in new[] { "hp", "start", "time", "every" })
                for (int i = 0; i < count; i++)
                {
                    var a = abilities[i];
                    if (!a.Acts || a.Trigger != trigger) continue;
                    switch (trigger)
                    {
                        case "hp":
                            if (s.Used(i) || healthPercent > a.Value) continue;
                            break;
                        case "start":
                            if (s.Used(i)) continue;
                            break;
                        case "time":
                            if (s.Used(i) || nowMs - s.StartMs < (long)(a.Value * 1000.0)) continue;
                            break;
                        default:
                            if (nowMs < s.NextMs[i]) continue;
                            s.NextMs[i] = nowMs + Cooldown(a.Value, random);
                            return i;
                    }
                    s.UsedMask |= 1 << i;
                    return i;
                }
            return -1;
        }

        /// <summary>The ability just picked holds the rest back this long.</summary>
        internal static void Busy(ArenaBeastScheduleState s, long nowMs, float seconds) =>
            s.BusyUntilMs = Math.Max(s.BusyUntilMs, nowMs + (long)(seconds * 1000.0));

        private static long Cooldown(float seconds, Func<double> random) =>
            (long)(seconds * 1000.0 * (1.0 - Spread + 2.0 * Spread * random()));

        // ------------------------------------------------------------------ Fúria do Tempo

        /// <summary>+aumento% from the moment it starts, and again every 10 s, up to +200%.</summary>
        internal static float TimeRageBonus(float increasePercent, long startMs, long nowMs)
        {
            if (startMs <= 0L || nowMs < startMs || increasePercent <= 0f) return 0f;
            long steps = 1 + (nowMs - startMs) / (long)(ArenaBeastKit.TimeRageStep * 1000f);
            return (float)Math.Min(ArenaBeastKit.TimeRageCap, increasePercent * steps);
        }

        /// <summary>What a hit of the creature is multiplied by: the Fúria (×dano) and the Fúria do Tempo (+%).</summary>
        internal static float DamageMultiplier(float enrage, float timeRageBonusPercent) =>
            (enrage > 0f ? enrage : 1f) * (1f + Math.Max(0f, timeRageBonusPercent) / 100f);
    }
}
