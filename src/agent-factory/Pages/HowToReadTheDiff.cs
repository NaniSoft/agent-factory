namespace AgentFactory.Pages;

using AgentFactory.Results;
using AgentFactory.Rounds;
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
    /// <remarks>
    /// <para>
    /// The fourth state — <see cref="DiffState.Unfinished"/> — is decided from the round's
    /// outcome rather than from the diff, and it is the one place the two are read
    /// together. <see cref="RoundOutcome.Produced"/> means the round's own last command
    /// returned zero, so an empty diff on a produced round genuinely is "the round ran and
    /// changed nothing". An empty diff on any other round is not that, and saying it is was
    /// the defect: the first real run's cards read <c>data-outcome="Produced"
    /// data-diff-state="empty"</c> over a round whose agent had exited 1 on a rate limit in
    /// two and a half seconds, having written nothing (#22).
    /// </para>
    /// <para>
    /// It sits <em>after</em> the "there is a change" check rather than before it, and
    /// that order is a claim about the reviewer's need: a round that got partway through
    /// and then failed has a real change on disk, and showing it is more useful than
    /// replacing it with a note that the round did not finish. The card's outcome and
    /// failure say the round did not finish; the diff says what it left behind.
    /// </para>
    /// </remarks>
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

        if (diff.HasText)
        {
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

        // No change on disk. Whether that is the round's decision is now a separate
        // question, and the answer is the round's outcome rather than anything the diff can
        // be asked.
        return new DiffOnTheBoard(
            State: round.Outcome == RoundOutcome.Produced ? DiffState.Empty : DiffState.Unfinished,
            Files: [],
            OmittedFiles: 0,
            OmittedLines: 0,
            TotalFiles: 0,
            TotalLines: 0,
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

    /// <summary>
    /// What a round with no change on disk is being said to have done, which is not the
    /// same sentence in the two cases.
    /// </summary>
    /// <remarks>
    /// The difference is the whole of the fourth state, and it is worth stating in words
    /// rather than only in a <c>data-</c> attribute. "Nothing on disk differs from the
    /// commit the round started at" is true of a rate-limited round, and reading it as
    /// "the round ran and decided to change nothing" is a claim about a round that never
    /// started — which is what the first real run's board said, over a payload two lines
    /// below it carrying <c>exit 1</c> and the provider's own error (#22).
    /// </remarks>
    public static string? SaysAboutAnUnchangedDisk(DiffOnTheBoard? diff) => diff switch
    {
        {
            State: DiffState.Empty,
        } => "Empty. Nothing on disk differs from the commit the round started at: the round ran to completion "
            + "and changed nothing.",

        {
            State: DiffState.Unfinished,
        } => "Unfinished. The round's own command did not succeed, so the round did not run to completion. "
            + "Nothing on disk differs from the commit it started at because that is where it stopped, not because "
            + "the round chose to leave it alone. What it did run is in the record below this.",

        _ => null,
    };
}

/// <summary>What the board has for one round's change.</summary>
/// <remarks>
/// The four are claims, and each one has to be a claim the round's record supports. Three
/// of them are about the <em>diff</em>: there is a change, there is demonstrably no change,
/// or no diff could be produced. The fourth is about the <em>round</em>, and it exists
/// because the first real run found a case none of the three could honestly cover: a round
/// whose agent was refused before it began reported <c>empty</c>, and "Empty. Nothing on
/// disk differs from the commit the round started at" is a true statement about the disk
/// and a false one about the round (#22). The disk claim was never the problem; pairing it
/// with an outcome that said the round had produced something was.
/// </remarks>
public enum DiffState
{
    /// <summary>No diff was generated, and the board says why.</summary>
    Unavailable,

    /// <summary>
    /// The round ran to completion and left the disk as it found it. A real outcome, and
    /// one a reviewer is entitled to be told about rather than have it rendered as nothing.
    /// </summary>
    Empty,

    /// <summary>
    /// The round's own last command did not succeed, so the round did not run to
    /// completion. Whatever the disk holds is where it stopped, not a change it made.
    /// </summary>
    Unfinished,

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
