namespace AgentFactory.Tests.Containers;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentFactory.Containers;
using AgentFactory.Failures;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The Docker CLI as a process, with the executable faked. What is under test is the
/// boundary: what is handed to the process, and what happens to it when a round's token
/// fires while it is still running.
/// </summary>
/// <remarks>
/// The fakes are real programs, because the claims are about processes — that arguments
/// arrive as arguments, that an exit code comes back, and that a process a cancelled round
/// started is gone afterwards. A recording stub would prove none of that, and would
/// happily agree with an implementation that leaked one.
/// </remarks>
public class DockerCliTests
{
    // Whether an argument reaches the Docker CLI as one word is asserted in
    // WorkerRoundTests, against the real CLI and a brief full of shell syntax: a batch
    // file standing in for the CLI would only show what cmd.exe does with its own
    // arguments, which is not the claim.

    [Fact]
    public async Task An_invocation_returns_the_exit_code_and_both_streams()
    {
        var said = new List<string>();
        var cli = new DockerCli(Echo, NullLogger<DockerCli>.Instance);

        var done = await cli.InvokeAsync(["fail"], new Collect(said), CancellationToken.None);

        // A round's outcome is read from exit codes, so a command that failed has to say
        // so here rather than being turned into a result. And both streams arrive on the
        // channel as the process writes them, which is how a round's log reaches the host
        // while the round is still running (ADR-0010). Each stream is matched in its own
        // right rather than by position, because the order the two pipes are drained in is
        // the operating system's business, not this code's.
        Assert.Equal(3, done.ExitCode);
        Assert.Contains(said, line => line.Trim() == "stdout: out");
        Assert.Contains(said, line => line.Trim() == "stderr: err");
    }

