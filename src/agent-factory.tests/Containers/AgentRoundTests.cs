namespace AgentFactory.Tests.Containers;

using AgentFactory.Containers;
using AgentFactory.Credentials;
using AgentFactory.Projects;
using AgentFactory.Results;
using AgentFactory.Rounds;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// A round in a real worker container, with the factory's own runtime and the factory's
/// own deriver, and a script standing in for the agent.
///
/// <para>
/// This is where the deriver is proved against a result file a real container really
/// wrote — which the hermetic deriver tests cannot do, because they write the fixtures
/// themselves and so agree with the deriver by construction. Everything here is the real
/// machinery: <c>docker create</c>, <c>docker start</c>, <c>docker logs</c>, <c>docker cp</c>,
/// the image's own entrypoint, the image's own recording wrapper, the image's own
/// collector, and the factory's own reading of the result.
/// </para>
///
/// <para>
/// What runs in the container is a deterministic script, exactly as
/// <c>worker/smoke-test.sh</c> does it and for the same reason: an LLM is not what is
/// under test, and a test that needs one tests the API key. The script behaves the way an
/// agent behaves — it changes a file, runs a command that passes, runs one that fails,
/// leaves a scratch file behind, writes a note, and commits — and every one of those is
/// something a deriver has to get right.
/// </para>
///
/// <para>
/// The round that really drives the model is <see cref="AgentFactAttribute"/>-gated and
/// lives alongside these; it is skipped with a stated reason on a machine with no
/// provider credential, which is what makes it honest rather than merely green.
/// </para>
/// </summary>
[Collection("worker containers")]
public class AgentRoundTests
{
    private const string RepoUrl = "https://github.com/octocat/Hello-World.git";
    private const string BaseRef = "master";

    /// <summary>
    /// What the round runs, standing in for the agent. Written in the shape
    /// <c>worker/smoke/round.sh</c> uses so that what the deriver is asked to read is the
    /// same shape the image produces in production — including the parts that only exist
    /// because a real agent leaves them: a scratch file git status can see and a diff
    /// cannot, and a failing command in the middle of a successful round.
    /// </summary>
    private const string TheRound = """
        set -uo pipefail
        out="${AGENT_FACTORY_OUT:-/out}"
        cd "$AGENT_FACTORY_WORK"

        run -o 'the tests, before the change' bash -c 'test -f README && echo "1 passed, 0 failed"'

        # A failing command in the middle of a round that goes on to succeed. The deriver
        # has to record the exit code and not the round's verdict: a build that fails a
        # command is a result, not a failure to retry (ADR-0001).
        run -o 'a check that fails on purpose' bash -c 'echo "lint: 2 problems" >&2; exit 3'

        # The change itself, and an untracked file the round leaves behind. A committed
        # file is clean in git status and visible in the diff; an untracked one is the
        # reverse. Reading only one of the two silently drops half of what a reviewer
        # judges, and this is the case that catches that.
        printf 'The answer is 42.\n' > answer.txt
        run -o 'the change' git add -A
        run -o 'the change' bash -c 'echo "scratch" > scratch.tmp'
        run -o 'the commit' git commit -q -m 'answer the question'
        run -o 'the tests, after the change' bash -c 'grep -q 42 answer.txt && echo "1 passed, 0 failed"'

        # The agent's one optional sentence.
        printf 'Added answer.txt, because the question was what the answer is.\n' > "$out/note.md"
        """;

