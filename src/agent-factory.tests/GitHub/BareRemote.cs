namespace AgentFactory.Tests.GitHub;

using System.Diagnostics;

/// <summary>
/// A bare git repository on this machine, for a push to land in.
/// </summary>
/// <remarks>
/// <para>
/// The merger has exactly one step that needs something remote to happen to, and it is the
/// step this ticket's argument is about. Testing it against github.com would mean a
/// network, a token and a real pull request in somebody's repository; testing it against a
/// stub would mean the property being asserted — that pushing the same commit twice is a
/// no-op, that a branch is never overwritten, that a tree with read-only object files can
/// still be pushed — was asserted about the stub rather than about git.
/// </para>
/// <para>
/// A directory git accepts as a remote is a remote, and nothing about the code under test
/// changes: it runs <c>git push</c> as a process against the URL the project file gave, and
/// a local path is such a URL. What this cannot do is exercise HTTPS authentication, which
/// is why the credential is covered where it is actually decided — the header on the request
/// and the child's environment — and not here.
/// </para>
/// </remarks>
public sealed class BareRemote : IDisposable
{
    private BareRemote(string path) => Path = path;

    /// <summary>Where the repository is, as a push target.</summary>
    public string Path { get; }

    /// <summary>
    /// The layout a GitHub repository has, so that the client's own repository parsing
    /// produces an owner and a name a test can read: <c>&lt;root&gt;/NaniSoft/nexus.git</c>.
    /// </summary>
    public static BareRemote Create()
    {
        // `System.IO.Path` written out, because this class has a property of that name and
        // a local called root is not worth renaming either.
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agent-factory-tests",
            Guid.NewGuid().ToString("n"),
            "NaniSoft",
            "nexus.git");

        Directory.CreateDirectory(root);
        Git(root, "init", "--bare", "--initial-branch", "main");

        return new BareRemote(root);
    }

    /// <summary>The commit the remote is holding on a branch, or null when it has no such branch.</summary>
    public string? CommitOn(string branch)
    {
        try
        {
            return Git(Path, "rev-parse", $"refs/heads/{branch}").Trim();
        }
        catch (InvalidOperationException)
        {
            // The branch is not there, which is the answer being asked for.
            return null;
        }
    }

    /// <summary>Every branch the remote is holding, which is the answer to "did that open a second one".</summary>
    public IReadOnlyList<string> Branches()
    {
        var listed = Git(Path, "for-each-ref", "--format=%(refname:short)", "refs/heads");
        return listed
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Runs git and fails the test if it could not, which is the right thing for a fixture:
    /// a bare repository that did not get created is a broken test, not a skipped one.
    /// </summary>
    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("there is no git on this machine's PATH");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed in a test fixture: {error}{output}");
        }

        return output;
    }

    public void Dispose()
    {
        try
        {
            // Two levels up and across: the remote sits at <run>/NaniSoft/nexus.git so that
            // the client's own repository parsing produces an owner and a name, and the
            // whole run is what is worth removing.
            var run = Directory.GetParent(Directory.GetParent(Path)!.FullName)!.FullName;

            // The same walk #8 documented, and for the same reason: a pushed repository's
            // object files arrive read-only from a tree lifted out of a container, and a
            // recursive delete on Windows refuses to remove one.
            if (Directory.Exists(run))
            {
                foreach (var file in Directory.EnumerateFiles(run, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
            }

            Directory.Delete(run, recursive: true);
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is not worth failing a test over.
        }
    }
}
