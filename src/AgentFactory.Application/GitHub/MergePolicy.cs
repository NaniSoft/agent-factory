namespace AgentFactory.Application.GitHub;

using AgentFactory.Application.Observability;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// How a change ships: what is resolved before anything is created, what is refused, in
/// what order, and what a merge is that closes the stories behind it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the factory's most argued-about logic, and it is policy rather than
/// transport.</strong> It used to be the body of one method on the REST client, which is
/// why the client held a store and why every question about merging — is this a retry or
/// a second change, is a closed pull request a decline, is a dirty tree shippable — was
/// answered by reading a class that also knew how to parse HTTP. The decisions are here
/// now and the parsing is not, and the argument for which is which is in
/// <see cref="MergePlan"/>: the policy resolves what is about to ship and hands the
/// adapter a value it cannot add to.
/// </para>
/// <para>
/// <strong>What one <see cref="MergeAsync"/> call does, and the order is the
/// argument.</strong>
/// </para>
/// <list type="number">
/// <item><description>Resolve what can be refused without leaving the building: the
/// project, its credential, the round's tree on this host, and the commit that tree is
/// on.</description></item>
/// <item><description><em>Look for a pull request for this branch first.</em> Read before
/// write, before the branch is pushed, and this is the step that makes a second call
/// safe.</description></item>
/// <item><description>Push the commit, unless the branch is already on the remote at
/// exactly that commit — which is what a previous attempt's push looks like.</description></item>
/// <item><description>Open the pull request, or use the one that is already there.</description></item>
/// <item><description>Refuse a pull request whose branch has moved on since the reviewer
/// judged it, then merge it.</description></item>
/// <item><description>Close the issues the merge ends the story of, and nothing
/// else.</description></item>
/// </list>
/// <para>
/// The second step is where the idempotency is made. A retry after a push that landed and
/// a merge that did not finds the pull request the first attempt opened and goes straight
/// to merging it, so it cannot open a second one; and because the branch is derived
/// deterministically from the issue, "this branch" is the same branch on every attempt.
/// The two windows git and GitHub themselves close — a second pull request for one head,
/// and a push onto a branch that has moved on — are described where they are closed.
/// </para>
/// <para>
/// <strong>Failures are declared here</strong>, by kind rather than by a word in a
/// message, and none of them is read out of a status code by this class: a classification
/// from a status code and two headers is
/// <see cref="TransientFailure"/>/<see cref="PermanentFailure"/>'s and belongs to the
/// adapter, and where GitHub's only signal is prose the policy asks it a second question
/// rather than guessing.
/// </para>
/// </remarks>
public sealed class MergePolicy(
    IWorkItemStore store,
    IMerger merger,
    ILogger<MergePolicy> logger) : IMergePolicy
{
    /// <inheritdoc />
    public async Task MergeAsync(WorkItem workItem, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        // The work item is handed over so that *which* change is shipping is a fact the
        // caller holds and cannot be disagreed with — but "which work item" is not the same
        // as "what it said when the caller last looked". The caller's copy is a snapshot,
        // and this method runs minutes after a reviewer clicked: a route recorded, a round
        // landed or an issue number stamped in between would all be missed. So the record is
        // re-read here, by id, before anything else is decided.
        //
        // This is not a re-lookup for the sake of it. It is what makes "the policy reads the
        // work item's own rounds out of the store" true of the whole work item rather than
        // of some of its fields, and the questions-issue close is the case that proves it: a
        // Grilled change's questions issue is stamped onto the record after the caller has
        // been handed its copy, and reading the snapshot left that issue open for ever.
        workItem = store.Get(workItem.Id) ?? workItem;

        // Everything that can be refused without leaving the building is refused first, so
        // a misconfigured project, an absent credential and a round whose tree never came
        // out of its container are all answered without a request and without a git
        // process. The one that is not there cannot be refused until the adapter is asked,
        // which is why it is asked before the tree is read rather than at the first REST
        // call: the property this whole first phase exists for is "nothing was sent and
        // nothing was started", not the order of two refusals that send nothing either.
        var tree = TreeFor(workItem);
        var plan = new MergePlan(workItem, tree, string.Empty);

        await merger.ResolveAsync(plan, cancellationToken).ConfigureAwait(false);

        var treeState = await merger.ReadTheTreeAsync(tree, cancellationToken).ConfigureAwait(false);
        var commit = CommitAt(tree, treeState);

        var shipping = plan with { Commit = commit };

        // The merger is the one component that holds no work item until it has gone looking
        // for one, and this is where it has it: from here on, every record this method
        // writes — including the ones its private methods write — carries the work item the
        // change belongs to. Shipping is the last thing that happens to a work item, and a
        // record of a push or a merge that names only `project#13` is a record a reader has
        // to join to a work item by hand, which is the join this scope removes.
        using var trace = logger.ForWorkItem(workItem.Id, workItem.Project, workItem.IssueNumber);

        logger.LogInformation(
            "Shipping {Project}#{Issue}: the change is commit {Commit} on branch {Branch}, pushed from {Tree}.",
            workItem.Project,
            workItem.IssueNumber,
            Short(commit),
            shipping.Branch,
            tree);

        // **Read before write.** Whether a pull request already exists is asked first,
        // before anything is created and before the branch is pushed, and it is the step
        // that makes this whole method safe to call twice. Every attempt for one work item
        // derives the same branch, so "the pull request for this branch" is a question with
        // one answer, and this is where it is asked.
        var existing = await merger.FindThePullRequestAsync(shipping, cancellationToken).ConfigureAwait(false);

        if (existing is { Merged: true })
        {
            // Already shipped. Not an error and not a second merge: `Done` means merged, and
            // the change is merged, so a second approve or a second attempt at a parked
            // merge has nothing left to do and says so rather than opening anything.
            logger.LogInformation(
                "The change for {Project}#{Issue} is already in {Repository}#{Pull} and merged, so there is "
                    + "nothing left to ship.",
                workItem.Project,
                workItem.IssueNumber,
                workItem.RepoUrl,
                existing.Number);

            await CloseTheStoriesAsync(shipping, existing.Number, cancellationToken).ConfigureAwait(false);

            return;
        }

        if (existing is { Merged: false, State: "closed" })
        {
            // A human closed this pull request without merging it. That is the same kind of
            // act as a rejection, and the asymmetry the whole loop is built on applies here
            // too: a decline is conclusive and the factory does not go around it. Permanent,
            // and refused with the pull request named so it can be looked at.
            throw new PermanentFailure(
                $"{workItem.RepoUrl} — #{existing.Number} — the pull request for this change — was closed without "
                    + "being merged, so the factory will not open another or merge over that. It is a decline, and "
                    + "the way out of one is a human's, from the board");
        }

        // An open pull request means the branch is already on the remote, so the push below
        // is only for the case where there is nothing there yet.
        if (existing is null)
        {
            await PushAsync(shipping, cancellationToken).ConfigureAwait(false);
        }

        var pull = existing ?? await merger.OpenThePullRequestAsync(shipping, cancellationToken).ConfigureAwait(false);

        // The pull request has to be the change a reviewer judged. If something else has
        // been pushed onto the factory's branch since — a human fixing a conflict, a hook,
        // a second machine running the same factory — the commits on it are not the ones
        // that were on the board, and merging would ship work nobody looked at. Refused,
        // permanently: it is not going to become the reviewed change again on its own.
        if (pull.HeadSha is { Length: > 0 } head && !string.Equals(head, commit, StringComparison.Ordinal))
        {
            throw new PermanentFailure(
                $"{workItem.RepoUrl} #{pull.Number} carries commit {Short(head)} on {shipping.Branch}, and this round's "
                    + $"change is {Short(commit)}: the branch has moved on since the reviewer judged it, so merging "
                    + "it would ship work the board never showed anybody");
        }

        await merger.MergeThePullRequestAsync(shipping, pull, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Merged {Repository}#{Pull} — the change for {Project}#{Issue} — as a merge commit on {Branch}.",
            workItem.RepoUrl,
            pull.Number,
            workItem.Project,
            workItem.IssueNumber,
            shipping.Branch);

        await CloseTheStoriesAsync(shipping, pull.Number, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the issues a merge ends the story of: the issue the change answers, and —
    /// when the change was grilled first — the questions issue whose conversation shaped
    /// it. Nothing short of the merge closes either one.
    /// </summary>
    /// <remarks>
    /// The pull request's body says <c>Refs</c> rather than <c>Closes</c>, so nothing
    /// GitHub did on its own has closed the answered issue, and the rule this carries is
    /// the reviewer's own: <c>merged</c> is what closes an issue, and anything short of
    /// that — a pull request declined without merging, a merge that never landed — leaves
    /// the issues exactly as open as they were. Both success paths above arrive here: the
    /// merge this call made, and the retry that found the pull request already merged.
    /// Each issue's state is read before its close, because the retry is a retry's
    /// ordinary answer and a retry creates nothing — a second close would post a second
    /// comment under an issue a human already saw closed.
    /// <para>
    /// The questions issue is closed beside the answered one, and the reason is the same
    /// rule read from the other side: its conversation is where the human answered, the
    /// answer is now on the base branch, and a questions issue left open over shipped work
    /// is the factory's own output outliving its purpose — found the hard way, when the
    /// first live Grill shipped and its questions issue sat open with nothing left to
    /// say. A close that cannot happen does not un-land a merge that did: the refusal is
    /// logged and the merge stands, because <c>Done</c> means merged and an issue left
    /// open beside a merged change is a state a human can finish with one click.
    /// </para>
    /// </remarks>
    private async Task CloseTheStoriesAsync(MergePlan plan, int pullNumber, CancellationToken cancellationToken)
    {
        await CloseWithCommentIfOpenAsync(
            plan,
            plan.IssueNumber,
            $"Merged as #{pullNumber} — the change answering this issue is on the base branch.",
            cancellationToken).ConfigureAwait(false);

        if (plan.WorkItem.RoutingIssue is { } questions && questions != plan.IssueNumber)
        {
            await CloseWithCommentIfOpenAsync(
                plan,
                questions,
                $"Merged as #{pullNumber} — the answers in this conversation shaped the change that landed. "
                    + "Closing it with the work it unblocked.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CloseWithCommentIfOpenAsync(
        MergePlan plan,
        int issueNumber,
        string because,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await merger.IsTheIssueOpenAsync(plan, issueNumber, cancellationToken).ConfigureAwait(false))
            {
                logger.LogInformation(
                    "{Repository}#{Issue} is already closed, so the merge closes nothing.",
                    plan.Repository,
                    issueNumber);
                return;
            }

            await merger.CloseTheIssueAsync(plan, issueNumber, because, cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Closed {Repository}#{Issue}: the change it belongs to is merged and on the base branch.",
                plan.Repository,
                issueNumber);
        }
        catch (Exception refused) when (refused is PermanentFailure or TransientFailure)
        {
            logger.LogWarning(
                "{Repository}#{Issue} could not be closed after the merge: {Refusal}. The merge stands; "
                    + "the issue is left for a human to close.",
                plan.Repository,
                issueNumber,
                refused.Message);
        }
    }

    /// <summary>
    /// Gets the branch onto the remote, or establishes that it is already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The remote is asked about the branch <em>before</em> git is, and that read is what
    /// makes the push idempotent rather than merely repeatable: a second attempt for a
    /// work item whose push already landed finds the branch at the same commit and sends
    /// nothing at all. A branch that exists at a <em>different</em> commit is refused
    /// permanently, because there is no <c>--force</c> anywhere in this factory and a
    /// branch somebody else owns is not this factory's to overwrite.
    /// </para>
    /// <para>
    /// A push git refuses is then classified by asking the remote two further questions
    /// rather than by reading git's output, because git has no status code and its prose
    /// is not a contract. If the branch is now at this commit, the push landed and git did
    /// not say so. If it is at another commit, the branch is not ours. If it is not there at
    /// all, then whether this factory could reach the remote *at all* decides: if it could
    /// not, the push failed because nothing was reachable and that is transient; if it
    /// could, then git's refusal is the remote's own answer and repeating it would only be
    /// refused the same way.
    /// </para>
    /// </remarks>
    private async Task PushAsync(MergePlan plan, CancellationToken cancellationToken)
    {
        var onRemote = await merger.BranchOnTheRemoteAsync(plan, cancellationToken).ConfigureAwait(false);

        if (string.Equals(onRemote, plan.Commit, StringComparison.Ordinal))
        {
            logger.LogInformation(
                "{Branch} is already on {Repository} at {Commit}, which is this change, so there is nothing to push.",
                plan.Branch,
                plan.Repository,
                Short(plan.Commit));

            return;
        }

        if (onRemote is { Length: > 0 } somebodyElse)
        {
            throw new PermanentFailure(
                $"{plan.Repository} already has a branch {plan.Branch}, at commit {Short(somebodyElse)}, and this "
                    + $"round's change is {Short(plan.Commit)}. The factory does not force a branch, so this change "
                    + "cannot be shipped on it and nothing has been pushed");
        }

        var pushed = await merger.PushTheCommitAsync(plan, cancellationToken).ConfigureAwait(false);

        if (pushed.Succeeded)
        {
            return;
        }

        // What did the remote actually do? The two questions, in the order that decides.
        var now = await merger.BranchOnTheRemoteAsync(plan, cancellationToken).ConfigureAwait(false);

        if (string.Equals(now, plan.Commit, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "git did not report pushing {Branch} to {Repository} ({Reason}), but the branch is there at "
                    + "{Commit}, so it landed and this is treated as a push that worked.",
                plan.Branch,
                plan.Repository,
                pushed.Summary,
                Short(plan.Commit));

            return;
        }

        if (now is { Length: > 0 } movedOn)
        {
            throw new PermanentFailure(
                $"{plan.Repository} has a branch {plan.Branch} at commit {Short(movedOn)} after git refused to push "
                    + $"this change's commit {Short(plan.Commit)} to it. The factory does not force a branch, so nothing "
                    + "has been pushed");
        }

        if (!await merger.TheRemoteIsReachableAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            throw new TransientFailure(
                $"git could not push {plan.Branch} to {plan.Repository} ({pushed.Summary}), and the repository could "
                    + "not be reached either, so this is a connection that was not there rather than an answer from "
                    + "the remote");
        }

        throw new PermanentFailure(
            $"git refused to push {plan.Branch} to {plan.Repository} — {pushed.Summary} — and the branch is not "
                + "there. The repository answered a request made straight after it, so this is the remote declining "
                + "the push rather than a connection that dropped, and asking again would be refused the same way");
    }

    /// <summary>
    /// The commit to ship, and the check that the tree holds nothing else.
    /// </summary>
    /// <remarks>
    /// The uncommitted check is the whole of "the diff a reviewer judges is the diff that
    /// would ship". The board's diff is <c>git diff</c> against the base of the working
    /// tree, so it shows a round's uncommitted files along with its commits; a push sends
    /// only what was committed. Shipping a change that is not the one on the board would
    /// break the one property the review surface exists for, and committing the difference
    /// here would make the host the author of work nobody reviewed, which is the thing
    /// ADR-0006 exists to prevent. So the two are refused against each other, and a human
    /// finishes it from the board.
    /// </remarks>
    private static string CommitAt(string tree, TreeAtHead head)
    {
        if (head.Commit is not { Length: > 0 } commit)
        {
            throw new PermanentFailure(
                $"the round's tree at {tree} has no commit to push: {head.Uncommitted ?? "git said nothing about it"}. "
                    + "A round that committed nothing produced no change for the host to ship, and the base it was "
                    + "built from is not this change");
        }

        if (head.Uncommitted is null)
        {
            throw new PermanentFailure(
                $"the round's tree at {tree} would not say what it is holding uncommitted. The factory will not push a "
                    + "change it cannot account for");
        }

        if (head.Uncommitted.Trim() is { Length: > 0 } leftBehind)
        {
            throw new PermanentFailure(
                $"the round left changes in its tree that it never committed ({Describe(leftBehind)}), so what the "
                    + "factory would push is not what the reviewer judged on the board. Committing them here would "
                    + "make the host the author of work nobody reviewed, so this is left for a human");
        }

        return commit;
    }

    /// <summary>
    /// The round's tree on this host, which is where its commit is. The host pushes, so a
    /// round whose tree did not come out of its container has nothing to push — the
    /// container is gone and there is no second copy of the change (ADR-0006).
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is the <em>latest</em> round's tree, and that is the whole of what #22 changed
    /// here. The path is per round rather than per work item — <c>docker cp</c> copies a
    /// directory into a destination that already exists rather than replacing it, so one
    /// directory per work item put round 2's tree inside round 1's — and a merger that
    /// still looked in the work item's own directory would be pushing round 1's commit
    /// while the reviewer believed they were judging round 2.
    /// </para>
    /// <para>
    /// The latest round with a tree, rather than the latest round: a round whose tree never
    /// came out is not a reason to ship an earlier one, so the search walks back until it
    /// finds a tree and says which round it is shipping if it cannot find one at all.
    /// </para>
    /// </remarks>
    private string TreeFor(WorkItem workItem)
    {
        var rounds = store.Rounds(workItem.Id);

        foreach (var round in Enumerable.Reverse(rounds))
        {
            if (round.Diff is { } diff && Directory.Exists(Path.Combine(diff.Tree, ".git")))
            {
                return diff.Tree;
            }
        }

        // Named by the rounds that had none, so the refusal says which round the reviewer
        // was looking at rather than only that the host has nothing.
        throw new PermanentFailure(
            $"none of the {rounds.Count} round(s) of {workItem.Project}#{workItem.IssueNumber} left a tree on this "
                + "host, so there is no commit to push. The containers that made the changes are gone and the host "
                + "holds the only copy of them, so this cannot be retried into existence and the change has to be "
                + "built again");
    }

    /// <summary>What git's porcelain said, in a form a reviewer can read on a board.</summary>
    private static string Describe(string porcelain) => string.Join(
        ", ",
        porcelain
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Take(3)
            .Select(line => line.Length <= 60 ? line : line[..57] + "…"));

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;
}