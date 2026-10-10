namespace AgentFactory.Api;

using System.Text.Json.Serialization;

/// <summary>
/// What <c>GET /api/board</c> returns: the factory's own judgement, serialised, and
/// nothing the factory would not say on its board. This is the walking skeleton's first
/// view model — deliberately the two facts a reviewer watching the machine needs before
/// anything else, the container budget and whether silence can merge — and it is shaped
/// so the lanes, work-item cards and intake can arrive beside it rather than replace it.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint that produces this resolves the factory's already-registered singletons
/// and reads them. It holds no policy, calls no seam, and re-decides nothing: the budget
/// is <see cref="Loop.Orchestrator.RoundsInFlight"/> against
/// <see cref="FactoryConstants.ContainerBudget"/>, exactly as the board's own header
/// reads it, and the mode is <see cref="FactoryOptions.AutoMerge"/>.
/// </para>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to a naming policy, because the JSON is a contract with a separate app: a web
/// default that quietly changed casing would break the renderer without failing a build.
/// They are camelCase so the TypeScript view model and this record agree by name.
/// </para>
/// </remarks>
public sealed record BoardView(
    [property: JsonPropertyName("budget")] BudgetView Budget,
    [property: JsonPropertyName("autoMerge")] bool AutoMerge);

/// <summary>
/// The container budget: how many worker containers the factory is inside, out of how
/// many it may run. A bounded machine that says nothing about its bound is
/// indistinguishable from a wedged one, which is why the board has always said it and
/// why it is the first thing the JSON surface carries.
/// </summary>
public sealed record BudgetView(
    [property: JsonPropertyName("inUse")] int InUse,
    [property: JsonPropertyName("of")] int Of);
