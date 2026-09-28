namespace AgentFactory.Containers;

/// <summary>
/// A Docker command that had to succeed and did not. This is a round that never
/// happened — the container was never created, or never started, or its log could not
/// be read — as distinct from a round that ran and failed, which comes back as a result
/// with an exit code in it.
/// </summary>
/// <remarks>
/// Telling those two apart is not this component's judgement to make: which of them is
/// worth another attempt, and how many, is the retry ticket's business (#7). What is
/// decided here is only that a container that broke is not silently reported as a round
/// that failed, because the loop treats a round that failed as a result a reviewer can
/// look at.
/// </remarks>
public sealed class WorkerContainerException : Exception
{
    public WorkerContainerException(string message)
        : base(message)
    {
    }

    public WorkerContainerException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
