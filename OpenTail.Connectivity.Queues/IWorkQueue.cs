namespace OpenTail.Connectivity.Queues;

/// <summary>
/// A durable list of jobs. Delivery is at least once: a job is leased to one worker at a time and comes back if the worker does
/// not complete it, so handlers must be idempotent. There is no ordering guarantee beyond "due jobs, oldest first, best effort".
/// </summary>
/// <remarks>
/// A scheduler that only enqueues with <see cref="EnqueueRequest.NotBefore"/> can be swapped without touching what runs the jobs.
/// What an implementation guarantees beyond the basics is declared in <see cref="Capabilities"/>; the conformance suite
/// (<see cref="WorkQueueConformance"/>) checks each declared guarantee.
/// <para>Jobs that load a shared resource name it in <see cref="EnqueueRequest.ConcurrencyKey"/>. The queue then hands out no
/// more of them at once than the key's limit (<see cref="WorkQueueOptions.ConcurrencyLimits"/>), across every worker, and stops
/// handing them out for a while when the resource keeps failing (a circuit breaker per key).</para>
/// </remarks>
public interface IWorkQueue : IAsyncDisposable
{
    WorkQueueCapabilities Capabilities { get; }

    /// <summary>Adds a job. With a <see cref="EnqueueRequest.Key"/> already used in the same queue, returns the first job's id and
    /// adds nothing (when <see cref="WorkQueueCapabilities.DeduplicatesKeys"/>).</summary>
    Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default);

    /// <summary>
    /// Leases up to <paramref name="max"/> due jobs for <paramref name="lease"/>; returns at once, possibly with none. Each lease
    /// counts as an attempt. A job past <see cref="WorkQueueOptions.MaxAttempts"/> or <see cref="WorkQueueOptions.MaxAge"/> goes
    /// to the dead letters instead of being handed out. Jobs whose concurrency key is at its limit, or whose circuit is open, are
    /// skipped (not handed out, no attempt used); later jobs with other keys are still handed out.
    /// </summary>
    Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default);

    /// <summary>As <see cref="LeaseAsync(string, int, TimeSpan, CancellationToken)"/>, also skipping jobs with any of
    /// <paramref name="skipConcurrencyKeys"/> (a worker still running a timed-out handler for that resource).</summary>
    Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, IReadOnlyCollection<string> skipConcurrencyKeys,
        CancellationToken ct = default);

    /// <summary>Finishes the job. False when the lease was lost (it expired and another worker took the job). Closes the circuit of
    /// the job's concurrency key: the resource worked.</summary>
    Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default);

    /// <summary>Gives the job back to run again after <paramref name="delay"/>. False when the lease was lost.</summary>
    Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default);

    /// <summary>As <see cref="RetryAsync(LeasedJob, TimeSpan, string?, CancellationToken)"/>; with
    /// <paramref name="resourceFailure"/>, the failure was the resource's (a timeout, a refused connection), not the job's, and
    /// counts towards opening the circuit of the job's concurrency key.</summary>
    Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error, bool resourceFailure, CancellationToken ct = default);

    /// <summary>Moves the job to the dead letters at once (a job that can never succeed). False when the lease was lost.</summary>
    Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default);

    /// <summary>Keeps a long job's lease for <paramref name="lease"/> from now. False when the lease was already lost.</summary>
    Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default);

    /// <summary>As <see cref="ExtendAsync(LeasedJob, TimeSpan, CancellationToken)"/>, recording the handler's latest heartbeat and
    /// progress note (shown in <see cref="PendingJob.Progress"/>; at most 200 characters kept).</summary>
    Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, string? progress, DateTimeOffset? lastHeartbeat, CancellationToken ct = default);

    /// <summary>The queue's dead letters, oldest first. Dead letters older than <see cref="WorkQueueOptions.DeadLetterRetention"/>
    /// are deleted.</summary>
    Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default);

    /// <summary>Puts a dead letter back as a fresh job (attempts reset, age counted from now). False when there is no such dead
    /// letter. It is released through its concurrency key's limit and circuit like any job. For many at once, see
    /// <see cref="WorkQueueOperations.RedriveMatchingAsync"/>.</summary>
    Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default);

    /// <summary>As <see cref="RedriveAsync(string, string, CancellationToken)"/>, not due before <paramref name="notBefore"/>
    /// (bulk redrive spreads jobs out this way).</summary>
    Task<bool> RedriveAsync(string queue, string id, DateTimeOffset notBefore, CancellationToken ct = default);

    /// <summary>Removes a dead letter for good. False when there is no such dead letter.</summary>
    Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default);

    /// <summary>
    /// Stops a job that has not finished: a waiting or delayed job never runs, and a running job's lease is revoked, so its worker
    /// cannot complete, retry or extend it (a <see cref="WorkQueueWorker"/> then cancels the handler). The job's key keeps
    /// deduplicating. False when the job is unknown or already finished (when <see cref="WorkQueueCapabilities.Cancellation"/>;
    /// otherwise <see cref="NotSupportedException"/>).
    /// </summary>
    Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default);

    /// <summary>Makes a delayed job (a <see cref="EnqueueRequest.NotBefore"/> or a retry's wait) due now. False when no such job is waiting.</summary>
    Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default);

    /// <summary>Unfinished jobs, oldest first, without their bodies (they may hold content an operator should not see). Lists
    /// running jobs too when <see cref="WorkQueueCapabilities.ListsRunningJobs"/>.</summary>
    Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default);

    /// <summary>Each concurrency key with running or waiting jobs, or an open circuit: how many run, how many wait, the limit and
    /// the circuit. Waiting counts are what the queue can see (see <see cref="WorkQueueCapabilities.ListsRunningJobs"/>).</summary>
    Task<IReadOnlyList<ConcurrencyKeyState>> ConcurrencyKeysAsync(string queue, CancellationToken ct = default);
}

