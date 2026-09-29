namespace AgentFactory.Tests.Boundary;

using AgentFactory.Failures;
using AgentFactory.GitHub;

/// <summary>
/// GitHub, faked. Intake reads a repository's open issues and its default branch
/// through the one GitHub seam, and the loop merges through it; here both come out of
/// what a test wrote down, and every repository the factory took a turn at is kept in the
/// order it took them, because round-robin is only provable from the order. Nothing here
/// reaches a network.
/// </summary>
public sealed class FakeGitHub : IGitHub
{
    private readonly Dictionary<string, Script> _repositories = new(StringComparer.Ordinal);
    private readonly List<string> _polled = [];
    private readonly Dictionary<string, int> _branchesResolved = new(StringComparer.Ordinal);
    private readonly List<MergeAttempt> _merges = [];

    /// <summary>
    /// What a merge refuses with, and how the factory has read it, or null when merges
    /// land. It starts as a permanent refusal, and that has not changed as the real merger
    /// landed: a fake that merged things on its own would let a test pass on a success the
    /// factory would only have in production, and every test of the loop's merge policy
    /// wants to see the refusal first. A test that wants Done asks for a merge that lands.
    /// </summary>
    /// <remarks>
    /// The wording changed when the real client landed, because the old one said the merger
    /// was not built and that stopped being true. The behaviour is identical and the reason
    /// is the same one: a seam that quietly landed everything would hide the loop's own
    /// decisions behind a fake's good intentions.
    /// </remarks>
    private Refusal? _mergeRefusal = new(
        FailureClass.Permanent,
        NoMergerBehindTheSeam);

    private const string NoMergerBehindTheSeam =
        "nothing is configured to land this merge: the fake GitHub refuses every merge until a test asks for one, "
            + "so that a test which wants Done has to say so";

    /// <summary>Every repository the poller took a turn at, in the order it took them.</summary>
    public IReadOnlyList<string> Polled => _polled;

    /// <summary>Every merge the loop asked for, in the order it asked.</summary>
    public IReadOnlyList<MergeAttempt> Merges => _merges;

    /// <summary>
    /// What the repository is like: the branch a change is built against, and every open
    /// issue on it. Calling this again replaces what is there, which is how a test moves
    /// the repository on between two passes.
    /// </summary>
    public FakeGitHub WithRepository(string repoUrl, string defaultBranch, params OpenIssue[] issues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);

