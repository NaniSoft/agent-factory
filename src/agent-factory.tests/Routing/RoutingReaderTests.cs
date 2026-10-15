namespace AgentFactory.Tests.Routing;

using AgentFactory.Domain.Routing;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Infrastructure.Routing;
using AgentFactory.WorkItems;

/// <summary>
/// Reading a routing round's verdict, hermetically. No Docker, no model, no loop and no
/// store: these are files written in the shape the brief asks the agent to write, and the
/// question is whether the factory can tell the three verdicts apart — and can tell a
/// verdict it cannot read from a verdict that says <c>Build</c>.
/// </summary>
/// <remarks>
/// The honest limit, which is the same one <c>RoundResultDeriverTests</c> states about
/// itself: a fixture this file wrote agrees with the reader by construction. What it does
/// establish is the whole of the *degradation* behaviour, which is where the danger is — a
/// reader that defaulted to <c>Build</c> would turn every truncation into a container spent
/// building something nobody decided should be built, and no happy-path fixture would notice.
/// </remarks>
public class RoutingReaderTests
{
    private readonly RoutingReader _reader = new();

    [Fact]
    public void A_build_verdict_is_read_with_the_rounds_own_reason()
    {
        var outcome = _reader.Read(Write(
            """
            {
              "schema": "agent-factory/routing@1",
              "verdict": "Build",
              "because": "the issue names one endpoint and the test that proves it"
            }
            """));

        var proposal = Assert.IsType<RoutingProposal>(outcome.Proposal);
        Assert.True(outcome.IsReadable);
        Assert.Null(outcome.UnreadableBecause);
        Assert.Equal(WorkItemKind.Build, proposal.Verdict);
        Assert.Equal("the issue names one endpoint and the test that proves it", proposal.Because);
        Assert.Empty(proposal.Questions);
        Assert.Null(proposal.Map);

        // A build ticket asks for nothing, which is what `AsksSomething` exists to say.
        Assert.False(proposal.AsksSomething);
    }

    [Fact]
    public void A_grill_verdict_carries_the_questions_a_person_has_to_answer()
    {
        var outcome = _reader.Read(Write(
            """
            {
              "schema": "agent-factory/routing@1",
              "verdict": "Grill",
              "because": "the shape is clear and two decisions about it are not",
              "questions": [
                { "question": "does a draft count as published?", "because": "it changes what a reviewer's click means" },
                { "question": "who may unpublish?", "because": "nothing on the issue says" }
              ]
            }
            """));

        var proposal = Assert.IsType<RoutingProposal>(outcome.Proposal);
        Assert.Equal(WorkItemKind.Grill, proposal.Verdict);
        Assert.True(proposal.AsksSomething);
        Assert.Equal(2, proposal.Questions.Count);
        Assert.Equal("does a draft count as published?", proposal.Questions[0].Question);
        Assert.Equal("it changes what a reviewer's click means", proposal.Questions[0].Because);
    }

    [Fact]
    public void A_wayfinder_verdict_carries_the_map_its_tickets_and_their_blocking_edges()
    {
        var outcome = _reader.Read(Write(
            """
            {
              "schema": "agent-factory/routing@1",
              "verdict": "Wayfinder",
              "because": "the destination is clear and none of the way is",
              "map": {
                "destination": "a schema where one write is one fact",
                "notes": "read the ADR on irreversibility first",
                "notYetSpecified": ["what to do about the audit table"],
                "outOfScope": ["backfilling history"],
                "tickets": [
                  { "title": "Decide the unit of write", "body": "one row or one document?", "blocks": ["Decide the read shape"] },
                  { "title": "Decide the read shape", "body": "what does a reader see mid-write?", "blocks": [] }
                ]
              }
            }
            """));

        var proposal = Assert.IsType<RoutingProposal>(outcome.Proposal);
        var map = Assert.IsType<ProposedMap>(proposal.Map);

        Assert.Equal("a schema where one write is one fact", map.Destination);
        Assert.Equal("read the ADR on irreversibility first", map.Notes);
        Assert.Equal(["what to do about the audit table"], map.NotYetSpecified);
        Assert.Equal(["backfilling history"], map.OutOfScope);

        // Order is the round's, so the host can file blockers first.
        Assert.Equal(["Decide the unit of write", "Decide the read shape"], map.Tickets.Select(t => t.Title));
        Assert.Equal(["Decide the read shape"], map.Tickets[0].Blocks);
        Assert.Empty(map.Tickets[1].Blocks);
    }