    [Fact]
    public async Task Both_streams_are_read_concurrently_without_losing_a_line_from_either()
    {
        // Two pumps, one per stream, appending to one bounded tail at the same time. A
        // StringBuilder is not thread-safe, and unsynchronised this is a real defect rather
        // than a theoretical one: it throws, and it drops lines. The first version of this
        // file did exactly that, and the suite went red about one run in four with either an
        // ArgumentOutOfRangeException or a missing stderr line.
        var said = new List<string>();
        var cli = new DockerCli(Both, NullLogger<DockerCli>.Instance);

        var done = await cli.InvokeAsync(["1000"], new Collect(said), CancellationToken.None);

        Assert.Equal(0, done.ExitCode);

        List<string> outLines;
        List<string> errLines;

        // Which line goes missing, if one does, is the whole point: a pump that stops one
        // line early is a pump truncating a round's log, and a tail that interleaves two
        // streams badly is a tail nobody can read.
        lock (said)
        {
            outLines = [.. said.Where(line => line.StartsWith("out-", StringComparison.Ordinal))];
            errLines = [.. said.Where(line => line.StartsWith("err-", StringComparison.Ordinal))];
        }

        Assert.Equal(1000, outLines.Count);
        Assert.Equal(1000, errLines.Count);
        // Trimmed, because the probe's own `1>&2` redirection leaves cmd.exe's trailing
        // space on the line and that is the probe's doing, not the pump's.
        Assert.Equal("out-1", outLines[0].Trim());
        Assert.Equal("out-1000", outLines[^1].Trim());
        Assert.Equal("err-1", errLines[0].Trim());
        Assert.Equal("err-1000", errLines[^1].Trim());

        // The two streams really were interleaved rather than read one after the other,
        // which is the case the single tail has to hold up under. Any err- line with an
        // out- line on both sides of it is proof of that.
        lock (said)
        {
            var interleaved = Enumerable
                .Range(1, said.Count - 2)
                .Where(index =>
                    said[index - 1].StartsWith("out-", StringComparison.Ordinal)
                    && said[index].StartsWith("err-", StringComparison.Ordinal)
                    && said[index + 1].StartsWith("out-", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(interleaved);
        }

        // The tail is the end of what was printed, with no line torn in half. Which of the
        // two streams it ends on is not asserted and cannot be: they are two OS pipes
        // drained by two pumps, so which drains last is the operating system's business and
        // a test that pinned it would be testing a race.
        Assert.DoesNotContain(said, line => line.Length == 0 || line[^1] == '\0');
        Assert.NotEmpty(done.Output);

        // The tail is whole lines, every one of them a line that was really printed, and it
        // is the *end* of the output rather than some arbitrary slice of it. A tail that
        // begins mid-line is a tail whose first line says something the round did not,
        // which is the one way a bounded tail can still lie.
        var tailLines = done.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(tailLines);
        Assert.All(tailLines, line => Assert.True(
            outLines.Any(printed => printed.TrimEnd() == line.TrimEnd())
                || errLines.Any(printed => printed.TrimEnd() == line.TrimEnd()),
            $"the tail holds a line that was never printed: {line}"));

        // It ends where the output ended, and it is a window rather than the whole of it:
        // 2,000 lines go past the bound, so the tail has to have dropped some.
        Assert.Equal(said[^1].TrimEnd(), tailLines[^1].TrimEnd());
        Assert.True(tailLines.Length < 2_000, $"the tail is a window, not the whole output ({tailLines.Length} lines)");
    }

    [Fact]
    public async Task An_invocations_output_tail_is_bounded_rather_than_the_whole_of_it()
    {
        // A round's log is megabytes and it all arrives on the channel. What a caller is
        // given back is the end of it, so no caller can hold a whole build in a string.
        var said = new List<string>();
        var cli = new DockerCli(Echo, NullLogger<DockerCli>.Instance);

        var done = await cli.InvokeAsync(["flood"], new Collect(said), CancellationToken.None);

        Assert.Equal(0, done.ExitCode);
        Assert.True(said.Count > 20_000, $"the process wrote every line to the channel ({said.Count})");
        Assert.True(done.Output.Length < 20_000, $"the returned tail is bounded ({done.Output.Length} bytes)");
        Assert.EndsWith("line 20000", done.Output.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_invocation_leaves_no_process_behind()
    {
        var cli = new DockerCli(Hangs, NullLogger<DockerCli>.Instance);
        using var rounds = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Counted before this test starts a process of its own, so an unrelated one on the
        // machine — another test, another program — is not mistaken for a leak.
        var baseline = HangingProcesses();

        // A process that will not stop on its own, which is what `docker logs -f` on a
        // round that is still going looks like from here.
        var running = cli.InvokeAsync(
            [.. Hang, "127.0.0.1"],
            new Wake(started),
            rounds.Token);

        await started.Task.WaitAsync(TimeSpan.FromMinutes(1));
        await EventuallyAsync(() => HangingProcesses() > baseline, "the process under test started");

        await rounds.CancelAsync();

        // The call ends, and it ends as a cancellation rather than as a command that failed.
        // Bounded, because a process that ignored the token would hang the suite rather
        // than fail it, and a test that hangs reports nothing at all.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => running.WaitAsync(TimeSpan.FromMinutes(1)));

        // The process it started is gone, and is not merely detached. Without the kill and
        // the reap in DockerCli this is where a cancelled round leaks a `docker` process
        // holding its end of a socket to the daemon — which is not a hypothetical: the
        // first version of this ticket hung for exactly that reason.
        await EventuallyAsync(() => HangingProcesses() <= baseline, "the process under test was reaped");
        Assert.Equal(baseline, HangingProcesses());
    }

    [Fact]
    public async Task An_invocation_with_no_command_is_refused_rather_than_run()
    {
        var cli = new DockerCli(Echo, NullLogger<DockerCli>.Instance);

        // Running the executable with no arguments at all would be running the Docker CLI
        // with no command, which prints its help and exits 0. That is not what a call here
        // means, and reporting it as a successful command would be a lie.
        await Assert.ThrowsAsync<ArgumentException>(
            () => cli.InvokeAsync([], null, CancellationToken.None));
    }

    [Fact]
    public async Task An_executable_that_is_not_there_is_a_named_refusal_and_not_a_bare_io_exception()
    {
        var cli = new DockerCli("agent-factory-no-such-executable", NullLogger<DockerCli>.Instance);

        var refused = await Assert.ThrowsAsync<PermanentFailure>(
            () => cli.InvokeAsync(["version"], null, CancellationToken.None));

        // A machine with no Docker is a deployment fact an operator can read, rather than
        // a Win32Exception from inside a process wrapper with nothing said about it.
        Assert.Contains("Docker CLI", refused.Message, StringComparison.Ordinal);

        // And it is permanent, which is the classification this is for: the binary is
        // either on the PATH or it is not, so a round asked for again in ten seconds would
        // begin a second identical failure and end exactly where the first one did. The
        // Win32Exception is still carried, so the cause is not lost.
        Assert.Equal(FailureClass.Permanent, refused.Class);
        Assert.IsType<System.ComponentModel.Win32Exception>(refused.InnerException);
    }

    /// <summary>
    /// The probe programs. Written on first use into the test's own output directory
    /// rather than committed as a build artefact, so what they are is all here. Each one
    /// writes its own <c>argv:</c> line per argument, which is how a test sees that an
    /// argument arrived as one word rather than as several.
    /// </summary>
    private static string Echo => Probe(
        "echo",
        OperatingSystem.IsWindows()
            ? """
              @echo off
              echo argv:%~1
              echo argv:%~2
              echo stdout: out
              echo stderr: err 1>&2
              if "%~1"=="fail" exit /b 3
              if "%~1"=="flood" for /L %%i in (1,1,20000) do echo line %%i
              exit /b 0
              """
            : """
              #!/bin/sh
              echo "argv:$1"
              echo "argv:$2"
              echo 'stdout: out'
              echo 'stderr: err' >&2
              [ "$1" = fail ] && exit 3
              if [ "$1" = flood ]; then i=1; while [ $i -le 20000 ]; do echo "line $i"; i=$((i+1)); done; fi
              exit 0
              """);

    /// <summary>
    /// A probe that writes hard to both streams at once, which is the case two pumps
    /// sharing one tail has to survive.
    /// </summary>
    private static string Both => Probe(
        "both",
        OperatingSystem.IsWindows()
            ? """
              @echo off
              for /L %%i in (1,1,1000) do (
                echo out-%%i
                echo err-%%i 1>&2
              )
              exit /b 0
              """
            : """
              #!/bin/sh
              i=1
              while [ $i -le 1000 ]; do
                echo "out-$i"
                echo "err-$i" >&2
                i=$((i+1))
              done
              exit 0
              """);

    /// <summary>
    /// A real long-lived process, for the cancellation test to leave behind or not. <c>ping</c>
    /// is used rather than another probe script because it is an OS binary whose process
    /// name can be counted: the claim is about a process being gone, and counting a named
    /// process is the only way to check that from outside.
    /// </summary>
    private static string Hangs => "ping";

    /// <summary>Ping's own "wait this long" flag, which is spelled differently per platform.</summary>
    private static IReadOnlyList<string> Hang => OperatingSystem.IsWindows() ? ["-n", "60"] : ["-c", "60"];

    private static int HangingProcesses()
    {
        var found = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Hangs));
        try
        {
            return found.Count(alive => !alive.HasExited);
        }
        finally
        {
            foreach (var process in found)
            {
                process.Dispose();
            }
        }
    }

    private static string Arg(string line) => line["argv:".Length..];

    private static string Probe(string name, string body)
    {
        // Named after its own contents, so a probe left in the output directory by an
        // earlier build is replaced rather than silently used, and written to one side and
        // moved into place so the process about to run it cannot read it half-written. One
        // flaky run of a test about processes is a test nobody trusts afterwards.
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..12];
        var path = Path.Combine(
            AppContext.BaseDirectory,
            $"agent-factory-{name}-{stamp}{(OperatingSystem.IsWindows() ? ".cmd" : ".sh")}");

        if (!File.Exists(path))
        {
            var writing = path + "." + Environment.ProcessId;
            File.WriteAllText(writing, body);
            File.Move(writing, path, overwrite: true);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    /// <summary>
    /// Waits for something to become true, bounded. A test that waits forever on a real
    /// process is a suite that stops reporting, so every such wait here has an end.
    /// </summary>
    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"timed out waiting until {what}");
    }

    /// <summary>
    /// Collects what a command reported. Locked, because <see cref="IDockerCli"/> reports
    /// from both of a command's streams at once and that is exactly the contract: a
    /// receiver that is not thread-safe drops lines, and a test whose own collector drops
    /// them would blame the code under test for it.
    /// </summary>
    private sealed class Collect(List<string> into) : IProgress<string>
    {
        private readonly Lock _lock = new();

        public void Report(string value)
        {
            lock (_lock)
            {
                into.Add(value);
            }
        }
    }

    /// <summary>Notices when a process has written its first line, so a test need not poll.</summary>
    private sealed class Wake(TaskCompletionSource arrived) : IProgress<string>
    {
        public void Report(string value) => arrived.TrySetResult();
    }
}
