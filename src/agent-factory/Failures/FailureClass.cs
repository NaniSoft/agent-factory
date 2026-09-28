namespace AgentFactory.Failures;

/// <summary>
/// Whether a failure is worth trying again. These are DESIGN.md's own two words: "a
/// build that fails its tests is not a transient failure, and retrying it is an
/// expensive way to hide the result."
/// </summary>
/// <remarks>
/// The pair is exhaustive and it is not a spectrum. A failure is either worth another
/// attempt or it is not, and the two differ by whether a second attempt could produce a
/// different answer — not by how badly the first one went.
/// </remarks>
public enum FailureClass
{
    /// <summary>
    /// The attempt failed to happen rather than happening and failing: an image that would
    /// not pull, a container that would not start, a daemon that was not answering, an API
    /// that returned 503. A second attempt is a fair question, and the retry policy is how
    /// it gets asked.
    /// </summary>
    Transient,

    /// <summary>
    /// The attempt happened and the answer was no. Retrying it would spend a container to
    /// be told the same thing, and — when the answer is a build whose own tests failed —
    /// would hide a result behind a delay. Escalated directly, with no retry at all.
    /// </summary>
    Permanent,
}

/// <summary>
/// Reading a classification off a failure. The whole of the factory's retry policy is a
/// comparison against this, and the comparison is made against what the failing component
/// said about itself rather than against anything guessed from its message.
/// </summary>
public static class Failures
{
    /// <summary>
    /// What class of failure this is, as the failing component declared it. A failure that
    /// has not declared itself is <see cref="FailureClass.Permanent"/>.
    /// </summary>
    /// <remarks>
    /// That default is the load-bearing half of this method and it is deliberately
    /// conservative. An unclassified failure is not retried, because the alternative is a
    /// retry nobody classified — and an unattended, unbounded, unclassified retry of the
    /// one operation the repository cannot take back is precisely what parking a failed
    /// merge in Escalated exists to prevent. When a real adapter lands (#10) it declares
    /// its own failures and this reads them; until then every GitHub refusal is permanent,
    /// which is the honest reading of "we do not know why this failed".
    /// </remarks>
    public static FailureClass Classify(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure is FactoryFailure declared ? declared.Class : FailureClass.Permanent;
    }
}
