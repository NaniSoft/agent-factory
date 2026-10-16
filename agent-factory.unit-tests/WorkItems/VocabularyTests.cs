namespace AgentFactory.UnitTests.WorkItems;

using AgentFactory.Domain.WorkItems;

/// <summary>
/// The factory's closed vocabularies: the three decisions, the lanes, and what a routing round
/// decided an issue is. Each is a set that must not grow by accident, and each has a refusal that
/// matters more than its happy path.
/// </summary>
public class VocabularyTests
{
    // ------------------------------------------------------------------ the decisions

    [Fact]
    public void There_are_three_decisions_and_no_fourth()
    {
        Assert.Equal([Decision.Approve, Decision.RequestChanges, Decision.Reject], Decisions.All);
    }

    [Fact]
    public void A_decision_is_read_by_its_slug_and_by_nothing_else()
    {
        // A slug is the wire format, and a reviewer's keyboard is not a schema — so the match is
        // case-insensitive. But nothing here parses an arbitrary name or a bare number into the set,
        // which is what keeps "there are exactly three" a fact about the write path.
        foreach (var decision in Decisions.All)
        {
            Assert.True(Decisions.TryParse(Decisions.Slug(decision), out var read));
            Assert.Equal(decision, read);

            Assert.True(Decisions.TryParse(Decisions.Slug(decision).ToUpperInvariant(), out var upper));
            Assert.Equal(decision, upper);
        }

        foreach (var posted in new string?[] { "", "ship", "4", "approve ", " approve", "request changes", "reject!", null, "0" })
        {
            Assert.False(Decisions.TryParse(posted, out _));
        }
    }

    [Fact]
    public void What_a_lane_offers_is_what_the_store_will_keep()
    {
        // Review offers all three. Escalated offers the two that finish a parked change, because
        // parking is not a grave. Everywhere else offers nothing, and RequestChanges in particular
        // is not offered on a parked item: whether a failed thing is tried again is the retry
        // policy's business, not a reviewer's click.
        Assert.Equal(Decisions.All, Decisions.OfferedIn(Swimlane.Review));
        Assert.Equal([Decision.Approve, Decision.Reject], Decisions.OfferedIn(Swimlane.Escalated));

        foreach (var lane in new[]
        {
            Swimlane.Backlog,
            Swimlane.Frontier,
            Swimlane.InProgress,
            Swimlane.Done,
            Swimlane.Waiting,
            Swimlane.Rejected,
        })
        {
            Assert.Empty(Decisions.OfferedIn(lane));
        }
    }

    [Fact]
    public void A_value_outside_the_three_has_no_slug_and_no_label()
    {
        // A cast value is not a decision, and it is thrown rather than rendered as something: a
        // board that showed a button for it would be offering a decision the store would refuse.
        Assert.Throws<ArgumentOutOfRangeException>(() => Decisions.Slug((Decision)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => Decisions.Label((Decision)99));

        Assert.Equal(Decisions.All.Count, Decisions.All.Select(Decisions.Slug).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Decisions.All.Count, Decisions.All.Select(Decisions.Label).Distinct(StringComparer.Ordinal).Count());
    }

    // ------------------------------------------------------------------ the lanes

    [Fact]
    public void The_lanes_a_reviewer_can_still_decide_about_are_review_and_escalated()
    {
        // Escalated is in because parking is not a grave: a human finishes a parked change from the
        // board. Waiting is out because it holds no change to judge — a map and a question set are
        // not diffs — so the two decisions would be about nothing. Rejected is out because a decline
        // is final; its absence here is what makes that finality structural.
        Assert.Equal([Swimlane.Review, Swimlane.Escalated], Swimlanes.Decidable);
        Assert.DoesNotContain(Swimlane.Waiting, Swimlanes.Decidable);
        Assert.DoesNotContain(Swimlane.Rejected, Swimlanes.Decidable);
    }

    [Fact]
    public void The_lane_set_and_its_labels()
    {
        Assert.Equal(
            [Swimlane.Backlog, Swimlane.Frontier, Swimlane.InProgress, Swimlane.Review, Swimlane.Done, Swimlane.Waiting],
            Swimlanes.All);

        Assert.Equal([Swimlane.Escalated, Swimlane.Rejected], Swimlanes.ParkedAndFinal);

        // The one lane whose name is two words; every other label is the enum's own name.
        Assert.Equal("In Progress", Swimlanes.Label(Swimlane.InProgress));
        Assert.Equal("Done", Swimlanes.Label(Swimlane.Done));
        Assert.Equal("Review", Swimlanes.Label(Swimlane.Review));
    }

    // ------------------------------------------------------------------ the routing verdicts

    [Fact]
    public void Only_a_build_verdict_is_buildable_and_unrouted_is_not_a_verdict()
    {
        Assert.True(WorkItemKinds.IsBuildable(WorkItemKind.Build));
        Assert.False(WorkItemKinds.IsBuildable(WorkItemKind.Grill));
        Assert.False(WorkItemKinds.IsBuildable(WorkItemKind.Wayfinder));

        // "Nobody has looked at this yet" is a fact and not a verdict, so it is not buildable
        // either — a board that read it as one would build an issue nobody had read.
        Assert.False(WorkItemKinds.IsBuildable(WorkItemKind.Unrouted));
        Assert.Equal(WorkItemKind.Unrouted, default(WorkItemKind));
    }

    [Fact]
    public void The_verdicts_have_their_own_words()
    {
        Assert.Equal("needs grilling", WorkItemKinds.Label(WorkItemKind.Grill));
        Assert.Equal("needs a wayfinder map", WorkItemKinds.Label(WorkItemKind.Wayfinder));
        Assert.Equal("Build", WorkItemKinds.Label(WorkItemKind.Build));
        Assert.Equal("Unrouted", WorkItemKinds.Label(WorkItemKind.Unrouted));
    }
}
