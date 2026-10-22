using Microsoft.Data.Sqlite;

namespace OpenTail.Connectivity.Queues.Sqlite;

/// <summary>
/// Cancelled jobs in a SQLite file, shared by every worker on one machine (for a broker's queue that cannot remove a message).
/// Entries older than <paramref name="retention"/> are dropped: by then the job has run out of attempts or age anyway.
/// </summary>
public sealed class SqliteCancelledJobs : ICancelledJobs
{
    private readonly string _connectionString;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;

    /// <param name="retention">Default 30 days; keep it above the queue's <see cref="WorkQueueOptions.MaxAge"/>.</param>
    public SqliteCancelledJobs(string path, TimeSpan? retention = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        _retention = retention ?? TimeSpan.FromDays(30);
        _time = time ?? TimeProvider.System;
        using var connection = Open();
        using var create = connection.CreateCommand();
        create.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS cancelled_jobs (queue TEXT NOT NULL, id TEXT NOT NULL, reason TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY (queue, id));
            CREATE INDEX IF NOT EXISTS cancelled_jobs_at ON cancelled_jobs (at);
            """;
        create.ExecuteNonQuery();
    }

    public Task MarkAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM cancelled_jobs WHERE at <= $cut;
            INSERT OR REPLACE INTO cancelled_jobs (queue, id, reason, at) VALUES ($queue, $id, $reason, $now);
            """;
        command.Parameters.AddWithValue("$cut", now - (long)_retention.TotalMilliseconds);
        command.Parameters.AddWithValue("$queue", queue);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM cancelled_jobs WHERE queue = $queue AND id = $id";
        command.Parameters.AddWithValue("$queue", queue);
        command.Parameters.AddWithValue("$id", id);
        return Task.FromResult(command.ExecuteScalar() is not null);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = FULL;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}
