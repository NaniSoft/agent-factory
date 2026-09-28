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

    public WorkItem Create(
        string project,
        string repoUrl,
        int issueNumber,
        string issueTitle,
        string baseBranch,
        Swimlane swimlane = Swimlane.Backlog)
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
            baseBranch,
            swimlane,
            RoundCount: 0,
            CreatedUtc: now,
            UpdatedUtc: now);

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_items (id, project, repo_url, issue_number, issue_title, base_branch, swimlane, round_count, created_utc, updated_utc)
            VALUES ($id, $project, $repoUrl, $issueNumber, $issueTitle, $baseBranch, $swimlane, $roundCount, $createdUtc, $updatedUtc);
            """;
        Bind(command, workItem);
        command.ExecuteNonQuery();

        return workItem;
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

    private IReadOnlyList<WorkItem> Query(string sql)
    {
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
                base_branch  TEXT    NOT NULL,
                swimlane     TEXT    NOT NULL,
                round_count  INTEGER NOT NULL,
                created_utc  TEXT    NOT NULL,
                updated_utc  TEXT    NOT NULL
            );

            -- Intake is idempotent against the store: one issue, one work item.
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
        Enum.Parse<Swimlane>(reader.GetString(6)),
        reader.GetInt32(7),
        Timestamp(reader.GetString(8)),
        Timestamp(reader.GetString(9)));

    private static DateTimeOffset Timestamp(string value) =>
        DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private const string Select = """
        SELECT id, project, repo_url, issue_number, issue_title, base_branch, swimlane, round_count, created_utc, updated_utc
        FROM work_items
        """;

    private const string Order = " ORDER BY created_utc, issue_number;";
}
