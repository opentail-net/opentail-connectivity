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
    private long _sequence;

    public WorkQueueCapabilities Capabilities { get; } = new(Durable: false, DelayedDelivery: true, LeaseExpiry: true, DeduplicatesKeys: true);

    public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default)
    {
        _options.Validate(request);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _entries.RemoveAll(e => e.State is State.Done or State.Cancelled && e.FinishedAt + _options.KeyRetentionOrDefault <= now);
            if (request.Key is { } key && _entries.FirstOrDefault(e => e.Queue == request.Queue && e.Key == key) is { } existing)
                return Task.FromResult(new EnqueueResult(existing.Id, Duplicate: true));
            var entry = new Entry(request.Queue, Guid.NewGuid().ToString("N"), request.Tenant, request.Body, request.Key,
                new Dictionary<string, string>(request.Headers ?? new Dictionary<string, string>(), StringComparer.Ordinal), now, ++_sequence)
            {
                NotBefore = request.NotBefore ?? now,
                Source = request.Source,
            };
            _entries.Add(entry);
            return Task.FromResult(new EnqueueResult(entry.Id, Duplicate: false));
        }
    }

    public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default)
    {
        WorkQueueOptions.ValidateQueueName(queue);
        WorkQueueOptions.ValidateLease(max, lease);
        var now = _time.GetUtcNow();
        var leased = new List<LeasedJob>();
        lock (_gate)
        {
            var due = _entries
                .Where(e => e.Queue == queue && ((e.State == State.Ready && e.NotBefore <= now) || (e.State == State.Leased && e.LeasedUntil <= now)))
                .OrderBy(e => e.NotBefore).ThenBy(e => e.Sequence)
                .ToList();
            foreach (var e in due)
            {
                if (leased.Count == max)
                    break;
                if (WorkQueueRules.DeadReason(e.Attempts, e.EnqueuedAt, now, _options) is { } reason)
                {
                    e.Kill(reason, now);
                    continue;
                }

                e.State = State.Leased;
                e.Attempts++;
                e.LeasedUntil = now + lease;
                e.LeaseToken = Guid.NewGuid().ToString("N");
                leased.Add(new LeasedJob(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, e.Attempts, e.EnqueuedAt, e.LeasedUntil, e.LeaseToken, e.Source));
            }
        }

        return Task.FromResult<IReadOnlyList<LeasedJob>>(leased);
    }

    public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default)
        => WithLease(job, e =>
        {
            e.State = State.Done;
            e.FinishedAt = _time.GetUtcNow();
        });

    public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default)
        => WithLease(job, e =>
        {
            e.State = State.Ready;
            e.NotBefore = _time.GetUtcNow() + (delay < TimeSpan.Zero ? TimeSpan.Zero : delay);
            e.LeaseToken = null;
            e.LastError = error;
        });

    public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default)
        => WithLease(job, e => e.Kill(reason, _time.GetUtcNow()));

    public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
        return WithLease(job, e => e.LeasedUntil = _time.GetUtcNow() + lease);
    }

    public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DeadLetter>>(_entries
                .Where(e => e.Queue == queue && e.State == State.Dead)
                .OrderBy(e => e.FinishedAt).ThenBy(e => e.Sequence)
                .Take(max)
                .Select(e => new DeadLetter(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, e.Attempts, e.EnqueuedAt, e.FinishedAt, e.DeadReason!, e.LastError, e.Source))
                .ToList());
        }
    }

    public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_entries.FirstOrDefault(e => e.Queue == queue && e.Id == id && e.State == State.Dead) is not { } e)
                return Task.FromResult(false);
            _entries.Remove(e);
            _entries.Add(new Entry(e.Queue, e.Id, e.Tenant, e.Body, e.Key, e.Headers, now, ++_sequence) { NotBefore = now, Source = e.Source });
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
                .Select(e => new PendingJob(e.Queue, e.Id, e.Tenant, e.Key, e.Source, e.Headers,
                    e.State == State.Leased && e.LeasedUntil > now ? PendingState.Running : e.NotBefore > now ? PendingState.Delayed : PendingState.Waiting,
                    e.Attempts, e.EnqueuedAt,
                    e.State == State.Leased && e.LeasedUntil > now ? e.LeasedUntil : e.NotBefore > now ? e.NotBefore : null,
                    e.LastError, System.Text.Encoding.UTF8.GetByteCount(e.Body)))
                .ToList());
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
        public string? Key { get; } = key;
        public IReadOnlyDictionary<string, string> Headers { get; } = headers;
        public DateTimeOffset EnqueuedAt { get; } = enqueuedAt;
        public long Sequence { get; } = sequence;
        public State State { get; set; }
        public DateTimeOffset NotBefore { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset LeasedUntil { get; set; }
        public string? LeaseToken { get; set; }
        public DateTimeOffset FinishedAt { get; set; }
        public string? DeadReason { get; set; }
        public string? LastError { get; set; }
        public string? Source { get; init; }

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
    public static string? DeadReason(int attemptsSoFar, DateTimeOffset enqueuedAt, DateTimeOffset now, WorkQueueOptions options)
    {
        if (attemptsSoFar >= options.MaxAttempts)
            return $"gave up after {attemptsSoFar} attempts";
        if (now - enqueuedAt > options.MaxAgeOrDefault)
            return $"older than the {options.MaxAgeOrDefault.TotalHours:0.##}-hour limit";
        return null;
    }
}
