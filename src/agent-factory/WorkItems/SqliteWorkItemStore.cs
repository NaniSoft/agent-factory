namespace AgentFactory.WorkItems;

using AgentFactory.Clock;
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
            UpdatedUtc: now);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_items (id, project, repo_url, issue_number, issue_title, issue_body, base_branch, swimlane, round_count, created_utc, updated_utc)
            VALUES ($id, $project, $repoUrl, $issueNumber, $issueTitle, $issueBody, $baseBranch, $swimlane, $roundCount, $createdUtc, $updatedUtc);
            """;
        Bind(command, workItem);
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
        command.CommandText = """
            UPDATE work_items
            SET swimlane = $swimlane, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$swimlane", swimlane.ToString());
        command.Parameters.AddWithValue("$updatedUtc", _clock.UtcNow.ToString("O"));

        if (command.ExecuteNonQuery() == 0)
        {
            throw new KeyNotFoundException($"no work item {id} to move to {swimlane}");
        }
    }

    public RoundResultRecord RecordRound(
        Guid workItemId,
        RoundOutcome outcome,
        string? resultPayload,
        string? agentNote,
        DateTimeOffset startedUtc)
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
        insert.CommandText = """
            INSERT INTO round_results (work_item_id, round_number, outcome, result_payload, agent_note, started_utc, completed_utc)
            VALUES ($workItemId, $roundNumber, $outcome, $resultPayload, $agentNote, $startedUtc, $completedUtc);
            """;
        insert.Parameters.AddWithValue("$workItemId", workItemId.ToString("D"));
        insert.Parameters.AddWithValue("$roundNumber", roundNumber);
        insert.Parameters.AddWithValue("$outcome", outcome.ToString());
        insert.Parameters.AddWithValue("$resultPayload", (object?)resultPayload ?? DBNull.Value);
        insert.Parameters.AddWithValue("$agentNote", (object?)agentNote ?? DBNull.Value);
        insert.Parameters.AddWithValue("$startedUtc", startedUtc.ToString("O"));
        insert.Parameters.AddWithValue("$completedUtc", now.ToString("O"));
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
            now);
    }

    public IReadOnlyList<RoundResultRecord> Rounds(Guid workItemId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT work_item_id, round_number, outcome, result_payload, agent_note, started_utc, completed_utc
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
                Timestamp(reader.GetString(6))));
        }

        return rounds;
    }

    public DecisionRecord RecordDecision(Guid workItemId, Decision decision, string? feedback)
    {
        // A decision is made about a work item in Review, and about one that is there.
        // Anything else is a caller's mistake rather than a decision: the loop is what
        // moves work items, and it moves them out of Review itself.
        var workItem = Get(workItemId)
            ?? throw new KeyNotFoundException($"no work item {workItemId} to decide about");

        if (workItem.Swimlane != Swimlane.Review)
        {
            throw new InvalidOperationException(
                $"{workItem.Project}#{workItem.IssueNumber} is in {Swimlanes.Label(workItem.Swimlane)}, "
                    + "not in Review, so there is nothing to decide about it");
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
        move.CommandText = """
            UPDATE work_items
            SET swimlane = $swimlane, updated_utc = $updatedUtc
            WHERE id = $id;
            """;
        move.Parameters.AddWithValue("$id", workItemId.ToString("D"));
        move.Parameters.AddWithValue("$swimlane", swimlane.ToString());
        move.Parameters.AddWithValue("$updatedUtc", now.ToString("O"));

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
                updated_utc  TEXT    NOT NULL
            );

            -- Intake is idempotent against the store: one issue, one work item. The index
            -- is the whole mechanism — nothing checks whether the issue is already there
            -- before writing, because a check and a write can disagree and the write is
            -- the only one of the two that is atomic.
            CREATE UNIQUE INDEX IF NOT EXISTS ix_work_items_source_issue
                ON work_items (repo_url, issue_number);

            -- One row per round, and a row is never replaced. A work item that has run
            -- three rounds keeps all three, because a reviewer judges the disagreement
            -- between them as much as the last one.
            CREATE TABLE IF NOT EXISTS round_results (
                work_item_id   TEXT    NOT NULL,
                round_number   INTEGER NOT NULL,
                outcome        TEXT    NOT NULL,
                result_payload TEXT    NULL,
                agent_note     TEXT    NULL,
                started_utc    TEXT    NOT NULL,
                completed_utc  TEXT    NOT NULL,
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
            """;
        command.ExecuteNonQuery();
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
        Timestamp(reader.GetString(10)));

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    /// <summary>SQLITE_CONSTRAINT. Any constraint, of which the unique index is the one we expect.</summary>
    private const int SqliteConstraint = 19;

    private const string Select = """
        SELECT id, project, repo_url, issue_number, issue_title, issue_body, base_branch, swimlane, round_count, created_utc, updated_utc
        FROM work_items
        """;

    private const string Order = " ORDER BY created_utc, issue_number;";
}
