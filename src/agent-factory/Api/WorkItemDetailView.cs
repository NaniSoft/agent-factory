namespace AgentFactory.Api;

using System.Text.Json.Serialization;
using AgentFactory.Failures;
using AgentFactory.Pages;
using AgentFactory.Results;
using AgentFactory.Rounds;
using AgentFactory.WorkItems;

/// <summary>
/// What <c>GET /api/work-items/{id}</c> returns: one work item's whole record, serialised,
/// and nothing the factory would not say about it. The work item's header, every round it
/// has run (its outcome, failure, payload, agent note, log and the host's diff), the
/// decisions a reviewer made about it and the decisions still offered, and how it ended —
/// the factory's judgement, shaped so the renderer re-decides nothing.
/// </summary>
/// <remarks>
/// <para>
/// This is the detail behind the work-item card on the board, and it is the same judgement
/// the board itself renders: the diff's four states come from
/// <see cref="HowToReadTheDiff"/>, the ending from <see cref="HowItEnded.Describe"/>, and
/// the offered decisions from <see cref="WorkItems.Decisions.OfferedIn"/>. The endpoint
/// that produces it holds no policy, calls no seam and moves nothing.
/// </para>
/// <para>
/// The property names are pinned with <see cref="JsonPropertyNameAttribute"/> rather than
/// left to a naming policy, because the JSON is a contract with a separate app: a web
/// default that quietly changed casing would break the renderer without failing a build.
/// They are camelCase so the TypeScript view model and these records agree by name.
/// </para>
/// </remarks>
public sealed record WorkItemDetailView(
    [property: JsonPropertyName("workItem")] WorkItemHeaderView WorkItem,
    [property: JsonPropertyName("rounds")] IReadOnlyList<RoundOnTheBoardView> Rounds,
    [property: JsonPropertyName("decisions")] IReadOnlyList<DecisionMadeView> Decisions,
    [property: JsonPropertyName("offeredDecisions")] IReadOnlyList<string> OfferedDecisions,
    [property: JsonPropertyName("ending")] string Ending)
{
    /// <summary>
    /// One work item's whole record, read from the store's own rows and the factory's own
    /// judgement. It re-decides nothing: every sentence and every state slug here is the
    /// judgement class's answer.
    /// </summary>
    public static WorkItemDetailView Of(WorkItem workItem, IWorkItemStore store)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(store);

        var rounds = store.Rounds(workItem.Id);
        var decisions = store.Decisions(workItem.Id);

        return new WorkItemDetailView(
            WorkItemHeaderView.Of(workItem),
            [.. rounds.Select(RoundOnTheBoardView.Of)],
            [.. decisions.Select(DecisionMadeView.Of)],
            [.. WorkItems.Decisions.OfferedIn(workItem.Swimlane).Select(WorkItems.Decisions.Slug)],
            HowItEnded.Describe(workItem, rounds, decisions));
    }
}

/// <summary>
/// The work item's own header: what it is, whose it is, where it sits and how much of the
/// round ceiling it has spent. Every field is the store's own record, so the renderer draws
/// the header and decides nothing.
/// </summary>
public sealed record WorkItemHeaderView(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("repoUrl")] string RepoUrl,
    [property: JsonPropertyName("issueNumber")] int IssueNumber,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("baseBranch")] string BaseBranch,
    [property: JsonPropertyName("lane")] string Lane,
    [property: JsonPropertyName("laneLabel")] string LaneLabel,
    [property: JsonPropertyName("roundCount")] int RoundCount,
    [property: JsonPropertyName("roundCeiling")] int RoundCeiling,
    [property: JsonPropertyName("createdUtc")] string CreatedUtc,
    [property: JsonPropertyName("updatedUtc")] string UpdatedUtc,
    [property: JsonPropertyName("reviewStartedUtc")] string? ReviewStartedUtc,
    [property: JsonPropertyName("mergeAttempts")] int MergeAttempts)
{
    public static WorkItemHeaderView Of(WorkItem workItem) => new(
        workItem.Id.ToString(),
        workItem.Project,
        workItem.RepoUrl,
        workItem.IssueNumber,
        workItem.IssueTitle,
        workItem.IssueBody,
        workItem.BaseBranch,
        workItem.Swimlane.ToString(),
        Swimlanes.Label(workItem.Swimlane),
        workItem.RoundCount,
        FactoryConstants.RoundCeiling,
        workItem.CreatedUtc.ToString("O"),
        workItem.UpdatedUtc.ToString("O"),
        workItem.ReviewStartedUtc?.ToString("O"),
        workItem.MergeAttempts);
}

/// <summary>
/// One round as the detail renders it: its number, its outcome and failure, how many times
/// the factory asked for it, when it ran, the payload the container recorded, the agent's
/// own note, the round's log, and its change as the host generated it.
/// </summary>
/// <remarks>
/// The payload and the diff are both here and neither replaces the other: the payload is
/// the container's account of itself and can be missing or bounded, while the diff is
/// <c>git diff</c> against the tree the round left and exists even for a round that wrote
/// no readable result — see <see cref="RoundResultRecord"/>.
/// </remarks>
public sealed record RoundOnTheBoardView(
    [property: JsonPropertyName("roundNumber")] int RoundNumber,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("attempts")] int Attempts,
    [property: JsonPropertyName("startedUtc")] string StartedUtc,
    [property: JsonPropertyName("completedUtc")] string CompletedUtc,
    [property: JsonPropertyName("payload")] string? Payload,
    [property: JsonPropertyName("agentNote")] string? AgentNote,
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("diff")] DiffOnTheBoardView? Diff)
{
    public static RoundOnTheBoardView Of(RoundResultRecord round)
    {
        ArgumentNullException.ThrowIfNull(round);

        var diff = HowToReadTheDiff.Of(round);

        return new RoundOnTheBoardView(
            round.RoundNumber,
            round.Outcome.ToString(),
            round.Failure?.ToString(),
            round.Attempts,
            round.StartedUtc.ToString("O"),
            round.CompletedUtc.ToString("O"),
            round.ResultPayload,
            round.AgentNote,
            round.Log,
            diff is null ? null : DiffOnTheBoardView.Of(diff));
    }
}

