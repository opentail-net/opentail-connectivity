using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OpenTail.Connectivity.Queues.Sqlite;

/// <summary>
/// A work queue in one SQLite file. Durable across crashes and power cuts: WAL with <c>synchronous=FULL</c>, so a job the caller
/// was told is enqueued (or completed) stays that way. Several workers, in one process or several, can share the file; every
/// change runs in an immediate transaction, so two workers never lease the same job, and concurrency limits and circuits are
/// exact. For one machine: a network share is not safe for SQLite.
/// </summary>
public sealed class SqliteWorkQueue : IWorkQueue
{
    private const int Ready = 0, Leased = 1, Done = 2, Dead = 3, Cancelled = 4;

    // A lease looks at most this many due jobs per call, skipping those whose concurrency key is full or whose circuit is open.
    private const int ScanLimit = 500;

    private const string JobColumns = "id, tenant, body, key, headers, enqueued_at, attempts, source, conc_key, max_run_ms";

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
                last_error TEXT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS jobs_key ON jobs (queue, key) WHERE key IS NOT NULL;
            CREATE INDEX IF NOT EXISTS jobs_due ON jobs (queue, state, not_before);
            CREATE INDEX IF NOT EXISTS jobs_finished ON jobs (state, finished_at);
            CREATE TABLE IF NOT EXISTS circuits (
                queue TEXT NOT NULL,
                conc_key TEXT NOT NULL,
                failures INTEGER NOT NULL,
                open_until INTEGER NOT NULL,
                next_cooldown INTEGER NOT NULL,
                probe_token TEXT NULL,
                probe_until INTEGER NOT NULL,
                PRIMARY KEY (queue, conc_key));
            CREATE TABLE IF NOT EXISTS key_starts (queue TEXT NOT NULL, conc_key TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS key_starts_at ON key_starts (queue, conc_key, at);
            """);
        // Columns added after the first 0.0.1; files made before get them here.
        foreach (var (column, type) in new[] { ("source", "TEXT"), ("conc_key", "TEXT"), ("max_run_ms", "INTEGER"), ("leased_at", "INTEGER"), ("progress", "TEXT"), ("heartbeat_at", "INTEGER") })
        {
            using var exists = Command(connection, null, "SELECT COUNT(*) FROM pragma_table_info('jobs') WHERE name = $c", ("$c", column));
            if ((long)exists.ExecuteScalar()! == 0)
                Execute(connection, null, $"ALTER TABLE jobs ADD COLUMN {column} {type} NULL");
        }

        Execute(connection, null, "CREATE INDEX IF NOT EXISTS jobs_conc ON jobs (queue, conc_key, state)");
    }

    public WorkQueueCapabilities Capabilities { get; } = new(Durable: true, DelayedDelivery: true, LeaseExpiry: true, DeduplicatesKeys: true);

    public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        _options.Validate(request);
        var now = Now;
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        Purge(connection, tx, now);
        if (request.Key is { } key)
        {
            // The new request's scope decides whether the earlier job with this key keeps it out, or lets the key go.
            using var find = Command(connection, tx, "SELECT id, state FROM jobs WHERE queue = $queue AND key = $key", ("$queue", request.Queue), ("$key", key));
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                var (existingId, state) = (reader.GetString(0), reader.GetInt32(1));
                reader.Close();
                if (WorkQueueRules.KeyBlocks(request.KeyScope, state == Ready, state == Leased))
                {
                    tx.Commit();
                    return Task.FromResult(new EnqueueResult(existingId, Duplicate: true));
                }

                Execute(connection, tx, "UPDATE jobs SET key = NULL WHERE id = $id", ("$id", existingId));
            }
        }

        var id = Guid.NewGuid().ToString("N");
        var added = Execute(connection, tx, """
            INSERT OR IGNORE INTO jobs (id, queue, tenant, body, key, headers, enqueued_at, not_before, state, source, conc_key, max_run_ms)
            VALUES ($id, $queue, $tenant, $body, $key, $headers, $now, $notBefore, $ready, $source, $conc, $maxRun)
            """,
            ("$id", id), ("$queue", request.Queue), ("$tenant", request.Tenant), ("$body", request.Body), ("$key", request.Key),
            ("$headers", JsonSerializer.Serialize(WorkQueueOptions.HeadersWithTrace(request))), ("$now", now),
            ("$notBefore", request.NotBefore is { } nb ? nb.ToUnixTimeMilliseconds() : now), ("$ready", Ready), ("$source", request.Source),
            ("$conc", request.ConcurrencyKey), ("$maxRun", request.MaxRunTime is { } m ? (long)m.TotalMilliseconds : null));
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
        => LeaseAsync(queue, max, lease, [], ct);

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, IReadOnlyCollection<string> skipConcurrencyKeys,
        CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        WorkQueueOptions.ValidateLease(max, lease);
        ArgumentNullException.ThrowIfNull(skipConcurrencyKeys);
        var now = Now;
        var until = now + (long)lease.TotalMilliseconds;
        var leased = new List<LeasedJob>();
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);

        var running = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var count = Command(connection, tx, """
            SELECT conc_key, COUNT(*) FROM jobs WHERE queue = $queue AND state = $leased AND leased_until > $now AND conc_key IS NOT NULL GROUP BY conc_key
            """, ("$queue", queue), ("$leased", Leased), ("$now", now)))
        using (var reader = count.ExecuteReader())
            while (reader.Read())
                running[reader.GetString(0)] = reader.GetInt32(1);

        // Keys that cannot release anything now are left out of the query itself, so a long run of jobs for one full or failing
        // resource at the head of the queue cannot hide jobs for other resources behind the scan limit.
        var circuits = new Dictionary<string, CircuitRecord?>(StringComparer.Ordinal);
        using (var select = Command(connection, tx, "SELECT conc_key, failures, open_until, next_cooldown, probe_token, probe_until FROM circuits WHERE queue = $queue",
                   ("$queue", queue)))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                circuits[reader.GetString(0)] = new CircuitRecord(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), NullableString(reader, 4), reader.GetInt64(5));
        // Starts within each rate window. Purged past the longest window.
        var started = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var longest = _options.RateLimits is { Count: > 0 } rates ? (long)rates.Values.Max(r => r.Per.TotalMilliseconds) : 0;
        if (longest > 0)
        {
            Execute(connection, tx, "DELETE FROM key_starts WHERE queue = $queue AND at <= $cut", ("$queue", queue), ("$cut", now - longest));
            using var select = Command(connection, tx, "SELECT conc_key, at FROM key_starts WHERE queue = $queue", ("$queue", queue));
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                var k = reader.GetString(0);
                if (_options.RateFor(k) is { } rate && reader.GetInt64(1) > now - (long)rate.Per.TotalMilliseconds)
                    (started.TryGetValue(k, out var list) ? list : started[k] = []).Add(reader.GetInt64(1));
            }
        }

        bool RateFull(string k) => _options.RateFor(k) is { } r && started.GetValueOrDefault(k)?.Count >= r.Count;

        var excluded = skipConcurrencyKeys
            .Concat(running.Where(r => r.Value >= (_options.LimitFor(r.Key) ?? int.MaxValue)).Select(r => r.Key))
            .Concat(started.Keys.Where(RateFull))
            .Concat(circuits.Where(c => !Circuits.MayRelease(c.Value, now, _options).Allowed).Select(c => c.Key))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exclusion = excluded.Count == 0 ? "" : $" AND (conc_key IS NULL OR conc_key NOT IN ({string.Join(", ", excluded.Select((_, i) => "$x" + i))}))";

        var due = new List<Row>();
        using (var select = Command(connection, tx, $"""
            SELECT {JobColumns} FROM jobs
            WHERE queue = $queue AND ((state = $ready AND not_before <= $now) OR (state = $leased AND leased_until <= $now)){exclusion}
            ORDER BY not_before, seq LIMIT $limit
            """, [("$queue", queue), ("$ready", Ready), ("$leased", Leased), ("$now", now), ("$limit", ScanLimit),
                .. excluded.Select((k, i) => ("$x" + i, (object?)k))]))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                due.Add(ReadRow(reader));

        foreach (var row in due)
        {
            if (leased.Count == max)
                break;
            var probe = false;
            if (row.ConcurrencyKey is { } ck)
            {
                if (skipConcurrencyKeys.Contains(ck) || running.GetValueOrDefault(ck) >= (_options.LimitFor(ck) ?? int.MaxValue) || RateFull(ck))
                    continue;
                if (!circuits.TryGetValue(ck, out var circuit))
                    circuits[ck] = circuit = ReadCircuit(connection, tx, queue, ck);
                (var allowed, probe) = Circuits.MayRelease(circuit, now, _options);
                if (!allowed)
                    continue; // not released: no attempt used
            }

            var enqueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(row.EnqueuedAt);
            if (WorkQueueRules.DeadReason(row.Attempts, enqueuedAt, _time.GetUtcNow(), _options) is { } reason)
            {
                Execute(connection, tx, "UPDATE jobs SET state = $dead, dead_reason = $reason, finished_at = $now, lease_token = NULL WHERE id = $id",
                    ("$dead", Dead), ("$reason", reason), ("$now", now), ("$id", row.Id));
                continue;
            }

            var token = Guid.NewGuid().ToString("N");
            Execute(connection, tx, """
                UPDATE jobs SET state = $leased, attempts = attempts + 1, leased_at = $now, leased_until = $until, lease_token = $token,
                    progress = NULL, heartbeat_at = NULL
                WHERE id = $id
                """, ("$leased", Leased), ("$now", now), ("$until", until), ("$token", token), ("$id", row.Id));
            if (row.ConcurrencyKey is { } key)
            {
                running[key] = running.GetValueOrDefault(key) + 1;
                if (_options.RateFor(key) is not null)
                {
                    Execute(connection, tx, "INSERT INTO key_starts (queue, conc_key, at) VALUES ($queue, $key, $now)", ("$queue", queue), ("$key", key), ("$now", now));
                    (started.TryGetValue(key, out var list) ? list : started[key] = []).Add(now);
                }
                if (probe)
                {
                    circuits[key] = Circuits.ProbeReleased(circuits[key]!, token, until);
                    WriteCircuit(connection, tx, queue, key, circuits[key]);
                }
            }

            leased.Add(new LeasedJob(queue, row.Id, row.Tenant, row.Body, row.Key, Headers(row.Headers), row.Attempts + 1, enqueuedAt,
                DateTimeOffset.FromUnixTimeMilliseconds(until), token, row.Source, row.ConcurrencyKey, RunTime(row.MaxRunMs)));
        }

        tx.Commit();
        return Task.FromResult<IReadOnlyList<LeasedJob>>(leased);
    }

    public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default)
        => WithLease(job, "state = $done, body = '', finished_at = $now, lease_token = NULL", (connection, tx) =>
        {
            if (job.ConcurrencyKey is { } key)
                Execute(connection, tx, "DELETE FROM circuits WHERE queue = $queue AND conc_key = $key", ("$queue", job.Queue), ("$key", key));
        }, ("$done", Done));

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default)
        => RetryAsync(job, delay, error, resourceFailure: false, ct);

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error, bool resourceFailure, CancellationToken ct = default)
        => WithLease(job, "state = $ready, not_before = $at, lease_token = NULL, leased_until = NULL, last_error = $error", (connection, tx) =>
        {
            if (job.ConcurrencyKey is not { } key) return;
            var circuit = ReadCircuit(connection, tx, job.Queue, key);
            WriteCircuit(connection, tx, job.Queue, key, resourceFailure ? Circuits.ResourceFailed(circuit, Now, _options) : Circuits.Settled(circuit, job.LeaseToken));
        }, ("$ready", Ready), ("$at", Now + (long)Math.Max(0, delay.TotalMilliseconds)), ("$error", error));

    public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return WithLease(job, "state = $dead, dead_reason = $reason, finished_at = $now, lease_token = NULL",
            (connection, tx) => FreeProbe(connection, tx, job.Queue, job.ConcurrencyKey, job.LeaseToken), ("$dead", Dead), ("$reason", reason));
    }

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default)
        => ExtendAsync(job, lease, progress: null, lastHeartbeat: null, ct);

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, string? progress, DateTimeOffset? lastHeartbeat, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return WithLease(job, "leased_until = $until, progress = COALESCE($progress, progress), heartbeat_at = COALESCE($beat, heartbeat_at)", null,
            ("$until", Now + (long)lease.TotalMilliseconds), ("$progress", progress is { Length: > 200 } ? progress[..200] : progress),
            ("$beat", lastHeartbeat?.ToUnixTimeMilliseconds()));
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        using var connection = Open();
        Purge(connection, null, Now);
        using var select = Command(connection, null, """
            SELECT id, tenant, body, key, headers, attempts, enqueued_at, finished_at, dead_reason, last_error, source, conc_key FROM jobs
            WHERE queue = $queue AND state = $dead ORDER BY finished_at, seq LIMIT $max
            """, ("$queue", queue), ("$dead", Dead), ("$max", max));
        using var reader = select.ExecuteReader();
        var dead = new List<DeadLetter>();
        while (reader.Read())
            dead.Add(new DeadLetter(queue, reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
                Headers(reader.GetString(4)), reader.GetInt32(5), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)), reader.GetString(8), NullableString(reader, 9),
                NullableString(reader, 10), NullableString(reader, 11)));
        return Task.FromResult<IReadOnlyList<DeadLetter>>(dead);
    }

    public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default)
        => RedriveAsync(queue, id, _time.GetUtcNow(), ct);

    public Task<bool> RedriveAsync(string queue, string id, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var now = Now;
        using var connection = Open();
        var changed = Execute(connection, null, """
            UPDATE jobs SET state = $ready, attempts = 0, enqueued_at = $now, not_before = MAX($now, $notBefore), finished_at = NULL,
                dead_reason = NULL, last_error = NULL, lease_token = NULL, leased_until = NULL
            WHERE queue = $queue AND id = $id AND state = $dead
            """, ("$ready", Ready), ("$now", now), ("$notBefore", notBefore.ToUnixTimeMilliseconds()), ("$queue", queue), ("$id", id), ("$dead", Dead));
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
        using var tx = connection.BeginTransaction(deferred: false);
        string? key = null, token = null;
        using (var find = Command(connection, tx, "SELECT conc_key, lease_token FROM jobs WHERE queue = $queue AND id = $id", ("$queue", queue), ("$id", id)))
        using (var reader = find.ExecuteReader())
            if (reader.Read())
                (key, token) = (NullableString(reader, 0), NullableString(reader, 1));
        // Clearing the token voids a running worker's lease: its complete, retry and extend find no match.
        var changed = Execute(connection, tx, """
            UPDATE jobs SET state = $cancelled, dead_reason = $reason, finished_at = $now, body = '', lease_token = NULL, leased_until = NULL
            WHERE queue = $queue AND id = $id AND state IN ($ready, $leased)
            """, ("$cancelled", Cancelled), ("$reason", reason), ("$now", Now), ("$queue", queue), ("$id", id), ("$ready", Ready), ("$leased", Leased));
        if (changed == 1 && token is not null)
            FreeProbe(connection, tx, queue, key, token);
        tx.Commit();
        return Task.FromResult(changed == 1);
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
            SELECT id, tenant, key, source, headers, state, attempts, enqueued_at, not_before, leased_until, last_error, length(CAST(body AS BLOB)),
                conc_key, max_run_ms, leased_at, progress, heartbeat_at
            FROM jobs WHERE queue = $queue AND state IN ($ready, $leased) ORDER BY enqueued_at, seq LIMIT $max
            """, ("$queue", queue), ("$ready", Ready), ("$leased", Leased), ("$max", max));
        using var reader = select.ExecuteReader();
        var pending = new List<PendingJob>();
        while (reader.Read())
        {
            var running = reader.GetInt32(5) == Leased && !reader.IsDBNull(9) && reader.GetInt64(9) > now;
            var notBefore = reader.GetInt64(8);
            var (state, until) = running ? (PendingState.Running, (long?)reader.GetInt64(9))
                : notBefore > now && reader.GetInt32(5) == Ready ? (PendingState.Delayed, (long?)notBefore) : (PendingState.Waiting, null);
            pending.Add(new PendingJob(queue, reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3),
                Headers(reader.GetString(4)), state, reader.GetInt32(6), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                until is { } u ? DateTimeOffset.FromUnixTimeMilliseconds(u) : null, NullableString(reader, 10), reader.GetInt32(11),
                NullableString(reader, 12), RunTime(reader.IsDBNull(13) ? null : reader.GetInt64(13)),
                running && !reader.IsDBNull(14) ? DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)) : null,
                running ? NullableString(reader, 15) : null,
                running && !reader.IsDBNull(16) ? DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(16)) : null));
        }

        return Task.FromResult<IReadOnlyList<PendingJob>>(pending);
    }

    public Task<IReadOnlyList<ConcurrencyKeyState>> ConcurrencyKeysAsync(string queue, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        var now = Now;
        using var connection = Open();
        var counts = new Dictionary<string, (int Running, int Waiting)>(StringComparer.Ordinal);
        using (var select = Command(connection, null, """
            SELECT conc_key,
                SUM(CASE WHEN state = $leased AND leased_until > $now THEN 1 ELSE 0 END),
                SUM(CASE WHEN state = $ready OR (state = $leased AND leased_until <= $now) THEN 1 ELSE 0 END)
            FROM jobs WHERE queue = $queue AND conc_key IS NOT NULL AND state IN ($ready, $leased) GROUP BY conc_key
            """, ("$queue", queue), ("$ready", Ready), ("$leased", Leased), ("$now", now)))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                counts[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        var circuits = new Dictionary<string, CircuitRecord>(StringComparer.Ordinal);
        using (var select = Command(connection, null, "SELECT conc_key, failures, open_until, next_cooldown, probe_token, probe_until FROM circuits WHERE queue = $queue",
                   ("$queue", queue)))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                circuits[reader.GetString(0)] = new CircuitRecord(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), NullableString(reader, 4), reader.GetInt64(5));

        var starts = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var select = Command(connection, null, "SELECT conc_key, at FROM key_starts WHERE queue = $queue", ("$queue", queue)))
        using (var reader = select.ExecuteReader())
            while (reader.Read())
                if (_options.RateFor(reader.GetString(0)) is { } rate && reader.GetInt64(1) > now - (long)rate.Per.TotalMilliseconds)
                    starts[reader.GetString(0)] = starts.GetValueOrDefault(reader.GetString(0)) + 1;

        return Task.FromResult<IReadOnlyList<ConcurrencyKeyState>>(counts.Keys.Union(circuits.Keys).Union(starts.Keys).Order(StringComparer.Ordinal).Select(k =>
        {
            var c = circuits.GetValueOrDefault(k);
            var state = Circuits.State(c, now, _options);
            var (r, w) = counts.GetValueOrDefault(k);
            return new ConcurrencyKeyState(queue, k, r, w, _options.LimitFor(k), state,
                state == CircuitState.Open ? DateTimeOffset.FromUnixTimeMilliseconds(c!.OpenUntilMs) : null, c?.Failures ?? 0,
                _options.RateFor(k), starts.GetValueOrDefault(k));
        }).ToList());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private TimeSpan? RunTime(long? ms) => ms is { } m ? TimeSpan.FromMilliseconds(m) : _options.DefaultMaxRunTime;

    /// <summary>Finished jobs are kept only so their keys keep deduplicating; dead letters until their retention ends.</summary>
    private void Purge(SqliteConnection connection, SqliteTransaction? tx, long now)
        => Execute(connection, tx, """
            DELETE FROM jobs WHERE (state IN ($done, $cancelled) AND finished_at <= $keyCut) OR (state = $dead AND finished_at <= $deadCut)
            """, ("$done", Done), ("$cancelled", Cancelled), ("$dead", Dead),
            ("$keyCut", now - (long)_options.KeyRetentionOrDefault.TotalMilliseconds),
            ("$deadCut", now - (long)_options.DeadLetterRetentionOrDefault.TotalMilliseconds));

    /// <summary>Applies a change only while the caller still holds the lease (same token, still leased), with any side effect on the
    /// circuit in the same transaction.</summary>
    private Task<bool> WithLease(LeasedJob job, string set, Action<SqliteConnection, SqliteTransaction>? also, params (string Name, object? Value)[] values)
    {
        ArgumentNullException.ThrowIfNull(job);
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        var changed = Execute(connection, tx,
            $"UPDATE jobs SET {set} WHERE queue = $queue AND id = $id AND state = $leasedState AND lease_token = $token",
            [.. values, ("$queue", job.Queue), ("$id", job.Id), ("$leasedState", Leased), ("$token", job.LeaseToken), ("$now", Now)]);
        if (changed == 1)
            also?.Invoke(connection, tx);
        tx.Commit();
        return Task.FromResult(changed == 1);
    }

    private void FreeProbe(SqliteConnection connection, SqliteTransaction tx, string queue, string? key, string token)
    {
        if (key is not null && ReadCircuit(connection, tx, queue, key) is { } circuit)
            WriteCircuit(connection, tx, queue, key, Circuits.Settled(circuit, token));
    }

    private static CircuitRecord? ReadCircuit(SqliteConnection connection, SqliteTransaction? tx, string queue, string key)
    {
        using var select = Command(connection, tx, "SELECT failures, open_until, next_cooldown, probe_token, probe_until FROM circuits WHERE queue = $queue AND conc_key = $key",
            ("$queue", queue), ("$key", key));
        using var reader = select.ExecuteReader();
        return reader.Read() ? new CircuitRecord(reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2), NullableString(reader, 3), reader.GetInt64(4)) : null;
    }

    private static void WriteCircuit(SqliteConnection connection, SqliteTransaction tx, string queue, string key, CircuitRecord? c)
    {
        if (c is null)
        {
            Execute(connection, tx, "DELETE FROM circuits WHERE queue = $queue AND conc_key = $key", ("$queue", queue), ("$key", key));
            return;
        }

        Execute(connection, tx, """
            INSERT OR REPLACE INTO circuits (queue, conc_key, failures, open_until, next_cooldown, probe_token, probe_until)
            VALUES ($queue, $key, $failures, $openUntil, $next, $probe, $probeUntil)
            """, ("$queue", queue), ("$key", key), ("$failures", c.Failures), ("$openUntil", c.OpenUntilMs), ("$next", c.NextCooldownMs),
            ("$probe", c.ProbeToken), ("$probeUntil", c.ProbeUntilMs));
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

    private static Row ReadRow(SqliteDataReader r)
        => new(r.GetString(0), r.GetString(1), r.GetString(2), NullableString(r, 3), r.GetString(4), r.GetInt64(5), r.GetInt32(6),
            NullableString(r, 7), NullableString(r, 8), r.IsDBNull(9) ? null : r.GetInt64(9));

    private static string? NullableString(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static IReadOnlyDictionary<string, string> Headers(string json)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private sealed record Row(
        string Id, string Tenant, string Body, string? Key, string Headers, long EnqueuedAt, int Attempts, string? Source, string? ConcurrencyKey, long? MaxRunMs);
}
