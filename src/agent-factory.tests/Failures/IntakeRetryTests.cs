namespace AgentFactory.Tests.Failures;

using AgentFactory;
using AgentFactory.Failures;
using AgentFactory.GitHub;
using AgentFactory.Observability;
using AgentFactory.Polling;
using AgentFactory.Tests.Boundary;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// A repository that cannot be read, and the schedule that follows. #4 left this as a note:
/// a failing project was retried at most once per sixty-second pass, and "adding backoff
/// will interact with the poller's fixed pass cadence in a way nothing currently asserts."
///
/// The interaction turns out to be that the pass cadence is a *floor* and the backoff is
/// a ceiling on how often the factory asks. A backoff that lengthens the wait can only ever
/// ask a question later, never sooner — and once it grows past the poll interval, later is
/// later than the pass would have gone anyway, so a project that has been failing for a
/// while is asked less and less often. That is what an exponential backoff is for, and it
/// is why the two constants compose the way they do rather than fighting.
///
/// <strong>And a permanent failure has no schedule at all, which is the change #16 asked
/// for.</strong> It used to be given no wait of its own, which meant the pass cadence read
/// it anyway — a full warning every sixty seconds, for ever, for a repository that is gone
/// or a credential that was never set. Two of the tests below changed with it, and their
/// names say what they now hold.
/// </summary>
/// <remarks>
/// Nothing here escalates, and that is a decision rather than an omission. Escalation is a
/// work item's state, and a project that cannot be read has produced no work item to
/// escalate; there is nothing on the board to park and nothing for a human to finish from
/// a card. What the poller does instead is stop asking — for ever, in the permanent case —
/// and say so once in the log and on the board, which is a project-level fault rather than a
/// lane. A repository that comes back is picked up, after a restart in the permanent case
/// and on the next pass in the transient one.
/// </remarks>
public class IntakeRetryTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task A_repository_that_cannot_be_read_is_tried_again_on_the_next_pass()
    {
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        await host.PollAsync();
        Assert.Equal(1, github.TimesPolled(RepoUrl));

        // Short of the poll interval, no pass is due at all — the pass cadence is the floor
        // and nothing shortens it.
        clock.Advance(TimeSpan.FromSeconds(59));
        await host.PollAsync();
        Assert.Equal(1, github.TimesPolled(RepoUrl));

        // A transient failure is retried, on the next pass, which is the shortest wait the
        // poller has. A transient failure is not worth asking about three times inside one
        // turn: a turn cannot wait, so a retry inside a turn would be three identical calls
        // in a row, and that is a stampede rather than a backoff.
        clock.Advance(TimeSpan.FromSeconds(1));
        await host.PollAsync();
        Assert.Equal(2, github.TimesPolled(RepoUrl));

        // And the project is picked up when it comes back, without a human doing anything.
        github.WithRepository(
            RepoUrl,
            defaultBranch: "main",
            OpenIssue.Plain(1, "An issue that was there all along", "A body."));
        clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();

        Assert.Equal(3, github.TimesPolled(RepoUrl));
        Assert.Equal(1, Assert.Single(host.Store.List()).IssueNumber);
    }

    [Fact]
    public async Task A_permanently_failing_repository_is_read_once_and_never_again()
    {
        // **This test used to assert the opposite**, and the change is the ticket's: a
        // permanent failure was "given no wait of its own", which meant the pass cadence
        // read it again sixty times an hour, for ever, with a full warning each time — an
        // unbounded log generator pointed at a misconfiguration that cannot fix itself.
        // A permanent failure is permanent, so it is read once and then not again.
        var clock = new TestClock();
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Permanent, "the repository does not exist");
        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, clock: clock, agent: new FakeNOpenCode(), github: github);

        // A day of passes, one every sixty seconds.
        for (var pass = 0; pass < 1440; pass++)
        {
            clock.Advance(FactoryConstants.PollInterval);
            await host.PollAsync();
        }

        Assert.Equal(1, github.TimesPolled(RepoUrl));
        Assert.Empty(host.Store.List());

        // And it said so once rather than 1440 times, which is the other half of the same
        // fix: a warning per pass is a per-minute account of a fault that will not change.
        var warned = Assert.Single(log.AtLeast(LogLevel.Warning));
        Assert.Equal(RepoUrl, warned.Field("Repository"));
        Assert.Equal(FailureClass.Permanent, warned.Field("Classification"));
        Assert.Contains("will not read this project again", warned.Message, StringComparison.Ordinal);

        // The state it is left in is the one the board renders: a project that failed, as
        // what, and with no moment at which it will be asked again.
        var state = Assert.Single(host.Poller.Intake);

        Assert.Equal("nexus", state.Project);
        Assert.Equal(IntakeStatus.Failing, state.Status);
        Assert.Equal(FailureClass.Permanent, state.Failure);
        Assert.Null(state.AgainAfterUtc);

        // **The cost, stated rather than hidden.** A project that has come back is not read
        // in this process either — a permanent failure is not something waiting to be
        // retried, it is something an operator has to go and fix, and everything it is
        // about (a project file, the set being served, an environment variable) is read at
        // start. A restart is what asks again, which is what the board says.
        github.WithRepository(RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "An issue", "A body."));
        clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();

        Assert.Equal(1, github.TimesPolled(RepoUrl));
        Assert.Empty(host.Store.List());

        // And after a restart it is read, and its issue becomes a work item, with no human
        // pressing anything.
        using var restarted = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        await using var second = await FactoryHost.StartAsync(restarted, clock, agent: new FakeNOpenCode(), github: github);

        await second.PollAsync();

        Assert.Equal(2, github.TimesPolled(RepoUrl));
        Assert.Equal(1, Assert.Single(second.Store.List()).IssueNumber);
    }

    [Fact]
    public async Task A_transient_failure_stops_the_poller_asking_as_often_as_the_pass_cadence_would()
    {
        // The interaction the #4 note was about, asserted rather than assumed: a backoff
        // may only lengthen the wait, so the sixty-second pass interval paces a project
        // through its first few failures, and the backoff takes over once it has grown past
        // that. The numbers are written out rather than read from the constants, because a
        // test that advanced by the constants would prove only that the poller compares
        // two values to each other.
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        // The waits are ten seconds, twenty, forty, eighty. The pass interval is sixty.
        Assert.Equal(TimeSpan.FromSeconds(10), FactoryConstants.PollBackoff(1));
        Assert.Equal(TimeSpan.FromSeconds(20), FactoryConstants.PollBackoff(2));
        Assert.Equal(TimeSpan.FromSeconds(40), FactoryConstants.PollBackoff(3));
        Assert.Equal(TimeSpan.FromSeconds(80), FactoryConstants.PollBackoff(4));
        Assert.Equal(TimeSpan.FromSeconds(60), FactoryConstants.PollInterval);

        // Passes at t=0, 60, 120, 180: four turns. The backoff is shorter than the interval
        // through all of them, so the cadence is what paces this project so far.
        for (var pass = 0; pass < 4; pass++)
        {
            await host.PollAsync();
            clock.Advance(FactoryConstants.PollInterval);
        }

        Assert.Equal(4, github.TimesPolled(RepoUrl));

        // The pass due at t=240 is not taken. The wait after the fourth failure is eighty
        // seconds, and 240 is not yet 260. This is the assertion: without a backoff that
        // grows, this pass would be read like every other one, and a repository that has
        // been failing for four minutes would be asked for ever at a minute a piece.
        await host.PollAsync();
        Assert.Equal(4, github.TimesPolled(RepoUrl));

        // The pass due at t=300 is taken, and the wait after it is longer still.
        clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();
        Assert.Equal(5, github.TimesPolled(RepoUrl));
    }

    [Fact]
    public async Task A_repository_that_has_been_failing_all_day_is_read_a_handful_of_times()
    {
        // The other end of the same trade, and the reason the growth stops somewhere. A
        // project that is down is not worth a request a minute for ever, and the ceiling
        // is what says how far the asking decays to: sixteen minutes, which is a handful
        // of reads across a day rather than fourteen hundred.
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        Assert.Equal(TimeSpan.FromMinutes(16), FactoryConstants.PollBackoffCeiling);
        Assert.Equal(FactoryConstants.PollBackoffCeiling, FactoryConstants.PollBackoff(50));

        // A day of passes, one every sixty seconds. The count is the whole of the claim.
        var passes = 0;
        while (clock.UtcNow - new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero) < TimeSpan.FromDays(1))
        {
            await host.PollAsync();
            clock.Advance(FactoryConstants.PollInterval);
            passes++;
        }

        Assert.Equal(1440, passes);
        Assert.True(
            github.TimesPolled(RepoUrl) < 200,
            $"a project down for a day should be read a handful of times, not {github.TimesPolled(RepoUrl)}");

        // And it is still being served, not written off. This is the cost of the ceiling,
        // stated rather than discovered: a project that comes back is picked up within
        // sixteen minutes. Sixteen passes, at worst, and not one of them empty.
        github.WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "An issue", "A body."));

        var read = false;
        for (var pass = 0; pass < 16 && !read; pass++)
        {
            clock.Advance(FactoryConstants.PollInterval);
            await host.PollAsync();
            read = host.Store.List().Count > 0;
        }

        Assert.True(read, "a project that comes back is picked up without anyone doing anything");
    }

    [Fact]
    public async Task A_permanently_failing_project_is_read_once_and_a_transient_one_is_still_being_read()
    {
        // The two schedules over a long enough run that they have parted company for good.
        // A transient failure is read a handful of times across a day; a permanent one is
        // read once and never again. Before this ticket they were the other way round — the
        // permanent one was read on every one of 1440 passes and the transient one a
        // handful of times — which is what made the pair so nearly indistinguishable on a
        // board that rendered neither.
        var clock = new TestClock();
        var permanent = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Permanent, "the repository does not exist");
        var transient = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");

        // Two roots, because two hosts against one SQLite file is two writers for a file
        // that has one writer by design (ADR-0009), and the point here is the schedules
        // rather than the store.
        using var goneRoot = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        using var downRoot = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);

        var factory = new[]
        {
            (Host: await FactoryHost.StartAsync(goneRoot, clock, github: permanent), GitHub: permanent),
            (Host: await FactoryHost.StartAsync(downRoot, clock, github: transient), GitHub: transient),
        };

        var passes = 0;
        while (clock.UtcNow - new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero) < TimeSpan.FromDays(1))
        {
            foreach (var (host, _) in factory)
            {
                await host.PollAsync();
            }

            clock.Advance(FactoryConstants.PollInterval);
            passes++;
        }

        Assert.Equal(1440, passes);

        // Once, for the one that is gone. It is not "asked less often" — it is not asked
        // again, and the difference is the whole of what a permanent failure means.
        Assert.Equal(1, permanent.TimesPolled(RepoUrl));

        // A handful, for the one that is merely down: the backoff grows past the interval
        // and then keeps growing, and the reads decay to a few a day.
        Assert.True(
            transient.TimesPolled(RepoUrl) < 200,
            $"a project down for a day is paced, and got read {transient.TimesPolled(RepoUrl)} times");

        foreach (var (host, _) in factory)
        {
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_repository_that_reads_again_starts_its_backoff_over()
    {
        // The backoff counts consecutive failures, not failures ever: a project that was
        // unreachable for an afternoon and then answered should not be paced for ever on
        // the strength of a failure from this morning.
        var clock = new TestClock();
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var github = new FakeGitHub()
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        // Four failures, which is enough to make the next pass too early to be taken.
        for (var pass = 0; pass < 4; pass++)
        {
            await host.PollAsync();
            clock.Advance(FactoryConstants.PollInterval);
        }

        await host.PollAsync();
        Assert.Equal(4, github.TimesPolled(RepoUrl));

        // The repository answers, and the run of failures is over.
        github.WithRepository(
            RepoUrl, defaultBranch: "main", OpenIssue.Plain(1, "An issue", "A body."));
        clock.Advance(FactoryConstants.PollInterval);
        await host.PollAsync();
        Assert.Equal(5, github.TimesPolled(RepoUrl));
        Assert.Single(host.Store.List());

        // Two more transient failures from a cleared count, and then four more passes. The
        // waits are ten, twenty, forty and eighty seconds again — all shorter than the
        // sixty-second interval, except the last, so four of these five passes are read and
        // the fifth is refused. Had the run not been reset, the wait after the fifth
        // failure in total would be minutes rather than seconds and every one of these
        // passes would have been refused.
        github.Failing(RepoUrl, FailureClass.Transient, "the API returned 503");

        var reads = 0;
        for (var pass = 0; pass < 5; pass++)
        {
            clock.Advance(FactoryConstants.PollInterval);
            var before = github.TimesPolled(RepoUrl);
            await host.PollAsync();
            reads += github.TimesPolled(RepoUrl) - before;
        }

        Assert.Equal(4, reads);
    }

    [Fact]
    public async Task A_failing_project_still_does_not_stop_the_rest_of_the_rotation()
    {
        // Unchanged and worth restating under a retry policy: backoff must not become
        // starvation. A project being paced is a project's own turn being skipped, and the
        // rotation carries on round to the others — which is the whole reason the rotation
        // is one project at a time (ADR-0007).
        var clock = new TestClock();
        const string Alpha = "https://github.com/NaniSoft/alpha";
        using var root = FactoryRoot.Create()
            .WithProjectFile("alpha.yaml", ProjectFile.For("alpha", Alpha))
            .WithProjectFile("nexus.yaml", ProjectFile.For("nexus", RepoUrl));
        var github = new FakeGitHub()
            .WithRepository(Alpha, "main", OpenIssue.Plain(1, "An issue", "A body."))
            .Failing(RepoUrl, FailureClass.Transient, "the API returned 503");
        await using var host = await FactoryHost.StartAsync(root, clock, github: github);

        // A day of passes. Alpha is read on every one of them — its own turn is never
        // skipped, because it never failed — while nexus's turns are paced away.
        var alphaTurns = 0;
        while (clock.UtcNow - new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.Zero) < TimeSpan.FromDays(1))
        {
            await host.PollAsync();
            clock.Advance(FactoryConstants.PollInterval);
            alphaTurns++;
        }

        Assert.Equal(1440, alphaTurns);
        Assert.Equal(alphaTurns, github.TimesPolled(Alpha));
        Assert.True(
            github.TimesPolled(RepoUrl) < 200,
            $"nexus is paced and alpha is not: {github.TimesPolled(RepoUrl)} against {alphaTurns}");

        // One issue in Backlog from alpha, and none from nexus, because nexus was never
        // successfully read at all.
        Assert.Equal("alpha", Assert.Single(host.Store.List()).Project);
    }
}