/// <param name="Queue">The queue's name: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.</param>
/// <param name="Tenant">Whose job this is. Workers refuse jobs for tenants they do not serve; one queue per tenant is simplest.</param>
/// <param name="Body">The job, usually JSON. Keep it small and prefer references: a body holding document text leaves the
/// governance that applies to the document (labels, flow rules, audit). Name the job's type and version in
/// <paramref name="Headers"/> (<see cref="JobHeaders"/>) so a handler can refuse a format it does not know.</param>
/// <param name="Key">Idempotency key: the same key in the same queue is one job.</param>
/// <param name="NotBefore">The job is not handed out before this instant.</param>
/// <param name="Headers">Small metadata (type and version, requester, trace id).</param>
/// <param name="Source">What created the job, as <c>kind:id</c> (<c>rule:7</c>, <c>schedule:daily-report</c>,
/// <c>continuation:c-4410</c>, <c>person:ann</c>). An operator stopping a job that keeps coming back needs to know what to turn off.</param>
/// <param name="ConcurrencyKey">The shared resource the job loads (<c>db:reports01</c>, <c>erp:acme</c>, <c>tenant:acme</c>).
/// Jobs with the same key run no more than its limit at once, across all workers.</param>
/// <param name="MaxRunTime">How long the handler may run before a <see cref="WorkQueueWorker"/> gives up on it; overrides
/// <see cref="WorkQueueOptions.DefaultMaxRunTime"/>.</param>
/// <param name="KeyScope">How long <paramref name="Key"/> keeps out a second job with the same key.</param>
public sealed record EnqueueRequest(
    string Queue,
    string Tenant,
    string Body,
    string? Key = null,
    DateTimeOffset? NotBefore = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Source = null,
    string? ConcurrencyKey = null,
    TimeSpan? MaxRunTime = null,
    KeyScope KeyScope = KeyScope.Retention);

