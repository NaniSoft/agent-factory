namespace AgentFactory.Results;

using System.Text;

/// <summary>
/// A diff, as the review surface reads it: one entry per file, each carrying the facts a
/// reviewer needs <em>before</em> opening it, and git's own text for that file underneath
/// them unchanged.
/// </summary>
/// <remarks>
/// <para>
/// This models git's diff format and nothing else. It has no opinion about which part of a
/// change matters, no ranking, and no summary of a hunk — a hunk is shown the way git
/// wrote it because a diff a reviewer cannot check against git is not the diff that would
/// ship (ADR-0011, ADR-0006).
/// </para>
/// <para>
/// It exists because a diff is the one part of a round's record with structure worth
/// keeping. The rest of the result is a handful of facts and reads fine as text; a diff is
/// a list of files, and rendering a list of files as one run of characters is what makes a
/// large change a wall rather than something a reviewer can judge. Parsing it here rather
/// than in the view is also what keeps the view from having to know what a diff is.
/// </para>
/// </remarks>
/// <param name="Path">The file, as git names it, relative to the repository root.</param>
/// <param name="PreviousPath">Where it was before, when git says it moved or was copied.</param>
/// <param name="Change">What git says happened to it.</param>
/// <param name="Added">Lines git adds.</param>
/// <param name="Removed">Lines git removes.</param>
/// <param name="Binary">
/// Whether git wrote this file's change in binary. It has no readable content, which is a
/// fact about the change and not a gap in the record.
/// </param>
/// <param name="Text">
/// Git's own text for this file's section, byte for byte — the <c>diff --git</c> header,
/// the modes, the hunks and the "\ No newline" markers. Nothing is removed, rewritten or
/// re-indented: this is what a reviewer would get from <c>git diff</c> on the same tree.
/// </param>
public sealed record DiffFile(
    string Path,
    string? PreviousPath,
    FileChange Change,
    int Added,
    int Removed,
    bool Binary,
    string Text);

/// <summary>
/// A whole diff: one <see cref="DiffFile"/> per file git wrote, in git's own order, plus
/// anything git put before the first file that is not part of a file's change at all.
/// </summary>
public sealed record DiffDocument(IReadOnlyList<DiffFile> Files, string Preamble)
{
    /// <summary>
    /// Reads a diff. Never throws and never invents a file: a diff that is empty, a
    /// preamble, or not a diff at all produces an empty document rather than an error,
    /// because "there is nothing here to read" is a thing a reviewer is entitled to be
    /// told rather than a failure.
    /// </summary>
    /// <param name="diff">
    /// A diff, as git wrote it. A section whose path cannot be read at all is dropped
    /// rather than named: a path invented out of a header is a path that does not exist,
    /// and a file on a reviewer's list that is not one is worse than a file missing from
    /// it.
    /// </param>
    public static DiffDocument Parse(string? diff)
    {
        if (string.IsNullOrWhiteSpace(diff))
        {
            return new DiffDocument([], string.Empty);
        }

        var text = GitDiff.Normalise(diff);
        var first = text.IndexOf(GitDiff.Header, StringComparison.Ordinal);
        var preamble = (first < 0 ? text : text[..first]).Trim();
        var files = new List<DiffFile>();

        foreach (var section in GitDiff.Sections(text))
        {
            if (GitDiff.PathOf(section) is not { Length: > 0 } path)
            {
                continue;
            }

            var (added, removed) = GitDiff.Counts(section);
            files.Add(new DiffFile(
                path,
                GitDiff.PreviousPathOf(section),
                GitDiff.Change(section),
                added,
                removed,
                GitDiff.IsBinary(section),
                section));
        }

        return new DiffDocument(files, preamble);
    }

    /// <summary>Lines of content across every file, which is what a wall of text is counted in.</summary>
    public int Lines => Files.Sum(file => file.Text.Split('\n').Length);
}

/// <summary>
/// The part of a diff the board shows, and an exact account of the part it does not.
/// </summary>
/// <remarks>
/// <para>
/// The bound is on whole files. A diff cut in the middle of a file shows a reviewer half
/// a change and a line count, and half a change is the one thing worse than no change: it
/// looks like the whole of what was done to that file. Cutting between files instead means
/// every file on the card is complete, and what is missing is missing in whole units that
/// are counted and named.
/// </para>
/// <para>
/// The count of what is not shown is the load-bearing half. A diff that stops without
/// saying how much it stopped at is indistinguishable from a diff of a smaller change,
/// which is the failure the whole of this ticket exists to prevent.
/// </para>
/// </remarks>
/// <param name="Files">The files shown, whole, in the diff's own order.</param>
/// <param name="OmittedFiles">How many files are not shown.</param>
/// <param name="OmittedLines">How many lines of content those files hold.</param>
/// <param name="TotalFiles">How many files the diff has, shown or not.</param>
/// <param name="TotalLines">How many lines of content the diff has, shown or not.</param>
public sealed record DiffWindow(
    IReadOnlyList<DiffFile> Files,
    int OmittedFiles,
    int OmittedLines,
    int TotalFiles,
    int TotalLines)
{
    /// <summary>Whether this is the whole diff, which is what an ordinary round's is.</summary>
    public bool IsWhole => OmittedFiles == 0;

    /// <summary>
    /// Shows as many whole files as <paramref name="maxFiles"/> and
    /// <paramref name="maxLines"/> allow. Generous on purpose: an agent's round touches
    /// a handful of files, so a bound that bites is a bound on a change nobody can read on
    /// one page, and the fact that it bit is stated rather than absorbed.
    /// </summary>
    public static DiffWindow Showing(DiffDocument document, int maxFiles, int maxLines)
    {
        ArgumentNullException.ThrowIfNull(document);

        var shown = new List<DiffFile>();
        var lines = 0;

        foreach (var file in document.Files)
        {
            var fileLines = file.Text.Split('\n').Length;

            // A file that is already past the budget is still shown, and the budget is
            // spent: showing an empty section would be worse than showing one file too
            // many, and the omission that follows is counted honestly either way.
            if (shown.Count >= maxFiles || (shown.Count > 0 && lines + fileLines > maxLines))
            {
                break;
            }

            shown.Add(file);
            lines += fileLines;
        }

        var omitted = document.Files.Skip(shown.Count).ToList();

        return new DiffWindow(
            shown,
            omitted.Count,
            omitted.Sum(file => file.Text.Split('\n').Length),
            document.Files.Count,
            document.Lines);
    }
}
