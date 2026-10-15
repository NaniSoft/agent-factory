namespace AgentFactory.Tests.Boundary;

using AgentFactory.Application.Rounds;
using AgentFactory.Infrastructure.Rounds;
using System.Diagnostics;

/// <summary>
/// The child process, faked: it records every request verbatim and answers from behaviour a
/// test wrote. There is no Docker left to stand in for — the real thing being faked is
/// <see cref="ProcessCli"/>, whose contract is one process in, one exit code and one bounded
/// tail out — so the round runner can be driven through every outcome without an agent, a
/// network or a daemon anywhere near the test.
/// </summary>
public sealed class FakeProcessCli : IProcessCli
{
    private readonly List<ProcessCall> _calls = [];
    private readonly List<TaskCompletionSource> _holds = [];
    private bool _realGit;
    private Func<ProcessRequest, ProcessAnswer> _gitAnswer = _ => new ProcessAnswer(0, string.Empty);
    private Func<ProcessRequest, CancellationToken, Task<ProcessAnswer>> _agent =
        (_, _) => Task.FromResult(new ProcessAnswer(0, string.Empty));

    /// <summary>Every invocation, in order, with the environment and token it was given.</summary>
    public IReadOnlyList<ProcessCall> Calls => _calls;

    /// <summary>Every line the fake reported to the caller's progress channel, in order.</summary>
    public List<string> Lines { get; } = [];

    public async Task<ProcessInvocation> InvokeAsync(
        ProcessRequest request,
        IProgress<string>? output,
        CancellationToken cancellationToken)
    {
        var call = new ProcessCall(
            request.Executable,
            [.. request.Arguments],
            request.WorkingDirectory,
            request.Environment is { } environment
                ? new Dictionary<string, string>(environment, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal),
            output,
            cancellationToken);
        _calls.Add(call);

        var answer = request.Executable switch
        {
            "git" => _realGit ? Real(request) : _gitAnswer(request),
            "opencode" => await _agent(request, cancellationToken),
            _ => new ProcessAnswer(0, string.Empty),
        };

        foreach (var line in answer.Lines)
        {
            output?.Report(line);
        }

        // A behaviour that holds is a round that runs. The token is honoured, the way the
        // real process is: a cancelled round does not keep waiting.
        if (answer.UntilReleased is { } held)
        {
            await held.Task.WaitAsync(cancellationToken);
        }

        return new ProcessInvocation(answer.ExitCode, answer.Tail);
    }

    /// <summary>The nth invocation, which is usually the one under test.</summary>
    public ProcessCall Call(int index) => _calls[index];

    /// <summary>Every invocation of one executable, in order.</summary>
    public IReadOnlyList<ProcessCall> From(string executable) =>
        _calls.Where(call => call.Executable == executable).ToList();

    /// <summary>The single invocation of one executable, or a failure saying there was not exactly one.</summary>
    public ProcessCall TheOnly(string executable) => Assert.Single(From(executable));

    /// <summary>The one agent invocation a round makes, or a failure saying there was not exactly one.</summary>
    public ProcessCall TheOnlyAgent() => Assert.Single(From("opencode"));

    /// <summary>Whether any invocation was a git one with the given subcommand.</summary>
    public bool RanGit(string subcommand) =>
        _calls.Any(call => call.Executable == "git"
            && call.Arguments.Count > 0
            && call.Arguments.Any(argument => argument == subcommand));

    /// <summary>The one clone a build round makes, or a failure saying there was not exactly one.</summary>
    public ProcessCall TheOnlyClone()
    {
        var clones = _calls.Where(call => call.Executable == "git"
            && call.Arguments.Contains("clone")).ToList();
        return Assert.Single(clones);
    }

    /// <summary>
    /// What the agent does when the round runs. The default is an agent that succeeds and
    /// says nothing; the helpers below give it work, a failure, or a hold.
    /// </summary>
    public FakeProcessCli Agent(Func<ProcessRequest, CancellationToken, Task<ProcessAnswer>> behavior)
    {
        _agent = behavior;
        return this;
    }

    /// <summary>An agent that exits with the code and tail given — a rate limit, a refusal.</summary>
    public FakeProcessCli AgentFails(int exitCode, string tail) =>
        Agent((_, _) => Task.FromResult(new ProcessAnswer(exitCode, tail)));

