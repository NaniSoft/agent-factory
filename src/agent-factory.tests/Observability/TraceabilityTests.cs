namespace AgentFactory.Tests.Observability;

using System.Net;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Observability;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The acceptance criterion, as a behaviour rather than a list of log lines: what happened
/// to a given work item is answerable from the records alone.
/// </summary>
/// <remarks>
/// <para>
/// One work item is driven the whole way through the real factory — real poller, real
/// orchestrator, real store, real board, with only the two seams faked — through a sequence
/// that includes a transient round failure, a retry, two rounds of real work, a reviewer's
/// request for changes, and an approval whose merge is refused. Then the questions are asked
/// of the records and nothing else: not the store, not the board, not a log line's prose.
/// </para>
/// <para>
/// Every assertion reads a <em>named field</em> off a record. That is the whole discipline
/// of this file, and it is what separates the claim from a weaker one: a test that grepped
/// the messages for "round 2" would pass against a factory whose rounds could not be told
/// apart, told how many ran, or joined to the work item they were for. The scope's keys are
/// what make the join, and the loop's structured parameters are what make the rest.
/// </para>
/// </remarks>
public class TraceabilityTests
{
    private const string Nexus = "nexus";
    private const string Repo = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task What_happened_to_a_work_item_is_answerable_from_the_records_alone()
    {
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub.WithRepository(
            Repo,
            "main",
            new OpenIssue(42, "the work item being traced", "what the maintainer wrote", [], []));

        // Round one fails the way infrastructure fails, is asked for again after the
        // backoff, and comes back with a result. Both are scripted before the first tick,
        // because a tick is what starts the round and the fake agent is the one thing in
        // this factory that has to be told what a round will be before it is asked for.
        host.Agent
            .FailingTransiently()
            .Producing("files changed, commands run", "the first round's note");

        // Intake, and the machine's first pass. One tick is one project's turn at intake
        // and then everything the loop can apply.
        await host.Tick();

        var work = Assert.Single(host.Store.List());
        await host.PromoteAsync(work.Id);

        // Acceptance is what starts the build. The first step starts attempt 1; the second
        // is the loop learning it failed transiently, which is when the retry's backoff
        // begins — two steps, because a machine settled here would spin on a backoff only
        // the clock can end.
        await host.Step();
        await host.Step();

        // The clock moves rather than the test sleeping, because the backoff is a comparison
        // against `IClock` and not a wait — which is the property that makes this whole
        // sequence testable in microseconds.
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await host.RunTheMachineAsync();

        Assert.Equal(Swimlane.Review, host.Store.Get(work.Id)!.Swimlane);

        // The reviewer sends it back, and the next round is the second one — so the work
        // item has two rounds and a feedback brief between them.
        host.Agent.Producing("renamed", "the second round's note");
        await Decide(host, work.Id, Decision.RequestChanges, "the naming is wrong");
        await host.RunTheMachineAsync();

        Assert.Equal(Swimlane.Review, host.Store.Get(work.Id)!.Swimlane);

        // And the approval whose merge is refused, which is the failure path this file is
        // mostly about: the change is not shipped, the work item is parked, and both facts
        // have to be findable.
        host.GitHub.RefusingToMerge(FailureClass.Transient, "the repository declined the merge");
        await Decide(host, work.Id, Decision.Approve);

        var records = log.About(work.Id);

        // **Which rounds ran, and what each one was.** The round number and its outcome are
        // two fields on one record, so this is two structured reads and no prose at all. A
        // factory that had a work item's rounds blurred together — or that recorded only
        // "a round finished" without saying which — could not answer this.
        var rounds = records
            .Where(record => record.Values("Outcome").Count > 0)
            .Select(record => (Round: (int)record.Field("Round")!, Outcome: record.Field("Outcome")!.ToString()!))
            .ToList();

        Assert.Equal(
            [
                (Round: 1, Outcome: nameof(RoundOutcome.Produced)),
                (Round: 2, Outcome: nameof(RoundOutcome.Produced)),
            ],
            rounds);

        // **That the first round fought the factory on the way.** The attempt count is a
        // field beside the round number, which is what makes "one round, two attempts"
        // different from "two rounds" — the two facts are the same pair of numbers and the
        // difference between them is a field, not a sentence somebody has to interpret.
        Assert.Equal(
            [(Round: 1, Attempts: 2), (Round: 2, Attempts: 1)],
            records.Where(record => record.Values("Attempts").Count > 0)
                .Select(record => (Round: (int)record.Field("Round")!, Attempts: (int)record.Field("Attempts")!))
                .ToList());

        // **What the factory decided, and what it did about it.** Both are fields on the one
        // record the loop writes when it applies a decision, and the swimlane is the loop's
        // own label rather than a word in the sentence — so a reviewer reading these records
        // sees the sequence of lanes rather than inferring it. The board's own record of
        // having *received* the click carries the decision without a lane, and is
        // deliberately not one of these: what a decision meant is the loop's answer, not the
        // page's.
        var decided = records
            .Where(record => record.Values("Decision").Count > 0 && record.Values("Swimlane").Count > 0)
            .Select(record => (Decision: record.Field("Decision")!.ToString(), Swimlane: record.Field("Swimlane")!.ToString()))
            .ToList();

        Assert.Equal(
            [
                (Decision: nameof(AgentFactory.WorkItems.Decision.RequestChanges), Swimlane: "Frontier"),
                (Decision: nameof(AgentFactory.WorkItems.Decision.Approve), Swimlane: "Escalated"),
            ],
            decided);

        // **How it ended.** Escalated, and the reason the approval produced that rather than
        // Done is a record of its own naming the refusal — the merge that did not land is
        // the single most important thing to find again after a factory has been running for
        // a week, and it is here as a field rather than as a sentence to be skimmed.
        Assert.Equal(Swimlane.Escalated, host.Store.Get(work.Id)!.Swimlane);

        var refused = Assert.Single(records, record =>
            record.Level == LogLevel.Warning && record.Values("Reason").Count > 0);

        Assert.Equal("the repository declined the merge", refused.Field("Reason"));
        Assert.Equal(1, refused.Field("Attempt"));
        Assert.Equal("the reviewer approved it", refused.Field("How"));

        // And the thing that must never be true: nothing about this work item's history is
        // only in the prose. Every record the factory wrote about it names it, and the two
        // questions the criterion asks — which rounds, and how it ended — were both answered
        // from fields rather than from text.
        Assert.All(records, record =>
            Assert.Contains(work.Id, record.Guids(WorkItemScope.WorkItemIdKey)));
    }

