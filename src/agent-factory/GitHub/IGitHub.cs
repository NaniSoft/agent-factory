namespace AgentFactory.GitHub;

/// <summary>
/// The one GitHub-facing seam. Polling and merging are the same boundary, so they are
/// one interface rather than two that can disagree about what a repository is.
/// </summary>
public interface IGitHub
{
    /// <summary>The repository's default branch, resolved when an issue is polled.</summary>
    Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Every open issue on the repository. The factory applies no label, assignee or
    /// any other filter: the board is where a human decides what is worth building.
    /// </summary>
    Task<IReadOnlyList<OpenIssue>> ListOpenIssuesAsync(string repoUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a pull request from the host, under the factory's own credential. A worker
    /// container holds nothing that can write to a remote (ADR-0006).
    /// </summary>
    Task<PullRequest> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Merges the change a work item produced, from the host, under the factory's own
    /// credential: the branch is pushed, the pull request is opened and the pull request
    /// is merged, in that order and from outside the container that wrote the change
    /// (ADR-0006). Only ever called after a decision.
    /// </summary>
    /// <remarks>
    /// The merge is one call because it is one thing to the loop, and the loop is what
    /// asks: <c>Done</c> means merged, so the factory has to be able to say whether a
    /// merge landed or not, and nothing above this seam knows or cares how it was done.
    /// Which pull request the change ships as, what it is called and what it says are the
    /// merger's own business and are not the loop's to decide; the loop knows the issue
    /// the change answers and nothing more. That is what the real client is (the merging
    /// ticket, #10), and it is what makes the call mean "shipped" or "not shipped".
    /// </remarks>
    /// <param name="repoUrl">The repository the change ships to.</param>
    /// <param name="issueNumber">
    /// The issue the change answers, which is what names the pull request it ships as.
    /// </param>
    Task MergeAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken);
}

/// <summary>
/// An open issue. The factory's input, and it stays GitHub's object. The labels and
/// assignees are carried because GitHub issues have them and the seam is faithful to
/// what it returns — and the poller never reads either of them, which is the point
/// (ADR-0007). They are here so that "the factory applies no filter" is a decision
/// visible in one place rather than an absence nobody can find.
/// </summary>
public sealed record OpenIssue(
    int Number,
    string Title,
    string Body,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Assignees)
{
    /// <summary>An issue with nothing else on it: no labels, nobody assigned.</summary>
    public static OpenIssue Plain(int number, string title, string body) => new(number, title, body, [], []);
}

/// <summary>A pull request the factory opened. Always a real one, so a merge is revertible.</summary>
public sealed record PullRequest(int Number, string Url);

public sealed record PullRequestRequest(
    string RepoUrl,
    string Branch,
    string Title,
    string Body,
    int IssueNumber);
