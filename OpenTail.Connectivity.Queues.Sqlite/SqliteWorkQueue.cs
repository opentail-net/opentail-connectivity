using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenTail.Connectivity.Queues.Sqlite;

/// <summary>
/// A work queue in one SQLite file. Durable across crashes and power cuts: WAL with <c>synchronous=FULL</c>, so a job the caller
/// was told is enqueued (or completed) stays that way. Several workers, in one process or several, can share the file; every
/// change runs in an immediate transaction, so two workers never lease the same job. For one machine: a network share is not
/// safe for SQLite.
/// </summary>
public sealed class SqliteWorkQueue : IWorkQueue
{
    private const int Ready = 0, Leased = 1, Done = 2, Dead = 3, Cancelled = 4;

    private readonly string _connectionString;
    private readonly WorkQueueOptions _options;
    private readonly TimeProvider _time;

    /// <param name="path">The database file; created with its folder if missing.</param>
    public SqliteWorkQueue(string path, WorkQueueOptions? options = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } folder)
            Directory.CreateDirectory(folder);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        _options = options ?? new WorkQueueOptions();
        _time = time ?? TimeProvider.System;
        using var connection = Open();
        Execute(connection, null, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS jobs (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                queue TEXT NOT NULL,
                tenant TEXT NOT NULL,
                body TEXT NOT NULL,
                key TEXT NULL,
                headers TEXT NOT NULL,
                enqueued_at INTEGER NOT NULL,
                not_before INTEGER NOT NULL,
                state INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                leased_until INTEGER NULL,
                lease_token TEXT NULL,
                finished_at INTEGER NULL,
                dead_reason TEXT NULL,
                last_error TEXT NULL,
                source TEXT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS jobs_key ON jobs (queue, key) WHERE key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS jobs_due ON jobs (queue, state, not_before);
            CREATE INDEX IF NOT EXISTS jobs_finished ON jobs (state, finished_at);
            """);
        // Files made by the first 0.0.1 (no source column).
        using (var columns = Command(connection, null, "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = 'source'"))
            if ((long)columns.ExecuteScalar()! == 0)
                Execute(connection, null, "ALTER TABLE jobs ADD COLUMN source TEXT NULL");
    }

    public WorkQueueCapabilities Capabilities { get; } = new(Durable: true, DelayedDelivery: true, LeaseExpiry: true, DeduplicatesKeys: true);

    public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        _options.Validate(request);
        var now = Now;
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        // Finished jobs are kept only so their keys keep deduplicating.
        Execute(connection, tx, "DELETE FROM jobs WHERE state IN ($done, $cancelled) AND finished_at <= $cut",
            ("$done", Done), ("$cancelled", Cancelled), ("$cut", now - (long)_options.KeyRetentionOrDefault.TotalMilliseconds));
        var id = Guid.NewGuid().ToString("N");
        var added = Execute(connection, tx, """
            INSERT OR IGNORE INTO jobs (id, queue, tenant, body, key, headers, enqueued_at, not_before, state, source)
            VALUES ($id, $queue, $tenant, $body, $key, $headers, $now, $notBefore, $ready, $source)
            """, ("$source", request.Source),
            ("$id", id), ("$queue", request.Queue), ("$tenant", request.Tenant), ("$body", request.Body), ("$key", request.Key),
            ("$headers", JsonSerializer.Serialize(request.Headers ?? new Dictionary<string, string>())), ("$now", now),
            ("$notBefore", request.NotBefore is { } nb ? nb.ToUnixTimeMilliseconds() : now), ("$ready", Ready));
        EnqueueResult result;
        if (added == 1)
        {
            result = new EnqueueResult(id, Duplicate: false);
        }
        else
        {
            using var find = Command(connection, tx, "SELECT id FROM jobs WHERE queue = $queue AND key = $key", ("$queue", request.Queue), ("$key", request.Key));
            result = new EnqueueResult((string)find.ExecuteScalar()!, Duplicate: true);
        }

        tx.Commit();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        WorkQueueOptions.ValidateLease(max, lease);
        var now = Now;
        var until = now + (long)lease.TotalMilliseconds;
        var leased = new List<LeasedJob>();
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        while (leased.Count < max)
        {
            var due = new List<Row>();
            using (var select = Command(connection, tx, """
                SELECT id, tenant, body, key, headers, enqueued_at, attempts, source FROM jobs
                WHERE queue = $queue AND ((state = $ready AND not_before <= $now) OR (state = $leased AND leased_until <= $now))
                ORDER BY not_before, seq LIMIT $limit
                """, ("$queue", queue), ("$ready", Ready), ("$leased", Leased), ("$now", now), ("$limit", max - leased.Count)))
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read())
                    due.Add(new Row(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                        reader.GetString(4), reader.GetInt64(5), reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            if (due.Count == 0)
                break;
            foreach (var row in due)
            {
                var enqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.EnqueuedAt);
                if (WorkQueueRules.DeadReason(row.Attempts, enqueuedAt, _time.GetUtcNow(), _options) is { } reason)
                {
                    Execute(connection, tx, "UPDATE jobs SET state = $dead, dead_reason = $reason, finished_at = $now, lease_token = NULL WHERE id = $id",
                        ("$dead", Dead), ("$reason", reason), ("$now", now), ("$id", row.Id));
                    continue;
                }

                var token = Guid.NewGuid().ToString("N");
                Execute(connection, tx, "UPDATE jobs SET state = $leased, attempts = attempts + 1, leased_until = $until, lease_token = $token WHERE id = $id",
                    ("$leased", Leased), ("$until", until), ("$token", token), ("$id", row.Id));
                leased.Add(new LeasedJob(queue, row.Id, row.Tenant, row.Body, row.Key, Headers(row.Headers), row.Attempts + 1, enqueuedAt,
                    DateTimeOffset.FromUnixTimeMilliseconds(until), token, row.Source));
            }
        }

        tx.Commit();
        return Task.FromResult<IReadOnlyList<LeasedJob>>(leased);
    }

    public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default)
        => WithLease(job, "state = $done, body = '', finished_at = $now, lease_token = NULL", ("$done", Done));

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default)
        => WithLease(job, "state = $ready, not_before = $at, lease_token = NULL, leased_until = NULL, last_error = $error",
            ("$ready", Ready), ("$at", Now + (long)Math.Max(0, delay.TotalMilliseconds)), ("$error", error));

    public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return WithLease(job, "state = $dead, dead_reason = $reason, finished_at = $now, lease_token = NULL", ("$dead", Dead), ("$reason", reason));
    }

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return WithLease(job, "leased_until = $until", ("$until", Now + (long)lease.TotalMilliseconds));
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        using var connection = Open();
        using var select = Command(connection, null, """
            SELECT id, tenant, body, key, headers, attempts, enqueued_at, finished_at, dead_reason, last_error, source FROM jobs
            WHERE queue = $queue AND state = $dead ORDER BY finished_at, seq LIMIT $max
            """, ("$queue", queue), ("$dead", Dead), ("$max", max));
        using var reader = select.ExecuteReader();
        var dead = new List<DeadLetter>();
        while (reader.Read())
            dead.Add(new DeadLetter(queue, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                Headers(reader.GetString(4)), reader.GetInt32(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        return Task.FromResult<IReadOnlyList<DeadLetter>>(dead);
    }

    public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var now = Now;
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        var changed = Execute(connection, tx, """
            UPDATE jobs SET state = $ready, attempts = 0, enqueued_at = $now, not_before = $now, finished_at = NULL,
                dead_reason = NULL, last_error = NULL, lease_token = NULL, leased_until = NULL
            WHERE queue = $queue AND id = $id AND state = $dead
            """, ("$ready", Ready), ("$now", now), ("$queue", queue), ("$id", id), ("$dead", Dead));
        tx.Commit();
        return Task.FromResult(changed == 1);
    }

    public Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        using var connection = Open();
        return Task.FromResult(Execute(connection, null, "DELETE FROM jobs WHERE queue = $queue AND id = $id AND state = $dead",
            ("$queue", queue), ("$id", id), ("$dead", Dead)) == 1);
    }

    public Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        using var connection = Open();
        // Clearing the token voids a running worker's lease: its complete, retry and extend find no match.
        return Task.FromResult(Execute(connection, null, """
            UPDATE jobs SET state = $cancelled, dead_reason = $reason, finished_at = $now, body = '', lease_token = NULL, leased_until = NULL
            WHERE queue = $queue AND id = $id AND state IN ($ready, $leased)
            """, ("$cancelled", Cancelled), ("$reason", reason), ("$now", Now), ("$queue", queue), ("$id", id), ("$ready", Ready), ("$leased", Leased)) == 1);
    }

    public Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        using var connection = Open();
        return Task.FromResult(Execute(connection, null, "UPDATE jobs SET not_before = MIN(not_before, $now) WHERE queue = $queue AND id = $id AND state = $ready",
            ("$now", Now), ("$queue", queue), ("$id", id), ("$ready", Ready)) == 1);
    }

    public Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var now = Now;
        using var connection = Open();
        using var select = Command(connection, null, """
            SELECT id, tenant, key, source, headers, state, attempts, enqueued_at, not_before, leased_until, last_error, length(CAST(body AS BLOB)) FROM jobs
            WHERE queue = $queue AND state IN ($ready, $leased) ORDER BY enqueued_at, seq LIMIT $max
            """, ("$queue", queue), ("$ready", Ready), ("$leased", Leased), ("$max", max));
        using var reader = select.ExecuteReader();
        var pending = new List<PendingJob>();
        while (reader.Read())
        {
            var running = reader.GetInt32(5) == Leased && !reader.IsDBNull(9) && reader.GetInt64(9) > now;
            var notBefore = reader.GetInt64(8);
            var (state, until) = running ? (PendingState.Running, (long?)reader.GetInt64(9))
                : notBefore > now && reader.GetInt32(5) == Ready ? (PendingState.Delayed, (long?)notBefore) : (PendingState.Waiting, null);
            pending.Add(new PendingJob(queue, reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), Headers(reader.GetString(4)), state, reader.GetInt32(6),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)), until is { } u ? DateTimeOffset.FromUnixTimeMilliseconds(u) : null,
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt32(11)));
        }

        return Task.FromResult<IReadOnlyList<PendingJob>>(pending);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Applies a change only while the caller still holds the lease (same token, still leased).</summary>
    private Task<bool> WithLease(LeasedJob job, string set, params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var connection = Open();
        var changed = Execute(connection, null,
            $"UPDATE jobs SET {set} WHERE queue = $queue AND id = $id AND state = $leasedState AND lease_token = $token",
            [.. values, ("$queue", job.Queue), ("$id", job.Id), ("$leasedState", Leased), ("$token", job.LeaseToken), ("$now", Now)]);
        return Task.FromResult(changed == 1);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Per connection: FULL makes each commit durable in WAL mode (NORMAL can lose the last commits on a power cut).
        Execute(connection, null, "PRAGMA synchronous = FULL;");
        return connection;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] values)
    {
        using var command = Command(connection, tx, sql, values);
        return command.ExecuteNonQuery();
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

    private static IReadOnlyDictionary<string, string> Headers(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private sealed record Row(string Id, string Tenant, string Body, string? Key, string Headers, long EnqueuedAt, int Attempts, string? Source);
}
