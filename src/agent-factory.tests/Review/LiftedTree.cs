namespace AgentFactory.Tests.Review;

using System.Diagnostics;

/// <summary>
/// A real git repository on this host that a fake worker container is holding, so a round
/// can be run for real and leave a real tree behind.
/// </summary>
/// <remarks>
/// <para>
/// The diff the board shows is <c>git diff</c> against the tree a round left (ADR-0006,
/// story 34). Testing that against a hand-written string would be testing a fixture
/// against itself: the fake would be deciding what git says, which is the one thing under
/// test. So the tree here is a genuine repository with a genuine commit on it, built by
/// git, and the fake Docker CLI copies that directory out byte for byte — read-only
/// attributes and all, because a tree lifted out of a Linux container arrives on a
/// Windows host exactly like that and the code reading it has to survive it.
/// </para>
/// <para>
/// It is deliberately not hermetic, because git is not a thing this factory can fake
/// without testing nothing. What it is not is a <em>container</em> test: no daemon, no
/// image and no model, and the suite runs on a machine that has never started either.
/// </para>
/// </remarks>
public sealed class LiftedTree : IDisposable
{
    private LiftedTree(string path, string startHead, string branch) =>
        (Path, StartHead, Branch) = (path, startHead, branch);

    /// <summary>Where the repository is, before the fake container lifts a copy out of it.</summary>
    public string Path { get; }

    /// <summary>The commit a round "starts" at, which is what its diff is taken against.</summary>
    public string StartHead { get; }

    /// <summary>The branch the work item's base named, which is where the clone's remote ref is.</summary>
    public string Branch { get; }

    /// <summary>
    /// A repository with one commit on it and a remote branch at that commit, which is
    /// the shape a fresh clone of a project's base has: the base is in the history, and
    /// the round is about to add to it.
    /// </summary>
    /// <param name="branch">The branch the round was given, and the base its diff is against.</param>
    /// <param name="at">
    /// Where to build it. A merger test needs the tree to be where the round runner would
    /// have lifted it — inside the factory's own rounds directory, under the work item's
    /// own id — because that path is what the merger computes and nothing else tells it
    /// where a round's commit is (ADR-0006).
    /// </param>
    public static LiftedTree WithACommitOn(string branch = "main", string? at = null)
    {
        var path = at ?? System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "agent-factory-tests",
            Guid.NewGuid().ToString("n"));

        Directory.CreateDirectory(path);

        var tree = new LiftedTree(path, string.Empty, branch);
        Git(tree, "init", "--initial-branch", branch);
        Git(tree, "config", "user.name", "agent-factory tests");
        Git(tree, "config", "user.email", "tests@agent-factory.invalid");
        Git(tree, "config", "commit.gpgsign", "false");

        File.WriteAllText(System.IO.Path.Combine(path, "README.md"), "# the project\n");
        File.WriteAllText(System.IO.Path.Combine(path, "src.txt"), "one\ntwo\n");
        Directory.CreateDirectory(System.IO.Path.Combine(path, "src"));
        File.WriteAllText(System.IO.Path.Combine(path, "src", "Index.cs"), "public sealed class Index { }\n");

        Git(tree, "add", "--all");
        Git(tree, "commit", "--message", "the commit the round starts from");

        var head = Git(tree, "rev-parse", "HEAD").Trim();
        Git(tree, "branch", "--force", $"refs/remotes/origin/{branch}", head);

        return new LiftedTree(path, head, branch);
    }

    /// <summary>
    /// A commit on top of the one the round started at, which is what a round that did
    /// some work and committed it leaves behind (ADR-0006: the container commits, the host
    /// pushes).
    /// </summary>
    public void Committed(string message, params (string Path, string Content)[] changes)
    {
        foreach (var (path, content) in changes)
        {
            var full = System.IO.Path.Combine(Path, path);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        Git(this, "add", "--all");
        Git(this, "commit", "--message", message);
    }

    /// <summary>
    /// A file the round left on disk without committing, which is a real and common
    /// outcome and one the review surface has to account for separately from the diff.
    /// </summary>
    public void LeftUncommitted(string path, string content)
    {
        var full = System.IO.Path.Combine(Path, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>
    /// Moves the base branch on, as the project's default branch would while a round was
    /// running, leaving the round's own start commit where it was.
    /// </summary>
    /// <remarks>
    /// This is what makes the base a round's diff is taken against observable. Diffed
    /// against the commit the round started at, the card shows what the round did; diffed
    /// against the branch's new tip, it shows what the round did <em>plus</em> a commit
    /// the round never saw and never wrote. Both are real diffs, and only one of them is
    /// the round's work — so the factory prefers the round's own record of where it began,
    /// and a test that could not tell the two apart would not be able to check that.
    /// </remarks>
    public void BaseBranchMovedOn(string message, string path, string content)
    {
        // The unrelated commit is built on a detached HEAD at the round's start commit, so
        // it never becomes an ancestor of the round's own commit. Only the remote-tracking
        // ref moves, which is exactly the shape of a base branch that advanced on the
        // remote while a round was working: the round's clone has not seen it, and the
        // round's HEAD is not a descendant of it.
        var round = Git(this, "rev-parse", "HEAD").Trim();

        Git(this, "checkout", "--detach", StartHead);
        Git(this, "add", "--all");
        File.WriteAllText(System.IO.Path.Combine(Path, path), content);
        Git(this, "add", "--all");
        Git(this, "commit", "--message", message);
        Git(this, "branch", "--force", $"refs/remotes/origin/{Branch}", Git(this, "rev-parse", "HEAD").Trim());

        // Back to where the round left it.
        Git(this, "checkout", "--force", "--detach", round);
    }

    /// <summary>
    /// Makes the object files read-only, which is what a tree lifted out of a Linux
    /// container looks like on a Windows host. A round's diff is a read of them, and the
    /// factory must not need to write to or delete them to get one — so the shape is
    /// reproduced here rather than assumed.
    /// </summary>
    public LiftedTree WithReadOnlyObjects()
    {
        foreach (var file in Directory.EnumerateFiles(
            System.IO.Path.Combine(Path, ".git", "objects"),
            "*",
            SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }

        return this;
    }

    /// <summary>
    /// The commit the tree is on right now, which is the one the host would push (ADR-0006).
    /// Read from git rather than remembered, so a test that commits something after building
    /// the tree still names the commit the change actually ended up on.
    /// </summary>
    public string Head() => Git(this, "rev-parse", "HEAD").Trim();

    private static string Git(LiftedTree tree, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = tree.Path,
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
            // The same walk #8 documented. A tree lifted out of a Linux container arrives
            // with read-only pack files, and a recursive delete on Windows refuses to
            // remove one. `ReadOnlyObjects` above makes that the normal case here rather
            // than an edge a test happens not to hit.
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
