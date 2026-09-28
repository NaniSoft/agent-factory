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

    /// <summary>Merges a pull request. Only ever called after a decision.</summary>
    Task MergeAsync(string repoUrl, int pullRequestNumber, CancellationToken cancellationToken);
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