    /// <summary>
    /// Every shape of unreadable, and the property they all share: <b>none of them reads as
    /// <c>Build</c></b>. This is the whole reason the reader is written the way it is, so it
    /// is asserted as one fact about many shapes rather than as many facts.
    /// </summary>
    [Theory]
    [InlineData("", "not valid JSON")]
    [InlineData("{ not json", "not valid JSON")]
    [InlineData("[]", "not a record this build can read")]
    [InlineData("""{"schema":"agent-factory/routing@2","verdict":"Build","because":"a future shape"}""", "shape this build does not read")]
    [InlineData("""{"schema":"agent-factory/routing@1","verdict":"Ship","because":"not one of the three"}""", "not one of the three")]
    [InlineData("""{"schema":"agent-factory/routing@1","verdict":"Build"}""", "no reason for it")]
    [InlineData("""{"schema":"agent-factory/routing@1","verdict":"Grill","because":"needs deciding"}""", "asked nothing")]
    [InlineData("""{"schema":"agent-factory/routing@1","verdict":"Wayfinder","because":"no way visible"}""", "charted no map")]
    public void A_verdict_that_cannot_be_read_is_never_read_as_a_build_ticket(string contents, string expected)
    {
        var outcome = _reader.Read(Write(contents));

        Assert.False(outcome.IsReadable);
        Assert.Null(outcome.Proposal);
        Assert.NotNull(outcome.UnreadableBecause);

        // A reason is a sentence a reviewer can read, not a code, and it must never be
        // blank — an unreadable verdict with no stated reason is the silence this whole
        // design is against.
        Assert.Contains(expected, outcome.UnreadableBecause, StringComparison.Ordinal);
    }

    /// <summary>
    /// No file at all is the shape a *build* round always arrives in, and it is the case
    /// most likely to be read as "nothing to do here" rather than as "this round said
    /// nothing". A build round writes no verdict at all, so the two must be tellable apart
    /// from a work item's state and they are: a build round is never routed.
    /// </summary>
    [Fact]
    public void No_verdict_at_all_is_unreadable_rather_than_empty()
    {
        var missing = _reader.Read(Path.Combine(Path.GetTempPath(), $"no-such-routing-{Guid.NewGuid():n}.json"));
        Assert.False(missing.IsReadable);
        Assert.Contains("no routing verdict", missing.UnreadableBecause!, StringComparison.Ordinal);

        var neverWritten = _reader.Read(null);
        Assert.False(neverWritten.IsReadable);
        Assert.Contains("no routing verdict", neverWritten.UnreadableBecause!, StringComparison.Ordinal);

        // An empty *file* is a different fact from no file, and it is read as the malformed
        // thing it is rather than as the absent one. A round that opened the file and wrote
        // nothing into it has told the factory something — that it got as far as the last
        // line and did not finish — and "could not be read" is the honest reading of that.
        var empty = _reader.Read(Write(string.Empty));
        Assert.False(empty.IsReadable);
        Assert.Contains("not valid JSON", empty.UnreadableBecause!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A ticket with no title is dropped rather than filed as an empty one. It is the only
    /// per-element filter in the reader, and it exists because a blocking edge is named by
    /// title: a ticket that cannot be named cannot be wired to anything, so it is not a
    /// ticket the factory could act on.
    /// </summary>
    [Fact]
    public void A_ticket_the_map_cannot_name_is_dropped_rather_than_filed_empty()
    {
        var outcome = _reader.Read(Write(
            """
            {
              "schema": "agent-factory/routing@1",
              "verdict": "Wayfinder",
              "because": "the way is not visible",
              "map": {
                "destination": "somewhere",
                "tickets": [
                  { "title": "", "body": "no name", "blocks": [] },
                  { "title": "Decide the thing", "body": "the real one", "blocks": [""] }
                ]
              }
            }
            """));

        var map = Assert.IsType<ProposedMap>(outcome.Proposal!.Map);
        var ticket = Assert.Single(map.Tickets);
        Assert.Equal("Decide the thing", ticket.Title);

        // A blank blocker is dropped for the same reason: there is no ticket by that name to
        // block on, so the edge is not one the factory could file.
        Assert.Empty(ticket.Blocks);
    }

    private static string Write(string contents)
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-factory-routing-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, "routing.json");
        File.WriteAllText(path, contents);
        return path;
    }
}
