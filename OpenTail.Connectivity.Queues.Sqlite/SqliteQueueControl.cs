using Microsoft.Data.Sqlite;

namespace OpenTail.Connectivity.Queues.Sqlite;

/// <summary>
/// Queue control in a SQLite file, shared by every worker on one machine, for a broker's queue (RabbitMQ) that cannot keep cancelled
/// ids, concurrency slots and circuits itself. Changes run in immediate transactions, so limits are exact across processes.
/// Cancelled ids older than <paramref name="retention"/> are dropped: by then the job has run out of attempts or age anyway.
/// </summary>
public sealed class SqliteQueueControl : IQueueControl
{
    private readonly string _connectionString;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;

    /// <param name="retention">How long cancelled ids are kept. Default 30 days; keep it above the queue's <see cref="WorkQueueOptions.MaxAge"/>.</param>
    public SqliteQueueControl(string path, TimeSpan? retention = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        _retention = retention ?? TimeSpan.FromDays(30);
        _time = time ?? TimeProvider.System;
        using var connection = Open();
        Run(connection, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS cancelled_jobs (queue TEXT NOT NULL, id TEXT NOT NULL, reason TEXT NOT NULL, at INTEGER NOT NULL, PRIMARY KEY (queue, id));
            CREATE INDEX IF NOT EXISTS cancelled_jobs_at ON cancelled_jobs (at);
            CREATE TABLE IF NOT EXISTS slots (queue TEXT NOT NULL, conc_key TEXT NOT NULL, token TEXT NOT NULL, until INTEGER NOT NULL, PRIMARY KEY (queue, conc_key, token));
            CREATE TABLE IF NOT EXISTS circuits (queue TEXT NOT NULL, conc_key TEXT NOT NULL, failures INTEGER NOT NULL, open_until INTEGER NOT NULL,
                next_cooldown INTEGER NOT NULL, probe_token TEXT NULL, probe_until INTEGER NOT NULL, PRIMARY KEY (queue, conc_key));
            CREATE TABLE IF NOT EXISTS key_starts (queue TEXT NOT NULL, conc_key TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS key_starts_at ON key_starts (queue, conc_key, at);
            """);
    }

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    public Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        using var connection = Open();
        Run(connection, null, """
            DELETE FROM cancelled_jobs WHERE at <= $cut;
            INSERT OR REPLACE INTO cancelled_jobs (queue, id, reason, at) VALUES ($queue, $id, $reason, $now);
            """, ("$cut", Now - (long)_retention.TotalMilliseconds), ("$queue", queue), ("$id", id), ("$reason", reason), ("$now", Now));
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
    {
        using var connection = Open();
        using var command = Command(connection, null, "SELECT 1 FROM cancelled_jobs WHERE queue = $queue AND id = $id", ("$queue", queue), ("$id", id));
        return Task.FromResult(command.ExecuteScalar() is not null);
    }

    public Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, DateTimeOffset until, CancellationToken ct = default)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        Run(connection, tx, "DELETE FROM slots WHERE queue = $queue AND conc_key = $key AND until <= $now", ("$queue", queue), ("$key", key), ("$now", Now));
        using var count = Command(connection, tx, "SELECT COUNT(*) FROM slots WHERE queue = $queue AND conc_key = $key", ("$queue", queue), ("$key", key));
        if ((long)count.ExecuteScalar()! >= limit)
            return Task.FromResult(false);
        Run(connection, tx, "INSERT OR REPLACE INTO slots (queue, conc_key, token, until) VALUES ($queue, $key, $token, $until)",
            ("$queue", queue), ("$key", key), ("$token", token), ("$until", until.ToUnixTimeMilliseconds()));
        tx.Commit();
        return Task.FromResult(true);
    }

    public Task RenewSlotAsync(string queue, string key, string token, DateTimeOffset until, CancellationToken ct = default)
    {
        using var connection = Open();
        Run(connection, null, "UPDATE slots SET until = $until WHERE queue = $queue AND conc_key = $key AND token = $token",
            ("$until", until.ToUnixTimeMilliseconds()), ("$queue", queue), ("$key", key), ("$token", token));
        return Task.CompletedTask;
    }

    public Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default)
    {
        using var connection = Open();
        Run(connection, null, "DELETE FROM slots WHERE queue = $queue AND conc_key = $key AND token = $token", ("$queue", queue), ("$key", key), ("$token", token));
        return Task.CompletedTask;
    }

    public Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        CircuitRecord? current = null;
        using (var select = Command(connection, tx, "SELECT failures, open_until, next_cooldown, probe_token, probe_until FROM circuits WHERE queue = $queue AND conc_key = $key",
                   ("$queue", queue), ("$key", key)))
        using (var r = select.ExecuteReader())
            if (r.Read())
                current = new CircuitRecord(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4));
        var next = change(current);
        if (next is null)
            Run(connection, tx, "DELETE FROM circuits WHERE queue = $queue AND conc_key = $key", ("$queue", queue), ("$key", key));
        else
            Run(connection, tx, """
                INSERT OR REPLACE INTO circuits (queue, conc_key, failures, open_until, next_cooldown, probe_token, probe_until)
                VALUES ($queue, $key, $failures, $openUntil, $next, $probe, $probeUntil)
                """, ("$queue", queue), ("$key", key), ("$failures", next.Failures), ("$openUntil", next.OpenUntilMs), ("$next", next.NextCooldownMs),
                ("$probe", next.ProbeToken), ("$probeUntil", next.ProbeUntilMs));
        tx.Commit();
        return Task.FromResult(next);
    }

    public Task<bool> TryTakeStartAsync(string queue, string key, RateLimit rate, DateTimeOffset now, CancellationToken ct = default)
    {
        var at = now.ToUnixTimeMilliseconds();
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        Run(connection, tx, "DELETE FROM key_starts WHERE queue = $queue AND conc_key = $key AND at <= $cut",
            ("$queue", queue), ("$key", key), ("$cut", at - (long)rate.Per.TotalMilliseconds));
        using var count = Command(connection, tx, "SELECT COUNT(*) FROM key_starts WHERE queue = $queue AND conc_key = $key", ("$queue", queue), ("$key", key));
        if ((long)count.ExecuteScalar()! >= rate.Count)
            return Task.FromResult(false);
        Run(connection, tx, "INSERT INTO key_starts (queue, conc_key, at) VALUES ($queue, $key, $at)", ("$queue", queue), ("$key", key), ("$at", at));
        tx.Commit();
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit, int Starts)>> KeysAsync(string queue, CancellationToken ct = default)
    {
        using var connection = Open();
        var running = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var select = Command(connection, null, "SELECT conc_key, COUNT(*) FROM slots WHERE queue = $queue AND until > $now GROUP BY conc_key", ("$queue", queue), ("$now", Now)))
        using (var r = select.ExecuteReader())
            while (r.Read())
                running[r.GetString(0)] = r.GetInt32(1);
        var circuits = new Dictionary<string, CircuitRecord>(StringComparer.Ordinal);
        using (var select = Command(connection, null, "SELECT conc_key, failures, open_until, next_cooldown, probe_token, probe_until FROM circuits WHERE queue = $queue", ("$queue", queue)))
        using (var r = select.ExecuteReader())
            while (r.Read())
                circuits[r.GetString(0)] = new CircuitRecord(r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5));
        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var select = Command(connection, null, "SELECT conc_key, COUNT(*) FROM key_starts WHERE queue = $queue AND at > $cut GROUP BY conc_key",
                   ("$queue", queue), ("$cut", Now - 3_600_000)))
        using (var r = select.ExecuteReader())
            while (r.Read())
                starts[r.GetString(0)] = r.GetInt32(1);
        return Task.FromResult<IReadOnlyList<(string, int, CircuitRecord?, int)>>(running.Keys.Union(circuits.Keys).Union(starts.Keys).Order(StringComparer.Ordinal)
            .Select(k => (k, running.GetValueOrDefault(k), circuits.GetValueOrDefault(k), starts.GetValueOrDefault(k))).ToList());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Run(connection, null, "PRAGMA synchronous = FULL;");
        return connection;
    }

    private static void Run(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, tx, sql, values);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
