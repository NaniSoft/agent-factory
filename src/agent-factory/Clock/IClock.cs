namespace AgentFactory.Clock;

/// <summary>
/// The system clock. Injectable because the 48-hour auto-merge and the 90-minute
/// round timeout are untestable without it.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real clock. The only one the factory itself ever runs on.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