        _repositories[repoUrl] = new Script(defaultBranch, issues, Failing: false, FailureClass: null, Failure: null);
        return this;
    }

    /// <summary>
    /// The repository cannot be read: every call against it throws, the way an API error
    /// or a repository that has gone away does. Transient by default, because the failures
    /// it stands for — a 503, a rate limit, a connection that dropped — are the ones the
    /// retry policy exists for, and a test that wants the other kind says so.
    /// </summary>
    public FakeGitHub Failing(
        string repoUrl,
        string message = "the repository could not be read") =>
        Failing(repoUrl, FailureClass.Transient, message);

    /// <summary>
    /// The repository cannot be read and reading it again will not help: a repository that
    /// has gone away, a credential with nothing to read it with, a name that is not a
    /// repository. The poller reads it once and then does not ask again in this process:
    /// a permanent failure is permanent, and the pass cadence asking sixty times an hour is
    /// how #16's run produced an unbounded log for a fault nothing would fix. It is asked
    /// again after a restart, because a restart is what re-reads a project file and an
    /// environment.
    /// </summary>
    public FakeGitHub Failing(
        string repoUrl,
        FailureClass failure,
        string message = "the repository could not be read")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        _repositories[repoUrl] = new Script("main", [], Failing: true, FailureClass: failure, Failure: message);
        return this;
    }

    /// <summary>How many turns the factory took at this repository.</summary>
    public int TimesPolled(string repoUrl) => _polled.Count(url => url == repoUrl);

    /// <summary>How many times the factory asked for this repository's default branch.</summary>
    public int BranchesResolved(string repoUrl) =>
        _branchesResolved.TryGetValue(repoUrl, out var times) ? times : 0;

    /// <summary>
    /// Merges land from now on, which is what a working merger looks like from here. Called
    /// again after <see cref="RefusingToMerge"/> it is a merger that has come back.
    /// </summary>
    public FakeGitHub Merging()
    {
        _mergeRefusal = null;
        return this;
    }

    /// <summary>
    /// Merges refuse with this, the way an unreachable API or a branch protection that will
    /// not let the merge through does. It is a permanent refusal: this is what a merger
    /// that cannot take the change says, and reading it again tomorrow would say the same
    /// thing. Every attempt is still recorded, so a test can see that the loop tried.
    /// </summary>
    public FakeGitHub RefusingToMerge(string message = "the change could not be merged") =>
        RefusingToMerge(FailureClass.Permanent, message);

    /// <summary>
    /// Merges refuse with this, and the factory has read that as one kind of failure or the
    /// other. A transient refusal is the one that gets another attempt; a permanent one is
    /// this attempt and no more, for ever. <see cref="Merging"/> afterwards is a merger that
    /// has come back, and is how a test watches a retried merge land.
    /// </summary>
    public FakeGitHub RefusingToMerge(FailureClass failure, string message = "the change could not be merged")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        _mergeRefusal = new Refusal(failure, message);
        return this;
    }

    public Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken)
    {
        var script = Read(repoUrl);
        _branchesResolved[repoUrl] = BranchesResolved(repoUrl) + 1;
        return Task.FromResult(script.DefaultBranch);
    }

    public Task<IReadOnlyList<OpenIssue>> ListOpenIssuesAsync(string repoUrl, CancellationToken cancellationToken)
    {
        // Recorded before the repository is read, so a turn that failed is still a turn
        // the factory took: the order is what proves the rotation went round.
        _polled.Add(repoUrl);
        return Task.FromResult<IReadOnlyList<OpenIssue>>(Read(repoUrl).Issues);
    }

    public Task<PullRequest> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken) =>
        // The loop never calls this, and never will: `MergeAsync` is one call and opening a
        // pull request is a step inside it (ADR-0006). A fake that implemented it anyway
        // would let a test exercise a path the factory does not have, and would be a second
        // answer to "how does a change get shipped" — the disagreement this seam exists to
        // prevent. Recorded here so that its absence is a decision rather than a gap.
        throw new NotSupportedException(
            "the loop asks for a merge and never for a pull request: one MergeAsync call pushes the branch, opens "
                + "the pull request if there is not already one, and merges it, and nothing above the seam decides "
                + "which pull request a change ships as");

    public Task MergeAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken)
    {
        // Recorded before the merge is answered, so a merge that failed is still a merge
        // the factory tried to make: the attempt is what a test reads.
        _merges.Add(new MergeAttempt(repoUrl, issueNumber));

        return _mergeRefusal is { } refusal
            ? Task.FromException(Refusing(refusal))
            : Task.CompletedTask;
    }

    /// <summary>
    /// The refusal as the exception the seam would really throw: a classified one, because
    /// the whole point of the classification is that it is a type rather than a word in a
    /// message. A seam that has not learned to classify throws an ordinary exception, and
    /// the factory reads that as permanent — so this fake classifies, and the unclassified
    /// case is the agent fake's <c>Throwing</c>.
    /// </summary>
    private static Exception Refusing(Refusal refusal) => refusal.Class switch
    {
        FailureClass.Transient => new TransientFailure(refusal.Message),
        _ => new PermanentFailure(refusal.Message),
    };

    private Script Read(string repoUrl)
    {
        if (!_repositories.TryGetValue(repoUrl, out var script))
        {
            throw new InvalidOperationException(
                $"the factory read {repoUrl}, which the test never described");
        }

        return script.Failing
            ? throw Refusing(new Refusal(
                script.FailureClass ?? FailureClass.Permanent,
                script.Failure ?? "the repository could not be read"))
            : script;
    }

    /// <summary>A repository as a test described it: readable, or not, and why not.</summary>
    private sealed record Script(
        string DefaultBranch,
        IReadOnlyList<OpenIssue> Issues,
        bool Failing,
        FailureClass? FailureClass,
        string? Failure);

    /// <summary>A merge the seam will not make, and the class of that refusal.</summary>
    private sealed record Refusal(FailureClass Class, string Message);
}

/// <summary>
/// One merge the loop asked for, read back off the fake: which repository the change was
/// to ship to, and which issue's change it was.
/// </summary>
public sealed record MergeAttempt(string RepoUrl, int IssueNumber);
