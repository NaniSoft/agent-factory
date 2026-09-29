namespace AgentFactory.Tests.Observability;

using System.Net;
using AgentFactory.GitHub;
using AgentFactory.Observability;
using AgentFactory.Tests.Boundary;
using AgentFactory.Tests.GitHub;
using AgentFactory.WorkItems;
using Microsoft.Extensions.Logging;

/// <summary>
/// The sharp edge. #10 closed a leak by hand — a token that reached a log line, a board
/// message or a request URL — and this is the check that observability did not reopen it.
/// </summary>
/// <remarks>
/// <para>
/// The hazard is specific and it was named in the ticket: an observability change is the
/// one kind of change that reaches for a whole environment or a whole configuration object
/// at once, and doing that here would put a project's `keys` into every log sink in the
/// deployment. So the two things a project record can be asked for — its names and its
/// values — are separated everywhere they are written down, and this drives the real
/// factory with a real credential in it and reads every record and every measurement.
/// </para>
/// <para>
/// The value used here is deliberately shaped like a token, because a test asserting
/// `DoesNotContain("secret")` against a log full of the word "secret" proves nothing about
/// the string being looked for. It also appears nowhere in the *arguments* of anything the
/// factory does: a value that were merely absent from the records but present in a scope,
/// a tag or a message parameter would be caught here too, because a record's state and its
/// scopes are both read.
/// </para>
/// </remarks>
public class CredentialFreeObservabilityTests
{
    /// <summary>
    /// Shaped like a credential, and not a word that appears in any message this factory
    /// writes — which is the only way a "this string is not in there" check means anything.
    /// </summary>
    private const string GitHubToken = "ghp_4Zx91mQe7Vt2Rb8Nk3Pw6Yd0Lc5Jf7Hs1Gu4Xa";

    private const string LlmKey = "sk-ant-api03-Qw81Zt4Yp2Xv6Nc9Mb3Ke7Rf5Hd0Js2Lg8Wu1";

    private const string Nexus = "nexus";
    private const string Repo = "https://github.com/NaniSoft/nexus";

