namespace AgentFactory.Tests.Endings;

using AgentFactory;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The other way a work item stops looping without a human finishing it: nobody looking.
/// A work item left in Review past the feedback threshold is merged, because a pipeline
/// held by a reviewer who is asleep is a pipeline that has stopped, and the design says
/// so plainly rather than inferring it (ADR-0008).
/// </summary>
/// <remarks>
/// The merge is a merge. It goes through the same <c>IGitHub</c> seam an approve does and
/// obeys the same rule, so a Done here is a claim about a repository and not about a
/// clock: with nothing behind the seam, the timeout cannot complete either, and a work
/// item that has been ignored for two days must not report a merge that did not happen.
/// </remarks>
public class SilenceTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_work_item_left_in_review_past_the_threshold_is_merged()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        var waitingSince = host.Store.Get(workItem.Id)!.ReviewStartedUtc;
        Assert.NotNull(waitingSince);
        Assert.Equal(host.Clock.UtcNow, waitingSince);

        // A second short of the threshold, nothing happens. The comparison is against the
        // constant and the clock, not against a timer, so time here is moved rather than
        // waited on.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold - TimeSpan.FromSeconds(1));
        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        // The last second, and the factory does what it says it will: the work item ships.
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.Settle();

        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);

        // And only a merge that landed puts it there. The work item's own lane is the
        // claim the board makes about a repository, and there was no reviewer approval on
        // this one — there was no reviewer at all.
        Assert.Empty(host.Store.Decisions(workItem.Id));

        // Said out loud, because this is the more dangerous of the two paths and a
        // work item that merged itself has to say so on the board rather than looking like
        // one a human merged.
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);
        var cause = board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause");
        Assert.Contains("nobody", cause, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FactoryConstants.FeedbackThresholdText, cause, StringComparison.Ordinal);

        // Once, and once only: Done is outside the timeout's reach, so no amount of
        // further time produces a second merge.
        host.Clock.Advance(TimeSpan.FromDays(4));
        await host.Settle();

        Assert.Single(github.Merges);
    }

    [Fact]
    public async Task A_merge_the_timeout_could_not_land_does_not_report_Done()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // Nobody reviewed it and there is no merger behind the seam, which is the state
        // the factory is actually in today. Ignoring this work item must not turn into a
        // claim that something shipped.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold);
        await host.Settle();

        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.DoesNotContain(host.Store.List(), item => item.Swimlane == Swimlane.Done);

        // It parks, where a human can finish it and where the loop will leave it alone.
        // A merge that did not land is a failure, and Escalated is what a failure is.
        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);

        await host.Settle();
        host.Clock.Advance(TimeSpan.FromDays(4));
        await host.Settle();

        Assert.Single(github.Merges);
    }

    [Fact]
    public async Task Ignoring_a_work_item_merges_it_and_declining_one_protects_the_repository()
    {
        // The asymmetry, in one test, because it is the design's whole point about
        // governing the timeout path (ADR-0008). The same factory, the same clock, the
        // same threshold, the same seam that would land every merge asked of it, and two
        // work items sitting in Review side by side. The only difference between them is
        // what a reviewer did: nothing to one, and a decline to the other. That is the
        // whole of the danger in the timeout path — it is the only way a change reaches
        // the repository without anyone having read it.
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        var ignored = await InReview(host, 42);
        var declined = await InReview(host, 43);

        // A reviewer who is paying attention declines one of them. The other one gets
        // nothing, which is the state the timeout exists to act on.
        using (await Board.DecideAsync(host.Board, declined.Id, "reject"))
        {
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(declined.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        // A post made by hand cannot reopen the declined one, so nothing about the
        // threshold below is an approval slipping through.
        using (await Board.PostByHandAsync(host.Board, declined.Id, "approve"))
        {
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(declined.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        // Forty-eight hours. The reviewer is asleep, and the pipeline does not stop.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromHours(1));
        await host.Settle();

        // Ignored: merged, because nothing objected and the design says silence is a
        // decision. Declined: still declined, and no merge was ever asked for on its
        // behalf — one attempt, and it was the other work item's.
        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(ignored.Id)!.Swimlane);
        Assert.Equal(Swimlane.Rejected, host.Store.Get(declined.Id)!.Swimlane);

        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(ignored.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);
        Assert.Contains(declined.Id.ToString(), board.Swimlane("Rejected"), StringComparison.Ordinal);
        Assert.DoesNotContain(declined.Id.ToString(), board.Swimlane("Done"), StringComparison.Ordinal);

        // And it stays that way. A decline is final, so no amount of further silence
        // brings the declined one back into the timeout's reach.
        host.Clock.Advance(TimeSpan.FromDays(4));
        await host.Settle();

        Assert.Single(github.Merges);
        Assert.Equal(Swimlane.Rejected, host.Store.Get(declined.Id)!.Swimlane);
    }

    [Fact]
    public async Task A_work_item_sent_back_and_reviewed_again_gets_the_threshold_over_again()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);
        var workItem = await InReview(host);

        // Nearly the whole threshold passes with the work item waiting on a reviewer,
        // and then the reviewer is there: the round is sent back, and the work item is
        // back in the build.
        host.Clock.Advance(FactoryConstants.FeedbackThreshold - TimeSpan.FromMinutes(1));
        using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Move the null check inside the lock."))
        {
        }

        Assert.Equal(Swimlane.Frontier, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Null(host.Store.Get(workItem.Id)!.ReviewStartedUtc);

        await host.Settle();
        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);

        // A work item that left Review is not one that has been left in Review. Its second
        // review starts its own threshold from the moment it re-entered, so it is not
        // merged a minute after a reviewer has just looked at it — even though the first
        // review's threshold expired while the round was running.
        var secondReview = host.Store.Get(workItem.Id)!.ReviewStartedUtc;
        Assert.NotNull(secondReview);
        Assert.Equal(host.Clock.UtcNow, secondReview);

        host.Clock.Advance(FactoryConstants.FeedbackThreshold - TimeSpan.FromSeconds(1));
        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        Assert.Empty(github.Merges);

        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.Settle();

        Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        Assert.Equal(Swimlane.Done, host.Store.Get(workItem.Id)!.Swimlane);
    }

    [Fact]
    public async Task The_threshold_is_measured_across_a_restart_and_merged_only_once()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        var clock = new TestClock();

        Guid id;
        await using (var first = await FactoryHost.StartAsync(root, clock: clock, agent: agent, github: github))
        {
            id = (await InReview(first)).Id;
            clock.Advance(FactoryConstants.FeedbackThreshold - TimeSpan.FromHours(1));
            await first.Settle();
            Assert.Empty(github.Merges);
        }

        // The wait is a fact about the work item, not about a process's memory, so a
        // restart neither forgets the reviewer was already given the threshold nor merges
        // early to catch up.
        await using (var second = await FactoryHost.StartAsync(root, clock: clock, agent: agent, github: github))
        {
            await second.Settle();
            Assert.Equal(Swimlane.Review, second.Store.Get(id)!.Swimlane);
            Assert.Empty(github.Merges);

            clock.Advance(TimeSpan.FromHours(1));
            await second.Settle();
            Assert.Equal([new MergeAttempt(RepoUrl, 42)], github.Merges);
        }

        // And a further restart does not merge it a second time.
        await using (var third = await FactoryHost.StartAsync(root, clock: clock, agent: agent, github: github))
        {
            await third.Settle();
            Assert.Single(github.Merges);
            Assert.Equal(Swimlane.Done, third.Store.Get(id)!.Swimlane);
        }
    }

    [Fact]
    public async Task The_threshold_is_one_named_code_constant_and_the_board_says_what_it_is()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent);
        await InReview(host);

        // The threshold is code and only code: forty-eight hours, named once, on the one
        // class every project gets it from. It is not a project file field, and the
        // project file schema refuses a field beyond its six, so no project can have a
        // different threshold from any other.
        Assert.Equal(TimeSpan.FromHours(48), FactoryConstants.FeedbackThreshold);

        // Which means a reviewer can find out, on the board, what their silence is worth
        // — before it happens rather than after.
        var board = await Board.ReadAsync(host.Board);

        // On the board, as the number and in the sentence that says what it does, so a
        // reviewer is not left to find it in the source.
        Assert.Equal(
            FactoryConstants.FeedbackThreshold.TotalHours.ToString("0"),
            board.Rendered("data-feedback-threshold", "threshold", "data-hours"));
        Assert.Contains(FactoryConstants.FeedbackThresholdText, board.Html, StringComparison.Ordinal);
        Assert.Contains("merged", board.Html, StringComparison.OrdinalIgnoreCase);

        // And the asymmetry is on the same surface, because a reviewer deciding whether to
        // look now needs both halves of it: what silence does, and what declining does.
        Assert.Contains("Escalated", board.Html, StringComparison.Ordinal);
        Assert.Contains("declining one protects the repository", board.Html, StringComparison.Ordinal);

        // Every work item waiting in Review says when its own silence will ship it, because
        // the timeout is the more dangerous of the two paths and the board is where it is
        // governed (ADR-0008).
        var id = board.ValuesOf("data-work-item").Single();
        var since = host.Store.Get(Guid.Parse(id))!.ReviewStartedUtc;
        Assert.NotNull(since);

        // Read as a browser would, because the board escapes what it writes into an
        // attribute and asserting on the raw markup would be asserting on the escaping.
        var expected = since.Value + FactoryConstants.FeedbackThreshold;
        Assert.Equal(
            expected.ToString("O"),
            System.Net.WebUtility.HtmlDecode(
                board.Rendered("data-work-item", id, "data-auto-merge")));
    }

    /// <summary>A work item with one round behind it, sitting in Review where a reviewer can decide.</summary>
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
