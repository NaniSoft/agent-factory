namespace AgentFactory.UnitTests.Results;

using AgentFactory.Application.Results;
using AgentFactory.Domain.Results;

/// <summary>
/// Git's own diff format, read in the one place in the factory that reads it. Two things depend
/// on this parser — the result deriver and the review surface — so a disagreement between them
/// about what a section says is exactly the failure it exists to make impossible.
/// </summary>
public class GitDiffTests
{
    private const string Modified = """
        diff --git a/src/index.js b/src/index.js
        index aaaaaaa..bbbbbbb 100644
        --- a/src/index.js
        +++ b/src/index.js
        @@ -1,2 +1,2 @@
        -old
        +new
        """;

    [Fact]
    public void No_diff_and_a_preamble_are_both_no_sections()
    {
        // Everything before the first header is a preamble with no file in it, and a path invented
        // out of a preamble is a path that does not exist.
        Assert.Empty(GitDiff.Sections(null));
        Assert.Empty(GitDiff.Sections(string.Empty));
        Assert.Empty(GitDiff.Sections("   "));
        Assert.Empty(GitDiff.Sections("not a diff at all\njust prose\n"));
    }

    [Fact]
    public void Each_file_the_diff_names_is_its_own_section_and_the_preamble_is_not_one()
    {
        var sections = GitDiff.Sections(
            "a preamble the caller put here\n" + Modified + "\ndiff --git a/gone.txt b/gone.txt\ndeleted file mode 100644\n");

        Assert.Equal(2, sections.Count);
        Assert.Equal("src/index.js", GitDiff.PathOf(sections[0]));
        Assert.Equal("gone.txt", GitDiff.PathOf(sections[1]));

        // The preamble is not read as a file, so nothing is invented from it.
        Assert.DoesNotContain(sections, section => section.Contains("preamble"));
    }

    [Fact]
    public void The_path_a_section_is_about_comes_from_the_most_trusted_side_first()
    {
        Assert.Equal("src/index.js", GitDiff.PathOf(Modified));

        // A rename names its destination explicitly, and the `+++`/`---` sides agree with it.
        Assert.Equal(
            "new/place.cs",
            GitDiff.PathOf("diff --git a/old/place.cs b/new/place.cs\nrename from old/place.cs\nrename to new/place.cs\n"));

        // A binary change has no `---`/`+++` pair at all, so the `diff --git` line is the last
        // resort — and it is still a file.
        Assert.Equal(
            "logo.png",
            GitDiff.PathOf("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n"));
    }

    [Fact]
    public void A_dev_null_side_is_not_a_file()
    {
        // `/dev/null` is how git says that side of the diff does not exist, so reporting it would
        // put a file in a reviewer's list that is not one.
        Assert.Null(GitDiff.PathFrom("--- /dev/null\n+++ b/fresh.txt\n", "--- "));
        Assert.Equal("fresh.txt", GitDiff.PathOf("--- /dev/null\n+++ b/fresh.txt\n"));

        // And the prefix stripper leaves it alone, because it is not a prefixed path.
        Assert.Equal("/dev/null", GitDiff.Strip("/dev/null"));
    }

    [Fact]
    public void A_path_with_a_space_is_not_split_and_a_b_prefix_is_found_from_the_right()
    {
        // git does not quote a path for a space, so the path runs to the end of the line; and the
        // shape of the header line is `diff --git a/<from> b/<to>`, so the `b/` must be found from
        // the right — a path called `b/src/x` must not have its own first segment mistaken for the
        // prefix.
        Assert.Equal("src/a file with spaces.txt", GitDiff.PathFrom("+++ b/src/a file with spaces.txt\n", "+++ "));
        Assert.Equal("src/b/x", GitDiff.HeaderPath("diff --git a/src/b/x b/src/b/x\n"));
    }