    [DockerFact]
    public async Task A_rounds_result_is_derived_from_what_its_own_container_observed()
    {
        // The whole of ADR-0011 against a real container: a round runs, the image records
        // what it observed, and the factory reads that into a result. Nothing is authored
        // and nothing is asserted — the files below are what `git status` and `git diff`
        // said inside the container, read back through `docker cp` and derived here.
        using var landing = new Landing();
        var (result, derived) = await UnderWay(() => RunARound(landing, TheRound, LlmKey: null));

        Assert.Equal(RoundOutcome.Produced, result.Outcome);

        // The container really ran, as the unprivileged user, holding no credential that
        // can write to a remote (ADR-0006). These are read off the result the container
        // wrote about itself, not off the command that asked for it.
        Assert.Equal(1000, derived.Environment.Uid);
        Assert.Equal("agent", derived.Environment.User);
        Assert.Empty(derived.Environment.CredentialNames);
        Assert.True(derived.Environment.IsARepository);

        // The files, from git. `answer.txt` is committed, so it is in the diff and not in
        // status; `scratch.tmp` is untracked, so it is in status and not in the diff. A
        // deriver that read either observation alone would miss one of them.
        var paths = derived.FilesChanged.Select(file => file.Path).ToArray();
        Assert.Contains("answer.txt", paths);
        Assert.Contains("scratch.tmp", paths);
        Assert.Equal(FileChange.Added, derived.FilesChanged.Single(file => file.Path == "answer.txt").Change);
        Assert.Equal(FileChange.Untracked, derived.FilesChanged.Single(file => file.Path == "scratch.tmp").Change);

        // The diff is the one a reviewer judges: against the commit the round started at,
        // so the round's own commit is in it rather than invisible.
        Assert.Contains("answer.txt", derived.Diff, StringComparison.Ordinal);
        Assert.Contains("The answer is 42.", derived.Diff, StringComparison.Ordinal);
        Assert.Contains("answer the question", string.Join('\n', derived.Commits.Select(commit => commit.Subject)), StringComparison.Ordinal);

        // The commands, from the recording wrapper, each with the exit code it returned.
        Assert.NotEmpty(derived.CommandsRun);
        Assert.Contains(derived.CommandsRun, command => command.Label == "the tests, after the change");
        Assert.Contains(derived.CommandsRun, command => !command.Passed && command.ExitCode == 3);
        Assert.Contains(derived.CommandsRun, command => command.Label == "the tests, before the change" && command.Passed);

        // And the agent's note, copied raw.
        Assert.Contains("Added answer.txt", result.AgentNote!, StringComparison.Ordinal);

        // The payload a reviewer reads carries all of it, which is the claim the whole
        // deriver exists to make true.
        foreach (var observable in new[]
                 {
                     "answer.txt",
                     "scratch.tmp",
                     "the tests, after the change",
                     "exit 3",
                     "The answer is 42.",
                 })
        {
            Assert.Contains(observable, result.ResultPayload!, StringComparison.Ordinal);
        }
    }

