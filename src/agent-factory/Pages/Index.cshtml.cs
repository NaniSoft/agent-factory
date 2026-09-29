using AgentFactory.Loop;
using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentFactory.Pages;

/// <summary>
/// The board. Server-rendered, and the only write path the factory has: the one form it
/// renders, on every work item a reviewer can still decide, is the only place in the whole
/// process where a human can change anything. It holds no policy of its own — it records
/// what a reviewer decided and asks the loop what that means, so the swimlane a work item
/// ends up in is the loop's answer and not the page's.
/// </summary>
public class IndexModel : PageModel
{
    private readonly IWorkItemStore _store;
    private readonly Orchestrator _loop;
    private readonly ProjectLoadReport _projects;

    public IndexModel(IWorkItemStore store, Orchestrator loop, ProjectLoadReport projects)
    {
        _store = store;
        _loop = loop;
        _projects = projects;
    }

    /// <summary>The set the factory is actually serving: exactly the files that validated.</summary>
    public IReadOnlyList<Project> Projects => _projects.Projects;

    /// <summary>The files that were refused, and why. A refusal is reported, not swallowed.</summary>
    public IReadOnlyList<ProjectFileRejection> Rejections => _projects.Rejections;

    /// <summary>
    /// The work items on the board, narrowed to one project when the reviewer has asked
    /// to see one. Everything else is untouched by the filter: a filter is a way of
    /// looking, not a policy, and the decisions a reviewer can make are the same three
    /// whatever is on the page (ADR-0005).
    /// </summary>
    public IReadOnlyList<WorkItem> WorkItems => Project is { Length: > 0 } project
        ? _store.List().Where(workItem => workItem.Project == project).ToList()
        : _store.List();

    /// <summary>
    /// The projects the reviewer can narrow the board to: the ones being served, plus any
    /// that have work items left over from a project file that has since been removed. A
    /// project the factory has stopped serving still has a history a reviewer is judging,
    /// and hiding it behind a filter that cannot be selected would lose it.
    /// </summary>
    public IReadOnlyList<string> ProjectsOnTheBoard => _projects.Projects
        .Select(project => project.Name)
        .Concat(_store.List().Select(workItem => workItem.Project))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The project the board is narrowed to, or empty for all of them. A name that is not
    /// one being served and has no work items of its own is not refused and not guessed at:
    /// it narrows the board to nothing, which is what asking to see nothing shows, and the
    /// filter itself still renders every project so the reviewer can pick another.
    /// </summary>
    public string? Project { get; private set; }

    /// <summary>
    /// How many worker containers the factory is inside, out of the budget. Said on the
    /// board because a reviewer watching a work item sit in Frontier is asking whether the
    /// factory is working or wedged, and the budget is the answer — a bounded machine that
    /// renders nothing about its bound reads as a stuck one.
    /// </summary>
    public int RoundsInFlight => _loop.RoundsInFlight;

    /// <summary>
    /// Every round a work item has run, oldest first. A reviewer in Review judges what
    /// the rounds produced, and past disagreements with earlier ones, so the board shows
    /// the whole record and not the latest round alone.
    /// </summary>
    public IReadOnlyList<RoundResultRecord> RoundsOf(Guid workItemId) => _store.Rounds(workItemId);

    /// <summary>
    /// Every decision the work item has been given, oldest first, in the reviewer's own
    /// words. Rendered in every lane rather than only in Review, because a work item that
    /// has been rejected should say so and say why, and nothing ends in silence.
    /// </summary>
    public IReadOnlyList<DecisionRecord> DecisionsOf(Guid workItemId) => _store.Decisions(workItemId);

    /// <summary>
    /// The decisions offered on a work item in a given lane, read from the set rather
    /// than written out in the view, so the board's buttons and the factory's decisions
    /// cannot drift apart. Review offers all three; a parked work item offers the two
    /// that finish it, and nothing else in the factory offers a reviewer anything at all.
    /// </summary>
    public IReadOnlyList<Decision> DecisionsOffered(Swimlane swimlane) => Decisions.OfferedIn(swimlane);

