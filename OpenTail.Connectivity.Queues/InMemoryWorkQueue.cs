namespace OpenTail.Connectivity.Queues;

/// <summary>
/// A work queue in process memory, for tests and throwaway work: everything is lost when the process ends. Thread safe.
/// </summary>
public sealed class InMemoryWorkQueue(WorkQueueOptions? options = null, TimeProvider? time = null) : IWorkQueue
{
    private readonly WorkQueueOptions _options = options ?? new WorkQueueOptions();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<(string Queue, string Key), CircuitRecord> _circuits = [];
    private readonly Dictionary<(string Queue, string Key), Queue<DateTimeOffset>> _starts = [];
    private long _sequence;

    public WorkQueueCapabilities Capabilities { get; } = new(Durable: false, DelayedDelivery: true, LeaseExpiry: true, DeduplicatesKeys: true);

    public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        _options.Validate(request);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            Purge(now);
            if (request.Key is { } key && _entries.FirstOrDefault(e => e.Queue == request.Queue && e.Key == key) is { } existing)
            {
                if (WorkQueueRules.KeyBlocks(request.KeyScope, existing.State == State.Ready, existing.State == State.Leased))
                    return Task.FromResult(new EnqueueResult(existing.Id, Duplicate: true));
                existing.Key = null; // the scope lets the key go: the earlier job keeps running or stays finished, without it
            }