/// <summary>How long an idempotency key keeps out a second job with the same key (when <see cref="WorkQueueCapabilities.DeduplicatesKeys"/>).</summary>
public enum KeyScope
{
    /// <summary>While the job waits or runs, and for <see cref="WorkQueueOptions.KeyRetention"/> after it finished: "this exact job,
    /// once" (a firing of a schedule, a payment).</summary>
    Retention,
    /// <summary>While the job waits or runs; once it finished (or was cancelled or died), the key may be used again: "one at a time".</summary>
    UntilFinished,
    /// <summary>Only while the job waits; once it has started, a new one may queue behind it: "debounce" (refresh the index for X
    /// once, however many changes asked for it, without missing a change made during a run).</summary>
    WhilePending,
}

/// <summary>Header names with an agreed meaning.</summary>
public static class JobHeaders
{
    /// <summary>What kind of job the body is (<c>report.render</c>).</summary>
    public const string Type = "type";

    /// <summary>The body's format version (<c>2</c>). A handler that does not know it throws <see cref="PermanentJobFailureException"/>.</summary>
    public const string Version = "v";

    /// <summary>W3C trace context of whatever enqueued the job, set by the queue; the worker continues the trace from it.</summary>
    public const string TraceParent = "traceparent";

    public const string TraceState = "tracestate";
}

/// <param name="Duplicate">True when the key was already used and nothing was added; <see cref="Id"/> is the first job's.</param>
public sealed record EnqueueResult(string Id, bool Duplicate);

/// <summary>A job leased to the caller. <see cref="LeaseToken"/> proves the lease; it changes every time the job is leased.</summary>
/// <param name="Attempt">1 on the first lease.</param>
/// <param name="MaxRunTime">The job's own limit, or the queue's default; null for none.</param>
public sealed record LeasedJob(
    string Queue,
    string Id,
    string Tenant,
    string Body,
    string? Key,
    IReadOnlyDictionary<string, string> Headers,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset LeasedUntil,
    string LeaseToken,
    string? Source = null,
    string? ConcurrencyKey = null,
    TimeSpan? MaxRunTime = null);

/// <param name="Reason">Why: given to <see cref="IWorkQueue.DeadLetterAsync"/>, or the attempts or age limit.</param>
/// <param name="LastError">The last error passed to <see cref="IWorkQueue.RetryAsync(LeasedJob, TimeSpan, string?, CancellationToken)"/>.</param>
public sealed record DeadLetter(
    string Queue,
    string Id,
    string Tenant,
    string Body,
    string? Key,
    IReadOnlyDictionary<string, string> Headers,
    int Attempts,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset DeadAt,
    string Reason,
    string? LastError,
    string? Source = null,
    string? ConcurrencyKey = null);

public enum PendingState
{
    /// <summary>Due, waiting for a worker (or for a free slot on its concurrency key).</summary>
    Waiting,
    /// <summary>Not due yet: a not-before time or a retry's wait.</summary>
    Delayed,
    /// <summary>Leased to a worker.</summary>
    Running,
}

/// <summary>An unfinished job as an operator sees it: everything but the body.</summary>
/// <param name="BodyBytes">The body's size in UTF-8 bytes.</param>
/// <param name="Until">For <see cref="PendingState.Delayed"/>, when it becomes due; for <see cref="PendingState.Running"/>, when the lease ends.</param>
/// <param name="RunningSince">For <see cref="PendingState.Running"/>, when this attempt was leased.</param>
public sealed record PendingJob(
    string Queue,
    string Id,
    string Tenant,
    string? Key,
    string? Source,
    IReadOnlyDictionary<string, string> Headers,
    PendingState State,
    int Attempts,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? Until,
    string? LastError,
    int BodyBytes,
    string? ConcurrencyKey = null,
    TimeSpan? MaxRunTime = null,
    DateTimeOffset? RunningSince = null,
    string? Progress = null,
    DateTimeOffset? LastHeartbeat = null);

public enum CircuitState
{
    /// <summary>Jobs are released up to the key's limit.</summary>
    Closed,
    /// <summary>The resource kept failing: nothing is released until <see cref="ConcurrencyKeyState.OpenUntil"/>.</summary>
    Open,
    /// <summary>The cool-down is over: one job is let through to test the resource.</summary>
    HalfOpen,
}

