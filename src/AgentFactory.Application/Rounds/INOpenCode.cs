namespace AgentFactory.Application.Rounds;

using AgentFactory.Domain.Rounds;

/// <summary>
/// The orchestrator's interface for running one round. One call, one round, one result.
/// Our own interface, not a package, and it sits above the container runtime
/// so the orchestrator never has a concept of Docker.
/// </summary>
public interface INOpenCode
{
    Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken);
}
