namespace AgentFactory.Tests.Observability;

using System.Net;
using AgentFactory.GitHub;
using AgentFactory.Observability;
using AgentFactory.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;

/// <summary>
/// The counters, and the claims the design makes about them: issues processed, builds
/// succeeded and failed, and rounds per work item — with the values they move by, for the
/// events they are supposed to move for.
/// </summary>
/// <remarks>
/// <para>
/// A counter is only a measurement if it moves by the right amount for the right reason, so
/// every test here drives a real factory and reads what was published. Nothing asserts on
/// an instrument's existence alone: an instrument nobody incremented and an instrument
/// nobody defined would both read as zero, and <see cref="RecordedMeasurements"/> refuses to
/// return an empty list for an instrument the meter never published at all.
/// </para>
/// <para>
/// The other half of the claim is what the counters do <em>not</em> carry, and it is the
/// half that is easy to get wrong: a metric tagged with a work item, a branch, a path or an
/// issue title is a time series per instance that nobody will ever read, and a copy of a
/// stranger's words in a store whose retention this repository does not control. Those are
/// asserted here against real published measurements rather than against the source.
/// </para>
/// </remarks>
public class MetricTests
{
    private const string Nexus = "nexus";
    private const string Atlas = "atlas";
    private const string NexusRepo = "https://github.com/NaniSoft/nexus";
    private const string AtlasRepo = "https://github.com/NaniSoft/atlas";

    [Fact]
    public async Task Issues_are_counted_when_intake_makes_a_work_item_and_not_when_it_finds_one_again()
    {
        // The reading that matters: "issues processed" is a count of work that arrived, not
        // of times the poller looked. Intake is idempotent against the store (ADR-0007), and a
        // counter that went up on every pass would be measuring the poll interval — which
        // would make it look like a factory doing four times the work.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(root, log, counters);

        host.GitHub.WithRepository(
            NexusRepo,
            "main",
            new OpenIssue(1, "one", "build it", [], []),
            new OpenIssue(2, "two", "build this", [], []));

        await host.PollAsync();
        Assert.Equal(2, host.Store.List().Count);
        Assert.Equal(2, measurements.Count(FactoryMetrics.IssuesProcessedName));

        // Three more passes over the same two open issues, and one new one. Nothing but the
        // new one is counted, because nothing but the new one became work.
        host.GitHub.WithRepository(
            NexusRepo,
            "main",
            new OpenIssue(1, "one", "build it", [], []),
            new OpenIssue(2, "two", "build this", [], []),
            new OpenIssue(3, "three", "and this", [], []));

        for (var pass = 0; pass < 3; pass++)
        {
            host.Clock.Advance(FactoryConstants.PollInterval);
            await host.PollAsync();
        }

        Assert.Equal(3, host.Store.List().Count);
        Assert.Equal(3, measurements.Count(FactoryMetrics.IssuesProcessedName));
    }

