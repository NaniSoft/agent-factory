namespace AgentFactory.Rounds;

using AgentFactory.Failures;

/// <summary>
/// A child process on this machine, as the round runner and the collector see it: one
/// invocation with its own arguments, working directory and environment, its streams
/// arriving as they are written and its exit code as the answer.
/// </summary>
/// <remarks>
/// This is the whole of what the factory knows about starting programs. It was once a
/// Docker CLI (ADR-0010's `docker create`/`logs`/`cp`), and the verbs went away with the
/// containers: a round is now one <c>git clone</c> and one <c>opencode run</c>, and both
/// go through here, which is what keeps "arguments one at a time, never through a shell"
/// one property of one component rather than a discipline every caller restates.
/// </remarks>
public interface IProcessCli
{
    /// <summary>
    /// Runs the process to completion and returns its exit code with the tail of what it
    /// wrote. Cancellation kills the whole process tree and reaps it before the
    /// <see cref="OperationCanceledException"/> leaves.
    /// </summary>
    /// <exception cref="PermanentFailure">
    /// There is no such executable on this machine's PATH. The binary is either there or
    /// it is not, and a second attempt in ten seconds begins a second identical failure.
    /// </exception>
    Task<ProcessInvocation> InvokeAsync(
        ProcessRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken);
}

/// <summary>One process the factory means to run, stated completely.</summary>
/// <param name="Executable">The program, resolved through <c>PATH</c>.</param>
/// <param name="Arguments">
/// The arguments, one per element. Never joined into a shell string: a branch name, a
/// repository URL or a file path a stranger chose must arrive as an argument and not as
/// syntax.
/// </param>
/// <param name="WorkingDirectory">Where the process runs, when it is not this process's own.</param>
/// <param name="Environment">
/// Variables layered over this process's own environment for the child. The round's brief,
/// model and credential travel this way, which is why the brief is quoted into a variable
/// and never into the argument list.
/// </param>
public sealed record ProcessRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>What one finished process said about itself.</summary>
/// <param name="ExitCode">The process's own exit code. Nothing here interprets it.</param>
/// <param name="Tail">
/// The bounded end of everything the process wrote on either stream, in the order it was
/// written. It is the round's log, and it is a tail because a ninety-minute build's whole
/// output belongs on the wire and in the log, not in a database row.
/// </param>
public sealed record ProcessInvocation(int ExitCode, string Tail);
