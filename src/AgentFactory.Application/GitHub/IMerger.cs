namespace AgentFactory.Application.GitHub;

/// <summary>
/// The merge's own knowledge of a repository and of git: every fact a merge is decided
/// on, and no decision.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a port, and it is the ninth.</strong> The merge is the one thing in
/// this factory that is both policy and transport, and before this was split the policy
/// and the transport were one class, which is why a REST client held a store. The
/// <em>policy</em> — which refusals there are, in what order they are reached, what a
/// branch is called and what a pull request says — is <see cref="MergePolicy"/>'s and
/// lives here in `Application`. This is what the outside has to be able to do: answer a
/// question about GitHub or about git, and nothing else.
/// </para>
/// <para>
/// <strong>Every method that ships takes the plan and nothing else.</strong> Not a store,
/// not a work item id, not a repository URL to look up: the adapter is handed what is
/// about to ship and is given no way to work out anything else. Two small extra arguments
/// appear on three methods and each is a number the policy is already holding — a pull
/// request's, an issue's, a commit the policy has just read out of the plan — rather than
/// anything the adapter could have gone and got for itself.
/// </para>
/// <para>
/// The refusals are not here and that is the point. A push that git refused, a branch at
/// another commit, a pull request a human closed, a tree holding changes nobody committed
/// — every one of those is a judgement about the factory's own work, and they are all made
/// by <see cref="MergePolicy"/> out of what this port reports. What this port classifies is
/// the narrower thing it can see: a status code and two headers
/// (<c>GitHubResponse</c>), and git's own exit code, which is a question about the remote
/// rather than an answer.
/// </para>
/// </remarks>
public interface IMerger
{
    /// <summary>
    /// Resolves the project this plan ships under and the credential it pushes with, and
    /// refuses either one that is not there.
    /// </summary>
    /// <remarks>
    /// It sends nothing and starts nothing, and that is the whole of why it is a call of
    /// its own rather than folded into the first request. "Resolve everything that can be
    /// refused without leaving the building, first" is the merge's first step and it is a
    /// policy about <em>order</em>, so the policy asks for it — but what "the project is
    /// served and its credential is in the environment" means is a fact only the outside
    /// can answer, because only the outside has the project files and the environment.
    /// <para>
    /// Both refusals are permanent, and the project's own key <em>name</em> is in the
    /// message because a name is not a secret and it is what an operator has to go and fix.
    /// </para>
    /// </remarks>
    Task ResolveAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// What a round's tree on this host says about itself: the commit it is on, and
    /// anything it is still holding that it never committed.
    /// </summary>
    /// <remarks>
    /// The one method that cannot take a plan, and the reason is not convenience: the
    /// commit is <em>in</em> the plan, so this is what produces it. It takes the tree and
    /// nothing else — no credential, because a read of the factory's own tree needs none.
    /// <para>
    /// Either answer may be null, and each null is a fact rather than an absence: git
    /// would not say what commit the tree is on, or it would not say what the tree is
    /// holding. Deciding what either of those means is
    /// <see cref="MergePolicy"/>'s, and both refusals are permanent.
    /// </para>
    /// </remarks>
    Task<TreeAtHead> ReadTheTreeAsync(string tree, CancellationToken cancellationToken);

    /// <summary>
    /// The pull request already open against this branch, whatever state it is in, or null
    /// when there is none.
    /// </summary>
    /// <remarks>
    /// Asked <em>before</em> anything is created and before the branch is pushed, and that
    /// read is what makes a second call safe to make. Every attempt for one work item
    /// derives the same branch, so "the pull request for this branch" is a question with
    /// one answer.
    /// <para>
    /// A merged pull request and a closed one are both returned here rather than as an
    /// absence, because the difference between them is the whole of what the idempotency
    /// argument rests on and an adapter that could not tell them apart would be a second
    /// opinion about the same thing.
    /// </para>
    /// </remarks>
    Task<PullRequestOnRemote?> FindThePullRequestAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// The commit the branch is at on the remote, or null when the branch is not there.
    /// </summary>
    /// <remarks>
    /// A 404 is the answer "no such branch" and only that. Anything else is a refusal, and
    /// a refusal here is the same failure the push would be: it happens before the push on
    /// a first attempt and after one on a retry.
    /// </remarks>
    Task<string?> BranchOnTheRemoteAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the repository can be reached at all, answered by making a request rather
    /// than by guessing from a failed one.
    /// </summary>
    /// <remarks>
    /// This is a classification, not a boolean: an answer that came back as a refusal
    /// proves the remote answered, so a push git refused was refused by the remote; an
    /// answer that never came back proves the opposite, and the push failed for want of a
    /// connection. Only <see cref="MergePolicy"/> knows which of those is which.
    /// </remarks>
    Task<bool> TheRemoteIsReachableAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the plan's commit to the plan's branch, and reports what git said.
    /// </summary>
    /// <remarks>
    /// A non-zero exit is <strong>not</strong> a failure of this method. git has no status
    /// code and its prose is not a contract, so what a non-zero exit means is a question
    /// about the remote that only the policy can turn into an answer — by asking this same
    /// port two further questions.
    /// </remarks>
    Task<PushAttempt> PushTheCommitAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the pull request for the plan, or returns the one that is already there.
    /// </summary>
    /// <remarks>
    /// The title and the body are the plan's, composed by <see cref="PullRequestText"/>,
    /// and the base is the work item's: intake resolved the default branch and the round
    /// fetched it, so the two are the same by construction.
    /// </remarks>
    Task<PullRequestOnRemote> OpenThePullRequestAsync(MergePlan plan, CancellationToken cancellationToken);

