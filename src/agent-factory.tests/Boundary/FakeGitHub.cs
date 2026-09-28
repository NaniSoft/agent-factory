namespace AgentFactory.Tests.Boundary;

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
    /// What a merge refuses with, or null when merges land. It starts as a refusal because
    /// that is what the seam has behind it today: there is no merger, and a fake that
    /// merged things on its own would let a test pass on a success the factory would not
    /// have in production. A test that wants Done asks for a merge that lands.
    /// </summary>
    private string? _mergeRefusal = NoMergerBehindTheSeam;

    private const string NoMergerBehindTheSeam =
        "there is no merger behind this seam yet: merging is the merger's business, and the merger is not built";

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

        _repositories[repoUrl] = new Script(defaultBranch, issues, Failing: false, Failure: null);
        return this;
    }

    /// <summary>
    /// The repository cannot be read: every call against it throws, the way an API
    /// error or a repository that has gone away does.
    /// </summary>
    public FakeGitHub Failing(
        string repoUrl,
        string message = "the repository could not be read")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);

        _repositories[repoUrl] = new Script("main", [], Failing: true, Failure: message);
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
    /// not let the merge through does. Every attempt is still recorded, so a test can see
    /// that the loop tried.
    /// </summary>
    public FakeGitHub RefusingToMerge(string message = "the change could not be merged")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _mergeRefusal = message;
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
        throw new NotSupportedException(
            "opening a pull request is the merger's business, and the merger is not built yet");

    public Task MergeAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken)
    {
        // Recorded before the merge is answered, so a merge that failed is still a merge
        // the factory tried to make: the attempt is what a test reads.
        _merges.Add(new MergeAttempt(repoUrl, issueNumber));

        return _mergeRefusal is { } refusal
            ? Task.FromException(new InvalidOperationException(refusal))
            : Task.CompletedTask;
    }

    private Script Read(string repoUrl)
    {
        if (!_repositories.TryGetValue(repoUrl, out var script))
        {
            throw new InvalidOperationException(
                $"the factory read {repoUrl}, which the test never described");
        }

        return script.Failing
            ? throw new InvalidOperationException(script.Failure)
            : script;
    }

    private sealed record Script(
        string DefaultBranch,
        IReadOnlyList<OpenIssue> Issues,
        bool Failing,
        string? Failure);
}

/// <summary>
/// One merge the loop asked for, read back off the fake: which repository the change was
/// to ship to, and which issue's change it was.
/// </summary>
public sealed record MergeAttempt(string RepoUrl, int IssueNumber);
