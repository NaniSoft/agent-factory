namespace AgentFactory.UnitTests.Boundary;

using AgentFactory.Application.GitHub;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;

/// <summary>
/// The hand-built collaborators a unit test needs, and the reason there are only two of them:
/// <see cref="MergePolicy"/> is the one component in `Application` whose rules turn on what a
/// repository said, so it is the one place a fake is worth having. Everything else under test here
/// is a pure function.
/// </summary>
/// <remarks>
/// These are substitutes for the two seams the policy declares — <see cref="IWorkItemStore"/> and
/// <see cref="IMerger"/> — and for nothing else. There is no HTTP, no git, no SQLite and no
/// service provider anywhere below: a test that wanted one would not compile, because this project
/// does not reference the projects that contain them.
/// </remarks>

/// <summary>A work item a merge can ship, with every field a test cares about spelled out.</summary>
internal static class AWorkItem
{
    /// <summary>One moment, shared, so a record built here is comparable and nothing reads a clock.</summary>
    public static readonly DateTimeOffset At = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static WorkItem For(
        int issueNumber = 42,
        string title = "The poller stops when a project is paced",
        Swimlane swimlane = Swimlane.Review,
        int roundCount = 1,
        int? routingIssue = null,
        int mergeAttempts = 0,
        WorkItemKind kind = WorkItemKind.Unrouted) =>
        new(
            Guid.NewGuid(),
            "nexus",
            "https://github.com/NaniSoft/nexus",
            issueNumber,
            title,
            "words",
            "main",
            swimlane,
            roundCount,
            At,
            At,
            swimlane == Swimlane.Review ? At : null,
            mergeAttempts,
            Kind: kind,
            RoutingIssue: routingIssue);
}

/// <summary>
/// The two reads <see cref="MergePolicy"/> makes of the store — the work item, re-read by id, and
/// its rounds — and the two a board card makes of it. Everything else the port declares is
/// unreachable from the rules under test and throws rather than quietly answering zero.
/// </summary>
internal sealed class FakeWorkItemStore(WorkItem? current = null) : IWorkItemStore
{
    private readonly List<RoundResultRecord> _rounds = [];
    private readonly List<DecisionRecord> _decisions = [];

    /// <summary>
    /// The record the store hands back from <see cref="Get"/>, which is the whole of what lets a
    /// test prove the policy reads the store's copy rather than the caller's snapshot.
    /// </summary>
    public WorkItem? Current { get; set; } = current;

    public FakeWorkItemStore Having(WorkItem workItem)
    {
        Current = workItem;
        return this;
    }

    public FakeWorkItemStore WithRound(RoundResultRecord round)
    {
        _rounds.Add(round);
        return this;
    }

    public FakeWorkItemStore WithDecision(DecisionRecord decision)
    {
        _decisions.Add(decision);
        return this;
    }

    public WorkItem? Get(Guid id) => Current;

    public IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId) => _rounds;

    public IReadOnlyList<DecisionRecord> Decisions(Guid workItemId) => _decisions;

    public WorkItemRecord Intake(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string issueBody,
        string baseBranch) => throw new NotSupportedException("the merge policy does not intake");

    public IReadOnlyList<WorkItem> List() => throw new NotSupportedException("the merge policy does not list");

    public void Move(Guid id, Swimlane swimlane) => throw new NotSupportedException("the merge policy does not move a work item");

    public void RecordRoute(Guid workItemId, WorkItemKind kind, string reason, int? routingIssue = null) =>
        throw new NotSupportedException("the merge policy does not record a route");

    public void RecordHandBack(Guid workItemId) => throw new NotSupportedException("the merge policy does not hand back");

    public RoundResultRecord RecordRound(
        Guid workItemId,
        RoundOutcome outcome,
        string? resultPayload,
        string? agentNote,
        DateTimeOffset startedUtc,
        int attempts = 1,
        FailureClass? failure = null,
        string? log = null,
        HostDiff? diff = null) => throw new NotSupportedException("the merge policy does not record a round");

    public void RecordMergeFailure(Guid workItemId, FailureClass failure, DateTimeOffset? retryAfterUtc) =>
        throw new NotSupportedException("the merge policy does not record a merge failure");

    public DecisionRecord RecordDecision(Guid workItemId, Decision decision, string? feedback) =>
        throw new NotSupportedException("the merge policy does not record a decision");

    public void ApplyDecision(Guid workItemId, int sequence, Swimlane swimlane) =>
        throw new NotSupportedException("the merge policy does not apply a decision");

    public void RecordPendingRound(PendingRound pending) =>
        throw new NotSupportedException("the merge policy does not record a pending round");

    public void ClearPendingRound(Guid workItemId) =>
        throw new NotSupportedException("the merge policy does not clear a pending round");

    public IReadOnlyList<PendingRound> ListPendingRounds() =>
        throw new NotSupportedException("the merge policy does not reconcile");

    public void SetPendingRoundRetryAfter(Guid workItemId, DateTimeOffset retryAfterUtc) =>
        throw new NotSupportedException("the merge policy does not pace a pending round");
}

/// <summary>
/// A remote that answers what a test tells it to and records every question it was asked. Each
/// answer is a delegate so a test can make the same question answer differently on its second
/// asking — which is exactly what the push-refusal cases turn on.
/// </summary>
internal sealed class FakeMerger : IMerger
{
    /// <summary>Every method called, in order, by name, so a test can assert what was <em>not</em> reached.</summary>
    public List<string> Asked { get; } = [];

