namespace AgentFactory.Tests.Review;

using System.Net;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// Approve means merged. The design says so — Done is "approved and merged", and approve
/// "triggers the auto-merge" — so an approval is not complete until a merge has actually
/// landed through the GitHub seam. Driven over the board's own HTTP surface, the way a
/// reviewer approves, with only the agent, the clock and GitHub faked. There is no merger
/// behind the seam, so the default here is an approval that cannot be carried out: which
/// is what a factory without a merger must do rather than report a merge that did not
/// happen.
/// </summary>
public class ApproveTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    private const string Merged = "nexus#42 was approved, but the change was not merged: "
        + "there is no merger behind this seam yet: merging is the merger's business, and the merger is not built. "
        + "It is still in Review, and nothing shipped.";

    [Fact]
    public async Task Approving_asks_the_seam_to_merge_and_a_work_item_reaches_done_only_when_one_landed()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using var response = await Board.DecideAsync(host.Board, workItem.Id, "approve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The merge was asked for through the one GitHub seam, against the repository the
        // work item was built for and the issue it answers. Not opened separately and not
        // merged by hand: one call, and Done is what it answers.
        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);

        // And only because it landed, the work item is Done and the decision says it is
        // what moved it. The record is the reviewer's approval and the loop's answer to it,
        // kept together.
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.Equal(Decision.Approve, recorded.Decision);
        Assert.Equal(Swimlane.Done, recorded.AppliedTo);
        Assert.False(recorded.IsPending);

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);
        Assert.DoesNotContain(workItem.Id.ToString(), board.Swimlane("Review"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approval_the_seam_could_not_carry_out_is_not_done_and_says_so()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // The reviewer approves, and the seam refuses, because there is no merger behind it.
        // The decision is a thing the reviewer did whether or not it could be carried out.
        using var response = await Board.DecideAsync(host.Board, workItem.Id, "approve");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);

        // The work item did not ship and does not say it did. Done means merged, so an
        // approval that could not be merged is not Done — and it is not Escalated either,
        // because which lane a failed merge goes to is the escalation ticket's (#6) and a
        // lane invented here would be that policy decided twice.
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.DoesNotContain(host.Store.List(), item => item.Swimlane == Swimlane.Done);

        var board = await Board.ReadAsync(host.Board);
        Assert.Empty(Board.ValuesOf(board.Swimlane("Done"), "data-work-item"));
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Review"), StringComparison.Ordinal);

        // The approval itself is not lost. It is on the record, in the order it was made,
        // marked as what the loop did with it — and what the loop did with it was leave the
        // work item in Review, which is exactly what a reviewer reading the board needs to
        // know: approved, not shipped.
        var recorded = Assert.Single(host.Store.Decisions(workItem.Id));
        Assert.Equal(Decision.Approve, recorded.Decision);
        Assert.Equal(host.Clock.UtcNow, recorded.DecidedUtc);
        Assert.Equal(Swimlane.Review, recorded.AppliedTo);
        Assert.False(recorded.IsPending, "the loop has acted on it; its answer was not Done");

        var rendered = Assert.Single(board.DecisionsOn(workItem.Id));
        Assert.Equal("approve", rendered.Decision);
        Assert.Equal("Review", rendered.AppliedTo);

        // And it is not silent: the loop's refusal comes back on the response the reviewer
        // is holding, in the reviewer's own terms, saying the merge did not happen and
        // nothing shipped. A refusal on the response rather than on the next board read,
        // because reading the board again would find a clean page and lose it.
        Assert.Equal(Merged, (await Board.ReadAsync(response)).Refusal());
    }

    [Fact]
    public async Task A_work_item_that_did_not_merge_still_offers_a_decision_and_can_be_approved_again()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        // It is in Review, so the board renders the decision form for it and the reviewer
        // has something to press. A work item parked where nothing can be done about it
        // would be the alternative, and the reviewer is the one who can still finish it.
        var stuck = await Board.ReadAsync(host.Board);
        Assert.NotEqual(string.Empty, stuck.DecisionFormFor(workItem.Id));

        // The reviewer approves again, now with a merger behind the seam. The second
        // approval is a second decision, kept whole beside the first: neither erases the
        // other, so the record says the work item was approved twice and merged once.
        github.Merging();
        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Equal([new MergeAttempt(RepoUrl, 42), new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);

        var decided = host.Store.Decisions(workItem.Id).ToList();
        Assert.Equal(2, decided.Count);
        Assert.All(decided, decision => Assert.Equal(Decision.Approve, decision.Decision));
        Assert.Equal(Swimlane.Review, decided[0].AppliedTo);
        Assert.Equal(Swimlane.Done, decided[1].AppliedTo);

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);

        // And the page the reviewer was holding throughout — read before the second
        // approval, so before anything merged — showed an empty Done lane, which is what
        // they were looking at when they pressed approve again.
        Assert.DoesNotContain(workItem.Id.ToString(), stuck.Swimlane("Done"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_done_lane_is_unreachable_by_approving_alone()
    {
        // The defect this ticket exists for: Approve was mapped straight to Done with no
        // merge call anywhere in the application, so a reviewer watched a work item move to
        // Done and nothing shipped. Everything below is written so that putting the mapping
        // back — approve to Done without asking the seam — fails here.
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        // Two work items, both approved, and the seam refusing every time. Two rather than
        // one because a loop that merges one and forgets another is still a loop that
        // reports a merge that did not happen.
        var first = await InReview(host, 42);
        var second = await InReview(host, 43);

        using (await Board.DecideAsync(host.Board, first.Id, "approve"))
        {
        }

        using (await Board.DecideAsync(host.Board, second.Id, "approve"))
        {
        }

        Assert.Equal(
            [new MergeAttempt(RepoUrl, 42), new MergeAttempt(RepoUrl, 43)],
            github.Merges);
        Assert.DoesNotContain(host.Store.List(), item => item.Swimlane == Swimlane.Done);

        var board = await Board.ReadAsync(host.Board);

        // Not one work item on the board's Done lane. The lane exists and is rendered —
        // it is the claim, not the lane, that is false.
        Assert.NotEqual(string.Empty, board.Swimlane("Done"));
        Assert.Empty(Board.ValuesOf(board.Swimlane("Done"), "data-work-item"));

        // And the loop is not a special case: asked again, with nothing to apply and
        // nothing to merge, it leaves both where the reviewer can still finish them.
        await host.Settle();

        Assert.DoesNotContain(host.Store.List(), item => item.Swimlane == Swimlane.Done);
        Assert.Equal(
            [new MergeAttempt(RepoUrl, 42), new MergeAttempt(RepoUrl, 43)],
            github.Merges);
    }

    [Fact]
    public async Task An_approval_that_was_never_carried_out_is_not_tried_again_by_the_loop()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge("branch protection would not let it through");
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
        {
        }

        Assert.Single(github.Merges);

        // Stepping the machine again does not try the merge again. Whether a merge that
        // failed is worth another attempt, how many, how soon, and telling a transient
        // failure from a permanent one, is the retry ticket's (#7). What this asserts is
        // only that nothing here retries on its own: an unclassified, unattended, unbounded
        // retry loop is not this ticket's fix to ship, and it would be worse than the one
        // attempt a reviewer can see and make again themselves.
        await host.Settle();
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Single(github.Merges);

        // Not across a restart either, for the same reason. An approval the loop cannot
        // carry out stays applied and stays visible; the reviewer decides whether to try
        // again, and the loop does not quietly re-run a merge behind their back.
        await using (var restarted = await FactoryHost.StartAsync(root, agent: agent, github: github))
        {
            await restarted.Settle();
            Assert.Single(github.Merges);
            Assert.Equal(Swimlane.Review, restarted.Store.Get(workItem.Id)!.Swimlane);
        }
    }

    [Fact]
    public async Task A_failed_merge_does_not_stop_the_loop_building_the_next_work_item()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().RefusingToMerge("the change could not be merged");
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        var stuck = await InReview(host, 42);
        var next = host.Store.Intake("nexus", RepoUrl, 43, "Another work item", IssueBody, "main").WorkItem;

        using (await Board.DecideAsync(host.Board, stuck.Id, "approve"))
        {
        }

        // One work item's merge failing is contained to that work item. The pipeline is not
        // held by it, and one refusal does not wedge the machine for everything behind it:
        // the work item behind it is accepted, built and waiting on a reviewer of its own.
        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(stuck.Id)!.Swimlane);
        Assert.Equal(Swimlane.Review, host.Store.Get(next.Id)!.Swimlane);
        Assert.Equal(1, host.Store.Get(next.Id)!.RoundCount);
        Assert.Single(host.Store.Rounds(next.Id));
        Assert.Single(github.Merges);
    }

    [Fact]
    public async Task The_loop_refuses_a_decision_it_does_not_understand_without_merging_anything()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // Four not-decisions, against a merger that would land every merge it was asked
        // for. None of them is an approval, so nothing is merged: the loop cannot be talked
        // into a merge by a value the board did not offer a reviewer.
        foreach (var decision in new[] { "shelve", "merge", "0", "" })
        {
            using var response = await Board.PostByHandAsync(host.Board, workItem.Id, decision, "Some words.");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
            Assert.Empty(host.Store.Decisions(workItem.Id));
        }

        Assert.Empty(github.Merges);
    }

    /// <summary>A work item with a round behind it, sitting in Review where a reviewer can decide.</summary>
    private static async Task<WorkItem> InReview(FactoryHost host, int issueNumber = 42)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }
}
