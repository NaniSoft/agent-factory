namespace AgentFactory.Infrastructure.Routing;

using AgentFactory.Application.GitHub;
using AgentFactory.Application.Observability;
using AgentFactory.Application.Routing;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Routing;
using AgentFactory.Domain.WorkItems;
using Microsoft.Extensions.Logging;
using System.Text;

/// <summary>
/// Turns a routing round's proposal into real issues in the project's tracker, and is the
/// only thing that does (ADR-0015).
/// </summary>
/// <remarks>
/// <para>
/// The container proposed and this published, because the host is where every write
/// credential lives and a round holds none (ADR-0006). Splitting it out of the orchestrator
/// is not tidiness: the orchestrator is the whole of the factory's <em>policy</em>, and which
/// issues a map becomes and in what order is not policy — it is the shape of a proposal
/// somebody else wrote. What is policy, and stays in the loop, is that a verdict is recorded
/// only once the questions a human has to answer actually exist.
/// </para>
/// <para>
/// <strong>Every issue is filed under a parent and the blocking edges are wired in a second
/// pass.</strong> The order is forced: a sub-issue has to exist before it can be a child of
/// anything, and a ticket has to exist before another ticket can block on it. So the map is
/// filed, its tickets are filed under it, and only then are the edges wired — and a map with
/// no wired edges is still a map, because the edges are for a reader's benefit rather than
/// the mechanism that decides anything.
/// </para>
/// </remarks>
public sealed class IssuePublisher(
    IGitHub github,
    ILogger<IssuePublisher> logger) : IIssuePublisher
{
    /// <summary>
    /// Files whatever the proposal asks a human to be given, and returns the issue it was
    /// filed as — null for a <see cref="WorkItemKind.Build"/>, which asks for nothing.
    /// </summary>
    /// <remarks>
    /// The parent is closed afterwards, with a comment saying where the work went. That is a
    /// state change on somebody's tracker and it is deliberate: an issue the factory has
    /// decided to decompose is not an issue anybody is going to close by hand, and leaving
    /// it open alongside a child that says the same thing is two open issues claiming the
    /// same work. It is a close rather than a delete and it carries the reason in the issue's
    /// own history, so it is reversible and explained.
    ///
    /// A close that fails does not fail the publication. The children are up, the verdict is
    /// real, and the only thing lost is tidiness — so losing tidiness must not lose the
    /// verdict.
    /// </remarks>
    public async Task<int?> PublishAsync(
        WorkItem workItem,
        RoutingProposal proposal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(proposal);

        // Opened first, before anything can fail, so a publication that is refused is still
        // attributable to the work item it was refused for. This component is handed a work
        // item's identity and writes records about it, and every component that does that
        // opens a scope — the check is `PolicyTests.A_record_about_a_work_item_is_written_
        // inside_a_scope_that_names_it`, and this being on that list rather than exempted
        // from it is the point (ADR-0015).
        using var trace = logger.ForWorkItem(workItem.Id, workItem.Project, workItem.IssueNumber);

        var filed = proposal.Verdict switch
        {
            WorkItemKind.Grill => await FileTheQuestionsAsync(workItem, proposal, cancellationToken)
                .ConfigureAwait(false),
            WorkItemKind.Wayfinder => await FileTheMapAsync(workItem, proposal, cancellationToken)
                .ConfigureAwait(false),
            _ => (int?)null,
        };

        // The maintainer's issue is deliberately left open. For a while this closed it,
        // saying where the work went — and the maintainer read a closed issue over work
        // that had not shipped, which is exactly the claim this factory's rule refuses to
        // make: a merge is what closes an issue, and a Grill has shipped nothing. The
        // questions issue carries the conversation, a human answers it there, and the loop
        // hands the work back when the answer arrives.

        return filed;
    }

    /// <summary>
    /// One question set, as one issue, as a child of the issue it is about. One issue rather
    /// than one per question: a human is answering a set of questions about one thing, and
    /// splitting them across issues would make the answer to each one land somewhere the
    /// others cannot see.
    /// </summary>
    private async Task<int> FileTheQuestionsAsync(
        WorkItem workItem,
        RoutingProposal proposal,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder();

        body.Append("## What is being asked\n\n")
            .Append(proposal.Because).Append("\n\n")
            .AppendLine("The factory asked the agent what kind of work ")
            .Append("#").Append(workItem.IssueNumber).AppendLine(" is, and it came back saying this needs ")
            .AppendLine("deciding before anybody writes code. These are the questions it could not decide alone.")
            .AppendLine()
            .AppendLine("## Questions")
            .AppendLine();

        for (var i = 0; i < proposal.Questions.Count; i++)
        {
            var question = proposal.Questions[i];
            body.Append(i + 1).Append(". **").Append(question.Question).AppendLine("**")
                .AppendLine()
                .Append("   Why it is asked: ").AppendLine(question.Because)
                .AppendLine();
        }

        body.AppendLine("## How to answer")
            .AppendLine()
            .AppendLine("Reply in a comment on this issue. The factory reads this issue's comments and picks the ")
            .AppendLine("work up from what you write there — it does not read your replies anywhere else, and it ")
            .AppendLine("will not guess at an answer it has not been given.")
            .AppendLine()
            .AppendLine("One reply answers every question. If you want to answer them one at a time, say which one you ")
            .AppendLine("are answering and the factory will wait for the rest rather than act on a partial set.");

        var created = await github.CreateIssueAsync(
            new NewIssue(workItem.RepoUrl, workItem.IssueNumber, $"Questions: #{workItem.IssueNumber}", body.ToString()),
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Filed {Questions} questions about {Project}#{Issue} as #{Filed}, for a human to answer.",
            proposal.Questions.Count,
            workItem.Project,
            workItem.IssueNumber,
            created.Number);

        return created.Number;
    }

    /// <summary>
    /// A wayfinder map: one issue for the map itself, one per decision ticket beneath it, and
    /// the blocking edges between them wired once every ticket has a number.
    /// </summary>
    private async Task<int> FileTheMapAsync(
        WorkItem workItem,
        RoutingProposal proposal,
        CancellationToken cancellationToken)
    {
        var map = proposal.Map
            ?? throw new InvalidOperationException("a wayfinder proposal with no map never reaches the publisher");

        var created = await github.CreateIssueAsync(
            new NewIssue(workItem.RepoUrl, workItem.IssueNumber, MapTitleFor(workItem), MapBodyFor(proposal, map)),
            cancellationToken).ConfigureAwait(false);

        var mapNumber = created.Number;
        logger.LogInformation(
            "Charted {Project}#{Issue} as map #{Map} with {Tickets} decision ticket(s).",
            workItem.Project,
            workItem.IssueNumber,
            mapNumber,
            map.Tickets.Count);

        // Filed in the order the round wrote them, which is dependency order — the round was
        // told to put blockers first. The edges are wired afterwards regardless, because the
        // second pass does not trust that and looks each one up by title.
        var filed = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ticket in map.Tickets)
        {
            var child = await github.CreateIssueAsync(
                new NewIssue(workItem.RepoUrl, mapNumber, ticket.Title, TicketBodyFor(ticket)),
                cancellationToken).ConfigureAwait(false);

            filed[ticket.Title] = child.Number;
        }

        await WireTheBlockingEdgesAsync(workItem, map, filed, cancellationToken).ConfigureAwait(false);

        return mapNumber;
    }

    /// <summary>
    /// The edges, in a second pass, by title. A title that names no filed ticket is logged
    /// and skipped rather than refused: the round named a blocker the factory cannot find, and
    /// the correct response to a map with one bad edge is a map with that edge missing, not a
    /// map that was not filed at all.
    /// </summary>
    private async Task WireTheBlockingEdgesAsync(
        WorkItem workItem,
        ProposedMap map,
        IReadOnlyDictionary<string, int> filed,
        CancellationToken cancellationToken)
    {
        foreach (var ticket in map.Tickets)
        {
            foreach (var blocker in ticket.Blocks)
            {
                if (!filed.TryGetValue(blocker, out var blockerNumber))
                {
                    logger.LogWarning(
                        "{Project}#{Issue}: the decision ticket {Ticket} is blocked by {Blocker}, which is not a "
                            + "ticket on the map. That edge has not been filed and the ticket can be worked earlier "
                            + "than the map says.",
                        workItem.Project,
                        workItem.IssueNumber,
                        ticket.Title,
                        blocker);

                    continue;
                }

                try
                {
                    await github.MakeBlockedByAsync(workItem.RepoUrl, filed[ticket.Title], blockerNumber, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (FactoryFailure refused)
                {
                    // One refused edge does not lose the map. The edges are how the frontier
                    // *reads*; nothing here is what stops a ticket being worked, so a
                    // half-wired map is still a map a human can work from.
                    logger.LogWarning(
                        refused,
                        "{Project}#{Issue}: the blocking edge from {Blocker} to {Ticket} was refused. The map stands "
                            + "without that edge.",
                        workItem.Project,
                        workItem.IssueNumber,
                        blocker,
                        ticket.Title);
                }
            }
        }
    }

    private static string MapTitleFor(WorkItem workItem) => $"Map: #{workItem.IssueNumber} {Shorten(workItem.IssueTitle)}";

    /// <summary>
    /// The map as an index, not a store. The decisions live on their own tickets and this
    /// links to them, which is the reason a reader can come here to see what the factory
    /// decided without reading everything it decided it with.
    /// </summary>
    private static string MapBodyFor(RoutingProposal proposal, ProposedMap map)
    {
        var body = new StringBuilder();

        body.Append("## Destination\n\n").AppendLine(map.Destination).AppendLine();

        if (map.Notes.Length > 0)
        {
            body.AppendLine("## Notes").AppendLine().AppendLine(map.Notes).AppendLine();
        }

        if (map.Tickets.Count > 0)
        {
            body.AppendLine("## Decisions to make")
                .AppendLine()
                .AppendLine("One ticket per decision, blocked by the tickets that have to be answered first:")
                .AppendLine();

            foreach (var ticket in map.Tickets)
            {
                var blockedBy = ticket.Blocks.Count == 0
                    ? string.Empty
                    : $" (blocked by {string.Join(", ", ticket.Blocks)})";

                body.Append("- **").Append(ticket.Title).Append("** — ").Append(FirstLine(ticket.Body))
                    .Append(blockedBy)
                    .AppendLine();
            }

            body.AppendLine();
        }

        Section(body, "Not yet specified", map.NotYetSpecified);
        Section(body, "Out of scope", map.OutOfScope);

        body.AppendLine("## Why this exists")
            .AppendLine()
            .AppendLine(proposal.Because);

        return body.ToString();
    }

    private static void Section(StringBuilder body, string heading, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        body.Append("## ").AppendLine(heading).AppendLine();
        foreach (var line in lines)
        {
            body.Append("- ").AppendLine(line);
        }

        body.AppendLine();
    }

    private static string TicketBodyFor(ProposedTicket ticket)
    {
        var body = new StringBuilder();

        body.AppendLine("## Question").AppendLine().AppendLine(ticket.Body).AppendLine();

        if (ticket.Blocks.Count > 0)
        {
            body.AppendLine("## Blocked by").AppendLine();
            foreach (var blocker in ticket.Blocks)
            {
                body.Append("- ").AppendLine(blocker);
            }

            body.AppendLine();
        }

        body.AppendLine("## How to answer")
            .AppendLine()
            .AppendLine("Reply in a comment on this issue. The factory reads its comments and continues from there.")
            .AppendLine()
            .AppendLine("Closing this issue is how a decision is recorded: the answer goes in the closing comment, and ")
            .AppendLine("the factory picks the next ticket up. A ticket the factory closes on its own has been ")
            .AppendLine("answered from what it could read, and the comment says so.");

        return body.ToString();
    }

    private static string FirstLine(string body) =>
        body.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    /// <summary>
    /// An issue title is a GitHub ref, a PR body and a list item, and this factory puts an
    /// issue's own words in all three — so the prefix that names the parent is kept short
    /// enough that the title stays readable, rather than being the whole of it.
    /// </summary>
    private static string Shorten(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length <= 60 ? trimmed : string.Concat(trimmed.AsSpan(0, 57), "...");
    }
}