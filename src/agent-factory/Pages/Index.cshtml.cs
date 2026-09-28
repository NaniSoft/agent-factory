using AgentFactory.Projects;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AgentFactory.Pages;

/// <summary>
/// The board. Server-rendered, read-only for now: its only write path is the
/// reviewer's three decisions, and those arrive with the decisions ticket.
/// </summary>
public class IndexModel : PageModel
{
    private readonly IWorkItemStore _store;
    private readonly ProjectLoadReport _projects;

    public IndexModel(IWorkItemStore store, ProjectLoadReport projects)
    {
        _store = store;
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

    public void OnGet()
    {
    }
}
