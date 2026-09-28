namespace AgentFactory.Containers;

using AgentFactory.Failures;

/// <summary>
/// A Docker command that had to succeed and did not. This is a round that never
/// happened — the container was never created, or never started, or its log could not
/// be read — as distinct from a round that ran and failed, which comes back as a result
/// with an exit code in it.
/// </summary>
/// <remarks>
/// <para>
/// The classification is this type's, and it is made here because this is the component
/// that knows what failed: every one of these verbs is a call to a daemon that was either
/// answering or not, so a failure of the call is transient — the image would not pull, the
/// container would not start, the log could not be read — and none of them says anything
/// about the change. Nothing above this can tell those apart from a round that ran and
/// whose own tests failed, which is why the answer is given here and carried up rather
/// than worked out from the message.
/// </para>
/// <para>
/// The verb is carried with it because it is the fact that made the classification: a
/// reader of a log line needs to know whether the round never started or never finished
/// being watched.
/// </para>
/// </remarks>
public sealed class WorkerContainerException : FactoryFailure
{
    public WorkerContainerException(string verb, string reason)
        : base($"docker {verb} failed for a round: {reason}")
    {
        Verb = verb;
    }

    /// <summary>Which command failed: <c>create</c>, <c>start</c> or <c>logs</c>.</summary>
    public string Verb { get; }

    public override FailureClass Class => FailureClass.Transient;
}