    [DockerFact]
    public async Task A_round_whose_change_failed_its_own_command_is_still_produced_and_not_a_retryable_failure()
    {
        // The structural half of "a build that fails its tests is not retried" (ADR-0001),
        // against a real round: the container worked, a command inside it failed, and the
        // failing exit code is data in the result rather than a classification on it.
        using var landing = new Landing();
        var (result, derived) = await UnderWay(() => RunARound(
            landing,
            "set -uo pipefail\ncd \"$AGENT_FACTORY_WORK\"\nrun -o 'the build' bash -c 'exit 1'\n",
            LlmKey: null));

        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Null(result.Failure);
        Assert.False(result.IsRetryable);

        // The failure is in the record, as a number and as what the command said — which is
        // the only place a reviewer can read it. Both the labelled command and the round's
        // own script are in there, because the wrapper records every invocation it wraps,
        // nested or not, and both of them really did exit 1.
        Assert.Contains(derived.FailedCommands, command => command.Label == "the build" && command.ExitCode == 1);
        Assert.All(derived.FailedCommands, command => Assert.Equal(1, command.ExitCode));
        Assert.Contains("exit 1", result.ResultPayload!, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_round_that_wrote_no_result_file_degrades_to_its_log_rather_than_to_nothing()
    {
        // Story 29 against a real container. The script below ends the round's shell before
        // the entrypoint can collect, so the result file genuinely is not there and the
        // `docker cp` genuinely fails — which is the shape the deriver has to degrade on,
        // and the one where a naive implementation renders an empty card.
        using var landing = new Landing();
        var (result, derived) = await UnderWay(() => RunARound(
            landing,
            // A round that leaves the result file's own directory unwritable, by putting a
            // plain file where the collector expects a directory. The entrypoint collects
            // after the round ends and cannot, so the result file genuinely is not there
            // and the `docker cp` genuinely fails — which is the shape the deriver has to
            // degrade on, and the one a fake would have to be told to produce.
            """
            set -uo pipefail
            echo 'the round said something worth keeping'
            printf 'not a directory' > "${AGENT_FACTORY_RESULT%/*}/nested"
            """,
            LlmKey: null,
            fetchTheRepository: false,
            resultPath: "/out/nested/result.json"));

        // The container ran and its log came back. The result is not a record, and it says
        // so rather than reading as a round that changed nothing and ran nothing — which
        // is the failure mode story 29 names.
        Assert.False(derived.IsARecord);
        Assert.NotNull(derived.UnreadableBecause);
        Assert.Empty(derived.FilesChanged);
        Assert.Empty(derived.CommandsRun);

        // The log is there and on the payload, so the round is not invisible.
        Assert.NotNull(result.Log);
        Assert.Contains("the round said something worth keeping", result.Log!, StringComparison.Ordinal);
        Assert.Contains("the round said something worth keeping", result.ResultPayload!, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.ResultPayload!, StringComparison.Ordinal);
    }

    [DockerFact]
    public async Task A_round_is_handed_the_llm_credential_and_holds_no_credential_that_can_write_to_a_remote()
    {
        // The sharp line, checked against a real container rather than against a recorded
        // argv. The round is given a project whose LLM key *is* configured and whose
        // GitHub key is not read at all, and the result says what the round was handed.
        //
        // This does not prove the key reached a provider — that is the agent test's job and
        // it needs a real credential. What it proves is the property that does not: the
        // round is handed the one credential it is supposed to have, and the round's own
        // observation of its environment agrees.
        using var landing = new Landing();
        var (result, derived) = await UnderWay(() => RunARound(
            landing,
            "set -uo pipefail\ncd \"$AGENT_FACTORY_WORK\"\nprintf 'a note\\n' > \"$AGENT_FACTORY_NOTE\"\n",
            LlmKey: "worker-round-probe-credential-value"));

        // The container recorded the *name* of the credential-shaped variable it was given
        // and no value of it, which is what the image does and what the deriver carries
        // through. A reviewer reads the name and can satisfy themselves the round had no
        // GitHub token (ADR-0006).
        Assert.Equal(["WORKER_ROUND_PROBE_API_KEY"], derived.Environment.CredentialNames);
        Assert.DoesNotContain(
            "worker-round-probe-credential-value",
            result.ResultPayload!,
            StringComparison.Ordinal);

        // The project's GitHub key is not in the round's environment at all, so it could
        // not have been handed over: the runner never reads that name.
        Assert.DoesNotContain("NEXUS_GITHUB_TOKEN", derived.Environment.CredentialNames);
    }

    // --- the agent, with a real provider -------------------------------------

    [AgentFact]
    public async Task A_real_agent_is_driven_non_interactively_inside_the_container_and_its_result_is_derived()
    {
        // The half that needs a provider. Where the test above stands a deterministic
        // script in for the agent, this drives the real OpenCode CLI over a real
        // repository with a real brief, and checks that what comes back is a result the
        // factory derived from the container rather than a sentence the model wrote.
        //
        // It is skipped, with the reason stated, on a machine with no provider credential —
        // which is the honest handling of the one thing a green suite cannot invent.
        using var landing = new Landing();
        var (result, derived) = await UnderWay(
            () => RunTheRealAgent(landing),
            TimeSpan.FromMinutes(10));

        Assert.Equal(RoundOutcome.Produced, result.Outcome);
        Assert.Equal(1000, derived.Environment.Uid);
        Assert.True(derived.Environment.IsARepository, "the agent was given a real repository to build in");

        // The agent ran non-interactively: the CLI is in the round's own record of what it
        // ran, which is what ADR-0004's claim reduces to on the factory's side.
        Assert.Contains(
            derived.CommandsRun,
            command => command.Command.StartsWith("opencode run", StringComparison.Ordinal));

        // The brief reached the agent as words, through the file the round wrote it to
        // rather than as a command line of its own.
        Assert.Contains(
            derived.CommandsRun,
            command => command.Command.Contains("brief.md", StringComparison.Ordinal));

        // The brief said one thing and the tree can say whether it happened. This is the
        // check that matters and the one the deriver's own tests cannot make: the file the
        // brief asked for, read off the container's own filesystem, and the factory's result
        // naming it without ever having been told to.
        var created = File.Exists(Path.Combine(landing.Path, "tree", "answer.txt"));
        var named = derived.FilesChanged.Any(file => file.Path == "answer.txt")
            || derived.Diff.Contains("answer.txt", StringComparison.Ordinal);

        // One direction is a hard assertion and the other is not, and the asymmetry is
        // deliberate. If the file is there, the result must name it: a result that omits a
        // change that happened is the failure this whole ticket exists to prevent. If the
        // file is not there, the result must *not* claim it — but a model is a model, and a
        // round that did not do the work is a legitimate outcome the factory records
        // honestly rather than one a test should fail on.
        if (created)
        {
            Assert.True(
                named,
                "the round left answer.txt on disk and the derived result does not name it, so the result is not "
                    + "what is on disk");
            Assert.Contains("42", derived.Diff, StringComparison.Ordinal);
        }

        // The agent's own account, kept and believed about nothing: whether it claims the
        // work or not, the result above came from git.
        Assert.NotNull(derived.AgentNote);

        // And what the factory says happened matches what the round's own lifted-out tree
        // says happened, read with git on this host rather than from the result.
        Assert.Equal(landing.Tree(), (derived.Commits.Count > 0, derived.Diff.Length > 0));
    }

    /// <summary>
    /// One real round: a real container from the real image, the real runtime, and the
    /// real deriver over whatever really came back.
    /// </summary>
    private static Task<FinishedRound> RunARound(
        Landing landing,
        string script,
        string? LlmKey,
        string? brief = null,
        bool fetchTheRepository = true,
        string? resultPath = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AGENT_FACTORY_BRIEF"] = brief ?? RoundBrief.For(ARound()),
            ["AGENT_FACTORY_AGENT_PROMPT"] = WorkerRoundRunner.AgentPrompt,
        };

        if (fetchTheRepository)
        {
            environment["AGENT_FACTORY_REPO_URL"] = RepoUrl;
            environment["AGENT_FACTORY_BASE_REF"] = BaseRef;
        }

        if (resultPath is not null)
        {
            // The image resolves the result path from this variable, so a round that cannot
            // write it is a round with no result — which is the case the degradation covers.
            environment["AGENT_FACTORY_RESULT"] = resultPath;
        }

        if (LlmKey is not null)
        {
            // A name that is credential-shaped on purpose, because the image collects the
            // *names* of variables matching that pattern into the result — `TOKEN`,
            // `API_KEY`, `SECRET` and the rest. A round handed one is therefore recorded as
            // having been handed one, and a reviewer reading the card can see what the
            // agent had. A name that did not look like a credential would prove nothing,
            // because the image would not have recorded it and the test would pass for the
            // wrong reason.
            environment["WORKER_ROUND_PROBE_API_KEY"] = LlmKey;
        }

        // `shell` is the factory's own mode, which runs the script under the recording
        // wrapper and then collects; `exec` runs one command through the wrapper and
        // collects too. Both go through the image's own entrypoint, which is the point.
        var command = script.StartsWith("set -uo", StringComparison.Ordinal)
            ? new[] { "shell", "-c", script }
            : new[] { "exec", "bash", "-c", script };

        return UnderWay(async () =>
        {
            var docker = new DockerCli(NullLogger<DockerCli>.Instance);
            var runtime = new ContainerRuntime(docker, NullLogger<ContainerRuntime>.Instance);
            var deriver = new RoundResultDeriver(NullLogger<RoundResultDeriver>.Instance);

            var run = await runtime.RunAsync(
                new WorkerContainerRequest(
                    Container,
                    WorkerImage.Tag,
                    command,
                    environment),
                landing.Path,
                log: null,
                CancellationToken.None);

            // Assembled the way the real round runner assembles it — the deriver's reading
            // rendered into the payload the board shows, with the log travelling alongside
            // it — so that what these tests assert on is the shape a reviewer actually
            // gets rather than a test-only arrangement of the deriver's own output.
            var derived = deriver.Derive(run.ResultFile, run.LogTail);
            var result = RoundResult.Produced(
                ResultPayload.Of(derived, run.LogTail),
                derived.AgentNote,
                run.LogTail);

            return new FinishedRound(result, derived);
        });
    }

    /// <summary>
    /// One finished round: what the board would show for it, and what the factory derived
    /// underneath. Both are returned because the two halves are asserted on differently —
    /// the derived one for the facts, the result one for what a reviewer would read.
    /// </summary>
    private sealed record FinishedRound(RoundResult Result, DerivedResult Derived);

    /// <summary>
    /// A round that really drives the agent, with the brief the factory would really write
    /// and a repository the agent can really change. The brief asks for one new file, which
    /// is the smallest thing that proves the agent did work rather than that it started.
    /// </summary>
    private static Task<FinishedRound> RunTheRealAgent(Landing landing)
    {
        var round = ARound(
            title: "Leave a file with the answer in it",
            body: "The repository has no answer file. Create one named `answer.txt` in the "
                + "repository root containing exactly `42` and nothing else. Run whatever the "
                + "project itself says to run; if it says nothing to run, run "
                + "`run cat answer.txt` and look at what it says.");

        // The factory's own round script, unmodified, and the brief the factory would
        // really write for that issue. Nothing here is a test-only path: if this round
        // produces a result, it is the same code path a production round takes.
        return RunARound(landing, WorkerRoundRunner.RoundScript, LlmKey: null, brief: RoundBrief.For(round));
    }

    private static Round ARound(
        string title = "A work item, end to end",
        string body = "What the issue says, in the maintainer's words.") => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "nexus",
        RepoUrl,
        42,
        title,
        body,
        BaseRef,
        string.Empty);

