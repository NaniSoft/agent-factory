namespace AgentFactory.GitHub;

using System.Diagnostics;
using System.Text;
using AgentFactory.Failures;
using AgentFactory.Results;

/// <summary>
/// Git, as a process, for the two things the host has to ask a round's tree: what commit it
/// is on, and whether there is anything in it that was never committed. And for the one
/// thing it has to do to it — push that commit to a branch.
/// </summary>
/// <remarks>
/// <para>
/// A process, and not a Git library, for the same reason <see cref="Containers.DockerCli"/>
/// is one: the arguments are the mechanism, they go in one at a time, and nothing the
/// factory passes — a repository URL, a branch name, a commit — can be read as shell
/// syntax on the way in. ADR-0010 names the Docker CLI's verbs rather than a
/// container-runtime package for the same reason this names git's.
/// </para>
/// <para>
/// The code is deliberately not folded into <see cref="HostDiffReader"/>, even though
/// that component also runs git against the same trees. The diff reader's refusals —
/// <c>--no-ext-diff</c>, <c>--no-textconv</c>, <c>safe.directory</c> — are its own and are
/// asserted as its own argument list; sharing the process plumbing would mean the two
/// components' argument lists and failure handling had to move together, and the point of
/// both is that each is readable on its own. What <em>is</em> shared is the
/// <c>safe.directory</c> configuration, taken from the diff reader rather than written out
/// again, because it exists for the same reason in both places: a tree created by uid
/// 1000 inside a Linux container is a repository git refuses to read on a Linux host, and
/// refused here would mean the factory cannot see a change it is holding.
/// </para>
/// <para>
/// The credential reaches git through the environment and never through the command line,
/// which is the whole of why it is here rather than in a URL. A token in an argument is
/// visible to every process on the machine for as long as git runs, and in any crash dump
/// that prints the command; a token in the environment of one short-lived child is scoped
/// to that child, for that push. It is also why no askpass program is written to disk and
/// no shell is involved.
/// </para>
/// </remarks>
public static class GitPusher
{
    /// <summary>The executable, resolved through <c>PATH</c>.</summary>
    public const string Executable = "git";

    /// <summary>
    /// The arguments that push one commit to one branch, with the two refusals that matter
    /// written into them rather than left to configuration.
    /// </summary>
    /// <remarks>
    /// <c>--porcelain</c> so the answer is a shape rather than prose, and — the load-bearing
    /// one — the refspec is a bare commit id. There is no <c>--force</c> anywhere in this
    /// factory, so a push onto a branch that has moved on is refused by git rather than
    /// resolved by the factory, and a branch somebody else owns can never be overwritten by
    /// a retry.
    ///
    /// The commit is named rather than a local branch, so the push has nothing to resolve in
    /// the tree's own configuration and no ref of the tree's own to update. That is what
    /// lets a push work at all against a tree whose object files arrived read-only from a
    /// container: nothing has to be written into the tree to send it.
    /// </remarks>
    public static IReadOnlyList<string> PushArguments(string remote, string commit, string branch) =>
    [
        "--no-pager",
        "push",
        "--porcelain",
        remote,
        $"{commit}:refs/heads/{branch}",
    ];

    /// <summary>What commit a tree is on. Fails on a tree with no commit at all.</summary>
    public static IReadOnlyList<string> HeadArguments() => ["--no-pager", "rev-parse", "HEAD"];

    /// <summary>
    /// What a tree is holding that was never committed. Empty output is a clean tree.
    /// </summary>
    /// <remarks>
    /// Untracked files are in it, and that is deliberate: a reviewer judging the board's
    /// diff is judging a <c>git diff</c> that includes everything uncommitted, and a push
    /// sends only what was committed. The two differ, which is the whole of why the merger
    /// asks.
    /// </remarks>
    public static IReadOnlyList<string> UncommittedArguments() => ["--no-pager", "status", "--porcelain"];

