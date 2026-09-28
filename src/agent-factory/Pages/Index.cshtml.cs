using AgentFactory.Loop;
using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentFactory.Pages;

/// <summary>
/// The board. Server-rendered, and the only write path the factory has: the one form it
/// renders, in Review, is the only place in the whole process where a human can change
/// anything. It holds no policy of its own — it records what a reviewer decided and asks
/// the loop what that means, so the swimlane a work item ends up in is the loop's answer
/// and not the page's.
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

    public IReadOnlyList<WorkItem> WorkItems => _store.List();

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
    /// The decisions offered on a work item in Review. Read from the set rather than
    /// written out in the view, so that the board's three buttons and the factory's three
    /// decisions cannot drift apart.
    /// </summary>
    public IReadOnlyList<Decision> DecisionsOffered() => Decisions.All;

    /// <summary>
    /// Why a decision was not made, when one was refused. The board says so on the board:
    /// a reviewer who is told nothing concludes the click was lost.
    /// </summary>
    public string? Refusal { get; private set; }

    public void OnGet()
    {
    }

    /// <summary>
    /// The board's only write. A reviewer posts one of the three decisions, the store
    /// keeps it, and the loop applies it.
    /// </summary>
    public async Task<IActionResult> OnPostDecision(string? workItem, string? decision, string? feedback)
    {
        // What a button sent. Anything that is not one of the three is not a decision,
        // and this endpoint has no other thing it can be asked to do — a form posted by
        // hand is refused, not interpreted.
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
        // this step is the decision and nothing else. The heartbeat that steps the loop
        // between decisions is the real agent's ticket; a reviewer's own click is a
        // heartbeat too, and the only one there is today.
        var step = await _loop.StepAsync();

        // A decision the loop could not carry out is said here, on the response the
        // reviewer is holding, for the same reason the store's refusals are: reading the
        // board again would find a clean page and lose it. An approval whose merge did not
        // land is the case — the decision is on the record and the work item is still in
        // Review, and a reviewer who were told nothing would conclude the click was lost
        // rather than that nothing shipped. What the loop says is what the reviewer is
        // told; the page does not decide for itself that a decision failed.
        if (step.Refusal is { Length: > 0 } refusal)
        {
            Refusal = refusal;
            return Page();
        }

        // Post, redirect, get: a reviewer who refreshes after deciding has not decided
        // a second time.
        return LocalRedirect("/");
    }
}
