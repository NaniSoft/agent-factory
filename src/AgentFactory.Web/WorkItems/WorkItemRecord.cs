namespace AgentFactory.WorkItems;

/// <summary>
/// What intake did with an issue: the work item for it, and whether this call made that
/// work item or found the one an earlier poll already made. Nothing else distinguishes
/// the two, because nothing else should — a re-poll is a no-op, not an event.
/// </summary>
public sealed record WorkItemRecord(WorkItem WorkItem, bool Created);