    [Fact]
    public async Task A_build_that_produced_a_result_and_a_build_that_did_not_are_one_count_and_two_answers()
    {
        // The two halves of "builds succeeded and builds failed", asserted as what they
        // really are: one instrument tagged by outcome. Two separate counters incremented at
        // two call sites can disagree — a path that increments neither is invisible, and a
        // path that increments both is equally invisible — and the identity below is the
        // property a pair of counters does not have.
        using var root = FactoryRoot.Create()
            .WithProjectFile("atlas.yaml", ProjectFile.For(Atlas, AtlasRepo))
            .WithProjectFile("nexus.yaml", ProjectFile.For(Nexus, NexusRepo));

        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub
            .WithRepository(NexusRepo, "main", new OpenIssue(1, "the nexus issue", "build it", [], []))
            .WithRepository(AtlasRepo, "main", new OpenIssue(2, "the atlas issue", "build this", [], []));

        // One project's round produces a result and the other's comes back without one. The
        // failure is a real one — the round runner is asked for a project file that is not
        // being served, which is permanent — and a round that did not produce is what a
        // failed build looks like from here.
        host.Agent
            .Producing("the change", "a note")
            .FailingPermanently();

        await host.PollAsync();
        await host.RunTheMachineAsync();

        Assert.Equal(1, measurements.Count(FactoryMetrics.RoundsName, nameof(RoundOutcome.Produced)));
        Assert.Equal(1, measurements.Count(FactoryMetrics.RoundsName, nameof(RoundOutcome.Failed)));
        Assert.Equal(0, measurements.Count(FactoryMetrics.RoundsName, nameof(RoundOutcome.TimedOut)));

        // **The identity.** Succeeded plus failed is the number of rounds the factory landed,
        // and the number the store holds is the number a reviewer would count on the board.
        // Three numbers that must be equal are a checkable property; two counters are not.
        var landed = host.Store.List().Sum(item => host.Store.Rounds(item.Id).Count);
        Assert.Equal(landed, measurements.Count(FactoryMetrics.RoundsName));
        Assert.Equal(
            landed,
            measurements.Values(FactoryMetrics.RoundsPerWorkItemName, FactoryMetrics.ProjectTag, Nexus)
                .Count + measurements.Values(FactoryMetrics.RoundsPerWorkItemName, FactoryMetrics.ProjectTag, Atlas)
                    .Count);

        // And each project's own rounds are counted against that project rather than
        // against the factory, because "one noisy repository cannot starve the rest" is a
        // claim about one repository (ADR-0007) and is not measurable without it. One
        // project produced and the other did not — asserted by *which* project rather than
        // by which one the rotation happened to reach first, because the order rounds are
        // handed out in is the loop's business and not this test's.
        var succeeded = measurements.Count(FactoryMetrics.RoundsName, FactoryMetrics.ProjectTag, Nexus) == 1
            ? Nexus
            : Atlas;
        var failed = succeeded == Nexus ? Atlas : Nexus;

        var outcome = succeeded == Nexus ? nameof(RoundOutcome.Produced) : nameof(RoundOutcome.Failed);
        Assert.Equal(
            outcome,
            measurements.Of(FactoryMetrics.RoundsName)
                .Single(measurement => Equals(measurement.Tag(FactoryMetrics.ProjectTag), succeeded))
                .Tag(FactoryMetrics.OutcomeTag));

        Assert.Equal(1, measurements.Count(FactoryMetrics.RoundsName, FactoryMetrics.ProjectTag, failed));
        Assert.Equal(1, measurements.Count(FactoryMetrics.RoundsPerWorkItemName, FactoryMetrics.ProjectTag, succeeded));
    }

    [Fact]
    public async Task Rounds_per_work_item_is_a_distribution_of_round_numbers_and_not_a_label()
    {
        // The instrument the criterion names, and the one most likely to be got wrong. A
        // counter tagged with the work item's id would be one series per work item — the
        // cardinality and privacy hazard in its purest form — and it would also be useless,
        // because the answer to "how many rounds does a review cost" is a distribution and a
        // distribution cannot be read off a set of unrelated series.
        //
        // So the value is the round number and the label is the project. What the histogram
        // then answers is: bucket *n* holds the rounds that were the *n*th of their work
        // item, so bucket 1 holding two and bucket 2 holding one is two work items that
        // reached a first round and one that went on to a second — which is the number a
        // maintainer wants from story 59, arrived at by reading two buckets rather than by
        // querying two thousand series.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub.WithRepository(
            NexusRepo, "main", new OpenIssue(1, "one round", "quick", [], []),
            new OpenIssue(2, "two rounds", "harder", [], []));

        // Three rounds scripted in total: one for the first work item and two for the
        // second. Scripted before the machine runs, because a round asked for with nothing
        // scripted for it is a permanent failure and this is about the shape of the
        // distribution rather than about failure.
        host.Agent.Producing("one").Producing("two").Producing("three");

        await host.PollAsync();
        await host.RunTheMachineAsync();

        var work = host.Store.List().ToList();
        Assert.Equal(2, work.Count);
        Assert.All(work, item => Assert.Single(host.Store.Rounds(item.Id)));

