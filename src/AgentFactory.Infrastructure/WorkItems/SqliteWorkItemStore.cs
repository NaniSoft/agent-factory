namespace AgentFactory.Infrastructure.WorkItems;

using AgentFactory.Application.Clock;
using AgentFactory.Application.WorkItems;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.WorkItems;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Work items in SQLite, the store of record. One writer, one process, one file
///: losing a work item on restart would mean losing an agent's work.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It is an adapter over EF Core, and it lives in `Infrastructure` because that is
/// what an adapter is.</strong> `IWorkItemStore` is the port and stays in `Application`;
/// the `DbContext`, the row types and the baseline migration are this project's, and no EF
/// type is named anywhere the port can see (#62, #77). The class keeps the word *SQLite*
/// because the word is about the record rather than the mechanism — ADR-0009 decides that
/// the store of record is one SQLite file, and a name saying so is the name of the
/// decision; *EF* would name the library, which is the part free to change.
/// </para>
/// <para>
/// <strong>Every write is narrow and every read is a projection.</strong> The single-column
/// writes are `ExecuteUpdate`, which goes straight to the database and never materialises
/// an entity, so the immutability of the port's positional records is not a property this
/// code has to defend — there is nothing tracked that could be mutated. The compound writes
/// (`RecordRound`, `RecordDecision`, `ApplyDecision`) keep the transaction they always had,
/// for the same reason: two rows that must move together.
/// </para>
/// <para>
/// **A context is made per call and thrown away.** That is what makes the failed insert
/// safe: EF's change tracker would otherwise hold a half-inserted row in `Added` state and
/// hand it to the next `SaveChanges` on the same context. It costs nothing, because the
/// provider pools connections.
/// </para>
/// <para>
/// **The schema is migrated at start, and the migration is committed.** `data/agent-factory.db`
/// is deliberately not migrated — the map decided a fresh database rebuilt from GitHub is
/// accepted — so the baseline is what a new file is built from and what every future
/// migration is a change against. EF also turns WAL on, which the hand-written store never
/// had, so a Board read can no longer block behind a loop write.
/// </para>
/// </remarks>
public sealed class SqliteWorkItemStore : IWorkItemStore
{
    private readonly DbContextOptions<FactoryDbContext> _options;
    private readonly IClock _clock;

    public SqliteWorkItemStore(string databasePath, IClock clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        _options = new DbContextOptionsBuilder<FactoryDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString())
            .Options;

