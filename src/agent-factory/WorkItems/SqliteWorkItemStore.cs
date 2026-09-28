namespace AgentFactory.WorkItems;

using AgentFactory.Clock;
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
        DateTimeOffset.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTimeOffset.Parse(reader.GetString(9), null, System.Globalization.DateTimeStyles.RoundtripKind));

    private const string Select = """
        SELECT id, project, repo_url, issue_number, issue_title, base_branch, swimlane, round_count, created_utc, updated_utc
        FROM work_items
        """;

    private const string Order = " ORDER BY created_utc, issue_number;";
}
