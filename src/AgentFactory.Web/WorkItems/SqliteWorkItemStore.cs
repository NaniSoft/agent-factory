namespace AgentFactory.WorkItems;

using AgentFactory.Application.Clock;
using AgentFactory.Domain.Failures;
using AgentFactory.Domain.Rounds;
using AgentFactory.Domain.Results;
using AgentFactory.Domain.WorkItems;
using AgentFactory.Rounds;
using Microsoft.Data.Sqlite;

/// <summary>
/// Work items in SQLite, the store of record. One writer, one process, one file
/// (ADR-0009): losing a work item on restart would mean losing an agent's work.
/// </summary>
public sealed class SqliteWorkItemStore : IWorkItemStore
{
    private readonly string _connectionString;
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

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        EnsureSchema();
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
        catch (SqliteException constraint) when (constraint.SqliteErrorCode == SqliteConstraint)
        {
            // The unique index on (repo_url, issue_number) is what notices the duplicate.
            // It decides inside the write rather than in a check made before it, so
            // nothing can slip a second row in between the two. The work item that is
            // already there is returned as it stands: re-polling an issue is not a reason
            // to change the record of one, and a work item three rounds deep must not
            // lose that because its issue was still open.
            var existing = Find(repoUrl, issueNumber);
            if (existing is null)
            {
                // A constraint that is not the duplicate we expected — a not-null or a
                // check, which would mean the row is genuinely malformed — leaves nothing
                // to find, and is rethrown rather than reported as an issue already taken.
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

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_items (id, project, repo_url, issue_number, issue_title, issue_body, base_branch, swimlane, round_count, created_utc, updated_utc, merge_attempts)
            VALUES ($id, $project, $repoUrl, $issueNumber, $issueTitle, $issueBody, $baseBranch, $swimlane, $roundCount, $createdUtc, $updatedUtc, $mergeAttempts);
            """;
        Bind(command, workItem);
        command.Parameters.AddWithValue("$reviewStartedUtc", DBNull.Value);
        command.ExecuteNonQuery();

        return workItem;
    }

    /// <summary>The work item for this issue, if the store already has one.</summary>
    private WorkItem? Find(string repoUrl, int issueNumber)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE repo_url = $repoUrl AND issue_number = $issueNumber;";
        command.Parameters.AddWithValue("$repoUrl", repoUrl);
        command.Parameters.AddWithValue("$issueNumber", issueNumber);

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public WorkItem? Get(Guid id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<WorkItem> List() => Query(Select + Order);

    public void Move(Guid id, Swimlane swimlane)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = SetSwimlane;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        BindSwimlane(command, swimlane, _clock.UtcNow);

        if (command.ExecuteNonQuery() == 0)
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
        //                 parks (ADR-0008) rather than being retried into the same silence.
        //
        // Two writes rather than one statement because `Move` is the only other writer of a
        // lane and the review clock is part of a move; keeping one writer per fact is worth a
        // second statement here.
        var lane = kind switch
        {
            WorkItemKind.Build => Swimlane.Backlog,
            WorkItemKind.Unrouted => Swimlane.Escalated,
            _ => Swimlane.Waiting,
        };

        Move(workItemId, lane);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE work_items
            SET kind = $kind, route_reason = $routeReason, routing_issue = $routingIssue, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$kind", kind.ToString());
        command.Parameters.AddWithValue("$routeReason", reason.Trim());
        command.Parameters.AddWithValue("$routingIssue", (object?)routingIssue ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", _clock.UtcNow.ToString("O"));

        if (command.ExecuteNonQuery() == 0)
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

        using var connection = Open();
        using var handBack = connection.CreateCommand();
        handBack.CommandText = """
            UPDATE work_items
            SET kind = 'Unrouted', route_reason = $routeReason, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        handBack.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        handBack.Parameters.AddWithValue(
            "$routeReason",
            $"The answers arrived in the conversation on #{question}; the conversation is being read whole.");
        handBack.Parameters.AddWithValue("$updatedUtc", _clock.UtcNow.ToString("O"));

        if (handBack.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to hand back");
        }
    }

    /// <inheritdoc />
    public void RecordPendingRound(PendingRound pending)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_rounds (work_item_id, round_number, attempt, purpose, asked_utc, retry_after_utc)
            VALUES ($id, $round, $attempt, $purpose, $asked, NULL);
            """;
        command.Parameters.AddWithValue("$id", pending.WorkItemId.ToString("D"));
        command.Parameters.AddWithValue("$round", pending.RoundNumber);
        command.Parameters.AddWithValue("$attempt", pending.Attempt);
        command.Parameters.AddWithValue("$purpose", pending.Purpose);
        command.Parameters.AddWithValue("$asked", pending.AskedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void ClearPendingRound(Guid workItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM pending_rounds WHERE work_item_id = $id;";
        command.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingRound> ListPendingRounds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT work_item_id, round_number, attempt, purpose, asked_utc, retry_after_utc "
            + "FROM pending_rounds ORDER BY asked_utc;";

        using var reader = command.ExecuteReader();
        var pending = new List<PendingRound>();
        while (reader.Read())
        {
            pending.Add(new PendingRound(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)),
                reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5))));
        }

        return pending;
    }

    /// <inheritdoc />
    public void SetPendingRoundRetryAfter(Guid workItemId, DateTimeOffset retryAfterUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_rounds SET retry_after_utc = $retryAfter WHERE work_item_id = $id;
            """;
        command.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        command.Parameters.AddWithValue("$retryAfter", retryAfterUtc.ToString("O"));
        command.ExecuteNonQuery();
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

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // The round's number is the work item's count of rounds, plus this one. Read
        // inside the transaction so two rounds can never be recorded as the same round.
        // No row means the work item is not there, which is a caller's mistake and not a
        // round that produced nothing.
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT round_count FROM work_items WHERE id = $id;";
        count.Parameters.AddWithValue("$id", workItemId.ToString("D"));

        if (count.ExecuteScalar() is not long previous)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to record a round against");
        }