    [Fact]
    public async Task A_work_items_story_ends_with_records_a_reader_can_order_by_round()
    {
        // The narrower half of the same claim, and the one concurrency makes hard: records
        // about a round carry the round they belong to, so "what did round 2 do" is a filter
        // rather than a reading. Without the round number on the record, a work item with
        // three rounds has nine records in one bag and no way to tell which are about which.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub.WithRepository(
            Repo, "main", new OpenIssue(7, "three rounds", "the brief", [], []));

        for (var round = 1; round <= FactoryConstants.RoundCeiling; round++)
        {
            host.Agent.Producing($"round {round}");
        }

        await host.Tick();
        var work = Assert.Single(host.Store.List());
        await host.PromoteAsync(work.Id);

        for (var round = 1; round <= FactoryConstants.RoundCeiling; round++)
        {
            await host.RunTheMachineAsync();

            if (round < FactoryConstants.RoundCeiling)
            {
                await Decide(host, work.Id, Decision.RequestChanges, $"not yet, this is round {round}");
            }
        }

        // Three rounds, then a request for changes on a work item that has spent them: the
        // exhaustion shape, and the fourth round is unreachable.
        await Decide(host, work.Id, Decision.RequestChanges, "and again");
        await host.RunTheMachineAsync();

        var rounds = log.About(work.Id)
            .Select(record => record.Values(WorkItemScope.RoundKey).FirstOrDefault())
            .OfType<int>()
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal([1, 2, 3], rounds);
        Assert.Equal(Swimlane.Escalated, host.Store.Get(work.Id)!.Swimlane);

        // And the counters agree with the records — which is the property that makes them
        // measurements rather than decoration. Three rounds, three of them produced.
        Assert.Equal(3, measurements.Count(FactoryMetrics.RoundsName, outcome: nameof(RoundOutcome.Produced)));
        Assert.Equal(
            [1L, 2L, 3L],
            measurements.Values(FactoryMetrics.RoundsPerWorkItemName, FactoryMetrics.ProjectTag, Nexus).Order().ToList());
    }

    /// <summary>
    /// A reviewer's click, over the board's real HTTP surface with the board's own form and
    /// antiforgery token, because that is the only way a human makes a decision and a test
    /// that posted to the store directly would be testing something a reviewer never
    /// touches.
    /// </summary>
    private static async Task Decide(
        FactoryHost host,
        Guid workItemId,
        Decision decision,
        string? feedback = null)
    {
        // The slug, because the slug is what a button posts. `Decisions.TryParse` reads the
        // wire format and nothing else, so an enum name here would be refused as a fourth
        // decision — which the board's own record would then say out loud.
        using var response = await Board.DecideAsync(
            host.Board, workItemId, Decisions.Slug(decision), decision == Decision.RequestChanges ? feedback : null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
