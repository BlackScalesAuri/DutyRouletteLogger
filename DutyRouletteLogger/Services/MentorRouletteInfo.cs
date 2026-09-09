namespace DutyRouletteLogger.Services;

/// <summary>
/// Shared identifiers for the "Duty Roulette: Mentor" queue and its associated achievement
/// chain, used by both <see cref="InstanceTrackerService"/> (chat announcement on completion)
/// and <see cref="Windows.DutyRouletteLoggerUI"/> (the Run History progress bar), so both stay
/// in sync with the same real game data instead of duplicating magic numbers.
/// </summary>
public static class MentorRouletteInfo
{
    /// <summary>
    /// Achievement sheet row 1604, "I Hope Mentor Will Notice Me VI" — the highest tier of the
    /// Duty Roulette: Mentor completion chain the game actually tracks
    /// (10 / 50 / 200 / 500 / 1,000 / 2,000). There is no official 3,000 tier.
    /// </summary>
    public const uint AchievementId = 1604;

    /// <summary>Used only if the game ever reports a max of 0 before the real value loads.</summary>
    public const uint FallbackMax = 2000;

    /// <summary>ContentRoulette sheet row 9's Name — the exact QueueType label recorded for a Mentor roulette run.</summary>
    public const string QueueTypeLabel = "Duty Roulette: Mentor";
}