/// <param name="Limit">Null when the key has no limit.</param>
/// <param name="Failures">Resource failures in a row.</param>
/// <param name="Rate">The key's rate limit, if any.</param>
/// <param name="StartedInWindow">Jobs started within the rate limit's window.</param>
public sealed record ConcurrencyKeyState(
    string Queue,
    string ConcurrencyKey,
    int Running,
    int Waiting,
    int? Limit,
    CircuitState Circuit,
    DateTimeOffset? OpenUntil,
    int Failures,
    RateLimit? Rate = null,
    int StartedInWindow = 0);

/// <summary>At most <paramref name="Count"/> jobs with a key start within any <paramref name="Per"/> (a sliding window): "100 a minute".</summary>
public sealed record RateLimit(int Count, TimeSpan Per);

/// <summary>What an implementation guarantees beyond at-least-once delivery. The conformance suite checks each one declared.</summary>
/// <param name="Durable">Jobs survive the process ending (and, where stated, a power cut).</param>
/// <param name="DelayedDelivery">Honours <see cref="EnqueueRequest.NotBefore"/> and retry delays exactly (never early).</param>
/// <param name="LeaseExpiry">A lease not completed in time hands the job to another worker without the first one disconnecting.</param>
/// <param name="DeduplicatesKeys">Enqueueing a key twice adds one job, for at least <see cref="WorkQueueOptions.KeyRetention"/>.</param>
/// <param name="Cancellation"><see cref="IWorkQueue.CancelAsync"/> works (a broker may need a shared store for it).</param>
/// <param name="ListsRunningJobs"><see cref="IWorkQueue.PendingAsync"/> includes leased jobs, not only waiting and delayed ones.</param>
/// <param name="ConcurrencyLimits">Concurrency keys are limited and circuit-broken across workers (a broker may need a shared store).</param>
public sealed record WorkQueueCapabilities(
    bool Durable, bool DelayedDelivery, bool LeaseExpiry, bool DeduplicatesKeys, bool Cancellation = true, bool ListsRunningJobs = true,
    bool ConcurrencyLimits = true);

