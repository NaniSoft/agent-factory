namespace AgentFactory.Pages;

using AgentFactory.Results;
using AgentFactory.WorkItems;

/// <summary>
/// One round's change, as the board shows it, and why it is shown that way.
/// </summary>
/// <remarks>
/// <para>
/// This is the review surface for a diff, and the whole of its judgement is here rather
/// than in the view. The view renders what this says; it decides nothing. That matters
/// because the decision a diff view makes is the one that can hide a change: a
/// presentation that trims, folds or ranks is a presentation that can be wrong in a way
/// nobody can check, and a reviewer's judgement of an agent's change is the one thing
/// this whole system exists to keep human.
/// </para>
/// <para>
/// So the rules are fixed and stated, and there are only two of them. Nothing is
/// summarised: git's own text for each file is rendered unchanged, and a file's
/// "<c>+12 −3</c>" is a count of lines in that text rather than a verdict about it. And
/// nothing is silently dropped: a diff too large to render whole says how much of it is
/// not on the page, counts the files and lines left out, and names the path the reviewer
/// can get the rest from — a path that is on the host because the host has to be able to
/// reach the round's commit anyway (ADR-0006).
/// </para>
/// </remarks>
internal static class HowToReadTheDiff
{
    /// <summary>
    /// How many files the board renders before it stops and says so. Generous on purpose:
    /// an agent's round touches a handful of files, so a bound that bites is a bound on a
    /// change nobody is going to read on one screen, and the thing that matters is that
    /// biting is visible rather than that it never happens.
    /// </summary>
    public const int MaxFiles = 200;

    /// <summary>
    /// How many lines of a diff the board renders, on top of the file bound. A diff under
    /// it is rendered whole, which is the ordinary case and the one every test in the
    /// suite exercises.
    /// </summary>
    public const int MaxLines = 20_000;

    /// <summary>
    /// <summary>
    /// Whether a round's own record says the container bounded the diff it recorded, and
    /// the board says so because there are then two observations of one change and only
    /// one of them is here. A reviewer who notices the difference is owed the answer.
    /// </summary>
    public static string? BoundedElsewhere(HostDiff? diff) =>
        diff is { ContainerBounded: true }
            ? "The container recorded a copy of this diff too and bounded it; this is the host's, taken from the "
                + "tree the round left, and it is not bounded."
            : null;

    /// <summary>
    /// The diff as the board shows it, and the exact account of the part it does not. Null
    /// when the round has no diff at all, which the board says rather than rendering an
    /// empty section: "no diff was generated" and "the round changed nothing" are very
    /// different claims and a reviewer is entitled to be told which is which.
    /// </summary>
    public static DiffOnTheBoard? Of(RoundResultRecord round)
    {
        if (round.Diff is not { } diff)
        {
            return null;
        }

        if (diff.UnavailableBecause is { } unavailable)
        {
            return new DiffOnTheBoard(
                State: DiffState.Unavailable,
                Files: [],
                OmittedFiles: 0,
                OmittedLines: 0,
                TotalFiles: 0,
                TotalLines: 0,
                Tree: diff.Tree,
                UnavailableBecause: unavailable,
                BoundedElsewhere: BoundedElsewhere(diff));
        }

        if (!diff.HasText)
        {
            // An empty diff is a real outcome, and saying so is the whole of what is
            // needed. A section with "nothing on disk differs" in it is a fact about the
            // round; an absent section would read as a round the factory did not look at.
            return new DiffOnTheBoard(
                State: DiffState.Empty,
                Files: [],
                OmittedFiles: 0,
                OmittedLines: 0,
                TotalFiles: 0,
                TotalLines: 0,
                Tree: diff.Tree,
                UnavailableBecause: null,
                BoundedElsewhere: BoundedElsewhere(diff));
        }

        var document = DiffDocument.Parse(diff.Text);
        var window = DiffWindow.Showing(document, MaxFiles, MaxLines);

        return new DiffOnTheBoard(
            State: DiffState.Shown,
            Files: window.Files,
            OmittedFiles: window.OmittedFiles,
            OmittedLines: window.OmittedLines,
            TotalFiles: window.TotalFiles,
            TotalLines: window.TotalLines,
            Tree: diff.Tree,
            UnavailableBecause: null,
            BoundedElsewhere: BoundedElsewhere(diff));
    }

    /// <summary>
    /// What was left off the page, said in the reviewer's terms. Null when nothing was,
    /// which is the ordinary case — and the count is in the record either way, on
    /// <c>data-omitted-files</c> and <c>data-omitted-lines</c>, so a test can assert the
    /// arithmetic rather than the prose.
    /// </summary>
    public static string? WhatIsLeftOff(DiffOnTheBoard? diff) =>
        diff is { OmittedFiles: > 0 }
            ? $"{diff.OmittedFiles} more file(s), {diff.OmittedLines} more line(s), are not on this page. Nothing is "
                + $"summarised in their place. The whole diff is the round's own tree, on this machine, at {diff.Tree}."
            : null;
}

/// <summary>What the board has for one round's change.</summary>
public enum DiffState
{
    /// <summary>No diff was generated, and the board says why.</summary>
    Unavailable,

    /// <summary>The round changed nothing on disk, which is a real outcome.</summary>
    Empty,

    /// <summary>There is a change to read.</summary>
    Shown,
}

/// <summary>
/// One round's diff as the board renders it: the files, and a complete account of
/// anything not on the page.
/// </summary>
public sealed record DiffOnTheBoard(
    DiffState State,
    IReadOnlyList<DiffFile> Files,
    int OmittedFiles,
    int OmittedLines,
    int TotalFiles,
    int TotalLines,
    string Tree,
    string? UnavailableBecause,
    string? BoundedElsewhere)
{
    /// <summary>
    /// Whether a file's section starts open. The first file is open and the rest are not:
    /// a reviewer looking at a card sees the shape of the change and the first file's
    /// actual content, and opens the others by name. Every file is on the page whatever
    /// the state, so a folded file is one click away rather than summarised away.
    /// </summary>
    public bool IsOpenAt(int index) => index == 0 && State == DiffState.Shown;
}