    public List<MergePlan> Merged { get; } = [];

    public List<int> ClosedIssues { get; } = [];

    public List<(MergePlan Plan, int Issue, string Because)> Closes { get; } = [];

    /// <summary>
    /// What the local "resolve the project and its credential" step does. It returns normally by
    /// default; a test sets it to throw to model a project that is not served or a credential that
    /// is not there.
    /// </summary>
    public Action<MergePlan>? OnResolve { get; set; }

    public Func<string, TreeAtHead> TreeAt { get; set; } =
        _ => new TreeAtHead(new string('c', 40), string.Empty);

    public Func<MergePlan, PullRequestOnRemote?> PullRequest { get; set; } = _ => null;

    public Func<MergePlan, string?> RemoteBranch { get; set; } = _ => null;

    public Func<MergePlan, bool> Reachable { get; set; } = _ => true;

    public Func<MergePlan, PushAttempt> Push { get; set; } = _ => new PushAttempt(true, string.Empty);

    public Func<MergePlan, PullRequestOnRemote> Open { get; set; } = plan => new PullRequestOnRemote(
        7,
        "https://github.com/NaniSoft/nexus/pull/7",
        "open",
        plan.Commit,
        plan.WorkItem.BaseBranch,
        Merged: false,
        "clean");

    public Func<MergePlan, int, bool> IssueOpen { get; set; } = (_, _) => true;

    public Action<MergePlan, int, string> Close { get; set; } = (_, _, _) => { };

    public bool Was(string method) => Asked.Contains(method);

    public Task ResolveAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(ResolveAsync));
        OnResolve?.Invoke(plan);
        return Task.CompletedTask;
    }

    public Task<TreeAtHead> ReadTheTreeAsync(string tree, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(ReadTheTreeAsync));
        return Task.FromResult(TreeAt(tree));
    }

    public Task<PullRequestOnRemote?> FindThePullRequestAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(FindThePullRequestAsync));
        return Task.FromResult(PullRequest(plan));
    }

    public Task<string?> BranchOnTheRemoteAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(BranchOnTheRemoteAsync));
        return Task.FromResult(RemoteBranch(plan));
    }

    public Task<bool> TheRemoteIsReachableAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(TheRemoteIsReachableAsync));
        return Task.FromResult(Reachable(plan));
    }

    public Task<PushAttempt> PushTheCommitAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(PushTheCommitAsync));
        return Task.FromResult(Push(plan));
    }

    public Task<PullRequestOnRemote> OpenThePullRequestAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(OpenThePullRequestAsync));
        return Task.FromResult(Open(plan));
    }

    public Task<PullRequestOnRemote?> ReadThePullRequestAsync(
        MergePlan plan,
        int number,
        CancellationToken cancellationToken)
    {
        Asked.Add(nameof(ReadThePullRequestAsync));
        return Task.FromResult<PullRequestOnRemote?>(null);
    }

    public Task MergeThePullRequestAsync(
        MergePlan plan,
        PullRequestOnRemote pull,
        CancellationToken cancellationToken)
    {
        Asked.Add(nameof(MergeThePullRequestAsync));
        Merged.Add(plan);
        return Task.CompletedTask;
    }

    public Task<bool> IsTheIssueOpenAsync(MergePlan plan, int issueNumber, CancellationToken cancellationToken)
    {
        Asked.Add(nameof(IsTheIssueOpenAsync));
        return Task.FromResult(IssueOpen(plan, issueNumber));
    }

    public Task CloseTheIssueAsync(
        MergePlan plan,
        int issueNumber,
        string because,
        CancellationToken cancellationToken)
    {
        Asked.Add(nameof(CloseTheIssueAsync));
        ClosedIssues.Add(issueNumber);
        Closes.Add((plan, issueNumber, because));
        Close(plan, issueNumber, because);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A directory that looks like a round's lifted tree, and the one thing that has to exist on disk
/// for the policy to accept it: a <c>.git</c> marker.
/// </summary>
/// <remarks>
/// `MergePolicy.TreeFor` asks <see cref="Directory.Exists(string)"/> before it will ship a round,
/// because a round whose tree never came out of its container has nothing to push. That check is a
/// fact about the filesystem and cannot be faked without changing the policy, so a test provides
/// two empty directories. There is no repository, no commit and no git process in it: the commit a
/// merge ships is what the fake's <see cref="FakeMerger.ReadTheTreeAsync"/> answers.
/// </remarks>
internal sealed class AHostTree : IDisposable
{
    public AHostTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "agent-factory-unit-trees", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}

/// <summary>One round's record, with a diff that points at a real (empty) tree on this host.</summary>
internal static class ARound
{
    public static RoundResultRecord Produced(Guid workItemId, string tree, int roundNumber = 1) =>
        new(
            workItemId,
            roundNumber,
            RoundOutcome.Produced,
            ResultPayload: "the round's own record",
            AgentNote: null,
            StartedUtc: AWorkItem.At,
            CompletedUtc: AWorkItem.At,
            Diff: new HostDiff(
                "diff --git a/src/index.js b/src/index.js\n",
                "main",
                tree,
                ContainerBounded: false,
                UnavailableBecause: null));
}
