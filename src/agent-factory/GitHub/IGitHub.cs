namespace AgentFactory.GitHub;

/// <summary>
/// The one GitHub-facing seam. Polling and merging are the same boundary, so they are
/// one interface rather than two that can disagree about what a repository is.
/// </summary>
public interface IGitHub
{
    /// <summary>
    /// The label every issue the factory creates carries, and the one label intake reads:
    /// an issue wearing it is this factory's own output — a Grill's questions, filed under
    /// the issue that asked for them — and not the repository's work. It exists for humans
    /// reading the tracker and for exactly this one fact, because a factory that takes its
    /// own questions as work is a loop that does not terminate.
    /// </summary>
    public const string FactoryIssueLabel = "agent-factory";

    /// <summary>The repository's default branch, resolved when an issue is polled.</summary>
    Task<string> GetDefaultBranchAsync(string repoUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Every open issue on the repository. No filter a repository's conventions could hang
    /// a triage decision on — labels, assignees — decides what is taken, because the board
    /// is where a human decides what is worth building (ADR-0007). The one exception is
    /// the factory's own authorship, <see cref="FactoryIssueLabel"/>, which the poller
    /// reads to decline its own output.
    /// </summary>
    Task<IReadOnlyList<OpenIssue>> ListOpenIssuesAsync(string repoUrl, CancellationToken cancellationToken);

    /// <summary>
    /// The conversation on one issue, in order. An issue is a conversation and not a
    /// document: the body is how it started, and the comments are everything everybody
    /// said since — which is where a human answers a Grill's questions, and the only
    /// place an answer has ever lived.
    /// </summary>
    Task<IReadOnlyList<IssueComment>> ReadCommentsAsync(string repoUrl, int issueNumber, CancellationToken cancellationToken);

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

    /// <summary>
    /// Creates one issue, from the host, under the project's own credential. Used for the
    /// questions and the maps a routing round proposed and the host has decided to publish
    /// (ADR-0015).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a write the project did not ask for in the sense of a pull request: it puts
    /// something in its issue tracker. It is here rather than inside the round because the
    /// round has no credential that can write anything (ADR-0006), and the agent's own words
    /// in a title reach GitHub as an ordinary string, never as a shell.
    /// </para>
    /// <para>
    /// It carries no closing keyword handling of any kind, for the same reason
    /// <c>MergeAsync</c>'s pull request body links rather than closes: this factory can merge
    /// or publish without a human having read every word, and a body that closed an issue
    /// would make that a second claim the write had not earned.
    /// </para>
    /// </remarks>
    /// <param name="request">What to create, and under which parent.</param>
    /// <exception cref="Failures.FactoryFailure">GitHub refused, and the refusal is classified.</exception>
    Task<CreatedIssue> CreateIssueAsync(NewIssue request, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="childNumber"/> is a sub-issue of
    /// <paramref name="parentNumber"/>, so GitHub's own UI shows the tree the factory built
    /// rather than the factory holding the only copy of it.
    /// </summary>
    /// <remarks>
    /// The parent is looked up first, and a refusal naming the issue it could not find is
    /// better than a 404 the caller has to interpret. Native linking is used rather than a
    /// marker in the body because a marker is invisible in the tracker's own view of what
    /// blocks what — which is the whole reason to use the native relationship.
    /// </remarks>
    /// <exception cref="Failures.FactoryFailure">GitHub refused, and the refusal is classified.</exception>
    Task MakeASubIssueAsync(string repoUrl, int parentNumber, int childNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="blockedNumber"/> cannot start until
    /// <paramref name="blockerNumber"/> is closed. GitHub's native blocking relationship, for
    /// the same reason as the parent link: the frontier should render where a human is
    /// already looking, not only here.
    /// </summary>
    /// <remarks>
    /// Called once per edge rather than in a batch, because each edge is its own request that
    /// can be refused on its own and a half-wired map is a map whose frontier a human cannot
    /// read. An edge that fails is logged by the caller and the map still stands — the
    /// blocking edges are a convenience for a reader, not the mechanism that stops anything
    /// being worked.
    /// </remarks>
    /// <exception cref="Failures.FactoryFailure">GitHub refused, and the refusal is classified.</exception>
    Task MakeBlockedByAsync(string repoUrl, int blockedNumber, int blockerNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Closes an issue the factory created and no longer needs — the parent a routing round
    /// turned into a map of children, which has been superseded by them and would otherwise
    /// sit in the factory's own intake queue for ever.
    /// </summary>
    /// <remarks>
    /// Closing is done with a comment rather than by closing silently, so the issue says why
    /// it closed in its own history rather than leaving a reader to infer it from the
    /// children. It is a close and not a delete: the map is the index that tells a human what
    /// the factory did with their issue, and deleting it would destroy the only account.
    /// </remarks>
    /// <exception cref="Failures.FactoryFailure">GitHub refused, and the refusal is classified.</exception>
    Task CloseIssueAsync(string repoUrl, int issueNumber, string because, CancellationToken cancellationToken);
}

/// <summary>
/// An issue the factory created. The number and URL, which is all anything needs of it: the
/// number is the handle every later call takes and the URL is what a board card links to.
/// </summary>
/// <param name="Number">The issue's number in its repository.</param>
/// <param name="Url">Where a human reads it.</param>
public sealed record CreatedIssue(int Number, string Url);

/// <summary>
/// One issue to create, and the parent it belongs under.
/// </summary>
/// <param name="RepoUrl">The repository both the parent and the new issue live in.</param>
/// <param name="ParentNumber">The issue this is a child of, or null for a root.</param>
/// <param name="Title">The issue's title. The round's words, passed through as data.</param>
/// <param name="Body">The issue's body, in markdown.</param>
public sealed record NewIssue(string RepoUrl, int? ParentNumber, string Title, string Body);

/// <summary>
/// An open issue. The factory's input, and it stays GitHub's object. The labels and
/// assignees are carried because GitHub issues have them and the seam is faithful to
/// what it returns — and the poller reads neither as a triage filter, which is the point
/// (ADR-0007): a repository's own conventions decide nothing about what is worth building.
/// The one read is the factory's own authorship label (<see cref="IGitHub.FactoryIssueLabel"/>),
/// which declines the factory's output rather than the repository's work.
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

/// <summary>
/// One turn of an issue's conversation: who said it, what they said, and when. The
/// created time is carried because it is how the loop tells an answer from the silence it
/// landed in — a Waiting work item watches its questions issue for anything said after the
/// question was asked, and a timestamp is the only honest test of that.
/// </summary>
public sealed record IssueComment(string Author, string Body, DateTimeOffset CreatedUtc);

/// <summary>A pull request the factory opened. Always a real one, so a merge is revertible.</summary>
public sealed record PullRequest(int Number, string Url);

public sealed record PullRequestRequest(
    string RepoUrl,
    string Branch,
    string Title,
    string Body,
    int IssueNumber);
