using System.Diagnostics.Metrics;

namespace OpenTail.Connectivity.Queues;

/// <summary>
/// Counters for what workers do, on the meter <c>OpenTail.Connectivity.Queues</c>: <c>opentail.queue.worker.events</c>, tagged
/// with <c>queue</c> and <c>kind</c> (completed, retried, dead_lettered, lease_lost, queue_error, timed_out, zombie). Any
/// OpenTelemetry or <see cref="MeterListener"/> setup picks them up.
/// </summary>
public static class WorkQueueMetrics
{
    public const string MeterName = "OpenTail.Connectivity.Queues";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Events = Meter.CreateCounter<long>("opentail.queue.worker.events", description: "What queue workers did with jobs.");

    internal static void Record(WorkerEventKind kind, string queue)
        => Events.Add(1, new KeyValuePair<string, object?>("queue", queue), new KeyValuePair<string, object?>("kind", Name(kind)));

    private static string Name(WorkerEventKind kind) => kind switch
    {
        WorkerEventKind.DeadLettered => "dead_lettered",
        WorkerEventKind.LeaseLost => "lease_lost",
        WorkerEventKind.QueueError => "queue_error",
        WorkerEventKind.TimedOut => "timed_out",
        _ => kind.ToString().ToLowerInvariant(),
    };
}

public enum QueueHealthStatus { Healthy, Degraded }

/// <summary>A queue's state at a glance, and whether it needs a person.</summary>
/// <param name="OldestWaiting">Age of the oldest job that is due and not running; null when none wait.</param>
/// <param name="DeadLastHour">Dead letters added in the last hour (of the most recent <see cref="WorkQueueHealthThresholds.Scan"/>).</param>
/// <param name="StuckRunning">Running jobs past their maximum run time (where the queue lists running jobs).</param>
/// <param name="Reasons">Why it is degraded, in words; empty when healthy.</param>
public sealed record QueueHealth(
    string Queue,
    QueueHealthStatus Status,
    int Waiting,
    int Running,
    TimeSpan? OldestWaiting,
    int DeadLastHour,
    int StuckRunning,
    IReadOnlyList<ConcurrencyKeyState> OpenCircuits,
    IReadOnlyList<string> Reasons);

/// <param name="OldestWaiting">Degraded when a due job has waited longer than this. Default 15 minutes.</param>
/// <param name="DeadPerHour">Degraded when more dead letters than this were added in the last hour. Default 10.</param>
/// <param name="Scan">How many jobs and dead letters are looked at. Default 1,000.</param>
public sealed record WorkQueueHealthThresholds(TimeSpan? OldestWaiting = null, int DeadPerHour = 10, int Scan = 1_000)
{
    public TimeSpan OldestWaitingOrDefault => OldestWaiting ?? TimeSpan.FromMinutes(15);
}

/// <summary>Reads a queue and says whether it needs attention: jobs waiting too long, dead letters piling up, open circuits, stuck jobs.</summary>
public static class WorkQueueHealth
{
    public static async Task<QueueHealth> CheckAsync(IWorkQueue queue, string name, WorkQueueHealthThresholds? thresholds = null,
        TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        var t = thresholds ?? new WorkQueueHealthThresholds();
        var now = (time ?? TimeProvider.System).GetUtcNow();
        var pending = await queue.PendingAsync(name, t.Scan, ct).ConfigureAwait(false);
        var dead = await queue.DeadLettersAsync(name, t.Scan, ct).ConfigureAwait(false);
        var keys = await queue.ConcurrencyKeysAsync(name, ct).ConfigureAwait(false);

        var waiting = pending.Where(p => p.State == PendingState.Waiting).ToList();
        TimeSpan? oldest = waiting.Count == 0 ? null : now - waiting.Min(p => p.EnqueuedAt);
        var deadLastHour = dead.Count(d => d.DeadAt >= now.AddHours(-1));
        var stuck = pending.Count(p => p is { State: PendingState.Running, RunningSince: { } since, MaxRunTime: { } max } && now - since > max);
        var open = keys.Where(k => k.Circuit != CircuitState.Closed).ToList();

        var reasons = new List<string>();
        if (oldest > t.OldestWaitingOrDefault)
            reasons.Add($"the oldest waiting job has waited {oldest.Value.TotalMinutes:0} min (limit {t.OldestWaitingOrDefault.TotalMinutes:0})");
        if (deadLastHour > t.DeadPerHour)
            reasons.Add($"{deadLastHour} jobs died in the last hour (limit {t.DeadPerHour})");
        if (stuck > 0)
            reasons.Add($"{stuck} running job(s) past their maximum run time");
        foreach (var k in open)
            reasons.Add($"{k.ConcurrencyKey} is failing; its circuit is {(k.Circuit == CircuitState.Open ? "open until " + k.OpenUntil?.ToString("HH:mm:ss") : "testing one job")}");
        return new QueueHealth(name, reasons.Count == 0 ? QueueHealthStatus.Healthy : QueueHealthStatus.Degraded,
            waiting.Count, pending.Count(p => p.State == PendingState.Running), oldest, deadLastHour, stuck, open, reasons);
    }
}