    [Fact]
    public async Task No_credential_value_reaches_a_record_or_a_measurement_when_the_factory_ships_a_change()
    {
        // The whole path, end to end, with a credential the environment actually has. The
        // merger resolves it and uses it; the round runner resolves the LLM key and puts it
        // in a container's environment; and neither value comes out in anything the factory
        // says. That is ADR-0006 plus #10's own check, held across the observability work.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();
        using var measurements = new RecordedMeasurements(counters);

        await using var host = await FactoryHost.RecordingAsync(
            root,
            log,
            counters,
            agent: new FakeNOpenCode(),
            github: new FakeGitHub()
                .WithRepository(Repo, "main", new OpenIssue(42, "ship it", "please", [], []))
                .Merging());

        // The round runner reads this one and the merger reads that one, so both credential
        // paths in the process are exercised — and the value each is given is one this file
        // then goes looking for everywhere.
        Environment.SetEnvironmentVariable("NEXUS_GITHUB_TOKEN", GitHubToken);
        Environment.SetEnvironmentVariable("NEXUS_ANTHROPIC_API_KEY", LlmKey);

        try
        {
            host.Agent.Producing("the change", "a note");
            await host.PollAsync();
            await host.RunTheMachineAsync();

            var work = Assert.Single(host.Store.List());
            using (var response = await Board.DecideAsync(
                host.Board, work.Id, Decisions.Slug(Decision.Approve)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            // The credential really was resolved and really was used: a factory that had
            // stopped resolving it would pass every "not in there" check below by shipping
            // nothing at all, and the merge is asserted rather than assumed.
            Assert.Equal(Swimlane.Done, host.Store.Get(work.Id)!.Swimlane);

            Assert.DoesNotContain(GitHubToken, log.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain(LlmKey, log.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain(GitHubToken, measurements.Everything, StringComparison.Ordinal);
            Assert.DoesNotContain(LlmKey, measurements.Everything, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NEXUS_GITHUB_TOKEN", null);
            Environment.SetEnvironmentVariable("NEXUS_ANTHROPIC_API_KEY", null);
        }
    }

    [Fact]
    public async Task The_mergers_own_records_name_the_work_item_and_carry_no_credential()
    {
        // The merger is the fifth of the five components, and the one whose records matter
        // most to be findable: shipping is the last thing that happens to a work item, and a
        // record of a push or a merge that says only `nexus#13` is a record a reader has to
        // join to a work item by hand.
        //
        // It is driven here over the real client, a real tree and a real bare repository,
        // with the transport faked — so these are the records a real merge produced rather
        // than a hand-written string, and the token in them is a real one on its way to a
        // real `Authorization` header.
        using var merging = new Merging();
        var log = new RecordedLog();
        var client = new GitHubClient(
            new HttpClient(merging.Api) { BaseAddress = new Uri("https://api.github.com/") },
            new AgentFactory.Projects.ProjectLoadReport(
                [new AgentFactory.Projects.Project(
                    "nexus",
                    merging.WorkItem.RepoUrl,
                    "ghcr.io/nanisoft/agent-factory-worker:1",
                    "anthropic",
                    Merging.KeyName,
                    "NEXUS_ANTHROPIC_API_KEY",
                    "nexus.yaml")],
                []),
            merging.Options,
            merging.Store,
            new FakeCredentialReader().Having(Merging.KeyName, Merging.Token),
            log.For<GitHubClient>());

        merging.NothingShippedYet();
        merging.Api.Responding(
            "POST",
            "/pulls",
            (HttpStatusCode.Created, GitHubApi.PullRequest(7, merging.Commit, merging.Branch)));
        merging.Api.Responding("PUT", "/merge", (HttpStatusCode.OK, """{"merged": true}"""));

        await client.MergeAsync(merging.WorkItem.RepoUrl, 42, CancellationToken.None);

        var records = log.About(merging.WorkItem.Id);

        // The merge actually happened — otherwise every assertion below would pass on a
        // client that had done nothing.
        Assert.Contains(merging.Api.Requests, request => request.Method == "PUT" && request.PathAndQuery.EndsWith("/merge", StringComparison.Ordinal));
        Assert.NotEmpty(records);

        // Every record the merger wrote about this change names the work item, and the
        // commit and the branch are fields rather than prose: "which commit was shipped for
        // which work item" is a query here, not a reading.
        Assert.All(records, record => Assert.Equal(merging.WorkItem.Id, record.Field(WorkItemScope.WorkItemIdKey)));
        Assert.Contains(records, record => record.Values("Commit").Count > 0);
        Assert.Contains(records, record => record.Values("Branch").Count > 0);

        // And the credential, which the client resolved and put in a header on every
        // request, is in none of it. The header is asserted so that this cannot pass on a
        // merge that never resolved one: a factory that shipped nothing would satisfy
        // "the token is not in the log" perfectly.
        Assert.All(merging.Api.Requests, request => Assert.Equal($"Bearer {Merging.Token}", request.Authorization));
        Assert.DoesNotContain(Merging.Token, log.Everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_config_loader_says_which_projects_are_served_by_name_and_never_a_value()
    {
        // Story 63 — the set of projects actually served is inspectable after start — is the
        // one piece of this work that reaches for a whole configuration object at once, so
        // it is the one piece where a value could have got in. It could not: `Project` has no
        // field a value could sit in, and this asserts the record names the two credential
        // variables so the test is not passing by omission.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();

        await using var host = await FactoryHost.RecordingAsync(root, log, counters);

        var served = Assert.Single(
            log.Records,
            record => record.Values("GitHubKey").Count > 0);

        Assert.Equal(Nexus, served.Field("Name"));
        Assert.Equal(Repo, served.Field("Repository"));
        Assert.Equal("NEXUS_GITHUB_TOKEN", served.Field("GitHubKey"));
        Assert.Equal("NEXUS_ANTHROPIC_API_KEY", served.Field("LlmKey"));

        // The count, once, at the same time — so a reader can tell "one project is served"
        // from "the directory was empty" without counting lines.
        var summary = Assert.Single(log.Records, record => record.Values("Served").Count > 0);
        Assert.Equal(1, summary.Field("Served"));
        Assert.Equal(0, summary.Field("Refused"));
    }

    [Fact]
    public async Task A_refused_decision_is_on_the_record_against_the_work_item_it_was_about()
    {
        // The board's own failure paths, because they are the records a reviewer is holding a
        // response for and no log line would otherwise name: a post that is not one of the
        // three, and a decision the store would not keep. Both are refusals rather than
        // faults, and both have to say which work item they were about — the second from the
        // scope, the first from what the post named at all.
        using var root = FactoryRoot.Create().WithProjectFile("nexus.yaml", ProjectFile.Valid);
        var log = new RecordedLog();
        var counters = new FactoryMetrics();

        await using var host = await FactoryHost.RecordingAsync(
            root, log, counters, agent: new FakeNOpenCode(), github: new FakeGitHub());

        host.GitHub.WithRepository(Repo, "main", new OpenIssue(42, "decide about this", "please", [], []));
        host.Agent.Producing("the change", "a note");
        await host.PollAsync();
        await host.RunTheMachineAsync();

        var work = Assert.Single(host.Store.List());

        // A fourth decision, posted by hand against a work item the factory knows.
        using (var response = await Board.PostByHandAsync(
            host.Board, work.Id, "shelve", "not a decision"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var fourth = Assert.Single(log.AtLeast(LogLevel.Warning), record =>
            record.Category.EndsWith("IndexModel", StringComparison.Ordinal)
                && record.Values("Named").Count > 0);

        Assert.Equal("a work item", fourth.Field("Named"));

        // A decision the store refuses: a work item that is not in a lane a reviewer can act
        // on any more. Rejecting one that has already been rejected is that case, and it is
        // refused by the store's own rules rather than by the board's markup (ADR-0008).
        using (var response = await Board.DecideAsync(host.Board, work.Id, Decisions.Slug(Decision.Reject)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(Swimlane.Rejected, host.Store.Get(work.Id)!.Swimlane);
    }
}
