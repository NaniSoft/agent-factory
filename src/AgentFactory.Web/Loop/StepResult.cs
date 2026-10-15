namespace AgentFactory.Loop;

/// <summary>
/// What one step of the machine made of itself. A step applies at most one transition, so
/// the question is whether it applied one and — when it could not — what it has to say
/// about that.
/// </summary>
/// <remarks>
/// The refusal exists because the loop is the component that knows a merge did not land,
/// and a reviewer who is told nothing concludes their click was lost. The board is the
/// only surface a human has, so the loop's answer travels back through the step and the
/// page renders it where the reviewer is looking, rather than the page working it out for
/// itself — which would be the page deciding policy the loop owns.
/// </remarks>
public readonly record struct StepResult(bool Applied, string? Refusal)
{
    /// <summary>
    /// A work item changed lane, or there was nothing for the machine to do. Nothing to
    /// say in either case, so nothing is said.
    /// </summary>
    public static StepResult Moved => new(Applied: true, Refusal: null);

    /// <summary>Nothing moved and there is nothing to say about why.</summary>
    public static StepResult Idle => new(Applied: false, Refusal: null);

    /// <summary>
    /// Nothing moved, and this is the answer: the reviewer decided, the decision is on the
    /// record, and the work item is not where the decision would have put it.
    /// </summary>
    public static StepResult Refused(string reason) => new(Applied: false, Refusal: reason);
}