    /// <summary>
    /// Why this work item is where it is, in a reviewer's words, for the lanes that are
    /// an ending rather than a stage: Done, Escalated, Rejected. Empty elsewhere, because
    /// a work item in Backlog has no cause yet.
    /// </summary>
    public string CauseOf(WorkItem workItem) => HowItEnded.Describe(workItem, RoundsOf(workItem.Id), DecisionsOf(workItem.Id));

    /// <summary>
    /// When a work item waiting in Review will be merged without one, or empty if it is
    /// not waiting on a reviewer at all. Rendered on the card rather than only in the
    /// header, because a timeout the reviewer cannot see per work item is a timeout they
    /// cannot act on while it still matters (ADR-0008).
    /// </summary>
    public DateTimeOffset? AutoMergeAt(WorkItem workItem) =>
        workItem.Swimlane == Swimlane.Review && workItem.ReviewStartedUtc is { } since
            ? since + FactoryConstants.FeedbackThreshold
            : null;

    /// <summary>
    /// Why a decision was not made, when one was refused. The board says so on the board:
    /// a reviewer who is told nothing concludes the click was lost.
    /// </summary>
    public string? Refusal { get; private set; }

    public void OnGet(string? project)
    {
        // A query string and a read. It is a way of looking at the board rather than a way
        // of changing it: no work item moves, no decision is recorded, and the reviewer's
        // three decisions are offered and refused on exactly the same terms whatever the
        // filter is (ADR-0005, ADR-0008). Nothing else on the board is a state, and a
        // filter that were one would be a fourth decision.
        Project = project?.Trim();
    }

    /// <summary>
    /// The board's only write. A reviewer posts one of the three decisions, the store
    /// keeps it, and the loop applies it.
    /// </summary>
    public async Task<IActionResult> OnPostDecision(string? workItem, string? decision, string? feedback, string? project)
    {
        // Kept so that a refusal renders the board the reviewer was actually looking at
        // rather than the unfiltered one. It is a read of the request and nothing more: a
        // post cannot change what a later read shows about which projects exist.
        Project = project?.Trim();

        // What a button sent. Anything that is not one of the three is not a decision,
        // and this endpoint has no other thing it can be asked to do — a form posted by
        // hand is refused, not interpreted. The message is kept in the words a reviewer
        // has already read; DecisionTests asserts them exactly, and there is nothing about
        // parking that makes a fourth decision worth naming here.
        if (!Guid.TryParse(workItem, out var id) || !Decisions.TryParse(decision, out var made))
        {
            Refusal = "That is not one of the three decisions. A work item in Review is "
                + "approved, sent back for changes, or rejected, and there is nothing else to decide.";
            return Page();
        }

        try
        {
            _store.RecordDecision(id, made, feedback);
        }
        catch (Exception refused) when (refused is KeyNotFoundException or InvalidOperationException)
        {
            // The store's objection is the reviewer's answer, in the store's words: it is
            // the component that knows which swimlane the work item is in and what it made
            // of the feedback. Anything else is a fault rather than a refusal, and is left
            // to fail rather than dressed up as a decision that was declined.
            Refusal = refused.Message;
            return Page();
        }

        // The decision is recorded and the swimlane it means is the loop's, so the board
        // asks the loop rather than moving a work item itself. One step applies one
        // transition, and a decision comes before everything else a step could apply, so
        // this step is the decision and nothing else. A reviewer's click is a step of its
        // own and always was; what is different now is that the heartbeat steps the loop
        // between decisions too, so a click is a second driver rather than the only one.
        var step = await _loop.StepAsync();

        // A decision the loop could not carry out is said here, on the response the
        // reviewer is holding, for the same reason the store's refusals are: reading the
        // board again would find a clean page and lose it. An approval whose merge did not
        // land is the case — the decision is on the record, the work item is parked, and a
        // reviewer who were told nothing would conclude the click was lost rather than that
        // nothing shipped. What the loop says is what the reviewer is told; the page does
        // not decide for itself that a decision failed.
        if (step.Refusal is { Length: > 0 } refusal)
        {
            Refusal = refusal;
            return Page();
        }

        // Post, redirect, get: a reviewer who refreshes after deciding has not decided
        // a second time. The filter rides along so that a reviewer judging one project
        // stays on it rather than being dropped onto the whole board by their own click.
        return LocalRedirect(Project is { Length: > 0 } narrowed
            ? $"/?project={Uri.EscapeDataString(narrowed)}"
            : "/");
    }
}