    /// <summary>
    /// The environment a git child of the merger runs with. Starts from the diff reader's
    /// own — the same <c>safe.directory</c>, for the same reason — and adds the three
    /// things a push needs.
    /// </summary>
    /// <param name="authorization">
    /// The <c>Authorization</c> header to send, or null to send none. A push to a remote on
    /// this machine needs none, which is what lets the whole merge path be tested without a
    /// network and without a credential.
    /// </param>
    public static IReadOnlyDictionary<string, string> Environment(string? authorization = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Never prompt. A push that cannot authenticate has to fail and be classified,
            // not sit on an invisible prompt waiting for a human who is not there.
            ["GIT_TERMINAL_PROMPT"] = "0",

            // Take no optional locks. Both the reads and the push are reads of a tree the
            // factory does not own, and a tree lifted out of a Linux container arrives on
            // Windows with read-only object files in it — git's index refresh must not be
            // the thing that fails, because the answer it is looking for has nothing to do
            // with what the factory is doing.
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

        // The safe.directory the diff reader already established, renumbered so the
        // credential can be a second entry rather than a second mechanism. Read through
        // its own numbering rather than by re-deriving the names, so there is one place
        // that knows what a round's tree needs to be readable.
        var count = 0;
        var inherited = int.TryParse(
            HostDiffReader.GitEnvironment.GetValueOrDefault("GIT_CONFIG_COUNT"),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var howMany)
            ? howMany
            : 0;

        for (var entry = 0; entry < inherited; entry++)
        {
            if (!HostDiffReader.GitEnvironment.TryGetValue($"GIT_CONFIG_KEY_{entry}", out var key)
                || !HostDiffReader.GitEnvironment.TryGetValue($"GIT_CONFIG_VALUE_{entry}", out var value))
            {
                continue;
            }

            environment[$"GIT_CONFIG_KEY_{count}"] = key;
            environment[$"GIT_CONFIG_VALUE_{count}"] = value;
            count++;
        }

        if (authorization is { Length: > 0 })
        {
            environment[$"GIT_CONFIG_KEY_{count}"] = "http.extraheader";
            environment[$"GIT_CONFIG_VALUE_{count}"] = authorization;
            count++;
        }

        environment["GIT_CONFIG_COUNT"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return environment;
    }

    /// <summary>
    /// The <c>Authorization</c> header a push is authenticated with, or null when there is
    /// no token. Built from the value and never returned anywhere it could be logged: the
    /// caller passes the result straight into a child process's environment and logs
    /// nothing.
    /// </summary>
    public static string? BasicAuthorization(string? token) =>
        token is { Length: > 0 }
            ? "Authorization: Basic " + Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes($"x-access-token:{token}"))
            : null;

    /// <summary>What one git invocation said.</summary>
    public sealed record GitOutcome(int ExitCode, string Output, string Error)
    {
        public bool Succeeded => ExitCode == 0;

        /// <summary>The output as one trimmed line, for a log or a refusal.</summary>
        public string Summary => Error.Trim() is { Length: > 0 } error
            ? error.Split('\n')[0].Trim()
            : Output.Trim() is { Length: > 0 } output
                ? output.Split('\n')[0].Trim()
                : "git said nothing about it";
    }

    /// <summary>Runs git in a round's tree and reports what it said.</summary>
    public static Task<GitOutcome> RunAsync(
        string tree,
        IReadOnlyList<string> arguments,
        string? authorization,
        CancellationToken cancellationToken) =>
        GitAsync(tree, arguments, authorization, cancellationToken);

    /// <summary>
    /// Pushes one commit to one branch, and reports what git said. A non-zero exit is not
    /// a failure of this method — what a non-zero exit means is a question about the
    /// remote, and the caller answers it by asking the remote rather than by reading git's
    /// words.
    /// </summary>
    public static Task<GitOutcome> PushAsync(
        string tree,
        string remote,
        string commit,
        string branch,
        string? authorization,
        CancellationToken cancellationToken) =>
        GitAsync(tree, PushArguments(remote, commit, branch), authorization, cancellationToken);

    private static async Task<GitOutcome> GitAsync(
        string tree,
        IReadOnlyList<string> arguments,
        string? authorization,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = tree,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in Environment(authorization))
        {
            start.Environment[name] = value;
        }

        using var process = new Process { StartInfo = start };

        try
        {
            if (!process.Start())
            {
                throw new PermanentFailure(
                    $"could not start '{Executable}': the Git CLI is not on this machine's PATH");
            }
        }
        catch (Exception failed) when (failed is not FactoryFailure)
        {
            // The same judgement the Docker CLI makes about itself, for the same reason:
            // git is either on the PATH or it is not, so a second attempt ten seconds later
            // begins a second identical failure. A machine that cannot reach its own round's
            // tree is a deployment fact, and it is named rather than left as a Win32Exception
            // from inside a process wrapper.
            throw new PermanentFailure(
                $"could not start '{Executable}': there is no Git CLI on this machine's PATH, so the host cannot "
                    + "reach a round's commit to push it",
                failed);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (System.IO.IOException)
        {
            // git exited before its stdin was closed. It is not reading any more.
        }

        var output = new StringBuilder();
        var error = new StringBuilder();

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            output.Append(await stdout.ConfigureAwait(false));
            error.Append(await stderr.ConfigureAwait(false));

            return new GitOutcome(process.ExitCode, output.ToString(), error.ToString());
        }
        catch (OperationCanceledException)
        {
            // The caller's token fired. The local git process is killed and reaped rather
            // than left running against a tree nobody is reading any more, and nothing here
            // is allowed to be the operation that could not happen.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reaping) when (reaping is InvalidOperationException or System.IO.IOException)
            {
                // It exited between the check and the kill, which is the outcome wanted.
            }

            throw;
        }
    }
}
