namespace AgentFactory.Polling;

using AgentFactory.Clock;
using AgentFactory.GitHub;
using AgentFactory.Projects;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// Intake: the factory reads the open issues of the projects it serves and turns them
/// into work items, one project's turn at a time. Every open issue becomes one; there
/// is no label filter, no assignee filter, no state filter beyond open, and no gate,
/// because which issues are worth building is a judgement a human makes on the board
/// and a filter here would put it in project configuration (ADR-0007).
/// </summary>
/// <remarks>
/// The poller is stepped like the loop is, not driven by a timer: one step is one
/// project's turn, and the interval is a comparison against <see cref="IClock"/>
/// rather than something waited on. Nothing here sleeps, defers or blocks, and a
/// project that cannot be read is contained to its own turn rather than stopping the
/// pass.
/// </remarks>
public sealed class Poller
{
    private readonly IWorkItemStore _store;
    private readonly IGitHub _github;
    private readonly IClock _clock;
    private readonly ILogger<Poller> _logger;

    /// <summary>
    /// The projects in rotation order. Derived from the directory by file name rather
    /// than maintained by anyone, and sorted here rather than taken on trust from the
    /// loader: which order the factory goes round in is this component's own property,
    /// and the file name is the thing the directory gives us.
    /// </summary>
    private readonly IReadOnlyList<Project> _rotation;

    /// <summary>Where in the rotation the next turn falls.</summary>
    private int _next;

    /// <summary>Whether a pass is part-way round the rotation.</summary>
    private bool _inPass;

    /// <summary>When the pass now running began. Null until the first one has.</summary>
    private DateTimeOffset? _passBeganAt;

    public Poller(IWorkItemStore store, IGitHub github, IClock clock, ProjectLoadReport projects, ILogger<Poller> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ArgumentNullException.ThrowIfNull(projects);

        _rotation = [.. projects.Projects.OrderBy(project => project.SourceFile, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Takes one project's turn at intake, and says whether it took one. A turn is one
    /// repository read and whatever work items that turns into, never more and never
    /// less, so a busy repository cannot spend a whole pass on itself and leave the
    /// rest waiting.
    /// </summary>
    public async Task<bool> StepAsync()
    {
        if (_rotation.Count == 0)
        {
            return false;
        }

        if (!_inPass)
        {
            if (!PassIsDue())
            {
                return false;
            }

            // The pass is stamped as it begins rather than as it ends, so the gap between
            // one pass starting and the next is the interval the constant names, whatever
            // a pass costs. Nothing here waits for it: the caller asks again, and until
            // the clock says the interval has passed there is nothing to do.
            _passBeganAt = _clock.UtcNow;
            _inPass = true;
        }

        var project = _rotation[_next];
        _next = (_next + 1) % _rotation.Count;

        await TakeATurnAt(project);

        // The rotation has come back to the first project, so the pass is over and the
        // next one waits for the interval rather than following on behind this one.
        if (_next == 0)
        {
            _inPass = false;
        }

        return true;
    }

    /// <summary>
    /// Takes a turn at every project in rotation order, and stops at the end of the
    /// pass. Whether another pass is due is the clock's business, and the caller's.
    /// </summary>
    public async Task PassAsync()
    {
        while (await StepAsync())
        {
        }
    }

    private bool PassIsDue() =>
        _passBeganAt is not { } began || _clock.UtcNow - began >= FactoryConstants.PollInterval;

    /// <summary>
    /// One project's turn: read the open issues, resolve the branch they are built
    /// against, and record the ones the store has not already got. A turn that fails is
    /// contained to itself.
    /// </summary>
    private async Task TakeATurnAt(Project project)
    {
        try
        {
            await IntakeFrom(project);
        }
        catch (Exception failure)
        {
            // One repository erroring must not take the pass down with it, or a single
            // unreachable project would stop every other project being read — which is
            // the starvation this rotation exists to prevent. The turn is over, the
            // rotation carries on, and the next pass tries this one again: as often as
            // the interval allows. Retrying sooner than that, and telling a transient
            // error from a permanent one, is the retry ticket's business; a refusal here
            // is the right answer either way, and the failure is logged rather than
            // swallowed so it can be answered without reading prose.
            _logger.LogWarning(
                failure,
                "Could not poll {Project} at {Repository} on this pass. Its project takes its next turn on the next pass",
                project.Name,
                project.RepoUrl);
        }
    }

    private async Task IntakeFrom(Project project)
    {
        // Indiscriminate, deliberately. Everything the seam returns becomes a candidate,
        // because this repository's own triage labels are this repository's convention
        // and a project's conventions are not the factory's business (ADR-0007).
        var issues = await _github.ListOpenIssuesAsync(project.RepoUrl, CancellationToken.None);

        // Nothing open means nothing to build against, so the branch is not asked for.
        if (issues.Count == 0)
        {
            return;
        }

        // The base is the repository's default branch, resolved now, and resolved once
        // for the turn rather than once per issue: every issue picked up in this pass is
        // built from the same starting point, and a work item already recorded keeps the
        // base it was created with.
        //
        // A read that is not bounded is bounded by the retry ticket, which is where
        // transient GitHub calls get their timeout. A cancellation token from the host
        // would only fire at shutdown, which is not a bound.
        var baseBranch = await _github.GetDefaultBranchAsync(project.RepoUrl, CancellationToken.None);

        foreach (var issue in issues)
        {
            var recorded = _store.Intake(
                project.Name,
                project.RepoUrl,
                issue.Number,
                issue.Title,
                issue.Body,
                baseBranch);

            if (recorded.Created)
            {
                _logger.LogInformation(
                    "Took {Project}#{Issue} into Backlog, to be built from {BaseBranch}.",
                    project.Name,
                    issue.Number,
                    baseBranch);
            }
        }
    }
}
