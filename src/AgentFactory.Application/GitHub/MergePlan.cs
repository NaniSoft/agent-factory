namespace AgentFactory.Application.GitHub;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// What is about to ship, resolved completely and not changeable afterwards: the work
/// item, the tree its round left on this host, and the commit that tree is on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the whole input to the adapter, and that is the property
/// <c>docs/architecture.md</c> asks for: an adapter never queries the store.</strong>
/// <c>GitHubClient</c> used to hold <c>IWorkItemStore</c> to find the round's tree, which
/// was legal — <c>Infrastructure</c> may depend on <c>Application</c> — and was also the
/// deepest coupling in the codebase, because two components write the store and one of
/// them was a REST client. The policy resolves what is about to ship into this one
/// immutable value and hands it over; the adapter is handed nothing else and can ask
/// nothing.
/// </para>
/// <para>
/// Only three things are carried, and the other three are derived rather than copied.
/// The work item is the factory's own record of the issue, the tree is the one place on
/// this host where the change exists, and the commit is what git says is on it — those
/// three are facts the policy had to go and look up. The branch, the pull request's title
/// and its body are <em>rules</em>, and a rule belongs with the policy: they are computed
/// here from <see cref="BranchName"/> and <see cref="PullRequestText"/> so that there is
/// exactly one derivation of each and the adapter cannot disagree with it.
/// </para>
/// <para>
/// It is a record, so nothing about it can be changed between the policy resolving it and
/// the adapter shipping it. That is not immutability for its own sake: it is what makes
/// "the change the reviewer judged" and "the change that ships" the same string at both
/// ends of the push.
/// </para>
/// </remarks>
/// <param name="WorkItem">
/// The work item being shipped. Everything the pull request is named and said comes from
/// here, read from the store rather than from GitHub, so that a pull request and the card
/// a reviewer read cannot disagree.
/// </param>
/// <param name="Tree">
/// The round's tree on this host, and therefore the only place the change exists: the
/// container that made it is gone.
/// </param>
/// <param name="Commit">The commit that tree is on, read from git rather than remembered.</param>
public sealed record MergePlan(WorkItem WorkItem, string Tree, string Commit)
{
    /// <summary>The project's own repository URL — the address the branch is pushed to.</summary>
    public string Repository => WorkItem.RepoUrl;

    /// <summary>The issue the change answers, which is what the pull request is about.</summary>
    public int IssueNumber => WorkItem.IssueNumber;

    /// <summary>
    /// The branch the change is pushed to, derived rather than carried: two attempts for
    /// one work item must derive the same branch, and a branch copied into a plan could be
    /// copied wrongly.
    /// </summary>
    public string Branch => BranchName.For(WorkItem.IssueNumber, WorkItem.IssueTitle);

    /// <summary>What the pull request is called: the issue's own title, unchanged.</summary>
    public string Title => PullRequestText.Title(WorkItem);

    /// <summary>What the pull request says — which links the issue and closes nothing.</summary>
    public string Body => PullRequestText.Body(WorkItem, Branch);
}