/// <param name="MaxAttempts">Leases a job gets before it goes to the dead letters. Default 5.</param>
/// <param name="MaxAge">Age (from enqueue) after which a job goes to the dead letters instead of running late. Default 24 hours.</param>
/// <param name="KeyRetention">How long a finished job's key still deduplicates. Default 7 days.</param>
/// <param name="MaxBodyBytes">Bodies over this many UTF-8 bytes are refused. Default 256 KB.</param>
/// <param name="ConcurrencyLimits">Concurrency key, or a prefix ending in <c>*</c> (<c>db:*</c>), to the most jobs with that key
/// running at once. The exact key wins, then the longest prefix, then <paramref name="DefaultConcurrencyLimit"/>.</param>
/// <param name="DefaultConcurrencyLimit">For keys no entry matches; null for no limit. Jobs without a key are never limited.</param>
/// <param name="DefaultMaxRunTime">For jobs that set none; null for no limit.</param>
/// <param name="CircuitFailures">Resource failures in a row for one key before its circuit opens. Default 5.</param>
/// <param name="CircuitCooldown">How long an open circuit stays open at first; doubles each time it opens again, up to
/// <paramref name="CircuitMaxCooldown"/>. Default 1 minute.</param>
/// <param name="CircuitMaxCooldown">Default 15 minutes.</param>
/// <param name="DeadLetterRetention">Dead letters older than this are deleted. Default 30 days.</param>
/// <param name="RateLimits">Concurrency key or prefix (<c>api:*</c>) to how many jobs with it may start per window, across all
/// workers; matched like <paramref name="ConcurrencyLimits"/>. Applies beside the concurrency limit.</param>
public sealed record WorkQueueOptions(
    int MaxAttempts = 5,
    TimeSpan? MaxAge = null,
    TimeSpan? KeyRetention = null,
    int MaxBodyBytes = 256 * 1024,
    IReadOnlyDictionary<string, int>? ConcurrencyLimits = null,
    int? DefaultConcurrencyLimit = null,
    TimeSpan? DefaultMaxRunTime = null,
    int CircuitFailures = 5,
    TimeSpan? CircuitCooldown = null,
    TimeSpan? CircuitMaxCooldown = null,
    TimeSpan? DeadLetterRetention = null,
    IReadOnlyDictionary<string, RateLimit>? RateLimits = null)
{
    public TimeSpan MaxAgeOrDefault => MaxAge ?? TimeSpan.FromHours(24);

    public TimeSpan KeyRetentionOrDefault => KeyRetention ?? TimeSpan.FromDays(7);

    public TimeSpan CircuitCooldownOrDefault => CircuitCooldown ?? TimeSpan.FromMinutes(1);

    public TimeSpan CircuitMaxCooldownOrDefault => CircuitMaxCooldown ?? TimeSpan.FromMinutes(15);

    public TimeSpan DeadLetterRetentionOrDefault => DeadLetterRetention ?? TimeSpan.FromDays(30);

    /// <summary>The limit for a concurrency key: exact entry, then the longest matching prefix, then the default.</summary>
    public int? LimitFor(string? concurrencyKey)
        => concurrencyKey is null ? null : Match(ConcurrencyLimits, concurrencyKey, out var limit) ? limit : DefaultConcurrencyLimit;

    /// <summary>The rate limit for a concurrency key (exact entry, then the longest matching prefix), or null.</summary>
    public RateLimit? RateFor(string? concurrencyKey)
        => concurrencyKey is not null && Match(RateLimits, concurrencyKey, out var rate) ? rate : null;

    private static bool Match<T>(IReadOnlyDictionary<string, T>? table, string key, out T value)
    {
        value = default!;
        if (table is null) return false;
        if (table.TryGetValue(key, out value!)) return true;
        foreach (var entry in table.Where(l => l.Key.EndsWith('*') && key.StartsWith(l.Key[..^1], StringComparison.Ordinal)).OrderByDescending(l => l.Key.Length))
        {
            value = entry.Value;
            return true;
        }

        return false;
    }

    /// <summary>The request's headers with the current trace context added (<see cref="JobHeaders.TraceParent"/>), unless the caller
    /// set one. Implementations store these instead of the request's own.</summary>
    public static Dictionary<string, string> HeadersWithTrace(EnqueueRequest request)
    {
        var headers = new Dictionary<string, string>(request.Headers ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (System.Diagnostics.Activity.Current is { } current && !headers.ContainsKey(JobHeaders.TraceParent))
        {
            headers[JobHeaders.TraceParent] = current.Id!;
            if (current.TraceStateString is { Length: > 0 } state) headers[JobHeaders.TraceState] = state;
        }

        return headers;
    }

    /// <summary>Checks a request against the shared rules; implementations call it first.</summary>
    public void Validate(EnqueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateQueueName(request.Queue);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Tenant, nameof(request.Tenant));
        ArgumentNullException.ThrowIfNull(request.Body, nameof(request.Body));
        if (System.Text.Encoding.UTF8.GetByteCount(request.Body) > MaxBodyBytes)
            throw new ArgumentException($"The job body is larger than {MaxBodyBytes} bytes; store the content elsewhere and enqueue a reference.", nameof(request));
        if (request.Key is { Length: 0 or > 200 })
            throw new ArgumentException("A key must be 1 to 200 characters.", nameof(request));
        if (request.Source is { Length: 0 or > 200 })
            throw new ArgumentException("A source must be 1 to 200 characters.", nameof(request));
        if (request.ConcurrencyKey is { Length: 0 or > 200 })
            throw new ArgumentException("A concurrency key must be 1 to 200 characters.", nameof(request));
        if (request.MaxRunTime is { } run && run <= TimeSpan.Zero)
            throw new ArgumentException("A maximum run time must be positive.", nameof(request));
    }

    public static void ValidateQueueName(string queue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queue);
        if (queue.Length > 100 || !queue.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            throw new ArgumentException("A queue name is 1 to 100 letters, digits, '.', '-' or '_'.", nameof(queue));
    }

    public static void ValidateLease(int max, TimeSpan lease)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);
    }
}