        // One of them is sent back, and the other is left alone: one work item costs two
        // rounds and the other costs one, which is the shape the histogram exists to answer.
        var second = work[1];
        using (var response = await Board.DecideAsync(
            host.Board, second.Id, Decisions.Slug(Decision.RequestChanges), "not yet"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await host.RunTheMachineAsync();
        Assert.Equal(2, host.Store.Rounds(second.Id).Count);

        // Two observations at round one, one at round two — and nothing that could identify
        // either work item anywhere in the measurement.
        Assert.Equal(
            [1, 1, 2],
            measurements.Values(FactoryMetrics.RoundsPerWorkItemName, FactoryMetrics.ProjectTag, Nexus)
                .Order().ToList());

        foreach (var item in work)
        {
            Assert.DoesNotContain(item.Id.ToString(), measurements.Everything, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task No_published_measurement_is_labelled_with_anything_but_a_project_and_an_outcome()
    {
        // The cardinality and privacy claim, against real measurements rather than against
        // the source. Every tag key published by a real run is inside the closed vocabulary;
        // and every tag *value* is a project name or an enum name — never a work item, a
        // branch, a path, a URL, an issue number or a credential name, all of which are
        // present in the run and none of which appear.
        using var root = FactoryRoot.Create()
            .WithProjectFile("atlas.yaml", ProjectFile.For(Atlas, AtlasRepo))
            .WithProjectFile("nexus.yaml", ProjectFile.For(Nexus, NexusRepo));

        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub
            .WithRepository(NexusRepo, "main", new OpenIssue(4242, "the nexus issue", "build it", [], []))
            .WithRepository(AtlasRepo, "main", new OpenIssue(99, "the atlas issue", "build this", [], []));

        host.Agent.Producing("the change", "a note");
        await host.PollAsync();
        await host.RunTheMachineAsync();

        // The vocabulary, as the closed set names it.
        Assert.Equal(
            FactoryMetrics.Tags.Order(StringComparer.Ordinal).ToList(),
            measurements.TagKeys);

        // The values. Everything this run knew that would be a leak if it were a label, and
        // none of it is one. Written out as the specific strings the run was given rather
        // than as a shape, because a shape check would pass for a value that merely looked
        // wrong.
        foreach (var forbidden in new[]
        {
            host.Store.List().Select(item => item.Id.ToString()),
            host.Store.List().Select(item => item.IssueNumber.ToString()),
            host.Store.List().Select(item => item.BaseBranch),
            host.Store.List().Select(item => item.RepoUrl),
            // The credential *names* the project files declare. They are not secrets, and
            // they are not in a metric either: a label is copied into stores whose retention
            // this repository does not control, and an environment variable's name has no
            // business being in one.
            new[] { "NEXUS_GITHUB_TOKEN", "NEXUS_ANTHROPIC_API_KEY", "ATLAS_GITHUB_TOKEN", "ATLAS_ANTHROPIC_API_KEY" },
        })
        {
            Assert.DoesNotContain(measurements.TagValues, value => forbidden.Contains(value));
        }

        // And the whole of it as one blob, which is what an exporter would write — so this
        // is a check on the measurement rather than on the arguments to it.
        Assert.DoesNotContain(NexusRepo, measurements.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain("main", measurements.TagValues);
    }

    [Fact]
    public async Task A_counter_nobody_ever_increments_is_still_readable_as_a_counter_rather_than_as_a_absence()
    {
        // Aimed at the failure mode a reader of `RecordedMeasurements` should know about,
        // and named here because it is the one that would make every metric test in this
        // file quietly pass: a test that asked for a counter nothing had incremented and got
        // zero back would be told the same thing as a test that asked for a counter nobody
        // defined. So the recorder distinguishes them, and this is the assertion that it
        // does.
        var metrics = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(metrics);

        Assert.Equal(0, measurements.Count(FactoryMetrics.RoundsName));
        Assert.Throws<InvalidOperationException>(() => measurements.Of("agent_factory.nonexistent"));
    }
}
