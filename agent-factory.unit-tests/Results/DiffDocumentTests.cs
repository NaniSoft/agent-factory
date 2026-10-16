namespace AgentFactory.UnitTests.Results;

using AgentFactory.Application.Results;
using AgentFactory.Domain.Results;

/// <summary>
/// A diff as the review surface reads it, and the bound the board puts on a large one.
/// </summary>
public class DiffDocumentTests
{
    [Fact]
    public void A_diff_that_is_empty_or_not_a_diff_produces_an_empty_document_rather_than_an_error()
    {
        // "There is nothing here to read" is a thing a reviewer is entitled to be told rather than
        // a failure, so parsing never throws and never invents a file.
        foreach (var diff in new string?[] { null, string.Empty, "   ", "just some prose\n" })
        {
            var document = DiffDocument.Parse(diff);
            Assert.Empty(document.Files);
            Assert.False(document.Lines > 0);
        }
    }

    [Fact]
    public void Each_file_is_read_with_its_path_its_counts_and_gits_own_text_unchanged()
    {
        var document = DiffDocument.Parse(ADiff("a.txt", "b.txt"));

        Assert.Equal(["a.txt", "b.txt"], document.Files.Select(file => file.Path));
        Assert.Equal(FileChange.Modified, document.Files[0].Change);
        Assert.Equal(1, document.Files[0].Added);
        Assert.Equal(1, document.Files[0].Removed);

        // Git's own text for the file is carried byte for byte: a diff a reviewer cannot check
        // against git is not the diff that would ship.
        Assert.Contains("diff --git a/a.txt b/a.txt", document.Files[0].Text, StringComparison.Ordinal);
        Assert.Contains("+new", document.Files[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_preamble_is_kept_beside_the_files_and_is_not_one_of_them()
    {
        var document = DiffDocument.Parse("a note the caller put at the top\n" + ADiff("a.txt"));

        Assert.Equal("a note the caller put at the top", document.Preamble);
        Assert.Single(document.Files);
    }

    [Fact]
    public void A_section_whose_path_cannot_be_read_is_dropped_rather_than_named()
    {
        // A path invented out of a header is a path that does not exist, and a file on a reviewer's
        // list that is not one is worse than a file missing from it.
        var document = DiffDocument.Parse("diff --git not-a-header\nindex abc..def 100644\n");

        Assert.Empty(document.Files);
    }

    // ------------------------------------------------------------------ the bound

    [Fact]
    public void A_diff_inside_the_bound_is_the_whole_diff()
    {
        var document = DiffDocument.Parse(ADiff("a.txt", "b.txt", "c.txt"));

        var window = DiffWindow.Showing(document, maxFiles: 200, maxLines: 20_000);

        Assert.True(window.IsWhole);
        Assert.Equal(0, window.OmittedFiles);
        Assert.Equal(3, window.Files.Count);
        Assert.Equal(3, window.TotalFiles);
        Assert.Equal(document.Lines, window.TotalLines);
    }

    [Fact]
    public void The_bound_cuts_between_files_and_never_in_the_middle_of_one()
    {
        // This is the rule. A diff cut in the middle of a file shows a reviewer half a change and a
        // line count, and half a change looks like the whole of what was done to that file.
        var document = DiffDocument.Parse(ADiff("a.txt", "b.txt", "c.txt"));

        // Each file is six lines, so a five-line budget admits none of them on its own — but the
        // first is always shown whole, because showing nothing would be worse.
        var window = DiffWindow.Showing(document, maxFiles: 10, maxLines: 5);

        Assert.False(window.IsWhole);
        Assert.Single(window.Files);

        // Identical text, not a truncated prefix: the file on the card is complete.
        Assert.Equal(document.Files[0].Text, window.Files[0].Text);
        Assert.EndsWith("+new", window.Files[0].Text, StringComparison.Ordinal);

        // And the count of what is not shown is the load-bearing half: a diff that stops without
        // saying how much it stopped at is indistinguishable from a diff of a smaller change.
        Assert.Equal(2, window.OmittedFiles);
        Assert.Equal(document.Lines - window.Files[0].Text.Split('\n').Length, window.OmittedLines);
        Assert.Equal(3, window.TotalFiles);
        Assert.Equal(document.Lines, window.TotalLines);
    }

    [Fact]
    public void The_file_bound_bites_before_the_line_bound_when_it_is_the_smaller_one()
    {
        var document = DiffDocument.Parse(ADiff("a.txt", "b.txt", "c.txt"));

        var window = DiffWindow.Showing(document, maxFiles: 2, maxLines: 20_000);

        Assert.Equal(["a.txt", "b.txt"], window.Files.Select(file => file.Path));
        Assert.Equal(1, window.OmittedFiles);
        Assert.Equal(3, window.TotalFiles);
    }

    /// <summary>N whole file sections, each six lines, with no preamble and no truncation.</summary>
    private static string ADiff(params string[] paths) => string.Join(
        "\n",
        paths.Select(path =>
            $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -1,2 +1,2 @@\n-old\n+new"));
}
