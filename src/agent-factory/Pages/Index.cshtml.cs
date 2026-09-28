using AgentFactory.Projects;
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

    public void OnGet()
    {
    }
}
