namespace AgentFactory.UnitTests.GitHub;

using AgentFactory.Application.GitHub;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.WorkItems;
using AgentFactory.UnitTests.Boundary;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The merge policy, driven with a fake <see cref="IMerger"/> and a fake
/// <see cref="AgentFactory.Application.WorkItems.IWorkItemStore"/> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The same refusals are exercised end to end in `MergeTests`, which runs the real client, a real
/// bare repository and real git. What this file is for is the layer underneath that: every refusal
/// here is a decision the policy makes out of what a port reports, and until this existed it could
/// only be reached by standing up the whole client. So each test below constructs the policy
/// directly, hands it the answer a repository would give, and asserts the decision — no HTTP, no
/// git, no temporary SQLite file, and nothing to start.
/// </para>
/// <para>
/// Where a rule is a refusal, the test asserts the refusal <em>and</em> that nothing left the
/// building first, because the ordering is the policy's own claim and the one an end-to-end test
/// cannot see: the whole point of resolving everything local first is that a misconfigured project,
/// a missing credential, a tree that never came out and a round that left work uncommitted are all
/// answered without a request and without a git process.
/// </para>
/// </remarks>
public class MergePolicyTests
{
    private const string Commit = "cccccccccccccccccccccccccccccccccccccccc";

    private static MergePolicy Policy(FakeWorkItemStore store, FakeMerger merger) =>
        new(store, merger, NullLogger<MergePolicy>.Instance);

    private static FakeWorkItemStore StoreShipping(WorkItem workItem, AHostTree tree) =>
        new FakeWorkItemStore(workItem).WithRound(ARound.Produced(workItem.Id, tree.Root));

    // ------------------------------------------------------------------ what shipping is

