namespace AgentFactory.Failures;

/// <summary>
/// A failure that says what class of failure it is. Classification is a property of
/// <em>this type</em> and not a value to be parsed out of a message, which is what keeps
/// "transient" from becoming a guess: a component that observed a failure decides at the
/// point it observed it, by throwing the kind of failure it means, and everything above it
/// reads the kind rather than reading a string.
/// </summary>
/// <remarks>
/// A caller that is handed some other exception has not been told anything, and
/// <see cref="Failures.Classify"/> reads that as permanent. The two subclasses exist so
/// that the throwing site reads as the judgement it is: <c>throw new TransientFailure(...)</c>
/// is a decision, and there is nowhere in the factory where a failure becomes transient
/// by having a word in it.
/// </remarks>
public abstract class FactoryFailure : Exception
{
    protected FactoryFailure(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>Whether another attempt is worth making, by the component that failed.</summary>
    public abstract FailureClass Class { get; }
}

/// <summary>
/// A failure that was the attempt failing to happen: an image that would not pull, a
/// container that would not start, a daemon that was not answering, an API that was not
/// reachable. Worth another attempt, up to the factory's retry ceiling and no further.
/// </summary>
public sealed class TransientFailure : FactoryFailure
{
    public TransientFailure(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public override FailureClass Class => FailureClass.Transient;
}

/// <summary>
/// A failure that was the attempt happening and the answer being no. Retrying it would
/// spend a container to be told the same thing, so it is escalated without a retry.
/// </summary>
/// <remarks>
/// A missing Docker CLI is this, and so is a project file that is not being served: both
/// are facts about the machine this process is running on, and neither of them changes
/// because the factory asked again in ten seconds.
/// </remarks>
public sealed class PermanentFailure : FactoryFailure
{
    public PermanentFailure(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public override FailureClass Class => FailureClass.Permanent;
}
