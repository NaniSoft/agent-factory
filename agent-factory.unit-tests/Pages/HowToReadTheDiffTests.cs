namespace AgentFactory.UnitTests.Pages;

using AgentFactory.Application.Pages;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.Rounds;
using AgentFactory.UnitTests.Boundary;

/// <summary>
/// One round's change as the board shows it. The whole of the judgement is here rather than in the
/// view, because the decision a diff view makes is the one that can hide a change.
/// </summary>
/// <remarks>
/// The load-bearing case is the fourth state: an empty diff on a round that ran to completion means
/// "changed nothing", and an empty diff on a round that did not run to completion means "did not
/// finish". They read the same on disk and are very different claims about the round, and this file
/// asserts both.
/// </remarks>
public class HowToReadTheDiffTests
{
    [Fact]
    public void A_round_with_no_diff_at_all_is_null_rather_than_an_empty_section()
    {
        Assert.Null(HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, diff: null)));
    }

    [Fact]
    public void A_diff_that_could_not_be_generated_says_why_and_shows_no_files()
    {
        var board = HowToReadTheDiff.Of(
            ARecord(RoundOutcome.Produced, HostDiff.Unavailable("some/tree", "git could not read the tree")));

        var diff = Assert.IsType<DiffOnTheBoard>(board);
        Assert.Equal(DiffState.Unavailable, diff.State);
        Assert.Equal("git could not read the tree", diff.UnavailableBecause);
        Assert.Empty(diff.Files);
    }

    [Fact]
    public void An_empty_diff_on_a_produced_round_is_a_round_that_changed_nothing()
    {
        var board = HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, new HostDiff("", "main", "some/tree", false, null)));

        var diff = Assert.IsType<DiffOnTheBoard>(board);
        Assert.Equal(DiffState.Empty, diff.State);
        Assert.Empty(diff.Files);
        Assert.Contains("ran to completion and changed nothing", HowToReadTheDiff.SaysAboutAnUnchangedDisk(diff)!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_diff_on_a_failed_round_is_a_round_that_did_not_finish()
    {
        var board = HowToReadTheDiff.Of(ARecord(RoundOutcome.Failed, new HostDiff("", "main", "some/tree", false, null)));

        var diff = Assert.IsType<DiffOnTheBoard>(board);
        Assert.Equal(DiffState.Unfinished, diff.State);
        Assert.Contains("did not run to completion", HowToReadTheDiff.SaysAboutAnUnchangedDisk(diff)!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_diff_means_two_different_things_and_the_board_says_which()
    {
        // The two states are the whole point of the fourth state: the disk claim is the same and
        // the claim about the round is not, and reading the first as the second is what the first
        // real run's board did over a rate-limited round (#22).
        var produced = Assert.IsType<DiffOnTheBoard>(
            HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, new HostDiff("", "main", "some/tree", false, null))));
        var failed = Assert.IsType<DiffOnTheBoard>(
            HowToReadTheDiff.Of(ARecord(RoundOutcome.Failed, new HostDiff("", "main", "some/tree", false, null))));

        Assert.NotEqual(produced.State, failed.State);
        Assert.NotEqual(
            HowToReadTheDiff.SaysAboutAnUnchangedDisk(produced),
            HowToReadTheDiff.SaysAboutAnUnchangedDisk(failed));
    }

    [Fact]
    public void A_round_that_changed_something_shows_the_files_with_gits_own_text()
    {
        var text = "diff --git a/src/index.js b/src/index.js\n--- a/src/index.js\n+++ b/src/index.js\n@@ -1 +1 @@\n-old\n+new\n";
        var board = HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, new HostDiff(text, "main", "some/tree", false, null)));

        var diff = Assert.IsType<DiffOnTheBoard>(board);
        Assert.Equal(DiffState.Shown, diff.State);
        var file = Assert.Single(diff.Files);
        Assert.Equal("src/index.js", file.Path);
        Assert.Contains("+new", file.Text, StringComparison.Ordinal);
        Assert.Equal(0, diff.OmittedFiles);
        Assert.True(diff.IsOpenAt(0));
        Assert.False(diff.IsOpenAt(1));
        Assert.Null(HowToReadTheDiff.SaysAboutAnUnchangedDisk(diff));
    }

    [Fact]
    public void A_bounded_diff_says_the_container_recorded_a_bounded_copy_too()
    {
        // Two observations of one change and only one of them is on the card: a reviewer who
        // notices the difference is owed the answer.
        var bounded = new HostDiff("diff --git a/x b/x\n", "main", "some/tree", ContainerBounded: true, null);
        var board = Assert.IsType<DiffOnTheBoard>(HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, bounded)));

        Assert.NotNull(board.BoundedElsewhere);
        Assert.Contains("bounded it", board.BoundedElsewhere!, StringComparison.Ordinal);

        var unbounded = new HostDiff("diff --git a/x b/x\n", "main", "some/tree", ContainerBounded: false, null);
        Assert.Null(HowToReadTheDiff.BoundedElsewhere(unbounded));
    }

    [Fact]
    public void What_was_left_off_the_page_is_counted_and_names_where_the_rest_is()
    {
        // A diff too large to render whole says how much of it is not on the page, counts the files
        // and lines left out, and names the path the reviewer can get the rest from.
        var many = string.Join(
            "\n",
            Enumerable.Range(1, HowToReadTheDiff.MaxFiles + 1).Select(index =>
                $"diff --git a/f{index}.txt b/f{index}.txt\n--- a/f{index}.txt\n+++ b/f{index}.txt\n@@ -1 +1 @@\n-old\n+new"));

        var board = Assert.IsType<DiffOnTheBoard>(
            HowToReadTheDiff.Of(ARecord(RoundOutcome.Produced, new HostDiff(many, "main", "some/tree", false, null))));

        Assert.Equal(DiffState.Shown, board.State);
        Assert.Equal(HowToReadTheDiff.MaxFiles, board.Files.Count);
        Assert.Equal(1, board.OmittedFiles);
        Assert.Equal(HowToReadTheDiff.MaxFiles + 1, board.TotalFiles);

        var leftOff = HowToReadTheDiff.WhatIsLeftOff(board);
        Assert.NotNull(leftOff);
        Assert.Contains("1 more file(s)", leftOff!, StringComparison.Ordinal);
        Assert.Contains("some/tree", leftOff!, StringComparison.Ordinal);

        // And a whole diff says nothing was left off.
        Assert.Null(HowToReadTheDiff.WhatIsLeftOff(
            Assert.IsType<DiffOnTheBoard>(HowToReadTheDiff.Of(
                ARecord(RoundOutcome.Produced, new HostDiff("diff --git a/x b/x\n", "main", "some/tree", false, null))))));
    }

    private static RoundResultRecord ARecord(RoundOutcome outcome, HostDiff? diff) =>
        new(
            Guid.NewGuid(),
            1,
            outcome,
            "the round's own record",
            null,
            AWorkItem.At,
            AWorkItem.At,
            Diff: diff);
}
