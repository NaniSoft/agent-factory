namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Loop;
using AgentFactory.Pages;
using AgentFactory.Projects;
using AgentFactory.WorkItems;

/// <summary>
/// What <c>GET /api/board</c> returns: the factory's own judgement, serialised, and
/// nothing the factory would not say on its board. It is the board the app renders —
/// the container budget and whether silence can merge, the lanes and the work-item
/// cards in them, the projects the factory serves and the files it refused — shaped so
/// the renderer re-decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint that produces this resolves the factory's already-registered singletons
/// and reads them. It holds no policy, calls no seam, and re-decides nothing: the budget
/// is <see cref="Orchestrator.RoundsInFlight"/> against
/// <see cref="FactoryConstants.ContainerBudget"/>, exactly as the board's own header
/// reads it, the mode is <see cref="FactoryOptions.AutoMerge"/>, and every card field is
/// the factory's own judgement — <see cref="Swimlanes.Label"/> for the lane's words,
/// <see cref="Decisions.OfferedIn"/> for the decisions a reviewer may make, and
/// <see cref="HowItEnded.Describe"/> for why a work item ended where it did.
/// </para>
/// <para>
/// The lanes are exactly the ones the board's own markup renders, in the same order:
/// the five swimlanes and then the two parked-and-final lanes, so a card in Escalated or
/// Rejected has a column of its own and the last two columns are the endings.
/// </para>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to a naming policy, because the JSON is a contract with a separate app: a web
/// default that quietly changed casing would break the renderer without failing a build.
/// They are camelCase so the TypeScript view model and these records agree by name.
/// </para>
/// </remarks>
public sealed record BoardView(
    [property: JsonPropertyName("budget")] BudgetView Budget,
    [property: JsonPropertyName("autoMerge")] bool AutoMerge,
    [property: JsonPropertyName("lanes")] IReadOnlyList<LaneView> Lanes,
    [property: JsonPropertyName("projects")] IReadOnlyList<ProjectView> Projects,
    [property: JsonPropertyName("rejections")] IReadOnlyList<RejectionView> Rejections)
{
    /// <summary>
    /// The whole board, read from the factory's own state and judgement.
    /// </summary>
    /// <remarks>
    /// This is the one place the endpoint's four reads are put together, and it is here
    /// rather than in the route so the shape the renderer sees is a fact about this record
    /// and not about the composition root. It calls no seam, waits for nothing and holds
    /// no state: the store, the load report, the loop's count and the option are the same
    /// singletons the board page reads.
    /// </remarks>
    public static BoardView Of(
        IWorkItemStore store,
        ProjectLoadReport projects,
        Orchestrator loop,
        FactoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(options);

        var workItems = store.List();

        // The five swimlanes and then the two endings, in the factory's own order. The
        // cards are read from the store's own record and its rounds and decisions, so the
        // ending a card carries is the factory's answer and not the renderer's.
        var lanes = Swimlanes.All
            .Concat(Swimlanes.ParkedAndFinal)
            .Select(lane => new LaneView(
                lane.ToString(),
                Swimlanes.Label(lane),
                [.. workItems
                    .Where(workItem => workItem.Swimlane == lane)
                    .Select(workItem => CardView.Of(workItem, store))]))
            .ToList();

        return new BoardView(
            new BudgetView(loop.RoundsInFlight, FactoryConstants.ContainerBudget),
            options.AutoMerge,
            lanes,
            [.. projects.Projects.Select(ProjectView.Of)],
            [.. projects.Rejections.Select(RejectionView.Of)]);
    }
}

/// <summary>
/// One lane, as a column the renderer draws: its name, the words a reviewer reads, and the
/// cards standing in it.
/// </summary>
/// <param name="Lane">
/// The lane's own name, the same slug the board's markup carries and the value a card's
/// <see cref="CardView.Lane"/> holds, so a column and its cards agree by name rather than
/// by position.
/// </param>
/// <param name="Label">The lane's words, from <see cref="Swimlanes.Label"/>, which is the
/// factory's one place a lane is spelled for a reader.</param>
/// <param name="Cards">The work items in this lane, in the store's own order.</param>
public sealed record LaneView(
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("cards")] IReadOnlyList<CardView> Cards);

