namespace AgentFactory.Infrastructure.Clock;

using AgentFactory.Application.Clock;

/// <summary>
/// The real clock, behind the port <see cref="IClock"/> declared inside. The only one the
/// factory itself ever runs on, and its being on this side of the boundary is the whole
/// of what a port is for: the inside says it needs the time, and the inside has no idea
/// how the time is arrived at.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}