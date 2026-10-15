namespace AgentFactory.Domain.Rounds;

/// <summary>How a round ended. What the factory does about it is policy, not observation.</summary>
/// <remarks>
/// <para>
/// The three are <em>observations about the round</em>, and the line between them is
/// whether the round's own last command succeeded — not whether the round's <em>work</em>
/// succeeded. Those are different things and the design's own sentence covers only the
/// second: "a round whose own commands failed is still Produced" is right for a failing
/// test and wrong for an agent that was refused before it began, and for a while the two
/// were the same signal (#22).
/// </para>
/// <para>
/// <see cref="Produced"/> therefore means the round's own command returned zero. What the
/// commands <em>inside</em> it returned is data inside the result and never an outcome:
/// a build whose tests failed produced a change, and that is a result a reviewer judges
/// (ADR-0001). <see cref="Failed"/> means the round's own command did not return zero, so
/// there is no finished round to judge — whatever is on disk is what it managed on the way
/// out, and the card says so rather than presenting it as a result.
/// </para>
/// </remarks>
public enum RoundOutcome
{
    /// <summary>
    /// The round ran to completion: its own last command returned zero. What the commands
    /// inside it returned is data in the result, not an outcome.
    /// </summary>
    Produced,

    /// <summary>
    /// The round's own last command did not return zero, or it never came back at all.
    /// Nothing here is a finished result, whatever the commands inside it recorded.
    /// </summary>
    Failed,

    /// <summary>The round went past the round timeout.</summary>
    TimedOut,
}
