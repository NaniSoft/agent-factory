namespace AgentFactory.WorkItems;

/// <summary>
/// The store of record. Work items are persisted, so a restart does not discard an
/// agent's work. One writer, in one process, against one SQLite file (ADR-0009).
/// </summary>
public interface IWorkItemStore
{
    /// <summary>Records a new work item. Intake is the only thing that calls this.</summary>
    WorkItem Create(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string baseBranch,
        Swimlane swimlane = Swimlane.Backlog);

    WorkItem? Get(Guid id);

    /// <summary>Every work item, oldest first.</summary>
    IReadOnlyList<WorkItem> List();
}