/// <summary>
/// One round's change, as the board shows it and as this surface serialises it: the state,
/// the files with git's own text for each, the count of what was left off, the tree the
/// change can be read from, and the two judgement sentences. It is
/// <see cref="DiffOnTheBoard"/> serialised, and the renderer re-decides nothing about it.
/// </summary>
/// <param name="State">
/// One of <c>shown</c>, <c>empty</c>, <c>unfinished</c> or <c>unavailable</c>. The four are
/// different claims and the renderer must tell them apart: <c>empty</c> is a round that ran
/// to completion and changed nothing, <c>unfinished</c> is a round that did not run to
/// completion, and <c>unavailable</c> is a diff that could not be generated at all.
/// </param>
/// <param name="SaysAboutAnUnchangedDisk">
/// The judgement's own sentence for an <c>empty</c> or <c>unfinished</c> state, or null for
/// a diff there is something to read. Serialised so the renderer writes no sentence of its
/// own about why a disk is unchanged.
/// </param>
/// <param name="WhatIsLeftOff">
/// What a bounded diff left off the page and where the rest is, or null when it is whole.
/// </param>
public sealed record DiffOnTheBoardView(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("files")] IReadOnlyList<DiffFileView> Files,
    [property: JsonPropertyName("omittedFiles")] int OmittedFiles,
    [property: JsonPropertyName("omittedLines")] int OmittedLines,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("totalLines")] int TotalLines,
    [property: JsonPropertyName("tree")] string Tree,
    [property: JsonPropertyName("unavailableBecause")] string? UnavailableBecause,
    [property: JsonPropertyName("boundedElsewhere")] string? BoundedElsewhere,
    [property: JsonPropertyName("saysAboutAnUnchangedDisk")] string? SaysAboutAnUnchangedDisk,
    [property: JsonPropertyName("whatIsLeftOff")] string? WhatIsLeftOff)
{
    public static DiffOnTheBoardView Of(DiffOnTheBoard diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        return new DiffOnTheBoardView(
            diff.State.ToString().ToLowerInvariant(),
            [.. diff.Files.Select((file, index) => DiffFileView.Of(file, diff.IsOpenAt(index)))],
            diff.OmittedFiles,
            diff.OmittedLines,
            diff.TotalFiles,
            diff.TotalLines,
            diff.Tree,
            diff.UnavailableBecause,
            diff.BoundedElsewhere,
            HowToReadTheDiff.SaysAboutAnUnchangedDisk(diff),
            HowToReadTheDiff.WhatIsLeftOff(diff));
    }
}

/// <summary>One file of a diff: its path, what git says happened, the line counts, and git's own text.</summary>
/// <param name="Open">
/// Whether the section starts open. The first file is open and the rest are not: the shape
/// of the change and the first file's actual content at a glance, and every file is on the
/// page either way. See <see cref="DiffOnTheBoard.IsOpenAt"/>.
/// </param>
public sealed record DiffFileView(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("previousPath")] string? PreviousPath,
    [property: JsonPropertyName("change")] string Change,
    [property: JsonPropertyName("added")] int Added,
    [property: JsonPropertyName("removed")] int Removed,
    [property: JsonPropertyName("binary")] bool Binary,
    [property: JsonPropertyName("open")] bool Open,
    [property: JsonPropertyName("text")] string Text)
{
    public static DiffFileView Of(DiffFile file, bool open)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new DiffFileView(
            file.Path,
            file.PreviousPath,
            file.Change.ToString().ToLowerInvariant(),
            file.Added,
            file.Removed,
            file.Binary,
            open,
            file.Text);
    }
}

/// <summary>
/// One decision the reviewer made: which of the three it was, the words they wrote, when
/// they wrote them, and the lane the loop put the work item in to apply it — null until the
/// loop has. It is <see cref="DecisionRecord"/> serialised.
/// </summary>
public sealed record DecisionMadeView(
    [property: JsonPropertyName("sequence")] int Sequence,
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("feedback")] string Feedback,
    [property: JsonPropertyName("decidedUtc")] string DecidedUtc,
    [property: JsonPropertyName("appliedTo")] string? AppliedTo,
    [property: JsonPropertyName("appliedToLabel")] string? AppliedToLabel)
{
    public static DecisionMadeView Of(DecisionRecord decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new DecisionMadeView(
            decision.Sequence,
            Decisions.Slug(decision.Decision),
            decision.Feedback,
            decision.DecidedUtc.ToString("O"),
            decision.AppliedTo?.ToString(),
            decision.AppliedTo is { } lane ? Swimlanes.Label(lane) : null);
    }
}
