namespace OpenTail.Connectivity.Queues;

/// <summary>
/// A durable list of jobs. Delivery is at least once: a job is leased to one worker at a time and comes back if the worker does
/// not complete it, so handlers must be idempotent. There is no ordering guarantee beyond "due jobs, oldest first, best effort".
/// </summary>
/// <remarks>
/// A scheduler that only enqueues with <see cref="EnqueueRequest.NotBefore"/> can be swapped without touching what runs the jobs.
/// What an implementation guarantees beyond the basics is declared in <see cref="Capabilities"/>; the conformance suite
/// (<see cref="WorkQueueConformance"/>) checks each declared guarantee.
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
    /// to the dead letters instead of being handed out.
    /// </summary>
    Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Finishes the job. False when the lease was lost (it expired and another worker took the job).</summary>
    Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default);

    /// <summary>Gives the job back to run again after <paramref name="delay"/>. False when the lease was lost.</summary>
    Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default);

    /// <summary>Moves the job to the dead letters at once (a job that can never succeed). False when the lease was lost.</summary>
    Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default);

    /// <summary>Keeps a long job's lease for <paramref name="lease"/> from now. False when the lease was already lost.</summary>
    Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default);

    /// <summary>The queue's dead letters, oldest first.</summary>
    Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default);

    /// <summary>Puts a dead letter back as a fresh job (attempts reset, age counted from now). False when there is no such dead letter.</summary>
    Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default);
}

/// <param name="Queue">The queue's name: letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.</param>
/// <param name="Tenant">Whose job this is. Workers refuse jobs for tenants they do not serve; one queue per tenant is simplest.</param>
/// <param name="Body">The job, usually JSON. Keep it small and prefer references: a body holding document text leaves the
/// governance that applies to the document (labels, flow rules, audit).</param>
/// <param name="Key">Idempotency key: the same key in the same queue is one job.</param>
/// <param name="NotBefore">The job is not handed out before this instant.</param>
/// <param name="Headers">Small metadata (requester, trace id, rule id).</param>
public sealed record EnqueueRequest(
    string Queue,
    string Tenant,
    string Body,
    string? Key = null,
    DateTimeOffset? NotBefore = null,
    IReadOnlyDictionary<string, string>? Headers = null);

/// <param name="Duplicate">True when the key was already used and nothing was added; <see cref="Id"/> is the first job's.</param>
public sealed record EnqueueResult(string Id, bool Duplicate);

/// <summary>A job leased to the caller. <see cref="LeaseToken"/> proves the lease; it changes every time the job is leased.</summary>
/// <param name="Attempt">1 on the first lease.</param>
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
    string LeaseToken);

/// <param name="Reason">Why: given to <see cref="IWorkQueue.DeadLetterAsync"/>, or the attempts or age limit.</param>
/// <param name="LastError">The last error passed to <see cref="IWorkQueue.RetryAsync"/>.</param>
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
    string? LastError);

/// <summary>What an implementation guarantees beyond at-least-once delivery. The conformance suite checks each one declared.</summary>
/// <param name="Durable">Jobs survive the process ending (and, where stated, a power cut).</param>
/// <param name="DelayedDelivery">Honours <see cref="EnqueueRequest.NotBefore"/> and retry delays exactly (never early).</param>
/// <param name="LeaseExpiry">A lease not completed in time hands the job to another worker without the first one disconnecting.</param>
/// <param name="DeduplicatesKeys">Enqueueing a key twice adds one job, for at least <see cref="WorkQueueOptions.KeyRetention"/>.</param>
public sealed record WorkQueueCapabilities(bool Durable, bool DelayedDelivery, bool LeaseExpiry, bool DeduplicatesKeys);

/// <param name="MaxAttempts">Leases a job gets before it goes to the dead letters. Default 5.</param>
/// <param name="MaxAge">Age (from enqueue) after which a job goes to the dead letters instead of running late. Default 24 hours.</param>
/// <param name="KeyRetention">How long a finished job's key still deduplicates. Default 7 days.</param>
/// <param name="MaxBodyBytes">Bodies over this many UTF-8 bytes are refused. Default 256 KB.</param>
public sealed record WorkQueueOptions(
    int MaxAttempts = 5,
    TimeSpan? MaxAge = null,
    TimeSpan? KeyRetention = null,
    int MaxBodyBytes = 256 * 1024)
{
    public TimeSpan MaxAgeOrDefault => MaxAge ?? TimeSpan.FromHours(24);

    public TimeSpan KeyRetentionOrDefault => KeyRetention ?? TimeSpan.FromDays(7);

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
