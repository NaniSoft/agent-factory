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
