namespace AgentFactory.Api;

using System.Text.Json.Serialization;

/// <summary>
/// What a reviewer's acceptance of a Backlog work item came to, as the renderer reads it.
/// It is the answer to <c>POST /api/work-items/{id}/accept</c>, which mirrors the deleted
/// board's build button: the loop accepts the work item into Frontier, or refuses because
/// it is not waiting in Backlog.
/// </summary>
/// <remarks>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to a naming policy, because the JSON is a contract with a separate app: a web
/// default that quietly changed casing would break the renderer without failing a build.
/// They are camelCase so the TypeScript view model and this record agree by name.
/// </remarks>
/// <param name="Applied">
/// Whether the loop moved the work item out of Backlog into Frontier. False means the work
/// item was not waiting in Backlog (or the id named none), and nothing changed.
/// </param>
/// <param name="Refusal">The factory's own words for why nothing was accepted, or null
/// when it was.</param>
public sealed record AcceptanceResult(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("refusal")] string? Refusal);
