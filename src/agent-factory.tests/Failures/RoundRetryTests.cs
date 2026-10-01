namespace AgentFactory.Tests.Failures;

using AgentFactory;
using AgentFactory.Failures;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// A round that failed, and what the factory does about it. The load-bearing distinction
/// of the whole ticket: a round that never got to run — a container that would not start,
/// an image that would not pull — is a transient failure and is worth another attempt,
/// while a round that ran and whose change failed is the answer, and retrying it is an
/// expensive way to hide the result. DESIGN.md says so in as many words, and these tests
/// are written so that an implementation which retries everything fails on the count
/// rather than on a word.
/// </summary>
/// <remarks>
/// Every test here counts <em>attempts</em>, not merely the state the work item ended in.
/// A test that only checks "it ended up escalated" passes whether the factory retried
/// three times or zero, which is the defect this file exists to make impossible. Attempts
/// are read off the fake agent's own list of the rounds it was asked for, and off the
/// attempt count recorded against the round.
/// </remarks>
public class RoundRetryTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private const string IssueBody = "What the issue says, in the maintainer's words.";

    [Fact]
    public async Task A_transient_failure_is_retried_and_the_round_that_succeeds_is_the_one_reviewed()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .Producing("src/Index.cs +12 -3", "Added the endpoint.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        // The first attempt fails transiently, which is not the same thing as a round that
        // ran and failed: the round has not ended, so the work item stays where it is,
        // holding its slot, and nothing is recorded against it yet.
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));
        Assert.Empty(host.Store.Rounds(workItem.Id));
        Assert.Equal(0, RoundCountOf(host, workItem.Id));

        // A second attempt is refused until the backoff has passed. The backoff is a
        // comparison against the clock, so the number of seconds are written out here: a
        // test that advanced by the constant would prove only that the loop compares two
        // values, not that the wait is the one the factory claims.
        clock.Advance(TimeSpan.FromSeconds(9));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
        Assert.Empty(host.Store.Rounds(workItem.Id));

        // The tenth second, and the round is asked for again. Same round, second attempt:
        // a container that would not start is not a second round, and the ceiling and the
        // board's "round 1 of 3" are both about rounds.
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, workItem.Id));

        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.Produced, round.Outcome);
        Assert.Equal(2, round.Attempts);
        Assert.Null(round.Failure);
        Assert.Equal(1, RoundCountOf(host, workItem.Id));

        // And the board says which round it was, and that it took two tries — because a
        // reviewer looking at a result wants to know the container fought the factory a
        // little on the way to producing it.
        var board = await Board.ReadAsync(host.Board);
        Assert.Equal(["2"], Board.ValuesOf(board.Swimlane("Review"), "data-attempts"));
        Assert.Equal("1", board.Rendered("data-work-item", workItem.Id.ToString(), "data-round-count"));
        Assert.Contains("2 attempts", board.Read("Review"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_three_times_and_then_the_work_item_is_parked()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create();

        // Three failures scripted, and nothing else. A fourth attempt would find the fake
        // with an empty script, which is how a factory that asks for a fourth attempt is
        // caught: the count of AskedFor is the assertion, and it is made before the
        // scripted rounds run out.
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        // The number is written out rather than read from the constant.
        Assert.Equal(3, FactoryConstants.TransientRetryAttempts);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Single(agent.AskedFor);

        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));

        // The second backoff is longer than the first: 20 seconds rather than 10.
        clock.Advance(TimeSpan.FromSeconds(19));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(2, agent.AskedFor.Count);

        clock.Advance(TimeSpan.FromSeconds(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(3, agent.AskedFor.Count);

        // The third attempt failed and the budget is spent, so the round ends. Retries
        // exhausted escalates: the work item is parked where a human can still merge or
        // decline it, and never merged over (ADR-0008).
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));

        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.Failed, round.Outcome);
        Assert.Equal(FailureClass.Transient, round.Failure);
        Assert.Equal(3, round.Attempts);

        // And a fourth attempt is not merely absent from the record: it is not reachable.
        // Time passes, the machine is stepped, and the factory restarts — none of which
        // buys a fourth attempt, because the attempts are counted on the round and the
        // ceiling of a transient failure is not reset by any of them.
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        host.Clock.Advance(TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        await using (var restarted = await FactoryHost.StartAsync(root, clock, agent))
        {
            await restarted.SettleWithin(TimeSpan.FromSeconds(30));

            Assert.Equal(3, agent.AskedFor.Count);
            Assert.Equal(3, restarted.Store.Rounds(workItem.Id).Single().Attempts);
            Assert.Equal(Swimlane.Escalated, restarted.Store.Get(workItem.Id)!.Swimlane);
        }
    }

    [Fact]
    public async Task A_permanent_failure_parks_the_work_item_without_a_second_attempt()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().FailingPermanently();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));

        // One attempt. Not two, and not three: a permanent failure escalates directly,
        // without retrying, because there is nothing a second attempt would change.
        Assert.Single(agent.AskedFor);

        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.Failed, round.Outcome);
        Assert.Equal(FailureClass.Permanent, round.Failure);
        Assert.Equal(1, round.Attempts);
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));

        // And it stays one, whatever else happens to the factory. Bounded settles, because
        // a retry policy that had lost its ceiling would never become Idle.
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        host.Clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromDays(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));
    }

    [Fact]
    public async Task A_build_that_failed_its_tests_is_not_retried_even_once()
    {
        // The test the whole ticket turns on, and the one a well-meaning "retry every
        // failure" implementation fails. DESIGN.md: "A build that fails its tests is not
        // a transient failure, and retrying it is an expensive way to hide the result."
        //
        // A round that ran, changed files, and left the repository's own tests failing
        // came back *with a result*: the container worked, and the failure is data inside
        // the result. So it is a Produced round, it goes to Review where a reviewer reads
        // the failing tests, and it is not retried — not because a counter was consulted
        // but because a result carries no classification to retry on.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Producing(
            """{"kind":"result","roundExitCode":1,"head":"59a7684"} {"kind":"test","name":"dotnet test","passed":0,"failed":41}""",
            "I could not make the tests pass.");
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));

        // ONE call to the agent. Not two, not three, not on any schedule.
        Assert.Single(agent.AskedFor);
        Assert.Single(agent.AskedFor, round => round.WorkItemId == workItem.Id);

        // The round is recorded as what it is: a round that produced a result, with no
        // failure classification at all, and one attempt.
        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.Produced, round.Outcome);
        Assert.Null(round.Failure);
        Assert.Equal(1, round.Attempts);

        // And the reviewer gets it, rather than a park: the tests that failed are the
        // reason there is a review at all.
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, workItem.Id));

        // The strongest form of the claim: no amount of time, no restart, and a seam that
        // would merge anything, turns a failed test run into another attempt. Bounded, so
        // that a policy which did retry it fails here with a message rather than hanging.
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(FactoryConstants.FeedbackThreshold + TimeSpan.FromDays(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
        Assert.Equal(1, host.Store.Rounds(workItem.Id).Single().Attempts);
    }

    [Fact]
    public async Task A_round_that_ran_past_the_round_timeout_is_handled_as_a_failure_and_is_not_retried()
    {
        // The spec's own state machine gives a timeout a row of its own:
        // "In Progress | round timed out | Escalated". So a round that never came back is
        // a failure and it is handled as one — the round ends, the work item parks, and
        // there is no retry. A round that hung for ninety minutes is the most expensive
        // thing the factory can do, and nothing in the record distinguishes "the daemon was
        // slow" from "the session deadlocks", so four ninety-minute containers is a way of
        // hiding a bug behind a policy.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, workItem.Id));
        Assert.Single(agent.AskedFor);

        // The number is written out rather than read from the constant.
        Assert.Equal(TimeSpan.FromMinutes(90), FactoryConstants.RoundTimeout);

        clock.Advance(FactoryConstants.RoundTimeout + TimeSpan.FromSeconds(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        // The round ended: the factory stopped waiting for it, told it to stop, and the
        // container runtime removed the container with it.
        Assert.True(agent.EndedARound, "the factory ends the round it stopped waiting for");
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));

        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.TimedOut, round.Outcome);
        Assert.Null(round.Failure);
        Assert.Equal(1, round.Attempts);

        // Handled as a failure, and not retried: one attempt, for ever.
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromDays(4));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
    }

    [Fact]
    public async Task A_round_that_throws_an_unclassified_failure_is_not_retried()
    {
        // The safe default, and the one the seam's own contract rests on: an exception that
        // has not classified itself is not retried. A retry nobody classified is exactly
        // the unbounded unattended retry that the parking of a failed merge exists to
        // prevent, and the fake's exception is deliberately an ordinary one.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode().Throwing("something went wrong inside the seam");
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Single(agent.AskedFor);
        var round = Assert.Single(host.Store.Rounds(workItem.Id));
        Assert.Equal(RoundOutcome.Failed, round.Outcome);
        Assert.Equal(FailureClass.Permanent, round.Failure);
        Assert.Equal(1, round.Attempts);
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));
    }

    [Fact]
    public async Task A_round_waiting_to_be_retried_holds_its_slot_and_three_of_them_cannot_hold_the_budget()
    {
        // **This test changed shape, deliberately, for the container budget.** It used to
        // take two work items and assert the second stayed in Backlog while the first's
        // round waited out its backoff — the #3 shape, where a slot was a boolean. The
        // budget makes that false by construction, and the property underneath it is worth
        // more at the budget than below it: a round waiting forty seconds has not ended,
        // so it still holds a container, and a third work item waits for one that has not
        // been given away.
        //
        // It also pins the confusion this shape is most likely to fall into. The attempt
        // ceiling is three attempts *at one round* and the budget is two rounds *at once*;
        // they are not the same number and neither may be read as the other. Three rounds
        // waiting out a backoff cannot happen here, because the third round is never
        // started — which is the whole point, and the reason the assertion is on the count
        // of rounds asked for rather than on the lanes.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);

        var first = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(first.Id);
        var second = host.Store.Intake("nexus", RepoUrl, 43, "Another work item", IssueBody, "main").WorkItem;
        await host.PromoteAsync(second.Id);
        var third = host.Store.Intake("nexus", RepoUrl, 44, "A work item with no container", IssueBody, "main").WorkItem;
        await host.PromoteAsync(third.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));

        // Two rounds, both failed transiently, both waiting out their backoff and both
        // still In Progress. Each is holding one of the two containers, and the third work
        // item has not been asked for a round at all.
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, first.Id));
        Assert.Equal(Swimlane.InProgress, SwimlaneOf(host, second.Id));
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
        Assert.Equal(2, agent.AskedFor.Count);
        Assert.Equal(2, host.RoundsInFlight);

        // The first backoff passes and both are asked for again, as the same two rounds. The
        // third work item is still not asked for: the budget counts rounds, and both of them
        // are spoken for however many attempts each has had. Two attempts apiece is the
        // point at which this is visible — a round on its third attempt has not become
        // three rounds, and the attempt ceiling of three is not a concurrency budget.
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(4, agent.AskedFor.Count);
        Assert.Equal(2, agent.AskedFor.Select(round => round.WorkItemId).Distinct().Count());
        Assert.DoesNotContain(agent.AskedFor, round => round.WorkItemId == third.Id);

        // And the third is waiting in Frontier rather than refused: it is a queue position,
        // not a rejection, and it starts the moment a container comes back.
        Assert.Equal(Swimlane.Frontier, SwimlaneOf(host, third.Id));
        Assert.Equal(2, host.RoundsInFlight);
    }

    [Fact]
    public async Task Every_way_a_round_can_fail_ends_in_a_lane_the_board_renders()
    {
        // Nothing ends in silence, which is the ticket's own acceptance criterion and the
        // only way to know it: one work item per way a round can fail, every one of them
        // in a lane the board draws, every one of them with a cause that says what
        // happened rather than only that something did.
        //
        // The work items are taken one at a time and settled before the next is created,
        // rather than two up front. With one round in flight that made no difference, and
        // with a budget of two it is the difference between a test that says what it means
        // and one that says it by accident: the fake agent is scripted in the order rounds
        // are asked for, so two work items in flight at once would each be described by
        // whichever script entry they happened to reach. Sequential intake makes "the third
        // attempt failed" mean the third attempt of that work item.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently()
            .FailingPermanently()
            .Producing("src/Review.cs +4 -0", "This one is fine.")
            .Throwing("the container died mid-round")
            .Stuck();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);

        var exhausted = await TakeOne(host, 42);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, exhausted.Id));

        var permanent = await TakeOne(host, 43);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, permanent.Id));

        var produced = await TakeOne(host, 44);
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.Review, SwimlaneOf(host, produced.Id));

        var threw = await TakeOne(host, 45);
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, threw.Id));

        var stuck = await TakeOne(host, 46);
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(FactoryConstants.RoundTimeout + TimeSpan.FromSeconds(1));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, stuck.Id));

        // Six ways of failing, and every one of them is on the board with a lane and a
        // cause. Three of them are Escalated cards that read differently from each other,
        // which is what "distinct states with distinct causes" has to mean in practice.
        var board = await Board.ReadAsync(host.Board);
        var escalated = board.Read("Escalated");

        foreach (var parked in new[] { exhausted, permanent, threw, stuck })
        {
            Assert.Contains(parked.Id.ToString(), escalated, StringComparison.Ordinal);
            Assert.NotEqual(
                string.Empty,
                board.Rendered("data-work-item", parked.Id.ToString(), "data-cause"));
        }

        // The three causes are told apart: exhausted retries, a permanent failure, and a
        // round that never came back. A test failure is not among them, because it is not
        // a failure of the round at all — it is a result, in Review.
        Assert.Contains("transient", CauseOf(board, exhausted), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 attempts", CauseOf(board, exhausted), StringComparison.Ordinal);
        Assert.Contains("permanent", CauseOf(board, permanent), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("permanent", CauseOf(board, threw), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TimedOut", CauseOf(board, stuck), StringComparison.Ordinal);
        Assert.DoesNotContain("transient", CauseOf(board, permanent), StringComparison.OrdinalIgnoreCase);

        // And the classification is on the round itself, so a reviewer can see why it was
        // tried three times rather than having to infer it from the card.
        Assert.Contains(
            FailureClass.Transient.ToString(),
            Board.ValuesOf(board.Swimlane("Escalated"), "data-failure"));
        Assert.Contains(
            FailureClass.Permanent.ToString(),
            Board.ValuesOf(board.Swimlane("Escalated"), "data-failure"));
    }

    [Fact]
    public async Task A_parked_work_item_with_rounds_to_spare_is_not_re_opened_by_anything()
    {
        // The question #6 left here, decided: no. A work item parked by a failure has spent
        // the retry policy, and re-opening it would mint a fresh budget for the same
        // failure while spending rounds that are a reviewer's to spend (ADR-0001, ADR-0008).
        //
        // So a work item can end up parked with two of its three rounds unspent, and they
        // stay unspent. That is the honest cost of the decision and it is said rather than
        // left to be discovered: the ceiling is a brake on the agent and a reviewer
        // disagreeing, and a work item the factory could not build is not a disagreement.
        // What a reviewer has is what they always had — two clicks, merge or decline, from
        // the board — and the board's decision set has not grown by a single one to
        // accommodate it.
        var clock = new TestClock();
        using var root = FactoryRoot.Create();
        var agent = new FakeNOpenCode()
            .FailingTransiently()
            .FailingTransiently()
            .FailingTransiently();
        await using var host = await FactoryHost.StartAsync(root, clock, agent);
        var workItem = host.Store.Intake("nexus", RepoUrl, 42, "A work item, end to end", IssueBody, "main").WorkItem;
        await host.PromoteAsync(workItem.Id);

        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(10));
        await host.SettleWithin(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        await host.SettleWithin(TimeSpan.FromSeconds(30));

        Assert.Equal(Swimlane.Escalated, SwimlaneOf(host, workItem.Id));
        Assert.Equal(1, RoundCountOf(host, workItem.Id));
        Assert.Equal(3, agent.AskedFor.Count);

        // A month, many steps, a restart. No fourth attempt and no second round: the loop
        // has no transition out of a parked work item, so this is not merely refused. A
        // bounded number of steps rather than settles, because a policy that re-opened a
        // parked work item would be a step that never becomes Idle.
        for (var step = 0; step < 20; step++)
        {
            for (var one = 0; one < 5; one++)
            {
                await host.Step();
            }

            clock.Advance(TimeSpan.FromDays(1));
        }

        await using (var restarted = await FactoryHost.StartAsync(root, clock, agent))
        {
            await restarted.SettleWithin(TimeSpan.FromSeconds(30));

            Assert.Equal(Swimlane.Escalated, restarted.Store.Get(workItem.Id)!.Swimlane);
            Assert.Equal(3, agent.AskedFor.Count);
            Assert.Equal(1, restarted.Store.Get(workItem.Id)!.RoundCount);
            Assert.Single(restarted.Store.Rounds(workItem.Id));
        }

        // And the board still offers a reviewer exactly the two decisions that finish a
        // parked work item. If a re-open had been the policy, this is where a fourth
        // button would have appeared, and it is the thing the ticket is not doing.
        var board = await Board.ReadAsync(host.Board);
        Assert.Contains(workItem.Id.ToString(), board.Swimlane("Escalated"), StringComparison.Ordinal);
        Assert.Equal(
            ["approve", "reject"],
            Board.ValuesOf(board.DecisionFormFor(workItem.Id), "data-decision"));
    }

    [Fact]
    public void Only_a_classified_transient_failure_is_ever_worth_retrying()
    {
        // The classification's own contract, asserted directly rather than only through the
        // loop: DESIGN.md's two named classes, and the rule that a result which came back
        // at all has no class to retry on. This is the property the "not retried" tests
        // above lean on, and it is the one that would be quietly broken by a well-meaning
        // change to a predicate.
        Assert.True(RoundResult.Failed(FailureClass.Transient).IsRetryable);
        Assert.False(RoundResult.Failed(FailureClass.Permanent).IsRetryable);
        Assert.False(RoundResult.TimedOut().IsRetryable);
        Assert.False(RoundResult.Produced("a result", null).IsRetryable);
        Assert.False(RoundResult.Produced("exit 1: 41 tests failed", null).IsRetryable);

        // An exception classifies itself, and an exception that does not is not transient.
        Assert.Equal(FailureClass.Transient, Failures.Classify(new TransientFailure("image pull")));
        Assert.Equal(FailureClass.Permanent, Failures.Classify(new PermanentFailure("no such repository")));
        Assert.Equal(
            FailureClass.Permanent,
            Failures.Classify(new InvalidOperationException("something went wrong")));

        // And the two ways of being transient cannot be confused with each other, so a
        // classifier that read the message could only do so by guessing.
        Assert.Equal(FailureClass.Transient, Failures.Classify(new TransientFailure("permanent")));
        Assert.Equal(FailureClass.Permanent, Failures.Classify(new PermanentFailure("transient")));
    }

    [Fact]
    public void The_retry_policy_is_code_and_grows_geometrically()
    {
        // Factory behaviour is code and not configuration, like every other constant here
        // (story 7), so it is named in one place and the numbers are written out: a test
        // that read the constant would prove only that the policy is whatever it says.
        Assert.Equal(3, FactoryConstants.TransientRetryAttempts);
        Assert.Equal(TimeSpan.FromSeconds(10), FactoryConstants.RetryBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(20), FactoryConstants.RetryBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(40), FactoryConstants.RetryBackoff(3));

        // The backoff is a comparison against the clock, not a wait: it is a TimeSpan this
        // process never sleeps on, and asking for an attempt that is not due is refused
        // rather than delayed. A backoff that slept would be a test that slept.
        Assert.Equal(
            FactoryConstants.RetryBackoff(1) + FactoryConstants.RetryBackoff(2),
            FactoryConstants.RetryBackoff(3) - FactoryConstants.RetryBackoff(1));

        // There is no fourth wait, because there is no fourth attempt: a backoff past the
        // last attempt would be a wait for something that is never going to be asked for.
        Assert.Equal(
            FactoryConstants.RetryBackoff(FactoryConstants.TransientRetryAttempts),
            FactoryConstants.RetryBackoff(FactoryConstants.TransientRetryAttempts + 1));
    }

    private static async Task<WorkItem> TakeOne(FactoryHost host, int issueNumber)
    {
        var workItem = host.Store
            .Intake("nexus", RepoUrl, issueNumber, $"Issue {issueNumber}", IssueBody, "main")
            .WorkItem;
        await host.PromoteAsync(workItem.Id);
        return workItem;
    }

    private static string CauseOf(Board board, WorkItem workItem) =>
        board.Rendered("data-work-item", workItem.Id.ToString(), "data-cause");

    private static Swimlane SwimlaneOf(FactoryHost host, Guid id) => host.Store.Get(id)!.Swimlane;

    private static int RoundCountOf(FactoryHost host, Guid id) => host.Store.Get(id)!.RoundCount;
}
