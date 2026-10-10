namespace AgentFactory.Api;

using System.Text.Json.Serialization;

/// <summary>
/// What a reviewer posts to decide about a work item: which of the three decisions,
/// and the words they wrote. The slug is the wire format, exactly as the board's own
/// buttons post it, and the feedback is the reviewer's reasons kept whole — the next
/// round's brief when the decision is a request for changes.
/// </summary>
/// <remarks>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather
/// than left to a naming policy, because the JSON is a contract with a separate app:
/// a web default that quietly changed casing would break the renderer without failing
/// a build. They are camelCase so the TypeScript view model and these records agree by
/// name.
/// </remarks>
public sealed record DecisionRequest(
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("feedback")] string? Feedback);

/// <summary>
/// What a decision came to, as the renderer reads it. <c>applied</c> is the loop's own
/// answer to the decision — whether it carried it out — and <c>refusal</c> is what the
/// factory said when it did not, in its own words: a decision that is not one of the
/// three, a work item outside <see cref="WorkItems.Swimlanes.Decidable"/>, a request for
/// changes with no reasons, or an approval the merge could not carry out. A refusal is
/// reported rather than swallowed, and it is rendered where the reviewer acted.
/// </summary>
/// <param name="Applied">
/// Whether the loop applied a transition for the decision. An approval that did not
/// merge is not applied, and the work item is where the loop parked it rather than
/// where the decision would have put it.
/// </param>
/// <param name="Refusal">The factory's own words for why nothing was applied, or null
/// when the decision was carried out.</param>
/// <param name="ResultingLane">
/// The lane the work item is in after the attempt, read from the store rather than
/// worked out here — the loop is what moves a work item. Empty only when the id named
/// no work item at all.
/// </param>
public sealed record DecisionResult(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("refusal")] string? Refusal,
    [property: JsonPropertyName("resultingLane")] string ResultingLane);
