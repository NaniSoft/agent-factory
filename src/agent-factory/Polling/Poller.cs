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
    /// What intake has last done with each project it has taken a turn at, keyed by the
    /// project's name. Kept here rather than in the store because a poll is a read:
    /// nothing about it is a fact a reviewer needs to survive a restart, and the next pass
    /// asks again either way.
    /// </summary>
    /// <remarks>
    /// It is both the schedule and the account — one record per project rather than a
    /// schedule here and a separate account there — because two dictionaries free to
    /// disagree are two answers to "why has this project not been read", and the one the
    /// board renders would be whichever one nothing had to reconcile.
    /// </remarks>
    private readonly Dictionary<string, IntakeRecord> _intake = new(StringComparer.Ordinal);

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
    /// A project that is not read is a turn taken and nothing read, and the step still
    /// says it took one. The rotation moved, which is what the caller is being told, and
    /// the pass is not over until the rotation has come round to the first project again —
    /// so a project left alone costs its own turn and nobody else's. A project that failed
    /// permanently is left alone in the same way and for the rest of this process's life:
    /// see <see cref="Pace"/>.
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

        var state = StateOf(project);

        // A project's turn is one of three things, and the difference between the second
        // and the third is a null. `AgainAfterUtc` is the wait a failure earned: it is set
        // for a transient failure, which will be asked again when it has passed, and null
        // for a permanent one, which will not be asked again at all.
        //
        // Reading that null as "no wait" is what #16 found: a permanent failure was read on
        // every pass for ever, with a full warning each time, because the pass cadence is
        // the fastest thing this factory asks anything and a project with no wait of its
        // own was simply read by it. So the null is spelled out here rather than left to be
        // inferred — and a project whose backoff has passed falls through to a read, which
        // is the case the old condition got wrong.
        if (state is { Status: IntakeStatus.Failing, AgainAfterUtc: { } again } && _clock.UtcNow < again)
        {
            // Waiting out a backoff. Said, because a project that is being asked less often
            // is a project's own turn being skipped and a reader of the log is owed that
            // much — and paced this way the record comes at most once a backoff, which is
            // the slowest this factory asks anything.
            using var trace = _logger.ForProject(project.Name);
            _logger.LogInformation(
                "{Project} has failed {Failures} time(s) in a row and is not due another turn until {Due}, "
                    + "so this pass leaves it alone rather than reading it sooner than that.",
                project.Name,
                state.Failures,
                again);
        }
        else if (state is { Status: IntakeStatus.Failing, AgainAfterUtc: null })
        {
            // A permanent failure, and nothing at all. It has already said so, once, in
            // `Pace`, and a line every pass for ever would be exactly the unbounded log
            // that refusing to retry it exists to stop. The board carries the state from
            // here, and the board is what a reviewer reads.
        }
        else
        {
            // Read. Which includes a project whose backoff has passed — a transient failure
            // with nothing owed to it is an ordinary project's turn, not a special case.
            await TakeATurnAt(project);
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

    /// <summary>
    /// What intake has last done with each project the factory serves, in rotation order —
    /// one record per project whether or not the rotation has reached it yet.
    /// </summary>
    /// <remarks>
    /// This is the whole of what the board knows about intake, and it is asked for on
    /// every render rather than pushed: intake's state is intake's own, and a component
    /// that kept a copy of it would be a second thing free to disagree with the poller
    /// about whether a project is failing.
    /// </remarks>
    public IReadOnlyList<IntakeRecord> Intake =>
        [.. _rotation.Select(StateOf)];

    /// <summary>
    /// What is known about one project, which before its turn comes round is nothing at
    /// all. Answered with an explicit "never polled" rather than an absent entry, because
    /// a project with no record is a project the board says nothing about and a reviewer
    /// reads that as a project with nothing open.
    /// </summary>
    private IntakeRecord StateOf(Project project) =>
        _intake.TryGetValue(project.Name, out var state) ? state : IntakeRecord.NeverRead(project);

    private bool PassIsDue() =>
        _passBeganAt is not { } began || _clock.UtcNow - began >= FactoryConstants.PollInterval;

    /// <summary>
    /// One project's turn: read the open issues, resolve the branch they are built
    /// against, and record the ones the store has not already got. A turn that fails is
    /// contained to itself, and what it failed as decides whether this project will be
    /// read again at all.
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
            // rotation carries on, and this project's next turn is decided by the
            // classification of what it failed as.
            //
            // Scoped by project rather than by work item, because there is no work item:
            // this is a record about a repository that could not be read, and it is
            // findable by which project it is about and by nothing else. See
            // `ProjectScope` for why that is a second vocabulary rather than a fifth key
            // in the work item's.
            using var trace = _logger.ForProject(project.Name);

            Pace(project, failure);
        }
    }

    /// <summary>
    /// Records how this project failed, and decides whether it will be read again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A transient failure is paced.</strong> The wait doubles for each failure in
    /// a row, so a repository that was down for an afternoon is read less and less often
    /// rather than every minute, and the pass interval is a floor under it rather than a
    /// competitor: a backoff can only lengthen a wait, never shorten one.
    /// </para>
    /// <para>
    /// <strong>A permanent one is not asked again at all, by this process, ever.</strong>
    /// That is the change #16 asked for and the reason is the classification itself: a
    /// repository that is gone, a credential that cannot read it and a name that is not a
    /// repository will say exactly the same thing in ten seconds, in ten minutes and
    /// tomorrow. The old answer was "no wait of its own", which meant the pass cadence
    /// asked anyway — sixty times an hour, for ever, for a fault no amount of asking
    /// touches. A permanent failure is permanent, so it is read once, it says so once, and
    /// from then on the board is where it lives.
    /// </para>
    /// <para>
    /// The cost is stated rather than hidden: a permanent failure is only cleared by a
    /// restart, because everything a permanent intake failure is about — a project file,
    /// the set being served, an environment variable that was not set when the factory
    /// started — is read at start and does not hot reload. The board says that, so the
    /// operator is told what to do rather than left watching a board that has stopped
    /// asking.
    /// </para>
    /// <para>
    /// A success clears the run of failures — see <see cref="IntakeFrom"/> — because the
    /// wait is owed to consecutive failures and not to a project's whole life.
    /// </para>
    /// </remarks>
    private void Pace(Project project, Exception failure)
    {
        var classification = Failures.Classify(failure);
        var failures = StateOf(project).Failures + 1;
        var again = classification == FailureClass.Transient
            ? _clock.UtcNow + FactoryConstants.PollBackoff(failures)
            : (DateTimeOffset?)null;

        _intake[project.Name] = new IntakeRecord(
            project.Name,
            project.RepoUrl,
            IntakeStatus.Failing,
            OpenIssues: 0,
            Failure: classification,
            Because: failure.Message,
            AtUtc: _clock.UtcNow,
            Failures: failures,
            AgainAfterUtc: again);

        if (classification == FailureClass.Permanent)
        {
            // Once. This is the only record this failure will ever produce, which is the
            // point of refusing to retry it: the log does not become a per-minute account
            // of a fault that will not change, and the board carries it from here.
            _logger.LogWarning(
                failure,
                "Could not poll {Project} at {Repository}: {Reason}. The factory has read that as a {Classification} "
                    + "failure, and it will not read this project again: nothing that has happened here changes on its "
                    + "own. Project files and the environment are read at start, so fixing it and restarting the factory "
                    + "is what asks again.",
                project.Name,
                project.RepoUrl,
                failure.Message,
                classification);

            return;
        }

        _logger.LogWarning(
            failure,
            "Could not poll {Project} at {Repository}: {Reason}. The factory has read that as a {Classification} "
                + "failure, the {Count} in a row, and this project is not read again before {Due}.",
            project.Name,
            project.RepoUrl,
            failure.Message,
            classification,
            failures,
            again);

        // Nothing is escalated here and nothing is parked, and that is a decision rather
        // than an omission: escalation is a work item's state (ADR-0008), and a repository
        // that cannot be read has produced no work item to escalate. The project-level
        // fault is rendered on the board instead — see `HowToReadIntake`.
    }

    private async Task IntakeFrom(Project project)
    {
        // Indiscriminate, deliberately. Everything the seam returns becomes a candidate,
        // because this repository's own triage labels are this repository's convention
        // and a project's conventions are not the factory's business (ADR-0007).
        var issues = await _github.ListOpenIssuesAsync(project.RepoUrl, CancellationToken.None);

        // The turn was taken and read. Whatever the rest of it does, this project is not
        // failing any more, so its wait is over — otherwise a repository that answered the
        // first read and then failed the second would keep a wait earned by a different
        // failure. Recorded rather than merely forgotten, because "polled" is one of the
        // three things the board has to be able to tell a reviewer apart, and forgetting
        // would leave this project indistinguishable from one the rotation has not reached.
        //
        // The count of open issues is recorded with it, so "polled and there is nothing
        // open" is a stated answer rather than an empty lane. That is the whole difference
        // #16 was about.
        _intake[project.Name] = new IntakeRecord(
            project.Name,
            project.RepoUrl,
            IntakeStatus.Polled,
            OpenIssues: issues.Count,
            Failure: null,
            Because: null,
            AtUtc: _clock.UtcNow,
            Failures: 0,
            AgainAfterUtc: null);

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
