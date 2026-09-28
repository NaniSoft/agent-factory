namespace AgentFactory.Containers;

/// <summary>
/// The Docker CLI as the factory uses it: one call is one invocation, and all that comes
/// back is the exit code and the tail of what it printed. The whole of what it printed
/// arrives on <c>output</c> as it is printed, which is how a round's log reaches the host
/// while the round is still running (ADR-0010).
/// </summary>
/// <remarks>
/// <para>
/// This is the seam beneath the container runtime, not a package. ADR-0010 names
/// <c>docker cp</c> and <c>docker logs</c> as the two ways anything crosses a round's
/// boundary, so the CLI's own verbs are the mechanism rather than an implementation
/// detail of one, and the factory takes no container-runtime package to get them.
/// </para>
/// <para>
/// <c>output</c> is written from both of the command's streams at once, so a receiver has
/// to be safe to call from two threads. That is a real obligation rather than a footnote:
/// the two streams are read by two pumps for exactly the reason a round's log cannot
/// afford to be read one stream at a time, and a receiver that is not thread-safe drops
/// lines from a 90-minute build without saying so.
/// </para>
/// </remarks>
public interface IDockerCli
{
    Task<DockerInvocation> InvokeAsync(
        IReadOnlyList<string> arguments,
        IProgress<string>? output,
        CancellationToken cancellationToken);
}

/// <summary>
/// One invocation's answer. <paramref name="Output"/> is the tail of what the command
/// printed, bounded, and never the whole of it: a round's log is megabytes and arrives
/// on the progress channel instead.
/// </summary>
public sealed record DockerInvocation(int ExitCode, string Output);
