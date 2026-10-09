using System;

namespace NpcValheim.Arena
{
    /// <summary>
    /// The WotLK 3.3.5 arena arithmetic, transcribed from TrinityCore 3.3.5
    /// (src/server/game/Battlegrounds/ArenaTeam.cpp and Arena.cpp), which is where the private
    /// servers people remember as "the real thing" got their numbers. Nothing here touches the
    /// game, so the checks run it outside Valheim against values worked out by hand.
    ///
    /// The one deliberate reading: TrinityCore computes the chance to win with a 650 divisor,
    /// not the 400 of textbook Elo. That is not a typo carried over -- it is what makes a
    /// 150-point gap mean "roughly even" in the arena and not "clear favourite".
    /// </summary>
    internal static class ArenaRules
    {
        /// <summary>ARENA_TIMELIMIT_POINTS_LOSS: both teams lose 16 when nobody wins in time.</summary>
        internal const int DefaultDrawPenalty = 16;

        /// <summary>ArenaTeam::GetChanceAgainst.</summary>
        internal static float ChanceAgainst(int ownRating, int opponentRating) =>
            1.0f / (1.0f + (float)Math.Exp((float)Math.Log(10.0f) * ((float)opponentRating - ownRating) / 650.0f));

        /// <summary>
        /// ArenaTeam::GetRatingMod -- the change to a TEAM rating or to a PERSONAL rating (the
        /// same function serves both; only the rating passed in differs), always measured
        /// against the opponent's matchmaking rating.
        ///
        /// Winners below 1300 gain more (win modifier 1, 48 by default), fading linearly to the
        /// normal modifier between 1000 and 1300: that is how a new team climbs out of 0.
        /// </summary>
        internal static int RatingMod(int ownRating, int opponentRating, bool won, ArenaTuning t)
        {
            float chance = ChanceAgainst(ownRating, opponentRating);
            float mod;
            if (won)
            {
                if (ownRating < 1300)
                {
                    float m1 = t.WinRatingModifier1;
                    if (ownRating < 1000)
                        mod = m1 * (1.0f - chance);
                    else
                        mod = ((m1 / 2.0f) + ((m1 / 2.0f) * (1300.0f - ownRating) / 300.0f)) * (1.0f - chance);
                }
                else
                {
                    mod = t.WinRatingModifier2 * (1.0f - chance);
                }
            }
            else
            {
                mod = t.LoseRatingModifier * (-chance);
            }
            return (int)Math.Ceiling(mod);
        }

        /// <summary>ArenaTeam::GetMatchmakerRatingMod: plain Elo on the hidden rating.</summary>
        internal static int MatchmakerRatingMod(int ownRating, int opponentRating, bool won, ArenaTuning t)
        {
            float chance = ChanceAgainst(ownRating, opponentRating);
            float mod = ((won ? 1.0f : 0.0f) - chance) * t.MatchmakerRatingModifier;
            return (int)Math.Ceiling(mod);
        }

        /// <summary>Ratings stop at zero (ArenaTeam::FinishGame, ModifyPersonalRating).</summary>
        internal static int Apply(int rating, int change) => rating + change < 0 ? 0 : rating + change;

        /// <summary>
        /// ArenaTeam::GetPoints for season 6 and later: a flat 344 up to 1500, a logistic curve
        /// above it, then 76% for 2v2 and 88% for 3v3. The rating used is the team's, unless the
        /// player's own personal rating trails it by more than 150 -- a carried player earns what
        /// they are worth, not what the team is.
        ///
        /// Brackets WoW never had (1v1) are paid like 2v2, the cheapest one there is.
        /// </summary>
        internal static int Points(int teamSize, int teamRating, int personalRating, float rate)
        {
            int rating = personalRating + 150 < teamRating ? personalRating : teamRating;
            float points = rating <= 1500
                ? 344f
                : 1511.26f / (1.0f + 1639.28f * (float)Math.Exp(-0.00412f * rating));

            if (teamSize <= 2) points *= 0.76f;
            else if (teamSize == 3) points *= 0.88f;

            points *= rate;
            return (int)points;
        }

        /// <summary>
        /// "To get points, a player has to participate in at least 30% of the matches":
        /// ceil(weekGames x 0.3). Done in integers so it cannot round differently on another
        /// machine; for every week length it equals TrinityCore's float version.
        /// </summary>
        internal static int RequiredGames(int teamWeekGames, int participationPercent)
        {
            if (teamWeekGames <= 0) return 0;
            int percent = Math.Max(0, participationPercent);
            return (teamWeekGames * percent + 99) / 100;
        }

        /// <summary>ArenaTeam::AddMember: a new member starts at the configured personal rating,
        /// or at 1000 when the team is already at 1000 or more.</summary>
        internal static int StartingPersonalRating(int teamRating, int configuredStart) =>
            configuredStart > 0 ? configuredStart : teamRating >= 1000 ? 1000 : 0;

        /// <summary>Signatures a charter needs before it can be turned in: team size minus one
        /// (PetitionsHandler), unless the server set its own number.</summary>
        internal static int RequiredSignatures(int teamSize, int configured) =>
            configured >= 0 ? configured : Math.Max(0, teamSize - 1);

        /// <summary>A team holds up to twice its bracket (ArenaTeam::AddMember); a 1v1 team is
        /// the player alone.</summary>
        internal static int MaxMembers(int teamSize) => teamSize <= 1 ? 1 : teamSize * 2;

        /// <summary>
        /// The rating an item requirement is checked against (Player::GetMaxPersonalArenaRatingRequirement):
        /// for each of the player's teams in the given bracket or a larger one, the lower of their
        /// personal rating and the team's; the best of those counts.
        /// </summary>
        internal static int PurchaseRating(int personalRating, int teamRating) =>
            Math.Min(personalRating, teamRating);

        internal static string BracketName(int size) => $"{size}v{size}";
    }
}
