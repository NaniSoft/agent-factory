namespace AgentFactory.Tests.Results;

using System.Text;
using System.Text.Json;
using AgentFactory.Results;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The deriver, driven against result files shaped like the ones the worker image's own
/// collector writes. No Docker and no model: what is under test is that a result is read
/// out of what a container observed, and — the load-bearing half — that a round's own
/// account of itself changes nothing about it (ADR-0011).
/// </summary>
/// <remarks>
/// The fixtures here are written to the shape <c>worker/bin/worker-collect</c> writes and
/// the shape <c>worker/smoke-test.sh</c> checks for. That they are hand-written rather than
/// lifted out of a real container is the one thing these tests do not prove, and it is
/// proved by <see cref="AgentFactory.Tests.Containers.WorkerDerivationTests"/>, which
/// derives from a real round's real result file.
/// </remarks>
public class RoundResultDeriverTests : IDisposable
{
    private readonly RoundResultDeriver _deriver = new(NullLogger<RoundResultDeriver>.Instance);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "agent-factory-results", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }

    [Fact]
    public void The_files_changed_come_from_git_and_not_from_the_agents_own_account_of_them()
    {
        // The most valuable test in this ticket, and the one ADR-0011 exists for. The note
        // claims three things and git contradicts all three: a file the round never
        // touched, a change to a file it did not touch in the way it says, and a deletion
        // that never happened. If the deriver believed the note, all three would appear.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0,"gitRepo":true,"user":"agent","uid":1000,"branch":"main","startHead":"aaa1111","head":"bbb2222"}
            {"kind":"git","field":"status","byteLength":90,"truncated":false,"text":"# branch.oid bbb2222\n# branch.head main\n1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/Actual.cs\n? src/scratch.tmp\n"}
            {"kind":"git","field":"diffFromRoundStart","byteLength":120,"truncated":false,"text":"diff --git a/src/Actual.cs b/src/Actual.cs\nindex aaaaaaa..bbbbbbb 100644\n--- a/src/Actual.cs\n+++ b/src/Actual.cs\n@@ -1,2 +1,2 @@\n-old\n+new\n"}
            {"kind":"note","field":"note","text":"I also rewrote README.md, deleted LICENSE and fixed the whole of src/Actual.cs."}
            {"kind":"end","collectedInMs":11}
            """));

        var paths = result.FilesChanged.Select(file => file.Path).ToArray();

        // Exactly what git saw: the one file the diff names, and the one untracked file
        // git status saw and the diff cannot. Not one of the three the note claims.
        Assert.Equal(["src/Actual.cs", "src/scratch.tmp"], paths);
        Assert.DoesNotContain("README.md", paths);
        Assert.DoesNotContain("LICENSE", paths);
        Assert.DoesNotContain("Fixed the whole of src/Actual.cs", paths);

        // And the counts came out of the diff's own lines: one removed, one added.
        var changed = result.FilesChanged.Single(file => file.Path == "src/Actual.cs");
        Assert.Equal(FileChange.Modified, changed.Change);
        Assert.Equal(1, changed.Added);
        Assert.Equal(1, changed.Removed);

        // The note is kept, verbatim and believed about nothing.
        Assert.Equal("I also rewrote README.md, deleted LICENSE and fixed the whole of src/Actual.cs.", result.AgentNote);
    }

    [Fact]
    public void A_file_the_round_committed_is_in_the_result_even_though_git_status_cannot_see_it()
    {
        // The two observations are not redundant, and the union is why. A committed change
        // is clean in `git status` and visible in the diff; an untracked file is the
        // reverse. Reading either alone silently drops half of what a reviewer judges, and
        // the smoke test exercises both halves for exactly this reason.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"status","text":"# branch.head main\n"}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/index.js b/src/index.js\nnew file mode 100644\n--- /dev/null\n+++ b/src/index.js\n@@ -0,0 +1 @@\n+module.exports = 42;\n"}
            """));

        var file = Assert.Single(result.FilesChanged);
        Assert.Equal("src/index.js", file.Path);
        Assert.Equal(FileChange.Added, file.Change);
        Assert.Equal(1, file.Added);
        Assert.False(file.Uncommitted, "the round committed it, so status has nothing to see");
    }

    [Fact]
    public void A_renamed_file_is_recorded_as_a_rename_with_where_it_came_from()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"status","text":"2 R. R. N... 100644 100644 100644 aaaaaaa bbbbbbb R100 new/place.cs\told/place.cs\n"}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/old/place.cs b/new/place.cs\nsimilarity index 100%\nrename from old/place.cs\nrename to new/place.cs\n"}
            """));

        var file = Assert.Single(result.FilesChanged);
        Assert.Equal("new/place.cs", file.Path);
        Assert.Equal("old/place.cs", file.PreviousPath);
        Assert.Equal(FileChange.Renamed, file.Change);
    }

    [Fact]
    public void A_deleted_file_and_an_added_file_are_told_apart()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n--- a/gone.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-was here\ndiff --git a/fresh.txt b/fresh.txt\nnew file mode 100644\n--- /dev/null\n+++ b/fresh.txt\n@@ -0,0 +1 @@\n+new here\n"}
            """));

        Assert.Equal(
            [FileChange.Added, FileChange.Deleted],
            result.FilesChanged.OrderBy(file => file.Path).Select(file => file.Change).ToArray());
    }

    [Fact]
    public void Every_command_the_round_ran_is_recorded_with_the_exit_code_it_returned()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1"}
            {"kind":"command","seq":1,"label":"the unit tests","argv":["./scripts/test.sh"],"cwd":"/work","env":[],"durationMs":11,"exitCode":0,"captured":true,"stdoutBytes":25,"stderrBytes":0,"stdoutTail":"1 passed, 0 failed\n","stderrTail":""}
            {"kind":"command","seq":2,"label":"lint","argv":["bash","-c","echo bad >&2; exit 3"],"cwd":"/work","env":[],"durationMs":4,"exitCode":3,"captured":true,"stdoutBytes":0,"stderrBytes":10,"stdoutTail":"","stderrTail":"2 problems\n"}
            """));

        Assert.Equal(2, result.CommandsRun.Count);
        Assert.Equal([1, 2], result.CommandsRun.Select(command => command.Sequence));

        var passing = result.CommandsRun[0];
        Assert.True(passing.Passed);
        Assert.Equal("the unit tests", passing.Label);
        Assert.Equal("./scripts/test.sh", passing.Command);
        Assert.Equal("/work", passing.WorkingDirectory);
        Assert.Equal("1 passed, 0 failed", passing.StdoutTail.Trim());

        var failing = result.CommandsRun[1];
        Assert.False(failing.Passed);
        Assert.Equal(3, failing.ExitCode);
        Assert.Equal("2 problems", failing.StderrTail.Trim());
        Assert.Equal(failing, Assert.Single(result.FailedCommands));
    }

    [Fact]
    public void The_rounds_own_exit_code_is_read_out_of_the_header_and_nothing_infers_it()
    {
        // #22: `roundExitCode` was written into every result file by the image and read by
        // nothing in the entire application. This is the read, at the layer that reads
        // records, and it is a plain field read — no inference from a command, no default,
        // and no decision here about what a non-zero one means.
        var finished = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":0}
            {"kind":"command","seq":1,"label":"the tests","argv":["./scripts/test.sh"],"exitCode":1}
            """));

        Assert.Equal(0, finished.Environment.RoundExitCode);
        Assert.True(finished.Environment.TheRoundRan);

        // The round's own exit code and a command's are different fields and stay that way.
        // A round can be finished *and* have run a command that failed — which is a build
        // whose tests failed, and is the case that must not become a retryable failure.
        Assert.Equal(1, Assert.Single(finished.FailedCommands).ExitCode);
        Assert.True(finished.Environment.TheRoundRan);

        var stopped = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":1}
            {"kind":"command","seq":1,"label":"the agent","argv":["opencode","run"],"exitCode":1}
            """));

        Assert.Equal(1, stopped.Environment.RoundExitCode);
        Assert.False(stopped.Environment.TheRoundRan);
    }

    [Fact]
    public void A_result_file_with_no_round_exit_code_in_it_claims_nothing_rather_than_guessing_zero()
    {
        // An absent field is not a zero and not a failure. `worker-collect` writes `null`
        // when it was not given one, and a result file from an older image has no such
        // field at all; both mean the round has not said how it ended, and reading either as
        // a failure would park a work item on the strength of a field that was never written.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","roundExitCode":null}
            {"kind":"command","seq":1,"label":"the tests","argv":["./scripts/test.sh"],"exitCode":0}
            """));

        Assert.Null(result.Environment.RoundExitCode);
        Assert.True(result.Environment.TheRoundRan);
    }

    [Fact]
    public void A_result_that_could_not_be_read_at_all_carries_no_exit_code()
    {
        // The degraded case: nothing was observed, so nothing is claimed — including that
        // the round failed. A `Nothing()` result is not evidence of a failure any more than
        // it is evidence of a success.
        var nothing = _deriver.Derive(AResultFile("this is not a result file at all"));

        Assert.False(nothing.IsARecord);
        Assert.Null(nothing.Environment.RoundExitCode);
        Assert.True(nothing.Environment.TheRoundRan);
    }

    [Fact]
    public void The_factory_records_what_a_command_returned_and_does_not_decide_what_it_meant()
    {
        // ADR-0011 settles the design's own contradiction about test commands: the agent
        // chooses what to run, the factory records what ran and what it returned, and
        // neither interprets it. A deriver that labelled a command "the tests" would be
        // imposing a convention, and one that turned a non-zero exit into a failure of the
        // round would be retrying a build that failed its own tests (ADR-0001).
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1"}
            {"kind":"command","seq":1,"label":"make check","argv":["make","check"],"exitCode":1}
            {"kind":"command","seq":2,"label":"a deployment","argv":["./deploy.sh"],"exitCode":0}
            """));

        // Both are commands. Neither is called a test, and the failing one is not called a
        // failure of anything but its own exit code.
        Assert.Equal(["make check", "a deployment"], result.CommandsRun.Select(command => command.Label));
        Assert.False(result.CommandsRun[0].Passed);
        Assert.True(result.CommandsRun[1].Passed);
    }

    [Fact]
    public void A_round_that_wrote_no_note_is_still_fully_recorded()
    {
        // The note is the only part a model authors and it is optional by construction
        // (ADR-0011): a round that omits it loses a sentence, not its record.
        var withNote = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"command","seq":1,"label":"build","argv":["make"],"exitCode":0}
            {"kind":"note","field":"note","text":"Fixed the parser."}
            """));

        var withoutNote = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"command","seq":1,"label":"build","argv":["make"],"exitCode":0}
            """));

        Assert.Equal("Fixed the parser.", withNote.AgentNote);
        Assert.Null(withoutNote.AgentNote);

        // Everything observed is identical. The note is the only difference and nothing
        // derives from it.
        Assert.Equal(
            withNote.FilesChanged.Select(file => file.Path),
            withoutNote.FilesChanged.Select(file => file.Path));
        Assert.Equal(
            withNote.CommandsRun.Select(command => command.Command),
            withoutNote.CommandsRun.Select(command => command.Command));
    }

    [Fact]
    public void A_malformed_result_degrades_to_saying_why_rather_than_to_showing_nothing()
    {
        // Story 29. A result the factory cannot read is a round that still happened, and a
        // round that renders as an empty card reads as a round which changed nothing —
        // a different and much worse claim than "this could not be read".
        var result = _deriver.Derive(AResultFile("this is not JSON at all\nnor is this\n"), "the log says it all");

        Assert.False(result.IsARecord);
        Assert.NotNull(result.UnreadableBecause);

        // And nothing is invented: an unreadable result does not render as a round with
        // no files and no commands, which is what "showed nothing" would have meant.
        Assert.Empty(result.FilesChanged);
        Assert.Empty(result.CommandsRun);
        Assert.Equal(string.Empty, result.Diff);

        var payload = ResultPayload.Of(result, "the log says it all");
        Assert.Contains("could not be read", payload, StringComparison.Ordinal);
        Assert.Contains("the log says it all", payload, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_with_no_header_is_unreadable_rather_than_a_round_that_did_nothing()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"command","seq":1,"argv":["make"],"exitCode":0}
            """));

        Assert.False(result.IsARecord);
        Assert.Contains("header", result.UnreadableBecause!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_result_file_is_unreadable_rather_than_an_exception()
    {
        var result = _deriver.Derive(null);
        Assert.False(result.IsARecord);

        var absent = _deriver.Derive(Path.Combine(_directory, "never-written.json"));
        Assert.False(absent.IsARecord);
        Assert.Contains("no result file", absent.UnreadableBecause!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_with_a_line_the_factory_cannot_read_says_so_instead_of_reading_as_a_whole_one()
    {
        // The design names this failure exactly: "a results payload that parses but is
        // missing the field the reviewer needs" is listed as something only integration
        // tests catch. A result with a record the factory skipped is that, so the count
        // travels to the board rather than being dropped here.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"command","seq":1,"argv":["make"],"exitCode":0}
            {"kind":"git","field":"status","text":"# branch.head main\n1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/Real.cs\n"}
            {"kind":"note","field":"note","text":"half a sentence, and then the conta
            """));

        // A truncated result costs its tail and nothing else, which is what the image's
        // JSON-lines format is for. The round is still a record, every record before the
        // damage is read, and the result says how much of it was not.
        Assert.True(result.IsARecord);
        Assert.Equal(1, result.UnreadableLines);

        Assert.Equal(["src/Real.cs"], result.FilesChanged.Select(file => file.Path));
        Assert.Single(result.CommandsRun);
        // The damaged line is the note, and a note the factory cannot read is no note.
        // The round above it is still recorded whole, which is the whole of ADR-0011's
        // "a round that omits it loses a sentence, not its record".
        Assert.Null(result.AgentNote);

        // And the board is told, rather than the shorter file being taken at face value.
        Assert.Contains("partial result", ResultPayload.Of(result), StringComparison.Ordinal);
        Assert.Contains("1 line(s)", ResultPayload.Of(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_of_a_result_that_is_not_a_record_at_all_is_counted_and_does_not_end_the_round()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"command","seq":1,"argv":["make"],"exitCode":0}
            """.Replace("\n", "\n", StringComparison.Ordinal) + "\r\n"
                + "\"a bare string is not a record\"\r\n"
                + "{\"kind\":\"command\",\"seq\":2,\"argv\":[\"make\",\"test\"],\"exitCode\":1}\r\n"));

        Assert.True(result.IsARecord);
        Assert.Equal(1, result.UnreadableLines);
        Assert.Equal(2, result.CommandsRun.Count);
    }

    [Fact]
    public void The_result_records_which_credentials_the_round_was_handed_and_never_a_value()
    {
        // ADR-0006. The image records the *names* of credential-shaped variables, and the
        // deriver carries them through as names. An empty list is the evidence a reviewer
        // can check; a claim that no credential existed would be worth nothing.
        var held = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"credentialEnvNames":["NEXUS_ANTHROPIC_API_KEY"],"remotes":""}
            {"kind":"git","field":"remotes","text":""}
            """));

        Assert.Equal(["NEXUS_ANTHROPIC_API_KEY"], held.Environment.CredentialNames);

        var empty = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"credentialEnvNames":[]}
            """));

        Assert.Empty(empty.Environment.CredentialNames);
        Assert.Contains("none", ResultPayload.Of(empty), StringComparison.Ordinal);
    }

    [Fact]
    public void The_diff_a_reviewer_judges_is_the_one_against_the_commit_the_round_started_at()
    {
        // `diffFromRoundStart` covers committed and uncommitted work alike, which is why
        // the image captures the base before the agent touches anything. A result that had
        // only the staged and unstaged diffs would show an empty diff for a round that
        // committed its work — the single most misleading thing this deriver could do.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"startHead":"aaa1111","head":"bbb2222"}
            {"kind":"git","field":"diffUnstaged","text":""}
            {"kind":"git","field":"diffStaged","text":""}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/index.js b/src/index.js\n--- a/src/index.js\n+++ b/src/index.js\n@@ -1 +1 @@\n-a\n+b\n"}
            {"kind":"git","field":"commitsSinceRoundStart","text":"bbb2222cccc3333 the round's own commit\n"}
            """));

        Assert.Contains("a/src/index.js", result.Diff, StringComparison.Ordinal);
        Assert.Contains("+b", result.Diff, StringComparison.Ordinal);

        var commit = Assert.Single(result.Commits);
        Assert.Equal("bbb2222cccc3333", commit.Id);
        Assert.Equal("the round's own commit", commit.Subject);
    }

    [Fact]
    public void A_result_derives_from_the_staged_and_unstaged_diffs_when_there_is_no_round_start_commit()
    {
        // A round whose working tree was not a repository, or whose base could not be
        // captured, still has git's own view of what changed on disk. Falling back keeps
        // that; rendering an empty diff would claim there was no change.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"startHead":null}
            {"kind":"git","field":"diffStaged","text":"diff --git a/staged.txt b/staged.txt\n--- a/staged.txt\n+++ b/staged.txt\n@@ -0,0 +1 @@\n+s\n"}
            {"kind":"git","field":"diffUnstaged","text":"diff --git a/loose.txt b/loose.txt\n--- a/loose.txt\n+++ b/loose.txt\n@@ -1 +1 @@\n-u\n+u\n"}
            """));

        Assert.Equal(["loose.txt", "staged.txt"], result.FilesChanged.Select(file => file.Path));
        Assert.Contains("+u", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public void A_round_with_no_git_repository_at_all_says_so_rather_than_reporting_no_changes()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":false,"uid":1000,"user":"agent"}
            {"kind":"command","seq":1,"label":"the tests","argv":["make","test"],"exitCode":0}
            """));

        Assert.False(result.Environment.IsARepository);
        Assert.Empty(result.FilesChanged);
        Assert.Single(result.CommandsRun);

        // The commands are still there, so the board is not empty, and the headline says
        // why there is no diff rather than implying a round that changed nothing.
        Assert.Contains("not a git repository", ResultPayload.Of(result), StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_git_quoted_arrives_unquoted_and_unmangled()
    {
        // Git quotes a path containing a space, a quote or anything above ASCII, and
        // escapes those characters inside the quotes. A path left quoted names a file that
        // does not exist, which on a board is a reviewer being shown the wrong file.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"status","text":"? \"src/a file with \\u0027quotes\\u0027 and spaces.txt\"\n"}
            """));

        var file = Assert.Single(result.FilesChanged);
        Assert.Equal(FileChange.Untracked, file.Change);
        Assert.DoesNotContain('"', file.Path);
        Assert.Contains(" ", file.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_the_repository_ignores_is_not_a_change_the_round_made()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"status","text":"! bin/ignored.dll\n? src/real.cs\n"}
            """));

        Assert.Equal(["src/real.cs"], result.FilesChanged.Select(file => file.Path));
    }

    [Fact]
    public void A_listener_the_container_found_is_read_and_not_dropped()
    {
        // The image records any listening socket it finds rather than assuming there are
        // none, and the design claims no worker container publishes an inbound port. A
        // result the factory silently dropped would be a claim nobody could check.
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1"}
            {"kind":"listener","proto":"/proc/net/tcp","port":4096,"localHex":"0100007F:1000","raw":"x"}
            {"kind":"end","collectedInMs":5,"commandRecords":0}
            """));

        // Unknown record kinds are skipped, but skipping is not silently losing: they are
        // not counted as unreadable, because an unknown record is not a broken one.
        Assert.True(result.IsARecord);
        Assert.Equal(0, result.UnreadableLines);
    }

    [Fact]
    public void A_diff_the_image_bounded_is_reported_as_bounded_rather_than_shown_short()
    {
        var result = _deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true}
            {"kind":"git","field":"diffFromRoundStart","byteLength":900000,"truncated":true,"text":"diff --git a/big.txt b/big.txt\n--- a/big.txt\n+++ b/big.txt\n@@ -1 +1 @@\n-a\n+b\n"}
            """));

        Assert.True(result.DiffTruncated);
        Assert.Contains("bounded this diff", ResultPayload.Of(result), StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_shows_the_files_the_commands_the_outcomes_and_the_diff()
    {
        // The board renders this text, so what a reviewer can see is what this asserts.
        // Every one of the four named things has to be in it: the files changed, the
        // commands run, what each returned, and the diff.
        var payload = ResultPayload.Of(_deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"user":"agent","uid":1000,"branch":"main","startHead":"aaa11111111","head":"bbb22222222","credentialEnvNames":[]}
            {"kind":"command","seq":1,"label":"the tests, after the change","argv":["./scripts/test.sh"],"exitCode":0,"durationMs":1100,"stdoutTail":"42 passed, 0 failed\n","stderrTail":""}
            {"kind":"command","seq":2,"label":"lint","argv":["bash","-c","exit 3"],"exitCode":3,"durationMs":40,"stdoutTail":"","stderrTail":"2 problems\n"}
            {"kind":"git","field":"status","text":"# branch.head main\n1 .M N... 100644 100644 100644 aaaaaaa bbbbbbb src/Actual.cs\n"}
            {"kind":"git","field":"diffFromRoundStart","text":"diff --git a/src/Actual.cs b/src/Actual.cs\n--- a/src/Actual.cs\n+++ b/src/Actual.cs\n@@ -1 +1 @@\n-old\n+new\n"}
            {"kind":"note","field":"note","text":"Fixed the off-by-one."}
            """)));

        Assert.Contains("src/Actual.cs", payload, StringComparison.Ordinal);
        Assert.Contains("./scripts/test.sh", payload, StringComparison.Ordinal);
        Assert.Contains("42 passed, 0 failed", payload, StringComparison.Ordinal);
        Assert.Contains("exit 3", payload, StringComparison.Ordinal);
        Assert.Contains("2 problems", payload, StringComparison.Ordinal);
        Assert.Contains("+new", payload, StringComparison.Ordinal);
        Assert.Contains("Fixed the off-by-one.", payload, StringComparison.Ordinal);

        // And the agent's sentence is visibly separated from everything derived, because it
        // is the one part a model wrote and it is worth the least. A reviewer reads the
        // order as much as the content: the observations come first, the account last.
        var account = payload.IndexOf("the agent's own account", StringComparison.Ordinal);
        Assert.True(account > 0);
        Assert.Contains("which nothing above is derived from", payload, StringComparison.Ordinal);
        Assert.True(
            payload.IndexOf("src/Actual.cs", StringComparison.Ordinal) < account,
            "the file git saw is named before the agent's account of it");
    }

    [Fact]
    public void A_result_with_nothing_in_it_still_says_that_rather_than_rendering_nothing()
    {
        // An empty `<pre>` on a card reads as a round that changed nothing and ran nothing.
        // A round whose commands all passed and which touched no file is a real, common
        // outcome, and it has to read as itself.
        var payload = ResultPayload.Of(_deriver.Derive(AResultFile(
            """
            {"kind":"result","schema":"agent-factory/worker-result@1","gitRepo":true,"user":"agent","uid":1000}
            {"kind":"command","seq":1,"label":"read the tree","argv":["ls"],"exitCode":0}
            """)));

        Assert.Contains("none: git saw the tree exactly as the round found it", payload, StringComparison.Ordinal);
        Assert.Contains("read the tree", payload, StringComparison.Ordinal);
        Assert.Contains("it wrote none, and the round is recorded whole without one", payload, StringComparison.Ordinal);
    }

    /// <summary>Writes a result file the way the image's collector would.</summary>
    private string AResultFile(string body)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "result.json");

        // CRLF because the file comes out of a Linux container over `docker cp` and is
        // read on a host that may disagree about line endings; a deriver that split on
        // "\r\n" and assumed "\n" would pass every hand-written fixture and fail every
        // real one.
        File.WriteAllText(path, body.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));

        return path;
    }
}