        var roundNumber = (int)previous + 1;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;

        // Five columns for the diff rather than one column of JSON, and that is a
        // deliberate choice against the tidier-looking alternative. This store is the
        // store of record (ADR-0009): a row it cannot read back is an agent's work nobody
        // can look at any more. JSON in a column has to be deserialised, and a
        // deserialisation that fails — or worse, succeeds with a field missing — is the
        // "results payload that parses but is missing the field the reviewer needs"
        // failure the design names, imported into the one place that has to be reliable.
        // Columns are read back as themselves, and a null in each of them is a plain
        // absence rather than a shape to be guessed at.
        insert.CommandText = """
            INSERT INTO round_results (work_item_id, round_number, outcome, result_payload, agent_note, started_utc, completed_utc, attempts, failure, log, diff, diff_base, diff_tree, diff_container_bounded, diff_unavailable)
            VALUES ($workItemId, $roundNumber, $outcome, $resultPayload, $agentNote, $startedUtc, $completedUtc, $attempts, $failure, $log, $diff, $diffBase, $diffTree, $diffContainerBounded, $diffUnavailable);
            """;
        insert.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        insert.Parameters.AddWithValue("$roundNumber", roundNumber);
        insert.Parameters.AddWithValue("$outcome", outcome.ToString());
        insert.Parameters.AddWithValue("$resultPayload", (object?)resultPayload ?? DBNull.Value);
        insert.Parameters.AddWithValue("$agentNote", (object?)agentNote ?? DBNull.Value);
        insert.Parameters.AddWithValue("$startedUtc", startedUtc.ToString("O"));
        insert.Parameters.AddWithValue("$completedUtc", now.ToString("O"));
        insert.Parameters.AddWithValue("$attempts", Math.Max(1, attempts));
        insert.Parameters.AddWithValue("$failure", (object?)failure?.ToString() ?? DBNull.Value);
        insert.Parameters.AddWithValue("$log", (object?)log ?? DBNull.Value);
        insert.Parameters.AddWithValue("$diff", (object?)diff?.Text ?? DBNull.Value);
        insert.Parameters.AddWithValue("$diffBase", (object?)diff?.Base ?? DBNull.Value);
        insert.Parameters.AddWithValue("$diffTree", (object?)diff?.Tree ?? DBNull.Value);
        insert.Parameters.AddWithValue("$diffContainerBounded", diff?.ContainerBounded == true ? 1 : 0);
        insert.Parameters.AddWithValue("$diffUnavailable", (object?)diff?.UnavailableBecause ?? DBNull.Value);
        insert.ExecuteNonQuery();

