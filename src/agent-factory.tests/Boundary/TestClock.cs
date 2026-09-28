namespace AgentFactory.Tests.Boundary;

using AgentFactory.Clock;

/// <summary>
/// A clock the test owns. The 48-hour auto-merge and the 90-minute round timeout are
/// measured against this and nothing else.
/// </summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset? start = null) =>
        UtcNow = start ?? new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
