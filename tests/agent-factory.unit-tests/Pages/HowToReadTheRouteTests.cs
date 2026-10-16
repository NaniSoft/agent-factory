namespace AgentFactory.UnitTests.Pages;

using AgentFactory.Application.Pages;
using AgentFactory.Domain.WorkItems;
using AgentFactory.UnitTests.Boundary;

/// <summary>
/// What a card says about the routing round that looked at its issue. The judgement is small and it
/// is the whole of it: a card must not let "nobody has looked at this yet" read as "this is ready to
/// build".
/// </summary>
public class HowToReadTheRouteTests
{
    [Fact]
    public void An_issue_nobody_has_looked_at_yet_is_said_in_those_words()
    {
        var said = HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Backlog));

        Assert.Contains("Nobody has looked at this issue yet", said, StringComparison.Ordinal);
        Assert.Contains("before it can be built", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_routed_build_ticket_is_still_waiting_for_the_reviewers_click()
    {
        var said = HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Backlog, kind: WorkItemKind.Build));

        Assert.Contains("already a build ticket", said, StringComparison.Ordinal);
        Assert.Contains("Nothing has been built yet", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_grill_and_a_wayfinder_are_different_waits_and_say_so()
    {
        Assert.Contains(
            "needs deciding with a person",
            HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Waiting, kind: WorkItemKind.Grill)),
            StringComparison.Ordinal);

        Assert.Contains(
            "charted as a map of decisions",
            HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Waiting, kind: WorkItemKind.Wayfinder)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_issue_whose_questions_have_answers_is_being_read_again()
    {
        var said = HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Waiting, routingIssue: 25));

        Assert.Contains("#25", said, StringComparison.Ordinal);
        Assert.Contains("reading the whole conversation again", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrouted_issue_that_was_parked_for_a_human_says_the_round_could_not_answer()
    {
        var said = HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Escalated));

        Assert.Contains("could not answer", said, StringComparison.Ordinal);
        Assert.Contains("parked", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_kind_this_build_does_not_know_is_still_a_sentence_and_not_an_absence()
    {
        // There is deliberately no default that reads as a verdict: a card that has to be read to
        // find out that its state is unreadable is a card nobody will read.
        var said = HowToReadTheRoute.Say(AWorkItem.For(kind: (WorkItemKind)99));

        Assert.False(string.IsNullOrWhiteSpace(said));
        Assert.Contains("not one this factory knows how to act on", said, StringComparison.Ordinal);
    }

    [Fact]
    public void No_two_kinds_read_the_same()
    {
        var said = new[]
        {
            HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Backlog)),
            HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Escalated)),
            HowToReadTheRoute.Say(AWorkItem.For(swimlane: Swimlane.Waiting, routingIssue: 25)),
            HowToReadTheRoute.Say(AWorkItem.For(kind: WorkItemKind.Build)),
            HowToReadTheRoute.Say(AWorkItem.For(kind: WorkItemKind.Grill)),
            HowToReadTheRoute.Say(AWorkItem.For(kind: WorkItemKind.Wayfinder)),
        };

        Assert.All(said, sentence => Assert.False(string.IsNullOrWhiteSpace(sentence)));
        Assert.Equal(said.Length, said.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Acceptance_is_offered_only_for_a_build_ticket_still_waiting_in_backlog()
    {
        Assert.True(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Backlog, kind: WorkItemKind.Build)));

        // An issue nobody has looked at is not a build ticket, so the control is gated on the kind
        // and not on the lane alone.
        Assert.False(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Backlog)));
        Assert.False(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Backlog, kind: WorkItemKind.Grill)));
        Assert.False(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Backlog, kind: WorkItemKind.Wayfinder)));

        // And a build ticket that has already left Backlog has been accepted already.
        Assert.False(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Frontier, kind: WorkItemKind.Build)));
        Assert.False(HowToReadTheRoute.OffersAcceptance(AWorkItem.For(swimlane: Swimlane.Review, kind: WorkItemKind.Build)));
    }
}
