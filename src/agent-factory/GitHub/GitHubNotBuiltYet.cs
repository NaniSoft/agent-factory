namespace AgentFactory.GitHub;

/// <summary>
/// The GitHub seam with nothing behind it yet. The real client belongs to the merging
/// ticket (#10); until it lands this is what the process has, and every call refuses in
/// as many words rather than the process failing to start or the call quietly doing
/// nothing.
/// </summary>
/// <remarks>
/// This is not an implementation and is not meant to be mistaken for one. It is
/// registered only if nothing else has claimed <see cref="IGitHub"/>, so the fake in the
/// test host wins there, and the real client replaces it when it exists. It is deleted
/// rather than left behind when that happens.
/// </remarks>
internal sealed class GitHubNotBuiltYet : IGitHub
{
    private const string Why =
        "this factory has no GitHub client yet: intake and the merger both need one, and "
        + "the client is the merging ticket's to write";

    public Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Why} (asked for the default branch of {repoUrl})");

    public Task<IReadOnlyList<OpenIssue>> ListOpenIssuesAsync(string repoUrl, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Why} (asked for the open issues of {repoUrl})");

    public Task<PullRequest> OpenPullRequestAsync(PullRequestRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Why} (asked to open a pull request on {request.RepoUrl})");

    public Task MergeAsync(string repoUrl, int pullRequestNumber, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"{Why} (asked to merge #{pullRequestNumber} on {repoUrl})");
}