        using var bump = connection.CreateCommand();
        bump.Transaction = transaction;
        bump.CommandText = """
            UPDATE work_items
            SET round_count = $roundCount, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        bump.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        bump.Parameters.AddWithValue("$roundCount", roundNumber);
        bump.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
        bump.ExecuteNonQuery();

        transaction.Commit();

        return new RoundResultRecord(
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
    }

    public IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work_item_id, round_number, outcome, result_payload, agent_note, started_utc, completed_utc, attempts, failure, log, diff, diff_base, diff_tree, diff_container_bounded, diff_unavailable
            FROM round_results
            WHERE work_item_id = $workItemId
            ORDER BY round_number;
            """;
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));

        var rounds = new List<RoundResultRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rounds.Add(new RoundResultRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                Enum.Parse<RoundOutcome>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                Timestamp(reader.GetString(5)),
                Timestamp(reader.GetString(6)),
                reader.IsDBNull(7) ? 1 : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : Enum.Parse<FailureClass>(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                ReadDiff(reader, 10)));
        }

        return rounds;
    }

    /// <summary>
    /// One round's diff, read back out of the five columns it was written into, or null
    /// when the row has none of them — a round recorded before this column existed, or a
    /// round whose container never produced a tree.
    /// </summary>
    /// <remarks>
    /// A row with a diff but no tree is still a diff: the tree is where a reviewer can go
    /// for the parts the board did not render, and its absence does not unmake the text.
    /// Conversely a row whose only diff column is set is the "there was no diff and here is
    /// why" shape, which is the one a round that broke before it could write anything down
    /// arrives in, and it is read back as that rather than as a round that changed
    /// nothing.
    /// </remarks>
    private static HostDiff? ReadDiff(SqliteDataReader reader, int first)
    {
        var text = reader.IsDBNull(first) ? null : reader.GetString(first);
        var base_ = reader.IsDBNull(first + 1) ? null : reader.GetString(first + 1);
        var tree = reader.IsDBNull(first + 2) ? null : reader.GetString(first + 2);
        var bounded = !reader.IsDBNull(first + 3) && reader.GetInt64(first + 3) != 0;
        var unavailable = reader.IsDBNull(first + 4) ? null : reader.GetString(first + 4);

        if (text is null && unavailable is null)
        {
            return null;
        }

        return new HostDiff(text ?? string.Empty, base_ ?? string.Empty, tree ?? string.Empty, bounded, unavailable);
    }

    public void RecordMergeFailure(Guid workItemId, FailureClass failure, DateTimeOffset? retryAfterUtc)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();

        // The count and the gate move together, and the count goes up in the database
        // rather than being worked out in the caller: a second attempt must not be
        // possible without the first one being on the record, and a restart must not hand
        // a merge the repository has already refused a fresh budget.
        command.CommandText = """
            UPDATE work_items
            SET merge_attempts = merge_attempts + 1, merge_retry_after_utc = $retryAfterUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        command.Parameters.AddWithValue(
            "$retryAfterUtc",
            retryAfterUtc is { } after ? after.ToString("O") : (object)DBNull.Value);

        if (command.ExecuteNonQuery() == 0)
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

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // The work item's decisions are numbered, and the number is where it sits in the
        // order they were made — which is what the brief for a later round is read out
        // of. Read inside the transaction, for the same reason round numbers are: two
        // decisions must never come out as the same decision.
        using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM decisions WHERE work_item_id = $workItemId;";
        count.Parameters.AddWithValue("$workItemId", workItem.Id.ToString("D"));

        if (count.ExecuteScalar() is not long before)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to decide about");
        }

        var sequence = checked((int)before) + 1;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO decisions (work_item_id, sequence, decision, feedback, decided_utc)
            VALUES ($workItemId, $sequence, $decision, $feedback, $decidedUtc);
            """;
        insert.Parameters.AddWithValue("$workItemId", workItem.Id.ToString("D"));
        insert.Parameters.AddWithValue("$sequence", sequence);
        insert.Parameters.AddWithValue("$decision", decision.ToString());
        insert.Parameters.AddWithValue("$feedback", words);
        insert.Parameters.AddWithValue("$decidedUtc", now.ToString("O"));
        insert.ExecuteNonQuery();

        // A decision clears the merge attempts this work item has behind it. A reviewer
        // approving again is a second, complete, attributable decision rather than a
        // continuation of the one that failed, and it is entitled to its own attempts
        // rather than inheriting the ones the last approval spent. Nothing else clears
        // them: a merge that failed and nobody has decided about again keeps its count, so
        // a restart cannot hand it a fresh budget either.
        using var forget = connection.CreateCommand();
        forget.Transaction = transaction;
        forget.CommandText = """
            UPDATE work_items
            SET merge_attempts = 0, merge_retry_after_utc = NULL
            WHERE id = $id;
            """;
        forget.Parameters.AddWithValue("$id", workItem.Id.ToString("D"));
        forget.ExecuteNonQuery();

        transaction.Commit();

        return new DecisionRecord(workItem.Id, sequence, decision, words, now, AppliedTo: null);
    }

    public void ApplyDecision(Guid workItemId, int sequence, Swimlane swimlane)
    {
        var now = _clock.UtcNow;

        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        // Marked only if it is still pending, so the decision itself refuses to be
        // applied twice rather than the loop having to remember that it had.
        using var apply = connection.CreateCommand();
        apply.Transaction = transaction;
        apply.CommandText = """
            UPDATE decisions
            SET applied_to = $appliedTo
            WHERE work_item_id = $workItemId AND sequence = $sequence AND applied_to IS NULL;
            """;
        apply.Parameters.AddWithValue("$appliedTo", swimlane.ToString());
        apply.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        apply.Parameters.AddWithValue("$sequence", sequence);

        if (apply.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException(
                $"no unapplied decision {sequence} on work item {workItemId} to apply to {swimlane}");
        }

        using var move = connection.CreateCommand();
        move.Transaction = transaction;
        move.CommandText = SetSwimlane;
        move.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        BindSwimlane(move, swimlane, now);

        if (move.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"no work item {workItemId} to move to {swimlane}");
        }

        transaction.Commit();
    }

    public IReadOnlyList<DecisionRecord> Decisions(Guid workItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work_item_id, sequence, decision, feedback, decided_utc, applied_to
            FROM decisions
            WHERE work_item_id = $workItemId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));

        var decisions = new List<DecisionRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            decisions.Add(new DecisionRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                Enum.Parse<Decision>(reader.GetString(2)),
                reader.GetString(3),
                Timestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : Enum.Parse<Swimlane>(reader.GetString(5))));
        }

        return decisions;
    }

    private IReadOnlyList<WorkItem> Query(string sql)    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        var workItems = new List<WorkItem>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            workItems.Add(Read(reader));
        }

        return workItems;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS work_items (
                id           TEXT    NOT NULL PRIMARY KEY,
                project      TEXT    NOT NULL,
                repo_url     TEXT    NOT NULL,
                issue_number INTEGER NOT NULL,
                issue_title  TEXT    NOT NULL,
                issue_body   TEXT    NOT NULL,
                base_branch  TEXT    NOT NULL,
                swimlane     TEXT    NOT NULL,
                round_count  INTEGER NOT NULL,
                created_utc  TEXT    NOT NULL,
                updated_utc  TEXT    NOT NULL,
                review_started_utc TEXT NULL,
                merge_attempts INTEGER NOT NULL DEFAULT 0,
                merge_retry_after_utc TEXT NULL,
                kind TEXT    NOT NULL DEFAULT 'Unrouted',
                route_reason TEXT  NULL,
                routing_issue INTEGER NULL
            );

            -- Intake is idempotent against the store: one issue, one work item. The index
            -- is the whole mechanism — nothing checks whether the issue is already there
            -- before writing, because a check and a write can disagree and the write is
            -- the only one of the two that is atomic.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_work_items_source_issue
                ON work_items (repo_url, issue_number);

            -- One row per round, and a row is never replaced. A work item that has run
            -- three rounds keeps all three, because a reviewer judges the disagreement
            -- between them as much as the last one. `attempts` and `failure` are the round's
            -- own story rather than a copy of it: how many times the factory asked for it,
            -- and what the round runner made of the attempt that did not come back. A round
            -- that came back with a result has no failure at all, which is what makes "a
            -- build that failed its tests is not retried" a property of the record.
            CREATE TABLE IF NOT EXISTS round_results (
                work_item_id   TEXT    NOT NULL,
                round_number   INTEGER NOT NULL,
                outcome        TEXT    NOT NULL,
                result_payload TEXT    NULL,
                agent_note     TEXT    NULL,
                started_utc    TEXT    NOT NULL,
                completed_utc  TEXT    NOT NULL,
                attempts       INTEGER NOT NULL DEFAULT 1,
                failure        TEXT    NULL,
                log            TEXT    NULL,

                -- The round's change, as the host generated it from the tree the round
                -- left. Five columns rather than one of JSON, so that a row is read back
                -- as itself and a missing field is a null rather than a shape to guess
                -- at. `diff_unavailable` is the "there is no diff and here is why" shape,
                -- which is different from an empty diff: a round that changed nothing and
                -- a round whose tree never came out are not the same fact.
                diff                 TEXT    NULL,
                diff_base            TEXT    NULL,
                diff_tree            TEXT    NULL,
                diff_container_bounded INTEGER NOT NULL DEFAULT 0,
                diff_unavailable     TEXT    NULL,

                PRIMARY KEY (work_item_id, round_number)
            );

            -- A decision is the reviewer's own: which of the three it was, and the words
            -- they wrote, kept whole and in the order they were made. The next round is
            -- handed those words as its brief, and a later reader has to be able to see
            -- what was asked for rather than only that something was. The sequence is
            -- per work item, because it is that work item's order and nobody else's.
            -- applied_to is null until the loop has acted on the decision, which is what
            -- tells a decision already acted on from one still waiting to be.
            CREATE TABLE IF NOT EXISTS decisions (
                work_item_id TEXT    NOT NULL,
                sequence     INTEGER NOT NULL,
                decision     TEXT    NOT NULL,
                feedback     TEXT    NOT NULL,
                decided_utc  TEXT    NOT NULL,
                applied_to   TEXT    NULL,
                PRIMARY KEY (work_item_id, sequence)
            );

            CREATE INDEX IF NOT EXISTS ix_decisions_work_item
                ON decisions (work_item_id, sequence);

            -- The round the factory has asked for and not yet seen the end of: one row per
            -- in-flight round, written before the ask and removed when the round ends any
            -- way at all. This row is the whole of what a restart needs — the work item a
            -- round was building, and which round and attempt it was — because
            -- reconciliation re-asks the round rather than pretending the process never
            -- died. `retry_after_utc` is the transient backoff's durable half: a wait that
            -- survives the process is a wait that still applies after it, and a retry
            -- asked for too soon after a restart is the same retry.
            CREATE TABLE IF NOT EXISTS pending_rounds (
                work_item_id    TEXT    NOT NULL PRIMARY KEY,
                round_number    INTEGER NOT NULL,
                attempt         INTEGER NOT NULL,
                purpose         TEXT    NOT NULL,
                asked_utc       TEXT    NOT NULL,
                retry_after_utc TEXT NULL
            );
            """;
        command.ExecuteNonQuery();

        // The pending-round row once named the container the round ran in, because that
        // container outlived the process and had to be found and taken away. A round is a
        // child process now, and there is nothing left to take away — so the column is a
        // residue of a concept the factory no longer has, and a file written by an earlier
        // build is relieved of it here rather than left carrying it.
        DropColumnIfPresent(connection, "pending_rounds", "container");

        // A database file written by an earlier build is a developer's own local file
        // (ADR-0009), and the columns added since are ones a round and a merge cannot be
        // read back without. Adding them here is idempotent, so a restart on an older file
        // does not fail on a schema this process did not write.
        AddColumnIfMissing(connection, "work_items", "merge_attempts", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "work_items", "merge_retry_after_utc", "TEXT NULL");

        // A work item recorded before the routing round existed is `Unrouted` rather than
        // `Build`, and that is the honest reading of it: nobody has looked at that issue.
        // Defaulting the other way would put every pre-existing work item straight into the
        // build queue and call that a routing round's decision (ADR-0015).
        AddColumnIfMissing(connection, "work_items", "kind", "TEXT NOT NULL DEFAULT 'Unrouted'");
        AddColumnIfMissing(connection, "work_items", "route_reason", "TEXT NULL");

        // Where the questions or the map were actually filed, so the board can point at the
        // issue a human is being asked on rather than describing it. Null for every work item
        // that was routed `Build` and for every one from before this existed, and both of
        // those are correct: a build ticket is answered on the maintainer's own issue.
        AddColumnIfMissing(connection, "work_items", "routing_issue", "INTEGER NULL");
        AddColumnIfMissing(connection, "round_results", "attempts", "INTEGER NOT NULL DEFAULT 1");
        AddColumnIfMissing(connection, "round_results", "failure", "TEXT NULL");
        AddColumnIfMissing(connection, "round_results", "log", "TEXT NULL");

        // A round recorded before the diff existed has no diff and is not retrofitted with
        // one: the tree it would have been read out of was not kept per round, and a
        // fabricated empty diff would say a round changed nothing when in fact nobody
        // looked. Such a round reads back with no diff at all, which the board renders as
        // "not recorded" rather than as an absence of change.
        AddColumnIfMissing(connection, "round_results", "diff", "TEXT NULL");
        AddColumnIfMissing(connection, "round_results", "diff_base", "TEXT NULL");
        AddColumnIfMissing(connection, "round_results", "diff_tree", "TEXT NULL");
        AddColumnIfMissing(connection, "round_results", "diff_container_bounded", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "round_results", "diff_unavailable", "TEXT NULL");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string declaration)
    {
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = $"PRAGMA table_info({table});";

            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }

    private static void DropColumnIfPresent(SqliteConnection connection, string table, string column)
    {
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = $"PRAGMA table_info({table});";

            using var reader = columns.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                {
                    continue;
                }

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE {table} DROP COLUMN {column};";
                alter.ExecuteNonQuery();
                return;
            }
        }
    }

    private static void Bind(SqliteCommand command, WorkItem workItem)
    {
        command.Parameters.AddWithValue("$id", workItem.Id.ToString("D"));
        command.Parameters.AddWithValue("$project", workItem.Project);
        command.Parameters.AddWithValue("$repoUrl", workItem.RepoUrl);
        command.Parameters.AddWithValue("$issueNumber", workItem.IssueNumber);
        command.Parameters.AddWithValue("$issueTitle", workItem.IssueTitle);
        command.Parameters.AddWithValue("$issueBody", workItem.IssueBody);
        command.Parameters.AddWithValue("$baseBranch", workItem.BaseBranch);
        command.Parameters.AddWithValue("$swimlane", workItem.Swimlane.ToString());
        command.Parameters.AddWithValue("$roundCount", workItem.RoundCount);
        command.Parameters.AddWithValue("$createdUtc", workItem.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", workItem.UpdatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$mergeAttempts", workItem.MergeAttempts);
    }

    /// <summary>
    /// Binds a move, and the two things a move also decides. The swimlane is what the
    /// loop decided; the moment the review started is the same write, because a work item
    /// that has just entered Review is one whose feedback clock starts now, and one that
    /// has just left it is not waiting on anybody. Both go through here — the loop's own
    /// move and the move a decision causes — so the two paths cannot disagree about when
    /// a review began.
    /// </summary>
    private static void BindSwimlane(SqliteCommand command, Swimlane swimlane, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$swimlane", swimlane.ToString());
        command.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));
        command.Parameters.AddWithValue(
            "$reviewStartedUtc",
            swimlane == Swimlane.Review ? now.ToString("O") : (object)DBNull.Value);
    }

    private static WorkItem Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetInt32(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        Enum.Parse<Swimlane>(reader.GetString(7)),
        reader.GetInt32(8),
        Timestamp(reader.GetString(9)),
        Timestamp(reader.GetString(10)),
        reader.IsDBNull(11) ? null : Timestamp(reader.GetString(11)),
        reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
        reader.IsDBNull(13) ? null : Timestamp(reader.GetString(13)),
        ReadKind(reader, 14),
        reader.IsDBNull(15) ? null : reader.GetString(15),
        reader.IsDBNull(16) ? null : reader.GetInt32(16));

    /// <summary>
    /// The work item's kind, read back as itself. A row whose kind this build does not
    /// recognise is read as <see cref="WorkItemKind.Unrouted"/> rather than refused, and the
    /// reason is the same as the column's default: an unknown verdict is a verdict nobody
    /// made, so it is the one that means "not looked at yet", and the next routing round
    /// will overwrite it with a real one.
    /// </summary>
    private static WorkItemKind ReadKind(SqliteDataReader reader, int index) =>
        !reader.IsDBNull(index) && Enum.TryParse<WorkItemKind>(reader.GetString(index), out var kind)
            ? kind
            : WorkItemKind.Unrouted;

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    /// <summary>SQLITE_CONSTRAINT. Any constraint, of which the unique index is the one we expect.</summary>
    private const int SqliteConstraint = 19;

    private const string Select = """
        SELECT id, project, repo_url, issue_number, issue_title, issue_body, base_branch, swimlane, round_count, created_utc, updated_utc, review_started_utc, merge_attempts, merge_retry_after_utc, kind, route_reason, routing_issue
        FROM work_items
        """;

    private const string Order = " ORDER BY created_utc, issue_number;";

    /// <summary>
    /// The one statement that puts a work item in a lane. The review's start is part of
    /// the move rather than a second write beside it, so a work item cannot end up in
    /// Review with no review to measure the feedback threshold from.
    /// </summary>
    private const string SetSwimlane = """
        UPDATE work_items
        SET swimlane = $swimlane, updated_utc = $updatedUtc, review_started_utc = $reviewStartedUtc
        WHERE id = $id;
        """;
}