    /// <summary>
    /// What the pull request is now, or null when it could not be read back.
    /// </summary>
    /// <remarks>
    /// Null is not a failure and is deliberately not raised as one: a read that fails is
    /// not a reason to change the answer the caller already has, and this exists only to
    /// say whether a merge landed despite refusing to report one.
    /// </remarks>
    Task<PullRequestOnRemote?> ReadThePullRequestAsync(
        MergePlan plan,
        int number,
        CancellationToken cancellationToken);

    /// <summary>
    /// Asks GitHub to merge the pull request, and reads it back when the answer is not a
    /// merge.
    /// </summary>
    /// <remarks>
    /// Refusals are raised here rather than returned, because classifying one is a
    /// judgement about a status code and two headers and this is where those are read
    /// (<c>GitHubResponse</c>). A 405 that GitHub uses for two different facts is
    /// answered by re-reading the pull request and looking at its own
    /// <c>mergeable_state</c>, which is a field rather than a sentence.
    /// </remarks>
    Task MergeThePullRequestAsync(
        MergePlan plan,
        PullRequestOnRemote pull,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether the issue is still open. Read before its close, because a retry creates
    /// nothing — a second close would post a second comment under an issue a human
    /// already saw closed.
    /// </summary>
    Task<bool> IsTheIssueOpenAsync(MergePlan plan, int issueNumber, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the issue with a comment saying why, on the project the plan names.
    /// </summary>
    Task CloseTheIssueAsync(
        MergePlan plan,
        int issueNumber,
        string because,
        CancellationToken cancellationToken);
}

/// <summary>
/// What a round's tree says about itself: the commit it is on, and what it is holding
/// that it never committed.
/// </summary>
/// <param name="Commit">The commit at HEAD, or null when git would not say.</param>
/// <param name="Uncommitted">
/// git's porcelain, verbatim, for everything in the tree that was never committed. Null
/// when git would not say; an empty string is a clean tree and is a different answer.
/// </param>
/// <remarks>
/// Two nullable facts rather than a result with a verdict in it, because whether either
/// one is acceptable is the policy's to decide and this is only what the tree said. The
/// empty string is load-bearing and is why there is no boolean: "a tree holding nothing"
/// and "a tree git would not describe" are very different claims about a change.
/// </remarks>
public sealed record TreeAtHead(string? Commit, string? Uncommitted);

/// <summary>
/// What git said about a push. An exit code, and the words that go with it.
/// </summary>
/// <param name="Succeeded">git's own exit code was zero.</param>
/// <param name="Summary">
/// What git said, trimmed to one line, for a log or a refusal. Never parsed and never
/// decided on — a caller that read this to learn why a push failed would be reading git's
/// prose as a contract, which is the one thing this codebase refuses to do.
/// </param>
public sealed record PushAttempt(bool Succeeded, string Summary);

/// <summary>
/// A pull request as GitHub describes it, which is the only account of it that decides
/// anything here.
/// </summary>
/// <param name="Number">Its number in the repository.</param>
/// <param name="Url">Where a human reads it.</param>
/// <param name="State">GitHub's own <c>open</c> or <c>closed</c>.</param>
/// <param name="HeadSha">The commit its branch is on, or empty when GitHub named none.</param>
/// <param name="BaseRef">The branch it lands on.</param>
/// <param name="Merged">
/// Read from <c>merged_at</c> rather than from <see cref="State"/>, because GitHub reports
/// a merged pull request as closed and the difference between a merge and a decline is the
/// whole of what the idempotency argument rests on.
/// </param>
/// <param name="MergeableState">
/// GitHub's own field, which is what tells a conflicting pull request from one whose base
/// moved while it was being looked at. Empty when GitHub sent none.
/// </param>
public sealed record PullRequestOnRemote(
    int Number,
    string Url,
    string State,
    string HeadSha,
    string BaseRef,
    bool Merged,
    string MergeableState);