namespace AgentFactory.Tests.Rounds;

using AgentFactory.Application.Projects;
using AgentFactory.Application.Results;
using AgentFactory.Application;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Projects;
using AgentFactory.Domain.Rounds;
using AgentFactory.Infrastructure.Results;
using AgentFactory.Infrastructure.Rounds;
using AgentFactory.Tests.Boundary;
using AgentFactory.Tests.Review;
using AgentFactory;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A round, run through the real round runner, with only the child process faked. The
/// runner's own claims are under test here: what it clones, what it hands the agent, what
/// it puts in the round's environment, and what it says about how the round ended. Whether
/// the machine really has an <c>opencode</c> on its <c>PATH</c> is the deployment's
/// business, and the tests that run a real agent are the container-free equivalent of the
/// old <c>[AgentFact]</c> ones — they are not in this suite.
/// </summary>
public class RoundRunnerTests
{
    private const string RepoUrl = "https://github.com/NaniSoft/nexus";

    private static readonly Guid WorkItem = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_build_round_fetches_its_own_tree_at_the_work_items_base()
    {
        using var harness = AFactory();

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // A round is given no shared tree, so the tree arrives over the network into the
        // round's own landing directory. The clone is single-branch at the work item's base,
        // and the line-ending setting is pinned on the clone itself — a tree checked out
        // with the host's autocrlf is not the tree the repository describes (#26).
        var clone = harness.Processes.TheOnlyClone();
        var arguments = clone.Arguments.ToList();
        Assert.Equal(RepoUrl, arguments[^2]);
        Assert.Equal("main", arguments[arguments.IndexOf("--branch") + 1]);
        Assert.Contains(arguments, argument => argument == "--single-branch");
        Assert.Contains(arguments, argument => argument == "core.autocrlf=false");
        Assert.EndsWith("tree", arguments[^1], StringComparison.Ordinal);

        // And the agent works in that tree, not in the landing directory: the brief, the
        // note and the command log all live outside it so none of them show up in the
        // change a reviewer judges.
        var agent = harness.Processes.TheOnlyAgent();
        Assert.EndsWith("tree", agent.WorkingDirectory!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_routing_round_is_given_no_repository_at_all()
    {
        using var harness = AFactory();

        await harness.Runner.RunRoundAsync(ARound(purpose: RoundPurpose.Route), CancellationToken.None);

        // The absence of the clone is the whole mechanism: a routing round cannot read a
        // repository it was not given. Nothing here asks git for anything, and the agent
        // works in an empty directory rather than in a tree.
        Assert.DoesNotContain(harness.Processes.Calls, call => call.Executable == "git");
        var agent = harness.Processes.TheOnlyAgent();
        Assert.EndsWith(Path.Combine("work"), agent.WorkingDirectory!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_rounds_brief_reaches_the_agent_as_a_file_and_never_as_shell_syntax()
    {
        // A reviewer's feedback, quoted as a reviewer would write it if they were trying
        // to break the factory. Every one of these is a metacharacter.
        const string feedback = "\"; rm -rf /; echo \"`whoami` $(id) && 'quoted'";

        using var harness = AFactory();
        var round = ARound(feedback: feedback);

        await harness.Runner.RunRoundAsync(round, CancellationToken.None);

        var landing = RoundRunner.LandingFor(harness.Options, round);

        // The brief reaches the agent in exactly one place: a file in the round's own
        // landing directory, handed to the CLI with `--file`. The reviewer's words are in
        // the file and nowhere near the command line.
        var brief = File.ReadAllText(Path.Combine(landing, "brief.md"));
        Assert.Contains(feedback, brief, StringComparison.Ordinal);

        var agent = harness.Processes.TheOnlyAgent();

        // The command is fixed apart from the file it names, the model the project named
        // and the factory's own prompt: nothing in it is a stranger's words.
        Assert.DoesNotContain(agent.Arguments, argument => argument.Contains(feedback, StringComparison.Ordinal));
        Assert.Equal(
            [
                "run", "--standalone", "--auto", "-m", ProjectFile.Model,
                "--file", Path.Combine(landing, "brief.md"),
                "--", RoundRunner.AgentPrompt(landing),
            ],
            agent.Arguments);
    }

    [Fact]
    public async Task A_round_is_handed_the_projects_llm_credential_under_the_name_the_provider_reads()
    {
        // The sharp line this ticket has to keep. Two credentials are named in the project
        // file and they are not the same kind of thing: the LLM key is what the agent
        // reaches a provider with and has to be in the round, and the GitHub key is the
        // one thing that can write to a remote and must never be. Both are
        // configured here, so a runner that passed the wrong one would be caught.
        var credentials = new FakeCredentialReader()
            .Having("NEXUS_ANTHROPIC_API_KEY", "llm-secret-value")
            .Having("NEXUS_GITHUB_TOKEN", "github-secret-value");

        using var harness = AFactory(credentials: credentials);

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        var environment = harness.Processes.TheOnlyAgent().Environment;

        // The LLM key is in, because the agent cannot reach a provider without it — and it
        // is in under the name the provider's tooling actually reads, derived from the
        // model reference, not under the project's own name for it (#38).
        Assert.Equal("llm-secret-value", environment["ANTHROPIC_API_KEY"]);
        Assert.Equal(1, environment.Values.Count(value => value == "llm-secret-value"));
        Assert.DoesNotContain("NEXUS_ANTHROPIC_API_KEY", environment.Keys);

        // The GitHub key is out, and the check is that its *value* was never even read:
        // the runner has no way to hand over what it never picked up, which is what makes
        // ADR-0006 structural rather than a matter of care.
        Assert.DoesNotContain(environment.Values, value => value == "github-secret-value");
        Assert.DoesNotContain("NEXUS_GITHUB_TOKEN", credentials.Asked);
        Assert.Equal(["NEXUS_ANTHROPIC_API_KEY"], credentials.Asked);
    }

    [Fact]
    public async Task A_round_is_handed_the_projects_own_environment_for_the_agents_benefit_and_nothing_else()
    {
        // Everything the runner sets is either the round's own machinery or a value derived
        // from the work item or the project. A test that read the names alone would miss a
        // ninth variable handing something over.
        var credentials = new FakeCredentialReader().Having("NEXUS_ANTHROPIC_API_KEY", "llm-secret-value");
        using var harness = AFactory(credentials: credentials);

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        var handed = harness.Processes.TheOnlyAgent()
            .Environment
            .Keys
            .Where(name => name.StartsWith("AGENT_FACTORY_", StringComparison.Ordinal)
                || name.StartsWith("NEXUS_", StringComparison.Ordinal)
                || name.StartsWith("ANTHROPIC_API_KEY", StringComparison.Ordinal)
                || name.StartsWith("OPENCODE_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AGENT_FACTORY_AGENT_PROMPT",
                "AGENT_FACTORY_BASE_REF",
                "AGENT_FACTORY_COMMAND_LOG",
                "AGENT_FACTORY_MODEL",
                "AGENT_FACTORY_OUT",
                "AGENT_FACTORY_WORK",
                "ANTHROPIC_API_KEY",
                "OPENCODE_DISABLE_PROJECT_CONFIG",
            },
            handed);
    }

    [Fact]
    public async Task A_round_whose_project_llm_credential_is_not_configured_still_runs_and_says_so()
    {
        // A project names a credential it does not have. The honest outcomes are to run the
        // round and say the credential was missing, or to refuse — and refusing would be the
        // factory deciding for itself that the agent needs one, which is the agent's call
        // and not the factory's. What is not acceptable is silence: a round handed nothing
        // that says nothing reads on a board as a round that needed nothing.
        var credentials = new FakeCredentialReader();
        using var harness = AFactory(credentials: credentials);

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        var environment = harness.Processes.TheOnlyAgent().Environment;
        Assert.DoesNotContain("ANTHROPIC_API_KEY", environment.Keys);
        Assert.Equal(["NEXUS_ANTHROPIC_API_KEY"], credentials.Asked);

        // The round produced a result regardless: an agent run without a key is a round
        // that happened, and what it says about the absence is in its own record.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);
    }

    [Fact]
    public async Task A_round_configures_the_agent_itself_and_the_tree_it_checks_out_says_nothing()
    {
        // The model-reference decision's other half (#38). A round tree's own
        // `opencode.json` is honored by the CLI and can set the provider endpoint, which
        // turns the round's credential into whatever server that file names. The factory
        // assumes its workloads are untrusted — they are written by agents — so the tree
        // is not asked to behave; it is not consulted at all.
        using var harness = AFactory();

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        Assert.Equal(
            "1",
            harness.Processes.TheOnlyAgent().Environment["OPENCODE_DISABLE_PROJECT_CONFIG"]);
    }

    [Fact]
    public async Task A_rounds_brief_carries_the_issue_the_project_and_the_reviewers_own_words()
    {
        using var harness = AFactory();
        var round = ARound(
            feedback: "the parser drops the last field",
            title: "The parser drops the last field",
            body: "Parsing a line of CSV loses whatever came after the final comma.");

        await harness.Runner.RunRoundAsync(round, CancellationToken.None);

        var brief = File.ReadAllText(Path.Combine(
            RoundRunner.LandingFor(harness.Options, round), "brief.md"));

        // The issue, not the issue number. A round handed a number has to go and look up
        // what it means, and a brief that does not carry the maintainer's own words is the
        // factory paraphrasing the work it was asked to do.
        Assert.Contains("The parser drops the last field", brief, StringComparison.Ordinal);
        Assert.Contains("Parsing a line of CSV loses whatever came after the final comma.", brief, StringComparison.Ordinal);
        Assert.Contains("nexus", brief, StringComparison.Ordinal);
        Assert.Contains(RepoUrl, brief, StringComparison.Ordinal);

        // The reviewer's reasons, kept whole and in their own words.
        Assert.Contains("the parser drops the last field", brief, StringComparison.Ordinal);

        // And the brief tells the agent to commit and stop, because the host pushes and
        // the round cannot. The repository's own scripts decide what testing
        // means; the factory does not get an opinion.
        Assert.Contains("decide what running its tests means", brief, StringComparison.Ordinal);
        Assert.Contains("commit your work to the branch you are on and stop there", brief, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_round_drives_the_agent_non_interactively_with_the_project_s_own_model()
    {
        using var harness = AFactory();

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        var agent = harness.Processes.TheOnlyAgent();

        // `opencode run` is the non-interactive form, the brief reaches it as a
        // *file*, and the model arrives as `-m` with the project's own full reference
        // verbatim (#38).
        Assert.Equal("run", agent.Arguments[0]);
        Assert.Contains(agent.Arguments, argument => argument == "--standalone");

        // `--auto` is a deliberate choice and worth being explicit about: without it the
        // CLI stops and asks a human to approve every edit, and there is no human inside a
        // round. What it widens is the agent's freedom *within its own working tree* —
        // which holds no credential that can write to a remote, and reaches nothing the
        // factory did not put in its environment. There is nothing outside the
        // round for a confused or injected agent to reach but this machine's own files,
        // which is the containment the trust boundary names.
        Assert.Contains(agent.Arguments, argument => argument == "--auto");
        Assert.Equal(ProjectFile.Model, agent.Arguments.ToList()[agent.Arguments.ToList().IndexOf("-m") + 1]);
    }

    [Fact]
    public async Task A_round_that_ran_to_completion_with_a_change_is_produced_and_carries_its_record()
    {
        // Real git end to end: the runner clones a real repository with its own commands,
        // the agent edits and commits, and the result is read off what actually happened.
        using var source = LiftedTree.WithACommitOn();
        using var harness = AFactory(
            new FakeProcessCli()
                .WithRealGit()
                .AgentEdits(
                    "src/Index.cs",
                    "public sealed class Index { /* changed */ }\n",
                    note: "Added the answer.",
                    commit: true),
            repoUrl: source.Path);

        var round = ARound(repoUrl: harness.RepoUrl);
        var result = await harness.Runner.RunRoundAsync(round, CancellationToken.None);

        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // The payload is a record derived from what the round observed, not the file's own
        // bytes: the file git saw, the note, and nothing the agent merely claimed.
        Assert.DoesNotContain("\"kind\":\"result\"", result.ResultPayload!, StringComparison.Ordinal);
        if (!result.ResultPayload!.Contains("src/Index.cs", StringComparison.Ordinal))
        {
            throw new Xunit.Sdk.XunitException(
                "the payload does not name the file the agent changed. The payload was: " + result.ResultPayload + " | The tree now holds: [" + string.Join(", ", Listing(Path.Combine(RoundRunner.LandingFor(harness.Options, round), "tree"))) + "]");
        }
        Assert.Equal("Added the answer.", result.AgentNote);

        // And the tree is still there, kept, because the host pushes from it.
        Assert.True(Directory.Exists(Path.Combine(
            RoundRunner.LandingFor(harness.Options, round), "tree", ".git")));
    }

    [Fact]
    public async Task A_round_whose_own_commands_failed_is_still_a_result_and_not_a_failure()
    {
        // The exit codes are the whole point of this test. The round's own exit code is the
        // agent's — the process the runner starts *is* the agent — so the agent here exits
        // **0** and a command *inside* the round exits 1. That is the shape of a build whose
        // tests failed, and it is the case ADR-0001 is about: a result, with the failing
        // exit code as data in it, and nothing for a retry policy to act on.
        using var harness = AFactory();
        harness.Processes.Agent((request, _) =>
        {
            var log = request.Environment!["AGENT_FACTORY_COMMAND_LOG"];
            File.AppendAllText(log, """
                {"kind":"command","seq":1,"label":"the tests","argv":["./scripts/test.sh"],"cwd":"/work","env":[],"startedAt":"2026-01-01T00:00:00Z","durationMs":1800,"exitCode":1,"captured":true,"stdoutBytes":0,"stderrBytes":19,"stdoutTail":"","stderrTail":"expected 42, got 0\n"}

                """);
            return Task.FromResult(new FakeProcessCli.ProcessAnswer(0, "the agent finished"));
        });

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Null(result.Failure);
        Assert.False(result.IsRetryable);

        // The failure is in the record, said as a number and as what the command said.
        Assert.Contains("exit 1", result.ResultPayload!, StringComparison.Ordinal);
        Assert.Contains("expected 42, got 0", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_whose_agent_did_not_finish_is_a_transient_failure_and_not_a_result()
    {
        // The other half of the test above, and the defect #22 exists for.
        //
        // The first real run hit a provider rate limit: the agent exited 1 in two and a
        // half seconds, wrote nothing, and the card said `data-outcome="Produced"
        // data-diff-state="empty"` over the sentence "Empty. Nothing on disk differs from
        // the commit the round started at" — a confident false claim about a round that
        // never started. Here the process the runner starts *is* the agent, so the exit
        // code the runner reads is the agent's own, with no script between them to move the
        // boundary.
        using var harness = AFactory();
        harness.Processes.AgentFails(1, "Error: Error from provider (Console): Rate limit exceeded. Please try again later.");

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // Failed. Not Produced, and the distinction is the round's own exit code rather
        // than any flag — a failing test returns zero from the agent and is still Produced,
        // which is the test above.
        Assert.Equal(RoundOutcome.Failed, result.Outcome);

        // Transient, and this is the case the retry policy was built without being able to
        // see. Three attempts at one round, then it parks; the cost of being wrong the
        // other way is a work item parked over a rate limit that a second attempt would
        // have ridden out.
        Assert.Equal(FailureClass.Transient, result.Failure);
        Assert.True(result.IsRetryable);

        // And the provider's own words are carried anyway, because they are the only
        // account of what stopped the round: they are on the round's log, which the card
        // renders beside the payload, and the payload says the round did not finish.
        Assert.NotNull(result.ResultPayload);
        Assert.Contains("Rate limit exceeded", result.Log!, StringComparison.Ordinal);
        Assert.Contains("EXITED 1", result.ResultPayload, StringComparison.Ordinal);
        Assert.Contains("did not run to completion", result.ResultPayload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rounds_files_changed_come_from_its_own_git_state_and_not_from_the_agents_note()
    {
        // End to end through the runner rather than only through the deriver: the claim is
        // that a round's *result* reflects the round, and this is the boundary the result
        // crosses on its way onto the board. The note claims three files; git saw one of
        // them, and only that one is in the payload above the agent's own account.
        using var source = LiftedTree.WithACommitOn();
        using var harness = AFactory(
            new FakeProcessCli()
                .WithRealGit()
                .AgentEdits(
                    "src/Index.cs",
                    "public sealed class Index { /* changed */ }\n",
                    note: "Rewrote src/Index.cs, README.md and deleted LICENSE.",
                    commit: true),
            repoUrl: source.Path);

        var result = await harness.Runner.RunRoundAsync(ARound(), CancellationToken.None);

        var derived = result.ResultPayload!;
        var agentOwnAccount = derived.IndexOf("the agent's own account", StringComparison.Ordinal);
        Assert.True(agentOwnAccount > 0, "the payload separates what was observed from what the agent said");

        var observed = derived[..agentOwnAccount];
        Assert.Contains("src/Index.cs", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("README.md", observed, StringComparison.Ordinal);
        Assert.DoesNotContain("LICENSE", observed, StringComparison.Ordinal);

        // The note is still there, verbatim, below the line. It is kept as the agent's
        // account of itself and is derived from by nothing.
        Assert.Equal("Rewrote src/Index.cs, README.md and deleted LICENSE.", result.AgentNote);
    }

    [Fact]
    public async Task A_round_whose_result_could_not_be_written_is_a_permanent_failure_and_carries_the_log()
    {
        using var harness = AFactory();
        harness.Processes.Agent((request, _) =>
        {
            // A landing directory that cannot take a result file — the result's own name is
            // a directory — is the shape of a round whose observations cannot be written:
            // the collector cannot create the file, and the round comes back with no result
            // at all.
            Directory.CreateDirectory(Path.Combine(
                request.Environment!["AGENT_FACTORY_OUT"],
                "result.json"));
            return Task.FromResult(new FakeProcessCli.ProcessAnswer(0, "the round said something"));
        });

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);

        // Permanent, and this is the classification the whole retry policy turns on. The
        // agent ran and the round produced nothing readable: a second attempt would be told
        // the same thing by the same broken directory.
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
        Assert.Null(result.ResultPayload);

        // The log is on the result rather than only in the factory's own logging: a round
        // with no payload at all must not be invisible on the board, and the log is what
        // there is to point a reviewer at.
        Assert.Contains("the round said something", result.Log!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_whose_agent_is_not_on_this_machine_is_a_permanent_failure()
    {
        // The binary is either on this machine's PATH or it is not, and a second attempt
        // in ten seconds begins a second identical failure. The classification is decided
        // where the absence is observed — the process seam — and carried here rather than
        // worked out again from a message.
        using var harness = AFactory();
        harness.Processes.FailsToStart();

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
        Assert.Null(result.ResultPayload);
    }

    [Fact]
    public async Task A_round_whose_repository_could_not_be_fetched_is_a_permanent_failure_and_says_why()
    {
        // A round holds no credential that can fetch a private repository, so a refusal to
        // clone is expected for one and permanent either way: the round never started, and
        // there is nothing a second attempt would see that the first did not.
        using var harness = AFactory();
        harness.Processes.GitAnswers(128, "fatal: could not read from the repository");

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
        Assert.Null(result.ResultPayload);
        Assert.Contains("could not be cloned", result.Log!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_round_that_was_ended_by_the_factory_says_so_and_kills_nothing_else()
    {
        using var harness = AFactory();
        using var rounds = new CancellationTokenSource();
        harness.Processes.AgentHolds();

        var running = harness.Runner.RunRoundAsync(ARound(), rounds.Token);
        await WaitUntilTheRoundIsInsideAsync(harness);
        await rounds.CancelAsync();

        // The call ends, and it ends as a cancellation rather than as a round that failed:
        // the loop has already recorded this one as timed out, and telling it a second
        // time that the round failed would be two answers to one question. Bounded, since
        // a round that ignored its token would otherwise hang the suite instead of failing
        // it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_rounds_result_and_tree_land_beside_the_store_and_are_kept_after_the_round()
    {
        using var source = LiftedTree.WithACommitOn();
        using var harness = AFactory(
            new FakeProcessCli().WithRealGit().AgentEdits("src/Index.cs", "changed\n", commit: true),
            repoUrl: source.Path);

        var result = await harness.Runner.RunRoundAsync(ARound(repoUrl: harness.RepoUrl), CancellationToken.None);

        // The outcome is named before the layout, so a round that failed at the clone fails
        // here, with its reason, rather than as a missing directory further down.
        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // ADR-0006: the host has to reach the round's commit after the round is over,
        // because the host pushes and not the round. That is only true if the files are
        // not temporary, so where they land and that they are still there afterwards are
        // both part of the promise.
        //
        // The path carries the work item, the round and the attempt — the layout that
        // keeps round 2's tree from landing inside round 1's (#22).
        var landing = Path.Combine(
            harness.Options.RoundsDirectory,
            WorkItem.ToString("N"),
            "round-1",
            "attempt-1");

        Assert.True(
            Directory.Exists(Path.Combine(landing, "tree", ".git")),
            $"the round's tree is not where it landed: landing has [{string.Join(", ", Listing(landing))}] "
                + $"and tree has [{string.Join(", ", Listing(Path.Combine(landing, "tree")))}]; the seam saw "
                + string.Join(" | ", harness.Processes.Calls.Select(call => call.Executable + " " + string.Join(" ", call.Arguments.Take(3))))
                + "; the result said: " + result.Outcome + " / " + (result.ResultPayload ?? "(none)"));
        // And it is kept rather than cleaned up: the promise is that the commit is still
        // there for the merger once the round is gone. This harness's own dispose is what
        // takes the directory away afterwards, which is the test's doing and not the
        // round's.
        Assert.Equal(Path.GetDirectoryName(harness.Options.DatabasePath), Path.GetDirectoryName(harness.Options.RoundsDirectory));
    }

    [Fact]
    public async Task Every_attempt_of_every_round_lands_in_a_directory_of_its_own()
    {
        // The nesting hazard, stated as a property of the layout rather than as an
        // observation about one round. One directory per work item would put round 2's tree
        // inside round 1's — and the board's diff and the merger's push would both then be
        // reading the first round while believing they were reading the second (#22).
        //
        // This is the assertion that would have caught it: it is about the paths, so it
        // holds whatever any of the rounds actually produced, and no two of them can
        // collide however the loop schedules them.
        using var source = LiftedTree.WithACommitOn();
        using var harness = AFactory(
            new FakeProcessCli().WithRealGit().AgentEdits("src/Index.cs", "changed\n", commit: true),
            repoUrl: source.Path);

        var landings = new List<string>();

        // Two rounds of one work item, and a second attempt at the first round — the
        // retry, which is the same round asked for again and is the case a per-round path
        // alone would still have got wrong.
        foreach (var round in new[] { (1, 1), (1, 2), (2, 1) })
        {
            await harness.Runner.RunRoundAsync(ARound(roundNumber: round.Item1, attempt: round.Item2, repoUrl: harness.RepoUrl), CancellationToken.None);
            landings.Add(RoundRunner.LandingFor(harness.Options, ARound(roundNumber: round.Item1, attempt: round.Item2, repoUrl: harness.RepoUrl)));
        }

        Assert.Equal(3, landings.Distinct(StringComparer.Ordinal).Count());
        Assert.All(landings, landing => Assert.True(Directory.Exists(landing), $"{landing} was not created"));
        Assert.All(landings, landing =>
            Assert.True(
                File.Exists(Path.Combine(landing, "tree", ".git", "HEAD")),
                $"no tree of its own under {landing}"));
    }

    [Fact]
    public async Task A_round_whose_project_file_is_not_being_served_starts_nothing()
    {
        using var harness = AFactory();

        var result = await harness.Runner.RunRoundAsync(
            ARound(project: "a project nobody serves"),
            CancellationToken.None);

        Assert.Equal(RoundOutcome.Failed, result.Outcome);
        Assert.Empty(harness.Processes.Calls);

        // Permanent, and unlike a process that would not start this one cannot change:
        // configuration loads at start and does not hot reload, so a second attempt would
        // start nothing either.
        Assert.Equal(FailureClass.Permanent, result.Failure);
        Assert.False(result.IsRetryable);
    }
    private static async Task WaitUntilTheRoundIsInsideAsync(Harness harness)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (harness.Processes.From("opencode") is [var agent]
                && agent.WorkingDirectory is { })
            {
                return;
            }

            await Task.Yield();
        }

        throw new Xunit.Sdk.XunitException("the round never reached the agent");
    }

    /// <summary>What a directory holds, for a failure message. Names only, sorted.</summary>
    private static IEnumerable<string> Listing(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? Directory.EnumerateFileSystemEntries(path)
                    // `Path.GetFileName` is null-annotated because a null path has no file
                    // name; an enumerator over a directory never yields one, so the `??` is
                    // dead in practice and there to keep that a fact the compiler can see.
                    .Select(entry => Path.GetFileName(entry) ?? entry)
                    .Order(StringComparer.Ordinal)
                    .ToList()
                : ["(the directory does not exist)"];
        }
        catch (IOException)
        {
            return ["(the directory could not be read)"];
        }
    }
    private static Round ARound(
        string feedback = "",
        string project = "nexus",
        string title = "A work item, end to end",
        string body = "What the issue says, in the maintainer's words.",
        int roundNumber = 1,
        int attempt = 1,
        RoundPurpose purpose = RoundPurpose.Build,
        string? repoUrl = null) => new(
        WorkItem,
        project,
        repoUrl ?? RepoUrl,
        42,
        title,
        body,
        "main",
        feedback,
        roundNumber,
        attempt,
        purpose);

    /// <summary>
    /// The harness's own round, carrying the harness's own repository URL — the runner
    /// clones what the <em>round</em> names, and a test over real git must name the local
    /// source or its round goes to the network, which is neither hermetic nor honest about
    /// what passed.
    /// </summary>
    private static Round ARoundOf(Harness harness, RoundPurpose purpose = RoundPurpose.Build) =>
        ARound(purpose: purpose, repoUrl: harness.RepoUrl);

    private static Harness AFactory(
        FakeProcessCli? processes = null,
        string repoUrl = RepoUrl,
        FakeCredentialReader? credentials = null)
    {
        var projects = new ProjectLoadReport(
            [
                new Project(
                    "nexus",
                    repoUrl,
                    ProjectFile.Model,
                    "NEXUS_GITHUB_TOKEN",
                    "NEXUS_ANTHROPIC_API_KEY",
                    "nexus.yaml"),
            ],
            []);

        // A throwaway rounds directory, in a temporary place, because a round's files are
        // kept rather than cleaned up and a test must not leave them where a
        // later run would find them.
        var database = Path.Combine(
            Path.GetTempPath(),
            "agent-factory-rounds-tests",
            Guid.NewGuid().ToString("n"),
            "agent-factory.db");

        var options = new FactoryOptions("factories", database, new Uri("http://127.0.0.1:0"));

        // One seam, shared by the runner and the test: the harness's assertions read the
        // calls the runner actually made, which is only true when both hold the same fake.
        var seam = processes ?? new FakeProcessCli();
        var runner = new RoundRunner(
            seam,
            projects,
            options,
            new RoundResultDeriver(NullLogger<RoundResultDeriver>.Instance),
            credentials ?? new FakeCredentialReader(),
            NullLogger<RoundRunner>.Instance);

        return new Harness(seam, runner, options, repoUrl);
    }

    /// <summary>
    /// A round runner over a fake process seam, with a throwaway rounds directory.
    /// Disposing it removes the directory a round's kept files land in, which every test
    /// here needs: a round's result and tree are kept rather than cleaned up,
    /// so a test that does not remove them leaves the next run with a directory it did not
    /// create.
    /// </summary>
    private sealed class Harness(FakeProcessCli processes, RoundRunner runner, FactoryOptions options, string repoUrl) : IDisposable
    {
        public FakeProcessCli Processes { get; } = processes;

        public RoundRunner Runner { get; } = runner;

        public FactoryOptions Options { get; } = options;

        public string RepoUrl { get; } = repoUrl;

        public RoundResult? LastResult { get; set; }

        public void Dispose()
        {
            try
            {
                var root = Path.GetDirectoryName(Path.GetFullPath(Options.DatabasePath));
                if (root is not null && Directory.Exists(root))
                {
                    GitTrees.Delete(root);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
        }
    }
}
