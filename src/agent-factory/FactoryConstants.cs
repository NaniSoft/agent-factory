namespace AgentFactory;

/// <summary>
/// Factory behaviour is code, never configuration. Every project gets identical
/// behaviour, so these live here and nowhere else.
/// </summary>
public static class FactoryConstants
{
    /// <summary>The work item gets at most three rounds.</summary>
    public const int RoundCeiling = 3;

    /// <summary>
    /// How long a work item may sit in Review before the factory merges it rather than
    /// hold the pipeline behind a reviewer who is not watching. A code constant and not
    /// configuration, like everything else here: every project gets the same threshold
    /// and changing it is a deliberate edit in one obvious place.
    /// </summary>
    /// <remarks>
    /// Measured against <c>IClock</c> from the moment the work item entered Review, so a
    /// test drives it by advancing time and nothing waits — and so the threshold is a
    /// comparison the loop makes rather than a timer it starts. It is measured in
    /// wall-clock time and is not interchangeable with the round ceiling: a reviewer who
    /// sends changes back quickly spends rounds, and a reviewer who walks away spends
    /// this (ADR-0001).
    /// </remarks>
    public static readonly TimeSpan FeedbackThreshold = TimeSpan.FromHours(48);

    /// <summary>
    /// The threshold as a reviewer reads it, in one place, so the board, the log and the
    /// loop's refusals cannot end up saying three different things about how long
    /// silence takes to ship a change.
    /// </summary>
    public static string FeedbackThresholdText => $"{FeedbackThreshold.TotalHours:0} hours";

    /// <summary>
    /// A round is bounded at ninety minutes, so a session that never comes back cannot
    /// hold a slot for ever. Measured against the clock, not a timer, so a test drives
    /// it by advancing time rather than by waiting.
    /// </summary>
    public static readonly TimeSpan RoundTimeout = TimeSpan.FromMinutes(90);

    /// <summary>How often the board refreshes itself.</summary>
    public const int BoardAutoRefreshSeconds = 5;

    /// <summary>
    /// How often a project pass runs over the projects the factory serves. Measured
    /// against the clock rather than waited on, so a test drives it by advancing time
    /// instead of sleeping, and so a slow pass cannot become a fast one.
    /// </summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
}