    [Fact]
    public void A_quoted_path_arrives_unquoted_and_unescaped()
    {
        Assert.Equal("src/a\tb.txt", GitDiff.Unquote("\"src/a\\tb.txt\""));

        // A path that arrived quoted and is left quoted names a file that does not exist.
        var quoted = GitDiff.PathFrom("+++ \"src/a\\tb.txt\"\n", "+++ ") ?? throw new InvalidOperationException("no path");
        Assert.DoesNotContain('"', quoted);
        Assert.Contains('\t', quoted);

        // And an unquoted path is left exactly as it is.
        Assert.Equal("plain/path.cs", GitDiff.Unquote("plain/path.cs"));
    }

    [Fact]
    public void What_git_said_happened_is_read_from_the_sections_own_headers()
    {
        Assert.Equal(FileChange.Added, GitDiff.Change("diff --git a/x b/x\nnew file mode 100644\n"));
        Assert.Equal(FileChange.Deleted, GitDiff.Change("diff --git a/x b/x\ndeleted file mode 100644\n"));
        Assert.Equal(FileChange.Renamed, GitDiff.Change("diff --git a/x b/x\nrename from x\nrename to y\n"));
        Assert.Equal(FileChange.Copied, GitDiff.Change("diff --git a/x b/x\ncopy from x\ncopy to y\n"));
        Assert.Equal(FileChange.TypeChanged, GitDiff.Change("diff --git a/x b/x\nold mode 100644\nnew mode 100755\n"));

        // A content change, and a binary one too: git writes a binary diff with no `---`/`+++` pair
        // and no other header to read, and it is still a change.
        Assert.Equal(FileChange.Modified, GitDiff.Change(Modified));
        Assert.Equal(
            FileChange.Modified,
            GitDiff.Change("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n"));
        Assert.True(GitDiff.IsBinary("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n"));
        Assert.True(GitDiff.IsBinary("diff --git a/logo.png b/logo.png\nGIT binary patch\n"));
    }

    [Fact]
    public void The_line_counts_are_content_lines_and_not_the_file_headers()
    {
        // The `---`/`+++` pair is not content, and `\ No newline at end of file` is neither. A
        // parser that counted either would report a change of a different size than the diff.
        var (added, removed) = GitDiff.Counts("""
            diff --git a/x b/x
            --- a/x
            +++ b/x
            @@ -1,2 +1,2 @@
            -old
            +new
            +also new
            \ No newline at end of file
            """);

        Assert.Equal(2, added);
        Assert.Equal(1, removed);
        Assert.Equal((0, 0), GitDiff.Counts("diff --git a/logo.png b/logo.png\nBinary files a/logo.png and b/logo.png differ\n"));
    }

    [Fact]
    public void Where_a_file_came_from_is_kept_as_git_named_it()
    {
        Assert.Equal("old/place.cs", GitDiff.PreviousPathOf("diff --git a/old/place.cs b/new/place.cs\nrename from old/place.cs\nrename to new/place.cs\n"));
        Assert.Null(GitDiff.PreviousPathOf(Modified));
        Assert.Equal("source.cs", GitDiff.PreviousPathOf("diff --git a/source.cs b/copy.cs\ncopy from source.cs\ncopy to copy.cs\n"));
    }

    [Fact]
    public void A_diff_lifted_out_of_linux_and_read_on_windows_has_one_set_of_line_endings()
    {
        // A section split on the wrong line ending leaves a stray carriage return on every line of
        // a reviewer's screen.
        var normalised = GitDiff.Normalise("a\r\nb\r\n");
        Assert.DoesNotContain('\r', normalised);
        Assert.Equal("a\nb\n", normalised);

        Assert.Equal("a\nb", GitDiff.Normalise("a\nb"));
    }

    [Fact]
    public void The_a_and_b_prefixes_are_stripped_only_when_they_are_prefixes()
    {
        Assert.Equal("x", GitDiff.Strip("a/x"));
        Assert.Equal("x", GitDiff.Strip("b/x"));
        Assert.Equal("src/x", GitDiff.Strip("src/x"));
        Assert.Equal("/dev/null", GitDiff.Strip("/dev/null"));
    }
}