    /// <summary>One container name, torn down whatever the test does.</summary>
    private static string Container =>
        $"agent-factory-agent-round-{Guid.NewGuid():N}";

    private static async Task<T> UnderWay<T>(Func<Task<T>> round, TimeSpan? limit = null)
    {
        try
        {
            return await round().WaitAsync(limit ?? TimeSpan.FromMinutes(3));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                "a real worker container round did not finish inside its limit. A real round is a real wait, and a "
                    + "wait with no bound is a suite that stops telling you anything");
        }
        finally
        {
            if (WorkerImage.Available)
            {
                WorkerImage.Docker("rm", "-f", Container);
            }
        }
    }

    /// <summary>
    /// Where a round's lifted-out files land, deleted on dispose. A tree lifted out of a
    /// Linux container arrives with the permissions it had there, and a git object file is
    /// read-only; Windows will not delete a read-only file, so the attributes go first.
    /// </summary>
    private sealed class Landing : IDisposable
    {
        public Landing()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agent-factory-agent", Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>
        /// What the round's own lifted-out tree says about its commits and its diff, read
        /// from the files on this host with git rather than from the result — so a test
        /// comparing the two is comparing the factory's reading against the container's own
        /// evidence, which is the only way "the result reflects what is on disk" is a claim
        /// about anything.
        /// </summary>
        /// <remarks>
        /// Read with git and not with <c>Directory</c> walking, because the question is
        /// what the repository thinks happened. A tree full of files and no commit is a
        /// different round from one commit with the same files, and only git can tell them
        /// apart.
        /// </remarks>
        public (bool HasCommits, bool HasDiff) Tree()
        {
            var tree = System.IO.Path.Combine(Path, "tree");
            if (!Directory.Exists(System.IO.Path.Combine(tree, ".git")))
            {
                return (false, false);
            }

            // `HEAD~1` rather than the round's start commit: the assertion is about whether
            // the round's own work reached a commit, and the round's start commit is the
            // base the entrypoint cloned, so HEAD~1 is the commit the round made.
            var log = Git(tree, "log", "--oneline");
            var diff = Git(tree, "diff", "HEAD~1", "HEAD");

            return (log is { Length: > 0 }, diff is { Length: > 0 });
        }

        /// <summary>The files the lifted-out tree has, as git reports them uncommitted.</summary>
        public IReadOnlyList<string> UncommittedInTree()
        {
            var tree = System.IO.Path.Combine(Path, "tree");
            var status = Git(tree, "status", "--porcelain") ?? string.Empty;

            return status
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line[3..].Trim())
                .ToList();
        }

        /// <summary>git against the lifted-out tree, which needs its objects writable to read.</summary>
        private static string? Git(string tree, params string[] arguments)
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = tree,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // A tree lifted out of a Linux container has its objects owned by uid 1000, and
            // git refuses to read a repository it does not own. The worktree owner is the
            // only way to make the host able to read what the container committed, and it
            // changes nothing about the tree itself.
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = "safe.directory";
            start.Environment["GIT_CONFIG_VALUE_0"] = "*";

            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = System.Diagnostics.Process.Start(start);
                if (process is null)
                {
                    return null;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(30_000);

                return process.ExitCode == 0 ? output : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a test over.
            }
            catch (UnauthorizedAccessException)
            {
                // As above, for a file whose attributes could not be cleared.
            }
        }
    }
}
