namespace AgentFactory.Application.Clock;

/// <summary>
/// The system clock. Injectable because the 48-hour auto-merge and the 90-minute
/// round timeout are untestable without it.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}