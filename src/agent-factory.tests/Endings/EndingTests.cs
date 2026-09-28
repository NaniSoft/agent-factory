namespace AgentFactory.Tests.Endings;

using AgentFactory;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// What the board says about the ways a work item ended. Nothing ends in silence, and
/// Escalated and Rejected are two states with two causes rather than one dead state
/// (ADR-0008): a failure and a human decline are different facts, and a board that
/// rendered them the same way would be hiding which one happened.
/// </summary>
public class EndingTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_parked_work_item_and_a_declined_one_say_why_in_different_words()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: SixRounds(), github: github);

        var exhausted = await Exhausted(host, 42);

        var waiting = await InReview(host, 43);
        using (await Board.DecideAsync(host.Board, waiting.Id, "reject"))
        {
        }

        // Two lanes, and the board renders both, because a state nothing renders is a
        // state nobody is watching.
        var board = await Board.ReadAsync(host.Board);
        Assert.NotEqual(string.Empty, board.Swimlane("Escalated"));
        Assert.NotEqual(string.Empty, board.Swimlane("Rejected"));
        Assert.Contains(exhausted.Id.ToString(), board.Swimlane("Escalated"), StringComparison.Ordinal);
        Assert.Contains(waiting.Id.ToString(), board.Swimlane("Rejected"), StringComparison.Ordinal);

        var parked = board.Rendered("data-work-item", exhausted.Id.ToString(), "data-cause");
        var declined = board.Rendered("data-work-item", waiting.Id.ToString(), "data-cause");

        // Each card names its own state, and each says the thing that is true of it and
        // not of the other. A failure and a decline are different facts, so "it is over"
        // would be a fact about neither: the parked one is out of rounds, and the declined
        // one is final because a human said so.
        Assert.NotEqual(parked, declined);
        Assert.StartsWith("Escalated.", parked, StringComparison.Ordinal);
        Assert.StartsWith("Rejected.", declined, StringComparison.Ordinal);

        Assert.Contains("rounds are spent", parked, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("that is final", parked, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("that is final", declined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rounds are spent", declined, StringComparison.OrdinalIgnoreCase);

        // And each says what a human can still do about it, which is the difference
        // between the two states rather than a difference in wording: a parked work item
        // can be finished from here, and a declined one is finished already.
        Assert.Contains("still merge or decline it from here", parked, StringComparison.Ordinal);
        Assert.DoesNotContain("from here", declined, StringComparison.OrdinalIgnoreCase);

        // The causes are on the cards in words, not only in an attribute.
        Assert.Contains(parked, board.Read("Escalated"), StringComparison.Ordinal);
        Assert.Contains(declined, board.Read("Rejected"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_button_the_board_offers_is_a_decision_the_store_will_keep()
    {
        // The board and the store agree about which lanes a reviewer can act on, and the
        // store is what enforces it. A button the board rendered and the store refused
        // would be a promise the factory does not keep; a decision the store would keep
        // but the board did not offer would be a way around the loop's policy that only a
        // hand-written post could find. So the two are the same set, in every lane.
        foreach (var lane in new[]
                 {
                     Swimlane.Backlog,
                     Swimlane.Frontier,
                     Swimlane.InProgress,
                     Swimlane.Review,
                     Swimlane.Done,
                     Swimlane.Escalated,
                     Swimlane.Rejected,
                 })
        {
            using var root = FactoryRoot.Create();
            var github = new FakeGitHub().Merging();
            await using var host = await FactoryHost.StartAsync(root, agent: ThreeRounds(), github: github);

            var workItem = await In(lane, host);
            var board = await Board.ReadAsync(host.Board);
            var offered = Board.ValuesOf(board.DecisionFormFor(workItem.Id), "data-decision");

            foreach (var decision in Decisions.All)
            {
                var kept = Keeps(host, workItem, decision);
                var shown = offered.Contains(Decisions.Slug(decision), StringComparer.Ordinal);

                Assert.True(
                    kept == shown,
                    $"in {Swimlanes.Label(lane)} the board shows {shown} for {decision} and the store keeps {kept}");
            }
        }
    }

    [Fact]
    public async Task A_parked_work_item_is_offered_exactly_the_two_decisions_that_finish_it()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: SixRounds(), github: github);
        var parked = await Exhausted(host, 42);

        // ADR-0008 and the glossary say the same two things: a human can still merge or
        // reject it. So those are the two the board offers, and the reason a third is not
        // there is not that a fourth way out of a work item exists but that re-opening the
        // build is the retry policy's business (#7).
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal(
            ["approve", "reject"],
            Board.ValuesOf(board.DecisionFormFor(parked.Id), "data-decision"));

        // A work item a reviewer merged is finished, and a human is not asked to decide
        // about one: nothing is offered on a Done card at all.
        var merged = await InReview(host, 43);
        using (await Board.DecideAsync(host.Board, merged.Id, "approve"))
        {
        }

        var after = await Board.ReadAsync(host.Board);
        Assert.Equal(Swimlane.Done, host.Store.Get(merged.Id)!.Swimlane);
        Assert.Equal(string.Empty, after.DecisionFormFor(merged.Id));
        Assert.Equal(["approve", "reject"], Board.ValuesOf(after.DecisionFormFor(parked.Id), "data-decision"));
    }

    [Fact]
    public async Task A_build_that_failed_is_visible_with_what_happened_and_where_it_went()
    {
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .Throwing("the container never wrote a result")
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, agent: agent);

        // A round that came back without a result is a build that failed, and there is
        // nothing there for a reviewer to judge, so it parks rather than pretending to be
        // something reviewable.
        var failed = host.Store
            .Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await host.Settle();
        Assert.Equal(Swimlane.Escalated, host.Store.Get(failed.Id)!.Swimlane);

        // A round that never came back at all is the same kind of thing seen the other
        // way. The round timeout is a comparison against the clock, so this moves time
        // rather than waiting for it.
        var timedOut = host.Store
            .Intake("nexus", RepoUrl, 43, "A work item, end to end", IssueBody, "main")
            .WorkItem;
        await host.Step();
        await host.Step();
        host.Clock.Advance(FactoryConstants.RoundTimeout + TimeSpan.FromMinutes(1));
        await host.Step();

        Assert.Equal(Swimlane.Escalated, host.Store.Get(timedOut.Id)!.Swimlane);
        Assert.True(host.Agent.EndedARound, "the factory ended the round rather than waiting for ever");

        // Both are on the board, in the lane, with the round's own outcome said plainly.
        // Failures are as visible as successes, or the board is a board nobody can trust
        // to say what went wrong.
        var board = await Board.ReadAsync(host.Board);
        var escalated = board.Read("Escalated");

        Assert.Contains(failed.Id.ToString(), escalated, StringComparison.Ordinal);
        Assert.Contains(timedOut.Id.ToString(), escalated, StringComparison.Ordinal);
        Assert.Equal(["Failed", "TimedOut"], Board.ValuesOf(board.Swimlane("Escalated"), "data-outcome"));

        // And each card says which of its rounds went wrong, rather than only that
        // something did.
        Assert.Contains(
            "Failed",
            board.Rendered("data-work-item", failed.Id.ToString(), "data-cause"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "TimedOut",
            board.Rendered("data-work-item", timedOut.Id.ToString(), "data-cause"),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_board_shows_how_much_of_the_ceiling_a_work_item_has_spent()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        await using var host = await FactoryHost.StartAsync(root, agent: ThreeRounds(), github: github);
        var workItem = await Exhausted(host, 42);

        // The count is of rounds run, which is what "round N of 3" has to mean for a
        // reviewer weighing whether to spend their own attention on a fourth.
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal("3", board.Rendered("data-work-item", workItem.Id.ToString(), "data-round-count"));
        Assert.Equal("3", board.Rendered("data-work-item", workItem.Id.ToString(), "data-rounds"));
        Assert.Contains(
            $"round {FactoryConstants.RoundCeiling} of {FactoryConstants.RoundCeiling}",
            board.Read("Escalated"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_item_merged_by_a_reviewer_says_so_and_one_merged_by_silence_says_that_instead()
    {
        using var root = FactoryRoot.Create();
        var github = new FakeGitHub().Merging();
        var agent = new FakeNOpenCode()
            .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
            .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "Second attempt.");
        await using var host = await FactoryHost.StartAsync(root, agent: agent, github: github);

        // One merged because a reviewer read it, and one merged because nobody did. Both
        // are Done, because Done means merged either way — and the two say different
        // things, because a reviewer looking at the board has to be able to tell a
        // reviewed merge from an unattended one.
        var reviewed = await InReview(host, 42);
        using (await Board.DecideAsync(host.Board, reviewed.Id, "approve"))
        {
        }

        var ignored = await InReview(host, 43);
        host.Clock.Advance(FactoryConstants.FeedbackThreshold);
        await host.Settle();

        Assert.Equal(Swimlane.Done, host.Store.Get(reviewed.Id)!.Swimlane);
        Assert.Equal(Swimlane.Done, host.Store.Get(ignored.Id)!.Swimlane);

        var board = await Board.ReadAsync(host.Board);
        var byReviewer = board.Rendered("data-work-item", reviewed.Id.ToString(), "data-cause");
        var bySilence = board.Rendered("data-work-item", ignored.Id.ToString(), "data-cause");

        Assert.NotEqual(byReviewer, bySilence);
        Assert.Contains("reviewer", byReviewer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nobody", bySilence, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(FactoryConstants.FeedbackThresholdText, bySilence, StringComparison.Ordinal);
    }

    /// <summary>Whether the store keeps a decision about this work item, which is what the board offers.</summary>
    private static bool Keeps(FactoryHost host, WorkItem workItem, Decision decision)
    {
        try
        {
            // Recording a decision does not move a work item, so the same one can be asked
            // about all three without the answer depending on the order they were asked in.
            _ = host.Store.RecordDecision(workItem.Id, decision, "Some words the reviewer wrote.");
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<WorkItem> In(Swimlane lane, FactoryHost host)
    {
        var workItem = await InReview(host, 42);

        switch (lane)
        {
            case Swimlane.Review:
                break;

            case Swimlane.Done:
                using (await Board.DecideAsync(host.Board, workItem.Id, "approve"))
                {
                }

                break;

            case Swimlane.Rejected:
                using (await Board.DecideAsync(host.Board, workItem.Id, "reject"))
                {
                }

                break;

            case Swimlane.Escalated:
                workItem = await Exhausted(host, 42);
                break;

            case Swimlane.Frontier:
            case Swimlane.InProgress:
                using (await Board.DecideAsync(host.Board, workItem.Id, "request-changes", "Move the null check inside the lock."))
                {
                }

                if (lane == Swimlane.InProgress)
                {
                    // One step, which starts the round. It is not awaited, so the work item
                    // is In Progress with a round in flight.
                    await host.Step();
                }

                break;

            case Swimlane.Backlog:
                workItem = host.Store
                    .Intake("nexus", RepoUrl, 43, "A second work item", IssueBody, "main")
                    .WorkItem;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(lane), lane, "every lane is covered");
        }

        Assert.Equal(lane, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    /// <summary>A work item with one round behind it, sitting in Review.</summary>
    private static async Task<WorkItem> InReview(FactoryHost host, int issueNumber = 42)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, "A work item, end to end", IssueBody, "main")
            .WorkItem;

        await host.Settle();

        Assert.Equal(Swimlane.Review, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    /// <summary>Three rounds the reviewer keeps sending back, and the work item left parked.</summary>
    private static async Task<WorkItem> Exhausted(FactoryHost host, int issueNumber = 42)
    {
        var workItem = await InReview(host, issueNumber);

        foreach (var words in new[] { "Move the null check inside the lock.", "Still outside.", "Third time." })
        {
            using var response = await Board.DecideAsync(host.Board, workItem.Id, "request-changes", words);
            Assert.Null((await Board.ReadAsync(response)).Refusal());
            await host.Settle();
        }

        Assert.Equal(Swimlane.Escalated, host.Store.Get(workItem.Id)!.Swimlane);
        return workItem;
    }

    private static FakeNOpenCode ThreeRounds() => new FakeNOpenCode()
        .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +15 -3", "Third attempt.");

    /// <summary>
    /// Three more on top, for a test that exhausts one work item and then needs a second
    /// in Review beside it. The ceiling is per work item, so the second gets its own
    /// rounds rather than what the first left over.
    /// </summary>
    private static FakeNOpenCode SixRounds() => new FakeNOpenCode()
        .Yielding(RoundOutcome.Produced, "src/Index.cs +12 -3", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +14 -3", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Index.cs +15 -3", "Third attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +4 -0", "First attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +6 -0", "Second attempt.")
        .Yielding(RoundOutcome.Produced, "src/Review.cs +8 -0", "Third attempt.");
}