        using var db = Open();
        db.Database.Migrate();
    }

    public WorkItemRecord Intake(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string issueBody,
        string baseBranch)
    {
        try
        {
            return new WorkItemRecord(
                Create(project, repoUrl, issueNumber, issueTitle, issueBody, baseBranch),
                Created: true);
        }
        catch (DbUpdateException refused) when (refused.InnerException is SqliteException constraint
            && constraint.SqliteErrorCode == SqliteConstraint)
        {
            // The unique index on (repo_url, issue_number) is what notices the duplicate.
            // It decides inside the write rather than in a check made before it, so
            // nothing can slip a second row in between the two. The work item that is
            // already there is returned as it stands: re-polling an issue is not a reason
            // to change the record of one, and a work item three rounds deep must not
            // lose that because its issue was still open.
            //
            // EF wraps the provider's refusal in a `DbUpdateException`, which is the one
            // shape this catch had to change to. `SqliteException` is still what decides
            // it: the code is the constraint's, not the wrapper's.
            var existing = Find(repoUrl, issueNumber);
            if (existing is null)
            {
                // A constraint that is not the duplicate we expected — a not-null, which
                // would mean the row is genuinely malformed — leaves nothing to find, and
                // is rethrown rather than reported as an issue already taken.
                throw;
            }

            return new WorkItemRecord(existing, Created: false);
        }
    }

    private WorkItem Create(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string issueBody,
        string baseBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);

        if (issueNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(issueNumber), issueNumber, "an issue number is positive");
        }

        var now = _clock.UtcNow;
        var workItem = new WorkItem(
            Guid.NewGuid(),
            project,
            repoUrl,
            issueNumber,
            issueTitle ?? string.Empty,
            issueBody ?? string.Empty,
            baseBranch,
            Swimlane.Backlog,
            RoundCount: 0,
            CreatedUtc: now,
            UpdatedUtc: now,
            ReviewStartedUtc: null);

        using var db = Open();
        db.WorkItems.Add(workItem.ToRow());
        db.SaveChanges();

        return workItem;
    }

    /// <summary>The work item for this issue, if the store already has one.</summary>
    private WorkItem? Find(string repoUrl, int issueNumber)
    {
        using var db = Open();
        return db.WorkItems
            .AsNoTracking()
            .SingleOrDefault(item => item.RepoUrl == repoUrl && item.IssueNumber == issueNumber)
            ?.ToWorkItem();
    }

    public WorkItem? Get(Guid id)
    {
        using var db = Open();
        return db.WorkItems.AsNoTracking().SingleOrDefault(item => item.Id == id)?.ToWorkItem();
    }

    public IReadOnlyList<WorkItem> List()
    {
        using var db = Open();

        // The order is the Board's and it is done by the database — `created_utc` is one of
        // the two columns carrying a tick converter precisely so that it can be. Only the
        // mapping to the port's record happens here.
        return db.WorkItems
            .AsNoTracking()
            .OrderBy(item => item.CreatedUtc)
            .ThenBy(item => item.IssueNumber)
            .AsEnumerable()
            .Select(item => item.ToWorkItem())
            .ToList();
    }

    public void Move(Guid id, Swimlane swimlane)
    {
        using var db = Open();
        var now = _clock.UtcNow;

        // The review's start is part of the move rather than a second write beside it, so a
        // work item cannot end up in Review with no review to measure the feedback threshold
        // from. The value is decided out here rather than inside the expression, because a
        // conditional over a captured swimlane is a `CASE` the provider has no reason to
        // translate.
        DateTimeOffset? reviewStarted = swimlane == Swimlane.Review ? now : null;

        var moved = db.WorkItems
            .Where(item => item.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.Swimlane, swimlane)
                .SetProperty(item => item.UpdatedUtc, now)
                .SetProperty(item => item.ReviewStartedUtc, reviewStarted));

        if (moved == 0)
        {
            throw new KeyNotFoundException($"no work item {id} to move to {swimlane}");
        }
    }

    public void RecordRoute(Guid workItemId, WorkItemKind kind, string reason, int? routingIssue = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(routingIssue ?? 1);

        // The reason is required whatever the verdict, and that is the whole of the reason
        // it is required: it is what a reviewer reads on the card, and a verdict with no
        // words under it is one nobody can check or disagree with.
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                "a routing round's answer needs its own reason: it is what a reviewer reads on the card, "
                    + "and an answer nobody can check is not worth recording");
        }

        if (routingIssue is { } and not > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(routingIssue), routingIssue, "an issue number is positive");
        }

        // `Unrouted` here is not "nobody has looked yet" — it is "a round looked and could
        // not answer", and it is a recording rather than a verdict. It is allowed for exactly
        // one reason: refusing it would throw away the only account of why a work item is
        // parked, and a parked work item with no stated reason is the silence the loop's whole
        // design is against.
        //
        // It cannot be confused with the other reading of `Unrouted`, because the two are in
        // different lanes. An `Unrouted` work item in Backlog has not been routed yet; an
        // `Unrouted` work item in Escalated had a round try and could not answer. Both are on
        // the card and they are not the same fact.
        if (kind == WorkItemKind.Unrouted && routingIssue is not null)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "a round that gave no verdict has nothing to file, so it cannot have an issue");
        }

        if (kind is WorkItemKind.Grill or WorkItemKind.Wayfinder && routingIssue is null)
        {
            throw new InvalidOperationException(
                $"{kind} waits on a person, and a person answers on an issue. Without one the work item "
                    + "would sit in Waiting for a question that was never asked anywhere.");
        }

        // The lane is the verdict's own, derived here rather than passed in — and this is the
        // one place all three answers are written down.
        //
        //   Build      → Backlog. Still a ticket waiting for the reviewer's click it came in
        //                 for (#40, ADR-0007). Routing is not the gate and does not become one.
        //   Grill,
        //   Wayfinder  → Waiting. Nothing a container can build, everything a person has to answer.
        //   Unrouted   → Escalated. A round ran and could not answer, which is a failure and
        //                 parks rather than being retried into the same silence.
        //
        // The move first, because it is the only writer of a lane and the review clock is part
        // of a move; the verdict follows it, and the two are the same fact recorded once each.
        var lane = kind switch
        {
            WorkItemKind.Build => Swimlane.Backlog,
            WorkItemKind.Unrouted => Swimlane.Escalated,
            _ => Swimlane.Waiting,
        };

        Move(workItemId, lane);

        var now = _clock.UtcNow;

        using var db = Open();
        var recorded = db.WorkItems
            .Where(item => item.Id == workItemId)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.Kind, kind)
                .SetProperty(item => item.RouteReason, reason.Trim())
                .SetProperty(item => item.RoutingIssue, routingIssue)
                .SetProperty(item => item.UpdatedUtc, now));

        if (recorded == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to record a route against");
        }
    }

    /// <inheritdoc />
    public void RecordHandBack(Guid workItemId)
    {
        var item = Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId} to hand back");

        // Only a work item waiting on its questions issue can be handed back, and the
        // guard is the fact rather than the formality: a Backlog item is already in the
        // pipeline, a Done item is shipped, and a rejected one is final. The questions
        // issue is what makes the answers findable — without one there is nothing whose
        // conversation could have been answered.
        if (item.Swimlane != Swimlane.Waiting || item.RoutingIssue is not { } question)
        {
            throw new InvalidOperationException(
                $"work item {workItemId} is not waiting on a questions issue, so there is nothing to hand back: "
                    + "a hand-back is what an answered Grill earns, and nothing else");
        }

        // Back to where a routing round finds it: Backlog, unrouted, with a reason that
        // says why. The questions issue is deliberately kept rather than cleared, because
        // it is where the answers live — the next routing round reads the whole
        // conversation, question and answers, and it can only do that if the record still
        // points at it. An Unrouted work item in Backlog carrying its questions issue is
        // therefore a handed-back item: the third reading of Unrouted, next to "nobody has
        // looked yet" and "a round looked and could not answer", and the route reason is
        // what tells them apart on the card.
        Move(workItemId, Swimlane.Backlog);

        var now = _clock.UtcNow;

        using var db = Open();
        var handedBack = db.WorkItems
            .Where(item => item.Id == workItemId)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.Kind, WorkItemKind.Unrouted)
                .SetProperty(
                    item => item.RouteReason,
                    $"The answers arrived in the conversation on #{question}; the conversation is being read whole.")
                .SetProperty(item => item.UpdatedUtc, now));

        if (handedBack == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to hand back");
        }
    }

    /// <inheritdoc />
    public void RecordPendingRound(PendingRound pending)
    {
        using var db = Open();
        db.PendingRounds.Add(pending.ToRow());
        db.SaveChanges();
    }

    /// <inheritdoc />
    public void ClearPendingRound(Guid workItemId)
    {
        using var db = Open();
        db.PendingRounds.Where(pending => pending.WorkItemId == workItemId).ExecuteDelete();
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingRound> ListPendingRounds()
    {
        using var db = Open();

        // Ordered by the database, on the other column carrying a tick converter: a round
        // asked for oldest first is the order reconciliation takes them in.
        return db.PendingRounds
            .AsNoTracking()
            .OrderBy(pending => pending.AskedUtc)
            .AsEnumerable()
            .Select(pending => pending.ToPendingRound())
            .ToList();
    }

    /// <inheritdoc />
    public void SetPendingRoundRetryAfter(Guid workItemId, DateTimeOffset retryAfterUtc)
    {
        using var db = Open();
        db.PendingRounds
            .Where(pending => pending.WorkItemId == workItemId)
            .ExecuteUpdate(set => set.SetProperty(pending => pending.RetryAfterUtc, (DateTimeOffset?)retryAfterUtc));
    }

    public RoundResultRecord RecordRound(
        Guid workItemId,
        RoundOutcome outcome,
        string? resultPayload,
        string? agentNote,
        DateTimeOffset startedUtc,
        int attempts = 1,
        FailureClass? failure = null,
        string? log = null,
        HostDiff? diff = null)
    {
        var now = _clock.UtcNow;

        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        // The round's number is the work item's count of rounds, plus this one. Read
        // inside the transaction so two rounds can never be recorded as the same round.
        // No row means the work item is not there, which is a caller's mistake and not a
        // round that produced nothing.
        var previous = db.WorkItems
            .Where(item => item.Id == workItemId)
            .Select(item => (int?)item.RoundCount)
            .FirstOrDefault()
            ?? throw new KeyNotFoundException($"no work item {workItemId} to record a round against");

        var roundNumber = previous + 1;
        var recorded = new RoundResultRecord(
            workItemId,
            roundNumber,
            outcome,
            resultPayload,
            agentNote,
            startedUtc,
            now,
            Math.Max(1, attempts),
            failure,
            log,
            diff);

        // Five columns for the diff rather than one column of JSON, and that is a
        // deliberate choice against the tidier-looking alternative. This store is the
        // store of record: a row it cannot read back is an agent's work nobody
        // can look at any more. JSON in a column has to be deserialised, and a
        // deserialisation that fails — or worse, succeeds with a field missing — is the
        // "results payload that parses but is missing the field the reviewer needs"
        // failure the design names, imported into the one place that has to be reliable.
        // Columns are read back as themselves, and a null in each of them is a plain
        // absence rather than a shape to be guessed at.
        db.RoundResults.Add(recorded.ToRow());
        db.SaveChanges();

        db.WorkItems
            .Where(item => item.Id == workItemId)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.RoundCount, roundNumber)
                .SetProperty(item => item.UpdatedUtc, now));

        transaction.Commit();

        return recorded;
    }

    public IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId)
    {
        using var db = Open();
        return db.RoundResults
            .AsNoTracking()
            .Where(round => round.WorkItemId == workItemId)
            .OrderBy(round => round.RoundNumber)
            .AsEnumerable()
            .Select(round => round.ToRecord())
            .ToList();
    }

    public void RecordMergeFailure(Guid workItemId, FailureClass failure, DateTimeOffset? retryAfterUtc)
    {
        using var db = Open();

        // The count and the gate move together, and the count goes up in the database
        // rather than being worked out in the caller: a second attempt must not be
        // possible without the first one being on the record, and a restart must not hand
        // a merge the repository has already refused a fresh budget.
        var recorded = db.WorkItems
            .Where(item => item.Id == workItemId)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.MergeAttempts, item => item.MergeAttempts + 1)
                .SetProperty(item => item.MergeRetryAfterUtc, retryAfterUtc));

        if (recorded == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to record a failed merge against");
        }
    }

    public DecisionRecord RecordDecision(Guid workItemId, Decision decision, string? feedback)
    {
        // A decision is made about a work item a reviewer can still act on, and about one
        // that is there. Anything else is a caller's mistake rather than a decision: the
        // loop is what moves work items, and it moves them out of Review itself.
        //
        // The two decidable lanes are Review and Escalated. Escalated is in the set
        // because parking is not a grave — a human finishes a parked work item from the
        // board, and a store that refused would make ADR-0008's promise unkeepable. The
        // refusal below names only Review, and that is deliberate rather than stale: it
        // is shown for lanes that are neither Review nor Escalated, where "not in Review"
        // is both true and the whole of the reason, and the words are ones a reviewer has
        // already read.
        var workItem = Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId} to decide about");

        if (!Swimlanes.Decidable.Contains(workItem.Swimlane))
        {
            throw new InvalidOperationException(
                $"{workItem.Project}#{workItem.IssueNumber} is in {Swimlanes.Label(workItem.Swimlane)}, "
                    + "not in Review, so there is nothing to decide about it");
        }

        // What finishes a parked work item is a human merging it or declining it. Sending
        // it round again is not offered and is refused here, and this is the policy rather
        // than a gap in it: a parked work item is finished by a human, and the only two
        // ways a human finishes one are the two decisions the board offers on it. Re-opening
        // the build would be a fourth thing to do with a work item and a fourth way out of
        // Escalated, and it is refused here — where the rule is — so the board can render
        // only the decisions that are real, rather than a button that always failed.
        //
        // The refusal is about parking and not about the rounds, and that is the whole
        // argument: a work item parked by a round that would not start may well have two
        // rounds to spare, and it is still parked. The retry policy has already spent
        // itself on the failure that parked it — three attempts for a transient one, one
        // for a permanent one — and re-opening the work item would mint a fresh budget for
        // the same failure while spending rounds that are a reviewer's to spend.
        if (workItem.Swimlane == Swimlane.Escalated && decision == Decision.RequestChanges)
        {
            throw new InvalidOperationException(
                $"{workItem.Project}#{workItem.IssueNumber} is parked in Escalated, and a parked work item "
                    + "is finished by a human: merged, or declined. It is not sent round again.");
        }

        // Feedback is the reviewer's reasons, and a request for changes hands them to
        // the next round as its brief. An empty brief is not a brief, and a placeholder
        // would be the factory putting words in the reviewer's mouth — so a request for
        // changes with nothing to say is refused rather than quietly sent round again
        // with nothing. Approve and Reject carry no brief, so they need no words.
        if (decision == Decision.RequestChanges && string.IsNullOrWhiteSpace(feedback))
        {
            throw new InvalidOperationException(
                "requesting changes needs the reviewer's reasons: they are the next round's brief, "
                    + "and there is nothing here to hand it");
        }

        var now = _clock.UtcNow;
        var words = feedback ?? string.Empty;

        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        // The work item's decisions are numbered, and the number is where it sits in the
        // order they were made — which is what the brief for a later round is read out
        // of. Counted inside the transaction, for the same reason round numbers are: two
        // decisions must never come out as the same decision.
        var before = db.Decisions.Count(made => made.WorkItemId == workItem.Id);
        var sequence = checked(before + 1);
        var recorded = new DecisionRecord(workItem.Id, sequence, decision, words, now, AppliedTo: null);

        db.Decisions.Add(recorded.ToRow());
        db.SaveChanges();

        // A decision clears the merge attempts this work item has behind it. A reviewer
        // approving again is a second, complete, attributable decision rather than a
        // continuation of the one that failed, and it is entitled to its own attempts
        // rather than inheriting the ones the last approval spent. Nothing else clears
        // them: a merge that failed and nobody has decided about again keeps its count, so
        // a restart cannot hand it a fresh budget either.
        db.WorkItems
            .Where(item => item.Id == workItem.Id)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.MergeAttempts, 0)
                .SetProperty(item => item.MergeRetryAfterUtc, (DateTimeOffset?)null));

        transaction.Commit();

        return recorded;
    }

    public void ApplyDecision(Guid workItemId, int sequence, Swimlane swimlane)
    {
        var now = _clock.UtcNow;

        using var db = Open();
        using var transaction = db.Database.BeginTransaction();

        // Marked only if it is still pending, so the decision itself refuses to be
        // applied twice rather than the loop having to remember that it had.
        var applied = db.Decisions
            .Where(made => made.WorkItemId == workItemId && made.Sequence == sequence && made.AppliedTo == null)
            .ExecuteUpdate(set => set.SetProperty(made => made.AppliedTo, swimlane));

        if (applied == 0)
        {
            throw new KeyNotFoundException(
                $"no unapplied decision {sequence} on work item {workItemId} to apply to {swimlane}");
        }

        DateTimeOffset? reviewStarted = swimlane == Swimlane.Review ? now : null;

        var moved = db.WorkItems
            .Where(item => item.Id == workItemId)
            .ExecuteUpdate(set => set
                .SetProperty(item => item.Swimlane, swimlane)
                .SetProperty(item => item.UpdatedUtc, now)
                .SetProperty(item => item.ReviewStartedUtc, reviewStarted));

        if (moved == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to move to {swimlane}");
        }

        transaction.Commit();
    }

    public IReadOnlyList<DecisionRecord> Decisions(Guid workItemId)
    {
        using var db = Open();
        return db.Decisions
            .AsNoTracking()
            .Where(made => made.WorkItemId == workItemId)
            .OrderBy(made => made.Sequence)
            .AsEnumerable()
            .Select(made => made.ToRecord())
            .ToList();
    }

    private FactoryDbContext Open() => new(_options);

    /// <summary>SQLITE_CONSTRAINT. Any constraint, of which the unique index is the one we expect.</summary>
    private const int SqliteConstraint = 19;
}
