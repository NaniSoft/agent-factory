namespace AgentFactory.Rounds;

/// <summary>
/// The agent seam with nothing behind it yet. The real implementation drives OpenCode
/// inside a worker container and belongs to the container runtime's ticket (#8); until
/// it lands this is what the process has, and a round asked for here refuses in as many
/// words rather than the process failing to start or a round reporting nothing at all.
/// </summary>
/// <remarks>
/// This is not an implementation and is not meant to be mistaken for one. It is
/// registered only if nothing else has claimed <see cref="INOpenCode"/>, so the fake in
/// the test host wins there, and the real implementation replaces it when it exists. It
/// is deleted rather than left behind when that happens.
/// </remarks>
internal sealed class NOpenCodeNotBuiltYet : INOpenCode
{
    public Task<RoundResult> RunRoundAsync(Round round, CancellationToken cancellationToken) =>
        throw new NotSupportedException(
            $"this factory has no agent yet: a round runs in a worker container, and the "
            + $"container runtime is not built (round {round.Project}#{round.IssueNumber})");
}
