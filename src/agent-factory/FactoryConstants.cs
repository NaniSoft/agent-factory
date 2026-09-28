namespace AgentFactory;

/// <summary>
/// Factory behaviour is code, never configuration. Every project gets identical
/// behaviour, so these live here and nowhere else.
/// </summary>
public static class FactoryConstants
{
    /// <summary>The work item gets at most three rounds.</summary>
    public const int RoundCeiling = 3;

    /// <summary>How often the board refreshes itself.</summary>
    public const int BoardAutoRefreshSeconds = 5;
}