    [Fact]
    public async Task The_change_ships_and_the_issue_it_answers_is_closed()
    {
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger();

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.True(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.True(merger.Was(nameof(IMerger.OpenThePullRequestAsync)));
        Assert.True(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));

        // The merged change is the round's own commit, on the branch derived from the issue.
        var shipped = Assert.Single(merger.Merged);
        Assert.Equal(Commit, shipped.Commit);
        Assert.Equal(BranchName.For(42, workItem.IssueTitle), shipped.Branch);

        // And the close carries the pull request's number, because that is the sentence a reader
        // of the issue needs.
        var close = Assert.Single(merger.Closes);
        Assert.Equal(42, close.Issue);
        Assert.Contains("#7", close.Because, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plan_derives_a_deterministic_branch_a_title_and_a_body()
    {
        // The branch name is not a label, it is the idempotency: two attempts for one work item
        // must derive the same branch, or the second attempt starts a second pull request. That
        // determinism is stated here as the plan's own property rather than read off the code.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For(issueNumber: 42, title: "Fix: the thing  (again!)");

        var first = new MergePlan(workItem, tree.Root, Commit);
        var second = new MergePlan(workItem, tree.Root, Commit);

        Assert.Equal("agent-factory/42-fix-the-thing-again", first.Branch);
        Assert.Equal(first.Branch, second.Branch);
        Assert.Equal(workItem.RepoUrl, first.Repository);
        Assert.Equal(42, first.IssueNumber);
        Assert.Equal(PullRequestText.Title(workItem), first.Title);
        Assert.Equal(PullRequestText.Body(workItem, first.Branch), first.Body);
    }

    // ------------------------------------------------------------------ idempotency

    [Fact]
    public async Task An_already_merged_pull_request_is_a_success_and_not_a_second_merge()
    {
        // A second approve, or a retry of a merge that landed: the change is in, so there is
        // nothing to push, nothing to open and nothing to merge — and the story still closes,
        // because the first attempt may have died between merging and closing.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            PullRequest = _ => new PullRequestOnRemote(
                7, "https://github.com/NaniSoft/nexus/pull/7", "closed", Commit, "main", Merged: true, "clean"),
        };

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.False(merger.Was(nameof(IMerger.OpenThePullRequestAsync)));
        Assert.False(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
        Assert.Contains(42, merger.ClosedIssues);
    }

    [Fact]
    public async Task A_branch_already_on_the_remote_at_this_commit_is_not_pushed_again()
    {
        // The second half of idempotency: the remote is asked about the branch before git is, so a
        // call that follows a push that landed sends nothing at all rather than asking git to
        // discover a no-op.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger { RemoteBranch = _ => Commit };

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.True(merger.Was(nameof(IMerger.OpenThePullRequestAsync)));
        Assert.True(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }

    // ------------------------------------------------------------------ the refusals

    [Fact]
    public async Task A_pull_request_a_person_closed_is_a_permanent_decline()
    {
        // A closed pull request is a human's decline, and the asymmetry the whole loop is built on
        // applies: the factory does not reopen it and does not merge over it.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            PullRequest = _ => new PullRequestOnRemote(
                7, "https://github.com/NaniSoft/nexus/pull/7", "closed", Commit, "main", Merged: false, "clean"),
        };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("#7", refused.Message, StringComparison.Ordinal);
        Assert.Equal(FailureClass.Permanent, Failures.Classify(refused));
        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.False(merger.Was(nameof(IMerger.OpenThePullRequestAsync)));
        Assert.False(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
        Assert.Empty(merger.ClosedIssues);
    }

    [Fact]
    public async Task A_branch_that_moved_on_since_the_reviewer_judged_it_is_refused()
    {
        // The change a reviewer judged is a commit. If something else has been pushed onto the
        // factory's branch since, merging would ship work the board never showed anybody.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var somebodyElse = new string('a', 40);
        var merger = new FakeMerger
        {
            PullRequest = _ => new PullRequestOnRemote(
                7, "https://github.com/NaniSoft/nexus/pull/7", "open", somebodyElse, "main", Merged: false, "clean"),
        };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("moved on", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }

    [Fact]
    public async Task A_round_that_left_changes_uncommitted_is_not_shipped()
    {
        // The board shows `git diff` against the base of the working tree, so it shows the round's
        // uncommitted files; a push sends only what was committed. Shipping the committed part and
        // calling it the reviewed change would break the one property the review surface exists
        // for — and committing the difference here would make the host the author of work nobody
        // reviewed.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger { TreeAt = _ => new TreeAtHead(Commit, " M src/Scratch.cs") };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("never committed", refused.Message, StringComparison.OrdinalIgnoreCase);

        // And the refusal is decided before anything is sent: no read of the remote, no branch
        // question, no push and no pull request.
        Assert.False(merger.Was(nameof(IMerger.FindThePullRequestAsync)));
        Assert.False(merger.Was(nameof(IMerger.BranchOnTheRemoteAsync)));
        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.False(merger.Was(nameof(IMerger.OpenThePullRequestAsync)));
        Assert.False(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }

    [Fact]
    public async Task A_round_with_no_commit_to_ship_is_refused()
    {
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger { TreeAt = _ => new TreeAtHead(null, string.Empty) };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("no commit to push", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(merger.Was(nameof(IMerger.FindThePullRequestAsync)));
    }

    [Fact]
    public async Task A_tree_git_will_not_describe_is_refused_rather_than_assumed_clean()
    {
        // `null` is "git would not say", and it is deliberately not the same as the empty string,
        // which is a clean tree. A change the factory cannot account for is not shippable.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger { TreeAt = _ => new TreeAtHead(Commit, null) };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("would not say", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_round_whose_tree_never_came_out_is_refused_before_anything_is_asked()
    {
        // The host holds the only copy of a round's change: the container is gone and there is
        // nothing to re-fetch, so this is permanent — and it is decided without touching the
        // merger at all, which is the local-first ordering in its purest form.
        var workItem = AWorkItem.For();
        var merger = new FakeMerger();
        var store = new FakeWorkItemStore(workItem);

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(store, merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("left a tree on this host", refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(merger.Asked);
    }

    [Fact]
    public async Task A_branch_somebody_else_owns_is_never_overwritten()
    {
        // There is no `--force` anywhere in this factory, and a branch already on the remote at a
        // different commit is not this factory's to overwrite.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger { RemoteBranch = _ => new string('b', 40) };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("does not force a branch", refused.Message, StringComparison.Ordinal);
        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
    }

    // ------------------------------------------------------------------ a push git refused

    [Fact]
    public async Task A_push_that_could_not_happen_because_nothing_was_reachable_is_transient()
    {
        // git has no status code and its prose is not a contract, so the push is classified by
        // asking the remote whether it can be reached at all — not by reading what git said.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            Push = _ => new PushAttempt(false, "fatal: unable to access: could not resolve host"),
            Reachable = _ => false,
        };

        var refused = await Assert.ThrowsAsync<TransientFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("not there rather than an answer from the remote", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_push_the_remote_refused_is_permanent()
    {
        // The same two questions, the other answer: the repository answers a request made straight
        // after git gave up, so git's refusal is the remote's own and asking again is futile.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            Push = _ => new PushAttempt(false, "remote: permission denied"),
            Reachable = _ => true,
        };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("the remote declining the push", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_push_that_landed_without_saying_so_is_taken_as_a_success()
    {
        // git reported a refusal but the branch is now at the commit: the push landed and git did
        // not say so. The mirror of the case above, and the reason the branch is asked about twice.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var landed = false;
        var merger = new FakeMerger
        {
            RemoteBranch = _ => landed ? Commit : null,
            Push = _ =>
            {
                landed = true;
                return new PushAttempt(false, "the connection dropped");
            },
        };

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.True(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }

    // ------------------------------------------------------------------ closing the story

    [Fact]
    public async Task A_merge_closes_the_questions_issue_whose_answers_shaped_the_change()
    {
        using var tree = new AHostTree();
        var workItem = AWorkItem.For(routingIssue: 25);
        var merger = new FakeMerger();

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.Equal([25, 42], merger.ClosedIssues.Order().ToArray());
    }

    [Fact]
    public async Task A_questions_issue_that_is_already_closed_is_not_closed_again()
    {
        // A retry is a retry's ordinary answer and creates nothing: a second close would post a
        // second comment under an issue a human already saw closed.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For(routingIssue: 25);
        var merger = new FakeMerger { IssueOpen = (_, issue) => issue == 42 };

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.Equal([42], merger.ClosedIssues);
    }

    [Fact]
    public async Task A_questions_issue_that_is_the_answered_issue_is_closed_once()
    {
        // The guard against closing the same issue twice, made reachable: a record whose questions
        // issue is the answered issue is closed once, not once as the issue and once as the
        // conversation.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For(routingIssue: 42);
        var merger = new FakeMerger();

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.Equal([42], merger.ClosedIssues);
    }

    [Fact]
    public async Task A_close_that_fails_does_not_un_land_the_merge()
    {
        // `Done` means merged, and an issue left open beside a merged change is a state a human can
        // finish with one click. A close that GitHub refuses is logged and the merge stands.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            Close = (_, issue, _) =>
            {
                if (issue == 42)
                {
                    throw new PermanentFailure("GitHub refused the close");
                }
            },
        };

        await Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None);

        Assert.True(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }

    [Fact]
    public async Task The_policy_ships_the_stores_copy_of_the_work_item_and_not_the_callers_snapshot()
    {
        // The caller's copy is a snapshot, and this method runs minutes after a reviewer clicked: a
        // route recorded, a round landed or an issue number stamped in between would be missed. The
        // questions issue is the case that proves it — stamped onto the record after the caller was
        // handed its copy, and left open for ever if the snapshot were read.
        using var tree = new AHostTree();
        var caller = AWorkItem.For(routingIssue: null);
        var stored = caller with { RoutingIssue = 25 };
        var store = new FakeWorkItemStore(stored).WithRound(ARound.Produced(caller.Id, tree.Root));
        var merger = new FakeMerger();

        await Policy(store, merger).MergeAsync(caller, CancellationToken.None);

        Assert.Contains(25, merger.ClosedIssues);
    }

    // ------------------------------------------------------------------ a project that cannot ship

    [Fact]
    public async Task A_project_that_is_not_served_or_has_no_credential_refuses_without_sending_anything()
    {
        // Resolving what can be refused without leaving the building is the policy's first step,
        // and this is that step failing: the port throws "this project is not served" or "its
        // credential is not there", and the policy lets it through before any request exists to
        // send. Both refusals are permanent, and the project's own key name is in the message a
        // human goes and fixes.
        using var tree = new AHostTree();
        var workItem = AWorkItem.For();
        var merger = new FakeMerger
        {
            OnResolve = _ => throw new PermanentFailure(
                "no project file is being served for https://github.com/NaniSoft/nexus (NEXUS_GITHUB_TOKEN)"),
        };

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => Policy(StoreShipping(workItem, tree), merger).MergeAsync(workItem, CancellationToken.None));

        Assert.Contains("NEXUS_GITHUB_TOKEN", refused.Message, StringComparison.Ordinal);
        Assert.Equal(FailureClass.Permanent, Failures.Classify(refused));
        Assert.False(merger.Was(nameof(IMerger.FindThePullRequestAsync)));
        Assert.False(merger.Was(nameof(IMerger.PushTheCommitAsync)));
        Assert.False(merger.Was(nameof(IMerger.MergeThePullRequestAsync)));
    }
}
