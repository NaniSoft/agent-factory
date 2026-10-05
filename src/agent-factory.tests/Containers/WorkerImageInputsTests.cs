namespace AgentFactory.Tests.Containers;

using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// The worker image's own build inputs are in the repository.
/// </summary>
/// <remarks>
/// <para>
/// <c>worker/Dockerfile</c> copies four files out of <c>worker/</c>. Between the image's
/// first commit and #41, three of them were not: <c>worker/bin/run</c>,
/// <c>worker/bin/worker-collect</c> and <c>worker/bin/worker-round</c> — the recording
/// wrapper, the collector and the round entrypoint, which between them are the whole of
/// what the image does in a round.
/// </para>
/// <para>
/// They went missing because <c>.gitignore</c> said <c>[Bb]in/</c>, which is a directory
/// <em>name</em> pattern and matched <c>worker/bin/</c> as readily as <c>src/*/bin/</c>. So
/// <c>docker build worker/</c> failed at the first <c>COPY</c> on any clean checkout, and
/// with it the compose deployment's own prerequisite, the image's smoke test, and every
/// container test that skips for want of an image.
/// </para>
/// <para>
/// Nothing caught it, and the reason is worth stating because it is the general hazard
/// here: <strong>an image built outside git makes its inputs invisible to everything that
/// reads the tree.</strong> The image existed on one machine, so every probe that asked
/// "is the image there?" said yes; every probe that asked "can this be built?" was not
/// written. Losing the source changed nothing a test could see.
/// </para>
/// <para>
/// So this reads the Dockerfile's own <c>COPY</c> lines and checks each source is in the
/// repository. It is the cheapest possible statement that <c>docker build worker/</c> can
/// succeed, and it needs no daemon — which is the point, since the machines that would
/// catch this the slow way are the machines with a daemon.
/// </para>
/// </remarks>
public class WorkerImageInputsTests
{
    /// <summary>The image's build context: the directory its Dockerfile lives in.</summary>
    private static string WorkerDirectory() => RepositoryRoot("worker");

    private static string Dockerfile() => Path.Combine(WorkerDirectory(), "Dockerfile");

    [Fact]
    public void Every_file_the_image_copies_out_of_the_repository_is_in_the_repository()
    {
        var sources = CopiedSources();

        Assert.NotEmpty(sources);

        var missing = sources
            .Where(source => !File.Exists(Path.Combine(WorkerDirectory(), source)))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Each one named, because "the build will fail" is not a message a reader can act
        // on and "worker/bin/run is not in the repository" is.
        Assert.True(
            missing.Count == 0,
            $"worker/Dockerfile copies files that are not in the repository, so `docker build worker/` cannot "
                + $"succeed on a clean checkout: {string.Join(", ", missing)}");

        // And the three that went missing are named explicitly, so restoring one and
        // losing another is a test failure rather than a count that happens to be right.
        foreach (var expected in new[]
                 {
                     "bin/run",
                     "bin/worker-collect",
                     "bin/worker-round",
                 })
        {
            Assert.Contains(expected, sources);
            Assert.True(
                File.Exists(Path.Combine(WorkerDirectory(), expected)),
                $"{expected} is copied by the image's Dockerfile and is not in the repository");
        }
    }

    [Fact]
    public void Nothing_named_bin_is_ignored_again()
    {
        // The other half of the same defect, and the one that would bring it back. A
        // `.gitignore` rule is not a convention: `git check-ignore` is the only question
        // that matters, and it is answered here without a daemon and without a checkout —
        // which is where the original rule was wrong for so long without anybody noticing
        // that nothing was ignoring it on purpose.
        var ignored = IgnoredFiles()
            .Where(path => path.Replace('\\', '/').Contains("worker/bin/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            ignored.Count == 0,
            $"files under worker/bin/ are gitignored again, so the image's own source would vanish from the "
                + $"repository a second time: {string.Join(", ", ignored)}");
    }

    [Fact]
    public void The_scripts_the_image_installs_carry_a_shebang()
    {
        // A copied script with no shebang is a script the kernel cannot run, and the
        // failure is an `exec format error` from `docker run` rather than anything a
        // reader can trace back to a file. Cheap to check, and it is the first thing a
        // hand-written script gets wrong.
        foreach (var source in new[] { "bin/run", "bin/worker-collect", "bin/worker-round" })
        {
            var text = File.ReadAllText(Path.Combine(WorkerDirectory(), source));

            Assert.StartsWith(
                "#!",
                text,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The repository-relative sources the image copies, from the Dockerfile's own
    /// <c>COPY</c> lines. Read out of the Dockerfile rather than written here, so a file
    /// added to the image is checked by this test without it being edited — a list of
    /// paths in a test is a list that stops matching the thing it is about.
    /// </summary>
    private static IReadOnlyList<string> CopiedSources()
    {
        var sources = new List<string>();

        foreach (Match match in Regex.Matches(File.ReadAllText(Dockerfile()), "^COPY\\s+(?!--from=)(\\S+)\\s+\\S+\\s*$",
            RegexOptions.Multiline))
        {
            var source = match.Groups[1].Value.Trim();

            // Anything that is not a plain relative path is not a file in the build
            // context: a URL, a stage name, or a wildcard the image resolves elsewhere.
            if (source.Contains("://", StringComparison.Ordinal)
                || source.Contains('*')
                || source.StartsWith('$'))
            {
                continue;
            }

            sources.Add(source);
        }

        return sources;
    }

    /// <summary>
    /// Every tracked-and-untracked file in the repository that <c>.gitignore</c> would
    /// exclude, from git itself. There is no way to answer this by reading
    /// <c>.gitignore</c> — the whole defect was in the difference between what a pattern
    /// looks like and what it matches.
    /// </summary>
    private static IReadOnlyList<string> IgnoredFiles()
    {
        var root = RepositoryRoot();

        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // `--others --ignored --exclude-standard`: the files git would not commit because
        // an ignore rule matches them. That is the exact question, asked of the tool that
        // decides it.
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--others");
        start.ArgumentList.Add("--ignored");
        start.ArgumentList.Add("--exclude-standard");
        start.ArgumentList.Add("--directory");

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("there is no git on this machine's PATH");

        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('/'))
            .ToList();
    }

    /// <summary>
    /// The repository root, found by walking up from the compiled assembly. Which is a real
    /// dependency — the same one <c>LiftedTree</c> has in needing a <c>git</c> binary —
    /// and is named rather than hard-coded because the suite runs out of <c>bin/</c> under
    /// a path nobody chose.
    /// </summary>
    private static string RepositoryRoot(string from = "src")
    {
        var marker = Path.Combine("src", "agent-factory.slnx");

        for (var at = new DirectoryInfo(Path.GetDirectoryName(typeof(WorkerImageInputsTests).Assembly.Location)!);
             at is not null;
             at = at.Parent)
        {
            if (File.Exists(Path.Combine(at.FullName, marker)))
            {
                return Path.Combine(at.FullName, from);
            }
        }

        throw new InvalidOperationException(
            "could not find the repository root above this assembly, so the worker's build context cannot be read");
    }
}