    /// <summary>
    /// An agent that edits a file in the round's own working tree before "exiting", writes
    /// its note beside the round's other files, and — when asked — commits. The edit is
    /// what the collector's git observations, the deriver and the host diff are all read
    /// off of, so the round the rest of the factory then reads is a round that actually
    /// happened.
    /// </summary>
    public FakeProcessCli AgentEdits(string relativePath, string content, string? note = null, bool commit = false) =>
        Agent((request, _) =>
        {
            var work = request.WorkingDirectory!;
            var target = Path.Combine(work, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, content);

            if (commit)
            {
                Real(request with
                {
                    Executable = "git",
                    Arguments = ["add", "--all"],
                    WorkingDirectory = work,
                });
                Real(request with
                {
                    Executable = "git",
                    Arguments = ["commit", "--message", "the change the round made"],
                    WorkingDirectory = work,
                });
            }

            var outDirectory = request.Environment?["AGENT_FACTORY_OUT"];
            if (note is { } && outDirectory is { })
            {
                File.WriteAllText(Path.Combine(outDirectory, "note.md"), note);
            }

            return Task.FromResult(new ProcessAnswer(0, "the agent says it worked"));
        });

    /// <summary>
    /// An agent that does not come back until <see cref="Release"/> is called — a round the
    /// factory is inside, for the tests that assert on what the machine does while one runs.
    /// The round's own token is honoured: a cancelled round stops waiting, exactly as the
    /// real process does.
    /// </summary>
    public FakeProcessCli AgentHolds()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _holds.Add(held);
        _agent = async (_, cancellationToken) =>
        {
            await held.Task.WaitAsync(cancellationToken);
            return new ProcessAnswer(0, string.Empty);
        };
        return this;
    }

    /// <summary>Releases every round the fake is holding.</summary>
    public void Release()
    {
        foreach (var held in _holds)
        {
            held.TrySetResult();
        }

        _holds.Clear();
    }

    /// <summary>
    /// Makes every git invocation a real one, run against this machine's own git binary.
    /// The clone the runner asks for then really clones, and the <c>rev-parse</c> it asks
    /// afterwards really answers — so the tree a round leaves is a genuine repository built
    /// by the runner's own commands, not a copy a fake put there. The repository URL has to
    /// be a path this machine can clone from; a local path is one.
    /// </summary>
    public FakeProcessCli WithRealGit()
    {
        _realGit = true;
        return this;
    }

    /// <summary>
    /// Makes every invocation fail the way a missing binary does: the seam throws the
    /// permanent refusal the real <see cref="ProcessCli"/> throws when there is no such
    /// executable on this machine's PATH.
    /// </summary>
    public FakeProcessCli FailsToStart()
    {
        _realGit = false;
        _gitAnswer = _ => throw new AgentFactory.Domain.Failures.PermanentFailure("there is no git on this machine's PATH: faked");
        _agent = (_, _) => throw new AgentFactory.Domain.Failures.PermanentFailure("there is no opencode on this machine's PATH: faked");
        return this;
    }

    /// <summary>What a git invocation answers, when git is not running for real.</summary>
    public FakeProcessCli GitAnswers(int exitCode, string tail)
    {
        _gitAnswer = _ => new ProcessAnswer(exitCode, tail);
        return this;
    }

    private static ProcessAnswer Real(ProcessRequest request)
    {
        var start = new ProcessStartInfo
        {
            FileName = request.Executable,
            WorkingDirectory = request.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"there is no {request.Executable} on this machine's PATH");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);

        return new ProcessAnswer(process.ExitCode, (stdout + stderr).Trim());
    }

    /// <summary>One invocation, with everything a shape assertion reads.</summary>
    public sealed record ProcessCall(
        string Executable,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment,
        IProgress<string>? Output,
        CancellationToken CancellationToken);

    /// <summary>What the fake answers: an exit code, a tail, the lines it reported live, and whether to hold.</summary>
    public sealed record ProcessAnswer(
        int ExitCode,
        string Tail,
        string[]? Lines = null,
        TaskCompletionSource? UntilReleased = null)
    {
        public string[] Lines { get; } = Lines ?? [];
    }
}