            var entry = new Entry(request.Queue, Guid.NewGuid().ToString("N"), request.Tenant, request.Body, request.Key,
                WorkQueueOptions.HeadersWithTrace(request), now, ++_sequence)
            {
                NotBefore = request.NotBefore ?? now,
                Source = request.Source,
                ConcurrencyKey = request.ConcurrencyKey,
                MaxRunTime = request.MaxRunTime,
            };
            _entries.Add(entry);
            return Task.FromResult(new EnqueueResult(entry.Id, Duplicate: false));
        }
    }

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default)
        => LeaseAsync(queue, max, lease, [], ct);

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, IReadOnlyCollection<string> skipConcurrencyKeys,
        CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        WorkQueueOptions.ValidateLease(max, lease);
        ArgumentNullException.ThrowIfNull(skipConcurrencyKeys);
        var now = _time.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        var leased = new List<LeasedJob>();
        lock (_gate)
        {
            var running = _entries
                .Where(e => e.Queue == queue && e.State == State.Leased && e.LeasedUntil > now && e.ConcurrencyKey is not null)
                .GroupBy(e => e.ConcurrencyKey!)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var due = _entries
                .Where(e => e.Queue == queue && ((e.State == State.Ready && e.NotBefore <= now) || (e.State == State.Leased && e.LeasedUntil <= now)))
                .OrderBy(e => e.NotBefore).ThenBy(e => e.Sequence)
                .ToList();
            foreach (var e in due)
            {
                if (leased.Count == max)
                    break;
                var probe = false;
                if (e.ConcurrencyKey is { } ck)
                {
                    if (skipConcurrencyKeys.Contains(ck) || running.GetValueOrDefault(ck) >= (_options.LimitFor(ck) ?? int.MaxValue))
                        continue;
                    if (_options.RateFor(ck) is { } rate && Started(queue, ck, rate, now).Count >= rate.Count)
                        continue;
                    (var allowed, probe) = Circuits.MayRelease(_circuits.GetValueOrDefault((queue, ck)), nowMs, _options);
                    if (!allowed)
                        continue; // not released: no attempt used
                }

                if (WorkQueueRules.DeadReason(e.Attempts, e.EnqueuedAt, now, _options) is { } reason)
                {
                    e.Kill(reason, now);
                    continue;
                }

                e.State = State.Leased;
                e.Attempts++;
                e.LeasedAt = now;
                e.LeasedUntil = now + lease;
                e.LeaseToken = Guid.NewGuid().ToString("N");
                e.Progress = null;
                e.LastHeartbeat = null;
                if (e.ConcurrencyKey is { } key)
                {
                    running[key] = running.GetValueOrDefault(key) + 1;
                    if (_options.RateFor(key) is { } rate)
                        Started(queue, key, rate, now).Enqueue(now);
                    if (probe)
                        _circuits[(queue, key)] = Circuits.ProbeReleased(_circuits[(queue, key)], e.LeaseToken, e.LeasedUntil.ToUnixTimeMilliseconds());
                }

                leased.Add(new LeasedJob(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, e.Attempts, e.EnqueuedAt, e.LeasedUntil, e.LeaseToken,
                    e.Source, e.ConcurrencyKey, e.MaxRunTime ?? _options.DefaultMaxRunTime));
            }
        }

        return Task.FromResult<IReadOnlyList<LeasedJob>>(leased);
    }

    public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default)
        => WithLease(job, e =>
        {
            e.State = State.Done;
            e.FinishedAt = _time.GetUtcNow();
            if (e.ConcurrencyKey is { } key)
                _circuits.Remove((e.Queue, key)); // the resource worked
        });

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default)
        => RetryAsync(job, delay, error, resourceFailure: false, ct);

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error, bool resourceFailure, CancellationToken ct = default)
        => WithLease(job, e =>
        {
            var now = _time.GetUtcNow();
            if (e.ConcurrencyKey is { } key)
            {
                var circuit = _circuits.GetValueOrDefault((e.Queue, key));
                var next = resourceFailure ? Circuits.ResourceFailed(circuit, now.ToUnixTimeMilliseconds(), _options) : Circuits.Settled(circuit, job.LeaseToken);
                if (next is null) _circuits.Remove((e.Queue, key));
                else _circuits[(e.Queue, key)] = next;
            }

            e.State = State.Ready;
            e.NotBefore = now + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
            e.LeaseToken = null;
            e.LastError = error;
        });

    public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default)
        => WithLease(job, e =>
        {
            FreeProbe(e, job.LeaseToken);
            e.Kill(reason, _time.GetUtcNow());
        });

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default)
        => ExtendAsync(job, lease, progress: null, lastHeartbeat: null, ct);

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, string? progress, DateTimeOffset? lastHeartbeat, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return WithLease(job, e =>
        {
            e.LeasedUntil = _time.GetUtcNow() + lease;
            if (progress is not null) e.Progress = progress.Length <= 200 ? progress : progress[..200];
            if (lastHeartbeat is not null) e.LastHeartbeat = lastHeartbeat;
        });
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Purge(_time.GetUtcNow());
            return Task.FromResult<IReadOnlyList<DeadLetter>>(_entries
                .Where(e => e.Queue == queue && e.State == State.Dead)
                .OrderBy(e => e.FinishedAt).ThenBy(e => e.Sequence)
                .Take(max)
                .Select(e => new DeadLetter(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, e.Attempts, e.EnqueuedAt, e.FinishedAt, e.DeadReason!,
                    e.LastError, e.Source, e.ConcurrencyKey))
                .ToList());
        }
    }

    public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default)
        => RedriveAsync(queue, id, _time.GetUtcNow(), ct);

    public Task<bool> RedriveAsync(string queue, string id, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Queue == queue && e.Id == id && e.State == State.Dead) is not { } e)
                return Task.FromResult(false);
            _entries.Remove(e);
            _entries.Add(new Entry(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, now, ++_sequence)
            {
                NotBefore = notBefore > now ? notBefore : now, Source = e.Source, ConcurrencyKey = e.ConcurrencyKey, MaxRunTime = e.MaxRunTime,
            });
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_entries.RemoveAll(e => e.Queue == queue && e.Id == id && e.State == State.Dead) == 1);
    }

    public Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Queue == queue && e.Id == id && e.State is State.Ready or State.Leased) is not { } e)
                return Task.FromResult(false);
            if (e.LeaseToken is { } token)
                FreeProbe(e, token);
            e.State = State.Cancelled;
            e.DeadReason = reason;
            e.FinishedAt = now;
            e.LeaseToken = null; // a running worker's lease is void from here
            return Task.FromResult(true);
        }
    }

    public Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Queue == queue && e.Id == id && e.State == State.Ready) is not { } e)
                return Task.FromResult(false);
            if (e.NotBefore > now) e.NotBefore = now;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<PendingJob>>(_entries
                .Where(e => e.Queue == queue && e.State is State.Ready or State.Leased)
                .OrderBy(e => e.EnqueuedAt).ThenBy(e => e.Sequence)
                .Take(max)
                .Select(e =>
                {
                    var running = e.State == State.Leased && e.LeasedUntil > now;
                    return new PendingJob(e.Queue, e.Id, e.Tenant, e.Key, e.Source, e.Headers,
                        running ? PendingState.Running : e.NotBefore > now ? PendingState.Delayed : PendingState.Waiting,
                        e.Attempts, e.EnqueuedAt, running ? e.LeasedUntil : e.NotBefore > now ? e.NotBefore : null,
                        e.LastError, System.Text.Encoding.UTF8.GetByteCount(e.Body), e.ConcurrencyKey, e.MaxRunTime ?? _options.DefaultMaxRunTime,
                        running ? e.LeasedAt : null, running ? e.Progress : null, running ? e.LastHeartbeat : null);
                })
                .ToList());
        }
    }

    public Task<IReadOnlyList<ConcurrencyKeyState>> ConcurrencyKeysAsync(string queue, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var nowMs = now.ToUnixTimeMilliseconds();
        lock (_gate)
        {
            var keys = _entries.Where(e => e.Queue == queue && e.ConcurrencyKey is not null && e.State is State.Ready or State.Leased)
                .Select(e => e.ConcurrencyKey!)
                .Concat(_circuits.Keys.Where(k => k.Queue == queue).Select(k => k.Key))
                .Concat(_starts.Where(k => k.Key.Queue == queue && k.Value.Count > 0).Select(k => k.Key.Key))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<ConcurrencyKeyState>>(keys.Select(k =>
            {
                var jobs = _entries.Where(e => e.Queue == queue && e.ConcurrencyKey == k).ToList();
                var circuit = _circuits.GetValueOrDefault((queue, k));
                var state = Circuits.State(circuit, nowMs, _options);
                return new ConcurrencyKeyState(queue, k,
                    jobs.Count(e => e.State == State.Leased && e.LeasedUntil > now),
                    jobs.Count(e => e.State == State.Ready || (e.State == State.Leased && e.LeasedUntil <= now)),
                    _options.LimitFor(k), state, state == CircuitState.Open ? DateTimeOffset.FromUnixTimeMilliseconds(circuit!.OpenUntilMs) : null,
                    circuit?.Failures ?? 0, _options.RateFor(k), _options.RateFor(k) is { } rate ? Started(queue, k, rate, now).Count : 0);
            }).ToList());
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The key's start times within the rate window (older ones dropped).</summary>
    private Queue<DateTimeOffset> Started(string queue, string key, JobRateLimit rate, DateTimeOffset now)
    {
        if (!_starts.TryGetValue((queue, key), out var starts))
            _starts[(queue, key)] = starts = new Queue<DateTimeOffset>();
        while (starts.Count > 0 && starts.Peek() <= now - rate.Per)
            starts.Dequeue();
        return starts;
    }

    private void FreeProbe(Entry e, string token)
    {
        if (e.ConcurrencyKey is { } key && Circuits.Settled(_circuits.GetValueOrDefault((e.Queue, key)), token) is { } settled)
            _circuits[(e.Queue, key)] = settled;
    }

    /// <summary>Drops finished jobs past key retention and dead letters past dead-letter retention.</summary>
    private void Purge(DateTimeOffset now)
        => _entries.RemoveAll(e =>
            (e.State is State.Done or State.Cancelled && e.FinishedAt + _options.KeyRetentionOrDefault <= now)
            || (e.State == State.Dead && e.FinishedAt + _options.DeadLetterRetentionOrDefault <= now));

    private Task<bool> WithLease(LeasedJob job, Action<Entry> change)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Queue == job.Queue && x.Id == job.Id);
            if (e is null || e.State != State.Leased || e.LeaseToken != job.LeaseToken)
                return Task.FromResult(false);
            change(e);
            return Task.FromResult(true);
        }
    }

    private enum State { Ready, Leased, Done, Dead, Cancelled }

    private sealed class Entry(
        string queue, string id, string tenant, string body, string? key, IReadOnlyDictionary<string, string> headers, DateTimeOffset enqueuedAt, long sequence)
    {
        public string Queue { get; } = queue;
        public string Id { get; } = id;
        public string Tenant { get; } = tenant;
        public string Body { get; } = body;
        public string? Key { get; set; } = key;
        public IReadOnlyDictionary<string, string> Headers { get; } = headers;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public long Sequence { get; } = sequence;
        public State State { get; set; }
        public DateTimeOffset NotBefore { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset LeasedAt { get; set; }
        public DateTimeOffset LeasedUntil { get; set; }
        public string? LeaseToken { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string? DeadReason { get; set; }
        public string? LastError { get; set; }
        public string? Source { get; init; }
        public string? Progress { get; set; }
        public DateTimeOffset? LastHeartbeat { get; set; }
        public string? ConcurrencyKey { get; init; }
        public TimeSpan? MaxRunTime { get; init; }

        public void Kill(string reason, DateTimeOffset now)
        {
            State = State.Dead;
            DeadReason = reason;
            FinishedAt = now;
            LeaseToken = null;
        }
    }
}

/// <summary>Rules every implementation applies the same way.</summary>
public static class WorkQueueRules
{
    /// <summary>Why a job about to be leased goes to the dead letters instead, or null when it may run.</summary>
    /// <summary>Whether an existing job with the same key keeps a new one out, under the new request's scope.</summary>
    public static bool KeyBlocks(KeyScope scope, bool existingWaiting, bool existingRunning) => scope switch
    {
        KeyScope.UntilFinished => existingWaiting || existingRunning,
        KeyScope.WhilePending => existingWaiting,
        _ => true,
    };

    public static string? DeadReason(int attemptsSoFar, DateTimeOffset enqueuedAt, DateTimeOffset now, WorkQueueOptions options)
    {
        if (attemptsSoFar >= options.MaxAttempts)
            return $"gave up after {attemptsSoFar} attempts";
        if (now - enqueuedAt > options.MaxAgeOrDefault)
            return $"older than the {options.MaxAgeOrDefault.TotalHours:0.##}-hour limit";
        return null;
    }
}