/// <summary>
/// One work item as a card: what it is, whose it is, where it sits and what a reviewer may
/// still do about it. Every field is the factory's own judgement, so the renderer draws a
/// card and decides nothing.
/// </summary>
/// <param name="Id">The work item's id, which is the card's key and what a decision posts.</param>
/// <param name="Project">The project the work item belongs to, which the card carries as a tag.</param>
/// <param name="IssueNumber">The issue the work item answers.</param>
/// <param name="Title">The issue's own title, unchanged.</param>
/// <param name="Lane">The lane's slug, matching the column it stands in.</param>
/// <param name="LaneLabel">The lane's words, matching the column's label.</param>
/// <param name="RoundCount">How many rounds the work item has run.</param>
/// <param name="RoundCeiling">
/// The ceiling the count is read against, from <see cref="FactoryConstants.RoundCeiling"/>,
/// so the card's "round N of 3" is the factory's arithmetic rather than the renderer's.
/// </param>
/// <param name="Ending">
/// Why the work item ended where it did, from <see cref="HowItEnded.Describe"/>, or empty
/// for a lane that is a stage rather than an ending.
/// </param>
/// <param name="Decisions">
/// The decisions a reviewer may still make about this work item, from
/// <see cref="Decisions.OfferedIn"/>, as the slugs a button posts. Empty for a lane a
/// reviewer cannot act on. The decision <em>form</em> is a later ticket's; the offered set
/// is the factory's judgement and belongs on the card either way.
/// </param>
public sealed record CardView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("issueNumber")] int IssueNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("laneLabel")] string LaneLabel,
    [property: JsonPropertyName("roundCount")] int RoundCount,
    [property: JsonPropertyName("roundCeiling")] int RoundCeiling,
    [property: JsonPropertyName("ending")] string Ending,
    [property: JsonPropertyName("decisions")] IReadOnlyList<string> Decisions)
{
    /// <summary>One work item and its record, read the way the board's own card reads them.</summary>
    public static CardView Of(WorkItem workItem, IWorkItemStore store)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(store);

        return new CardView(
            workItem.Id.ToString(),
            workItem.Project,
            workItem.IssueNumber,
            workItem.IssueTitle,
            workItem.Swimlane.ToString(),
            Swimlanes.Label(workItem.Swimlane),
            workItem.RoundCount,
            FactoryConstants.RoundCeiling,
            HowItEnded.Describe(workItem, store.Rounds(workItem.Id), store.Decisions(workItem.Id)),
            [.. WorkItems.Decisions.OfferedIn(workItem.Swimlane).Select(WorkItems.Decisions.Slug)]);
    }
}

/// <summary>
/// One project the factory serves, as the renderer offers it in the filter. Credentials are
/// names and never values, exactly as the project record holds them.
/// </summary>
public sealed record ProjectView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("repoUrl")] string RepoUrl,
    [property: JsonPropertyName("workerImage")] string WorkerImage,
    [property: JsonPropertyName("llmModel")] string LlmModel,
    [property: JsonPropertyName("githubKeyName")] string GitHubKeyName,
    [property: JsonPropertyName("llmKeyName")] string LlmKeyName,
    [property: JsonPropertyName("sourceFile")] string SourceFile)
{
    public static ProjectView Of(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new ProjectView(
            project.Name,
            project.RepoUrl,
            project.WorkerImage,
            project.LlmModel,
            project.GitHubKeyName,
            project.LlmKeyName,
            project.SourceFile);
    }
}

/// <summary>
/// One project file the loader refused, and why. A refusal is reported rather than
/// swallowed, and it reaches the renderer beside the projects that were served.
/// </summary>
public sealed record RejectionView(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("message")] string Message)
{
    public static RejectionView Of(ProjectFileRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        return new RejectionView(rejection.FileName, rejection.Reason.ToString(), rejection.Message);
    }
}

/// <summary>
/// The container budget: how many worker containers the factory is inside, out of how
/// many it may run. A bounded machine that says nothing about its bound is
/// indistinguishable from a wedged one, which is why the board has always said it and
/// why it is the first thing the JSON surface carries.
/// </summary>
public sealed record BudgetView(
    [property: JsonPropertyName("inUse")] int InUse,
    [property: JsonPropertyName("of")] int Of);
