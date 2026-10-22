namespace OpenTail.Connectivity.Queues;

/// <summary>
/// The circuit of one concurrency key, as a queue stores it. Times are Unix milliseconds. <see cref="NextCooldownMs"/> is how long
/// the circuit stays open the next time it opens.
/// </summary>
/// <param name="ProbeToken">The lease token of the one job let through while half-open, until it settles or its lease ends.</param>
public sealed record CircuitRecord(int Failures, long OpenUntilMs, long NextCooldownMs, string? ProbeToken, long ProbeUntilMs);

/// <summary>
/// The circuit breaker every queue applies the same way. Closed: jobs flow up to the key's limit. After
/// <see cref="WorkQueueOptions.CircuitFailures"/> resource failures in a row it opens: nothing is released, so waiting jobs use no
/// attempts. When the cool-down ends it is half-open: one job goes through; success closes it, a resource failure opens it again
/// for twice as long (up to <see cref="WorkQueueOptions.CircuitMaxCooldown"/>).
/// </summary>
public static class Circuits
{
    public static CircuitState State(CircuitRecord? c, long now, WorkQueueOptions options)
        => c is null || c.Failures < options.CircuitFailures ? CircuitState.Closed
            : c.OpenUntilMs > now ? CircuitState.Open
            : CircuitState.HalfOpen;

    /// <summary>Whether a job with this key may be released now, and whether it is the half-open probe.</summary>
    public static (bool Allowed, bool Probe) MayRelease(CircuitRecord? c, long now, WorkQueueOptions options)
        => State(c, now, options) switch
        {
            CircuitState.Closed => (true, false),
            CircuitState.Open => (false, false),
            _ => c!.ProbeToken is not null && c.ProbeUntilMs > now ? (false, false) : (true, true),
        };

    /// <summary>The probe was released under <paramref name="token"/> until <paramref name="until"/>.</summary>
    public static CircuitRecord ProbeReleased(CircuitRecord c, string token, long until) => c with { ProbeToken = token, ProbeUntilMs = until };

    /// <summary>The resource failed. Opens the circuit at the threshold, or again after a failed probe; failures of jobs that were
    /// already running while it is open only count.</summary>
    public static CircuitRecord ResourceFailed(CircuitRecord? c, long now, WorkQueueOptions options)
    {
        var baseMs = (long)options.CircuitCooldownOrDefault.TotalMilliseconds;
        var maxMs = (long)options.CircuitMaxCooldownOrDefault.TotalMilliseconds;
        var record = c ?? new CircuitRecord(0, 0, baseMs, null, 0);
        var failures = record.Failures + 1;
        if (failures < options.CircuitFailures || record.OpenUntilMs > now)
            return record with { Failures = failures };
        return new CircuitRecord(failures, now + record.NextCooldownMs, Math.Min(record.NextCooldownMs * 2, maxMs), null, 0);
    }

    /// <summary>A job settled without blaming the resource (bad input, cancelled): frees the probe if it was the probe.</summary>
    public static CircuitRecord? Settled(CircuitRecord? c, string leaseToken)
        => c is not null && c.ProbeToken == leaseToken ? c with { ProbeToken = null, ProbeUntilMs = 0 } : c;
}

/// <summary>Operations over many jobs, built on <see cref="IWorkQueue"/>.</summary>
public static class WorkQueueOperations
{
    /// <summary>
    /// Redrives every dead letter that matches, oldest first, and returns how many. Use it when a resource is back after an
    /// outage: the jobs are released through their concurrency keys' limits and circuits, so they trickle back rather than
    /// stampede. Jobs without a key come back all at once unless <paramref name="spreadOver"/> spaces them out evenly. With
    /// <paramref name="dryRun"/>, only counts.
    /// </summary>
    public static async Task<int> RedriveMatchingAsync(IWorkQueue queue, string name, DeadLetterFilter filter, bool dryRun = false,
        TimeSpan? spreadOver = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(filter);
        var matching = (await queue.DeadLettersAsync(name, filter.Max, ct).ConfigureAwait(false)).Where(filter.Matches).ToList();
        if (dryRun)
            return matching.Count;
        var redriven = 0;
        var start = DateTimeOffset.UtcNow;
        for (var i = 0; i < matching.Count; i++)
        {
            var at = spreadOver is { } spread && matching.Count > 1 ? start + spread * i / (matching.Count - 1) : start;
            if (await queue.RedriveAsync(name, matching[i].Id, at, ct).ConfigureAwait(false))
                redriven++;
        }

        return redriven;
    }
}

/// <summary>Which dead letters to act on. Every condition given must hold.</summary>
/// <param name="ConcurrencyKey">Exact key, or a prefix ending in <c>*</c>.</param>
/// <param name="ReasonContains">Case-insensitive, in the reason or the last error.</param>
/// <param name="Source">Exact source.</param>
/// <param name="DeadSince">Died at or after this instant (for example, when the outage began).</param>
/// <param name="Max">At most this many dead letters are looked at. Default 1,000.</param>
public sealed record DeadLetterFilter(
    string? ConcurrencyKey = null,
    string? ReasonContains = null,
    string? Source = null,
    DateTimeOffset? DeadSince = null,
    int Max = 1_000)
{
    public bool Matches(DeadLetter d)
        => (ConcurrencyKey is null || (ConcurrencyKey.EndsWith('*')
                ? d.ConcurrencyKey?.StartsWith(ConcurrencyKey[..^1], StringComparison.Ordinal) == true
                : d.ConcurrencyKey == ConcurrencyKey))
           && (ReasonContains is null
               || d.Reason.Contains(ReasonContains, StringComparison.OrdinalIgnoreCase)
               || d.LastError?.Contains(ReasonContains, StringComparison.OrdinalIgnoreCase) == true)
           && (Source is null || d.Source == Source)
           && (DeadSince is null || d.DeadAt >= DeadSince);
}
