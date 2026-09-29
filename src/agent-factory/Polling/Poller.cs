namespace AgentFactory.Polling;

using AgentFactory.Clock;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Observability;
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
    private readonly FactoryMetrics _metrics;

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

    /// <summary>
    /// What each project has failed at, and until when it should be left alone. Keyed by
    /// the project's name because that is what a work item carries, and kept here rather
    /// than in the store because a poll is a read: nothing about it is a fact a reviewer
    /// needs to survive a restart, and the next pass asks again either way.
    /// </summary>
    private readonly Dictionary<string, Backoff> _backoff = new(StringComparer.Ordinal);

    public Poller(
        IWorkItemStore store,
        IGitHub github,
        IClock clock,
        ProjectLoadReport projects,
        ILogger<Poller> logger,
        FactoryMetrics metrics)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _github = github ?? throw new ArgumentNullException(nameof(github));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        ArgumentNullException.ThrowIfNull(projects);

        _rotation = [.. projects.Projects.OrderBy(project => project.SourceFile, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Takes one project's turn at intake, and says whether it took one. A turn is one
    /// repository read and whatever work items that turns into, never more and never
    /// less, so a busy repository cannot spend a whole pass on itself and leave the
    /// rest waiting.
    /// </summary>
    /// <remarks>
    /// A project waiting out a backoff is a turn taken and nothing read, and the step
    /// still says it took one. The rotation moved, which is what the caller is being told,
    /// and the pass is not over until the rotation has come round to the first project
    /// again — so a paced project costs its own turn and nobody else's.
    /// </remarks>
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

        if (TurnIsDue(project))
        {
            await TakeATurnAt(project);
        }
        else
        {
            _logger.LogInformation(
                "{Project} failed {Failures} time(s) in a row and is not due another turn until {Due}, "
                    + "so this pass leaves it alone rather than reading it sooner than that.",
                project.Name,
                _backoff[project.Name].Failures,
                _backoff[project.Name].DueUtc);
        }

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
    /// Whether this project may be read now. It may be, unless it failed recently enough
    /// that the longer of the two waits — the pass interval and its own backoff — has not
    /// elapsed.
    /// </summary>
    /// <remarks>
    /// The pass interval and the backoff compose rather than compete, and the composition
    /// is the point. A backoff can only ever make the wait longer, so the interval is a
    /// floor and the backoff is a ceiling on how often the factory asks — and because the
    /// first few backoffs are shorter than the interval, it is the interval that paces a
    /// project for its first few failures and the backoff only once it has grown past it.
    /// That is the interaction the poll interval and a retry policy have, and it is
    /// asserted rather than assumed.
    /// </remarks>
    /// <remarks>
    /// A project with no wait — one that has never failed, or one whose last failure was
    /// permanent — is always due, and the pass interval is the only thing pacing it. The
    /// comparison is written so that a null wait is "no wait" rather than "never", which is
    /// the difference between a repository that is gone being read once a minute and being
    /// read once and then never again.
    /// </remarks>
    private bool TurnIsDue(Project project) =>
        !_backoff.TryGetValue(project.Name, out var waiting)
        || waiting.DueUtc is not { } due
        || _clock.UtcNow >= due;
    /// <summary>
    /// One project's turn: read the open issues, resolve the branch they are built
    /// against, and record the ones the store has not already got. A turn that fails is
    /// contained to itself, and what it failed as decides how long this project is left
    /// alone afterwards.
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
            // rotation carries on, and this project's next turn is paced by the failure.
            //
            // The classification is the exception's own, read here and nowhere else. A
            // transient failure doubles the wait before the next read, so a repository
            // that was down for an afternoon is asked less and less often rather than
            // every minute; a permanent one does not, because a repository that is gone
            // or a credential that cannot read it will say exactly the same thing in ten
            // seconds and the shorter wait is the pass cadence either way. Neither is
            // escalated, because escalation is a work item's state and a repository that
            // cannot be read has produced no work item to park.
            //
            // A success clears it: the backoff counts consecutive failures, not failures
            // ever, so a project that answers again is not paced for ever on the strength
            // of a failure from this morning.
            Pace(project, failure);
        }
    }

    /// <summary>
    /// Sets how long this project is left alone, or forgets that it was ever failing.
    /// </summary>
    private void Pace(Project project, Exception failure)
    {
        var classification = Failures.Classify(failure);
        var failures = (_backoff.TryGetValue(project.Name, out var previous) ? previous.Failures : 0) + 1;

        // A transient failure is paced: the wait doubles for each failure in a row, so a
        // repository that was down for an afternoon is read less and less often rather
        // than every minute. A permanent one is given no wait of its own, because a
        // repository that is gone or a credential that cannot read it says exactly the
        // same thing in ten seconds, and the pass cadence is already the slowest this
        // factory asks anything — inventing a second schedule there would be the retry
        // policy asking twice in the one place it promised not to.
        //
        // Neither is escalated, because escalation is a work item's state and a repository
        // that cannot be read has produced no work item to park. And neither is a reason to
        // stop serving the project: the wait is a schedule, not a verdict, and a project
        // that answers again is read on the next pass it is due for.
        //
        // A success clears the count — see <see cref="IntakeFrom"/> — because the wait is
        // owed to a run of failures, not to a project's whole life.
        var due = classification == FailureClass.Transient
            ? _clock.UtcNow + FactoryConstants.PollBackoff(failures)
            : (DateTimeOffset?)null;

        _backoff[project.Name] = new Backoff(failures, due);

        _logger.LogWarning(
            failure,
            "Could not poll {Project} at {Repository}: {Reason}. The factory has read that as a {Classification} failure, "
                + "the {Count} in a row, and its next turn is {Due}.",
            project.Name,
            project.RepoUrl,
            failure.Message,
            classification,
            failures,
            due is { } after ? $"not before {after}" : "on the next pass, as any other");
    }

    /// <summary>What a project has failed at, and until when it should be left alone.</summary>
    private sealed record Backoff(int Failures, DateTimeOffset? DueUtc);

    private async Task IntakeFrom(Project project)
    {
        // Indiscriminate, deliberately. Everything the seam returns becomes a candidate,
        // because this repository's own triage labels are this repository's convention
        // and a project's conventions are not the factory's business (ADR-0007).
        var issues = await _github.ListOpenIssuesAsync(project.RepoUrl, CancellationToken.None);

        // The turn was taken and read. Whatever the rest of it does, this project is not
        // failing any more, so its backoff is over — otherwise a repository that answered
        // the first read and then failed the second would keep a wait earned by a
        // different failure.
        _backoff.Remove(project.Name);

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
        // A read that is not bounded is bounded by the seam's own client, which is the
        // merger's and the reader's ticket (#10), and is deliberately not a timer here: a
        // cancellation that fires on a delay is a timer, and this factory has none. It is
        // the same bound the merge call has, for the same reason.
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
                // Counted here and not in the loop: a work item exists from the moment
                // intake makes it, and "issues processed" is a fact about the world arriving
                // rather than about the machine starting on it. Counted on creation rather
                // than on every read, because intake is idempotent (ADR-0007) and a counter
                // that went up on every pass would be counting poll ticks.
                _metrics.IssueProcessed(project.Name);

                // The scope, opened after the record rather than before: the work item's id
                // is what intake has just been given, and it is the id every later record
                // about this work item is found by. So the first record of a work item's
                // life is stamped with the life itself, rather than starting at the moment
                // the loop first picked it up.
                using var trace = _logger.ForWorkItem(
                    recorded.WorkItem.Id, recorded.WorkItem.Project, recorded.WorkItem.IssueNumber);

                _logger.LogInformation(
                    "Took {Project}#{Issue} into Backlog, to be built from {BaseBranch}.",
                    project.Name,
                    issue.Number,
                    baseBranch);
            }
        }
    }
}
