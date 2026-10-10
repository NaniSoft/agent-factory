namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Loop;
using AgentFactory.Pages;
using AgentFactory.Polling;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using AgentFactory.Workspaces;

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
    [property: JsonPropertyName("rejections")] IReadOnlyList<RejectionView> Rejections,
    [property: JsonPropertyName("intake")] IntakeView Intake)
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
        FactoryOptions options,
        Poller poller,
        ReviewWorkspaces workspaces)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(workspaces);

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
                    .Select(workItem => CardView.Of(workItem, store, workspaces))]))
            .ToList();

        return new BoardView(
            new BudgetView(loop.RoundsInFlight, FactoryConstants.ContainerBudget),
            options.AutoMerge,
            lanes,
            [.. projects.Projects.Select(ProjectView.Of)],
            [.. projects.Rejections.Select(RejectionView.Of)],
            IntakeView.Of(poller));
    }
}

/// <summary>
/// What intake says about the whole factory and about each project it serves, serialised
/// from <see cref="HowToReadIntake"/> — the same judgement the board renders above its
/// lanes. An empty Backlog is three different facts, and this is the section that
/// tells them apart: never polled, polled (with what it found), and failing (with the
/// classification and when the project will next be asked).
/// </summary>
/// <remarks>
/// It is rendered whenever the factory serves a project, healthy or not, because a section
/// that only appears when something is wrong is a section whose absence carries no
/// information. And it is not narrowed by the project filter: a filter narrows what a
/// reviewer is looking at, and a fault in the machine is not a view of it.
/// </remarks>
/// <param name="Status">
/// The worst state of any project, from <see cref="HowToReadIntake.WorstOf"/>, so a renderer
/// can say "something here is not working" without reading every row.
/// </param>
/// <param name="Summary">
/// The one line above the rows, from <see cref="HowToReadIntake.Summarise"/>, which says
/// out loud that an empty Backlog does not mean there is nothing to do whenever it does not.
/// </param>
/// <param name="Projects">How many projects the factory serves.</param>
/// <param name="Polled">How many of them were read successfully.</param>
/// <param name="NeverPolled">How many the rotation has not reached.</param>
/// <param name="Failing">How many could not be read.</param>
/// <param name="Rows">One row per served project, in the poller's rotation order.</param>
public sealed record IntakeView(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("projects")] int Projects,
    [property: JsonPropertyName("polled")] int Polled,
    [property: JsonPropertyName("neverPolled")] int NeverPolled,
    [property: JsonPropertyName("failing")] int Failing,
    [property: JsonPropertyName("rows")] IReadOnlyList<IntakeRowView> Rows)
{
    /// <summary>
    /// The whole of intake as the board shows it, read from the poller's own account and
    /// re-decided nowhere.
    /// </summary>
    public static IntakeView Of(Poller poller)
    {
        ArgumentNullException.ThrowIfNull(poller);

        var rows = HowToReadIntake.Each(poller.Intake);

        return new IntakeView(
            HowToReadIntake.Slug(HowToReadIntake.WorstOf(rows)),
            HowToReadIntake.Summarise(rows),
            rows.Count,
            rows.Count(row => row.Status == IntakeStatus.Polled),
            rows.Count(row => row.Status == IntakeStatus.NeverPolled),
            rows.Count(row => row.Status == IntakeStatus.Failing),
            [.. rows.Select(IntakeRowView.Of)]);
    }
}

/// <summary>
/// One project's intake as the renderer draws it: the state, what was found or what was
/// refused, and when this project will be read again — which is a different answer for each
/// of the three states. Every field is the factory's own judgement, so the renderer draws
/// the row and decides nothing.
/// </summary>
/// <param name="Project">The project's name, as its project file spells it.</param>
/// <param name="RepoUrl">The repository, so the row says which one it is about.</param>
/// <param name="Status">The state's slug, from <see cref="HowToReadIntake.Slug"/>.</param>
/// <param name="OpenIssues">
/// How many open issues the last successful read found, and null for the two states where
/// there has not been one. Null rather than zero, because zero is a real answer.
/// </param>
/// <param name="Failure">The failure's classification, or null when the read succeeded.</param>
/// <param name="Because">What the failed turn said, in its own words.</param>
/// <param name="AtUtc">When the last turn finished, either way, as ISO-8601 or null.</param>
/// <param name="Failures">How many times in a row this project has failed.</param>
/// <param name="AgainAfterUtc">When this project will next be read, as ISO-8601, or null.</param>
/// <param name="Again">
/// Whether this project will be read again and when: <c>never</c> for a permanent failure,
/// a moment for a transient one, and <c>next-pass</c> for a project that is fine.
/// </param>
/// <param name="Says">The row's sentence, in the reviewer's words.</param>
public sealed record IntakeRowView(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("repoUrl")] string RepoUrl,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("openIssues")] int? OpenIssues,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("because")] string? Because,
    [property: JsonPropertyName("atUtc")] string? AtUtc,
    [property: JsonPropertyName("failures")] int Failures,
    [property: JsonPropertyName("againAfterUtc")] string? AgainAfterUtc,
    [property: JsonPropertyName("again")] string Again,
    [property: JsonPropertyName("says")] string Says)
{
    /// <summary>One project's intake, read the way the board's own intake row reads it.</summary>
    public static IntakeRowView Of(IntakeOnTheBoard row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new IntakeRowView(
            row.Project,
            row.RepoUrl,
            HowToReadIntake.Slug(row.Status),
            row.OpenIssues,
            row.Failure?.ToString(),
            row.Because,
            row.AtUtc?.ToString("O"),
            row.Failures,
            row.AgainAfterUtc?.ToString("O"),
            row.Again,
            row.Says);
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
/// <param name="Workspace">
/// The review workspace open for this work item, from
/// <see cref="ReviewWorkspaces.ViewFor"/>, or null when none has been asked for or all
/// have been taken away. It travels on the card so a board read after a workspace was
/// opened shows the link and the time left without the renderer holding state of its own.
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
    [property: JsonPropertyName("decisions")] IReadOnlyList<string> Decisions,
    [property: JsonPropertyName("workspace")] WorkspaceView? Workspace)
{
    /// <summary>One work item and its record, read the way the board's own card reads them.</summary>
    public static CardView Of(WorkItem workItem, IWorkItemStore store, ReviewWorkspaces workspaces)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(workspaces);

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
            [.. WorkItems.Decisions.OfferedIn(workItem.Swimlane).Select(WorkItems.Decisions.Slug)],
            workspaces.ViewFor(workItem.Id) is { } view ? WorkspaceView.Of(view) : null);
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
