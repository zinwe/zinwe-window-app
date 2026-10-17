using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Zinwe.Agent.Core.Events;

/// <summary>
/// Durable outbox for events (DataDirectory\events.db, SQLite). Events survive restarts and offline periods and
/// are deleted only after the backend has accepted them. Bounded: past <see cref="Capacity"/> rows the oldest go.
/// Thread-safe: every call opens its own connection under one in-process lock.
/// </summary>
public sealed class EventQueue
{
    public const int DefaultCapacity = 10_000;

    private readonly string _connectionString;
    private readonly Lock _lock = new();
    private readonly Func<bool>? _recordingAllowed;

    /// <param name="recordingAllowed">
    /// Asked before each event is stored; false drops it (nothing is recorded until the member has agreed to
    /// monitoring — see <see cref="Work.ConsentGate"/>). Null = always record.
    /// </param>
    public EventQueue(string dataDirectory, int capacity = DefaultCapacity, Func<bool>? recordingAllowed = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
        _recordingAllowed = recordingAllowed;
        Directory.CreateDirectory(dataDirectory);
        FilePath = Path.Combine(dataDirectory, "events.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pooled handles left open on the file: tidy for a long-running service, and tests can delete it.
            Pooling = false,
        }.ToString();

        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS events (
                seq         INTEGER PRIMARY KEY AUTOINCREMENT,
                id          TEXT    NOT NULL UNIQUE,
                type        TEXT    NOT NULL,
                occurred_at TEXT    NOT NULL,
                severity    TEXT    NOT NULL,
                data        TEXT    NOT NULL
            );
            """);
    }

    public string FilePath { get; }

    public int Capacity { get; }

    /// <summary>Raised after an event is stored (on the caller's thread). The uploader uses it to send urgent events at once.</summary>
    public event EventHandler<AgentEvent>? Enqueued;

    /// <summary>
    /// Stores <paramref name="evt"/>. Returns how many of the oldest events were dropped to stay within
    /// <see cref="Capacity"/> (callers log a warning when this is non-zero). A repeated id is ignored, and
    /// nothing is stored while recording is paused (no consent yet).
    /// </summary>
    public int Enqueue(AgentEvent evt) => Enqueue(evt, always: false);

    /// <summary>
    /// Like <see cref="Enqueue(AgentEvent)"/>, but stored even while recording is paused: only for confirmations of
    /// the owner's own action that hold nothing about the member (e.g. <c>usb.restored</c>), which the dashboard
    /// needs to clear its state.
    /// </summary>
    public int EnqueueAlways(AgentEvent evt) => Enqueue(evt, always: true);

    private int Enqueue(AgentEvent evt, bool always)
    {
        evt.Validate();
        if (!always && _recordingAllowed is { } allowed && !allowed())
        {
            return 0;
        }
        int dropped;
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT OR IGNORE INTO events (id, type, occurred_at, severity, data)
                    VALUES ($id, $type, $occurred_at, $severity, $data);
                    """;
                insert.Parameters.AddWithValue("$id", evt.Id);
                insert.Parameters.AddWithValue("$type", evt.Type);
                insert.Parameters.AddWithValue("$occurred_at", evt.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$severity", evt.Severity);
                insert.Parameters.AddWithValue("$data", evt.DataJson);
                insert.ExecuteNonQuery();
            }

            using (var trim = connection.CreateCommand())
            {
                trim.Transaction = tx;
                trim.CommandText = """
                    DELETE FROM events
                    WHERE seq IN (SELECT seq FROM events ORDER BY seq DESC LIMIT -1 OFFSET $capacity);
                    """;
                trim.Parameters.AddWithValue("$capacity", Capacity);
                dropped = trim.ExecuteNonQuery();
            }

            tx.Commit();
        }
        Enqueued?.Invoke(this, evt);
        return dropped;
    }

    /// <summary>Oldest-first, without removing anything.</summary>
    public IReadOnlyList<AgentEvent> PeekBatch(int max)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        lock (_lock)
        {
            using var connection = Open();
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT id, type, occurred_at, severity, data FROM events ORDER BY seq LIMIT $max;";
            select.Parameters.AddWithValue("$max", max);

            var batch = new List<AgentEvent>(Math.Min(max, 128));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                batch.Add(new AgentEvent(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture),
                    reader.GetString(3),
                    reader.GetString(4)));
            }
            return batch;
        }
    }

    /// <summary>Removes the given events (after the backend has accepted them). Returns how many rows went.</summary>
    public int Delete(IEnumerable<string> ids)
    {
        lock (_lock)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var delete = connection.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM events WHERE id = $id;";
            var id = delete.Parameters.Add("$id", SqliteType.Text);

            var removed = 0;
            foreach (var value in ids)
            {
                id.Value = value;
                removed += delete.ExecuteNonQuery();
            }
            tx.Commit();
            return removed;
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            using var connection = Open();
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM events;";
            return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
