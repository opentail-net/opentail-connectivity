using System.Diagnostics;

namespace OpenTail.Connectivity.Queues;

/// <summary>
/// The job a <see cref="WorkQueueWorker"/> handler is running, reachable from anywhere in the handler's call chain through
/// <see cref="Current"/>. Call <see cref="Heartbeat"/> as work progresses: with
/// <see cref="WorkQueueWorkerOptions.HeartbeatTimeout"/> set, a handler that stops calling it is treated as hung. The progress note is
/// kept with the job and shown to operators.
/// </summary>
public sealed class JobContext
{
    internal static readonly AsyncLocal<JobContext?> CurrentContext = new();

    private readonly TimeProvider _time;
    private long _lastHeartbeatTicks;
    private string? _progress;

    internal JobContext(LeasedJob job, TimeProvider time)
    {
        Job = job;
        _time = time;
        _lastHeartbeatTicks = time.GetUtcNow().UtcTicks;
    }

    /// <summary>The job of the handler running on this call chain, or null outside one.</summary>
    public static JobContext? Current => CurrentContext.Value;

    public LeasedJob Job { get; }

    /// <summary>The last heartbeat, or when the handler started if it has not sent one.</summary>
    public DateTimeOffset LastHeartbeat => new(Interlocked.Read(ref _lastHeartbeatTicks), TimeSpan.Zero);

    public string? Progress => Volatile.Read(ref _progress);

    internal bool HeartbeatReported { get; private set; }

    /// <summary>Says the handler is making progress, with an optional note (<c>"page 40 of 120"</c>, at most 200 characters kept).
    /// Cheap: it records the time; the worker passes it on with the next lease renewal.</summary>
    public void Heartbeat(string? progress = null)
    {
        Interlocked.Exchange(ref _lastHeartbeatTicks, _time.GetUtcNow().UtcTicks);
        if (progress is not null) Volatile.Write(ref _progress, progress.Length <= 200 ? progress : progress[..200]);
        HeartbeatReported = true;
    }
}

/// <summary>
/// Trace context through the queue: the queue stores the enqueuer's W3C <c>traceparent</c> in the job's headers, and the worker
/// starts the handler's activity as its child, on the activity source <c>OpenTail.Connectivity.Queues</c>, with the OpenTelemetry
/// messaging attributes.
/// </summary>
public static class JobTrace
{
    public const string SourceName = "OpenTail.Connectivity.Queues";

    private static readonly ActivitySource Source = new(SourceName);

    internal static Activity? StartProcessing(LeasedJob job)
    {
        ActivityContext.TryParse(job.Headers.GetValueOrDefault(JobHeaders.TraceParent), job.Headers.GetValueOrDefault(JobHeaders.TraceState), out var parent);
        var activity = Source.StartActivity($"process {job.Queue}", ActivityKind.Consumer, parent);
        if (activity is null) return null;
        activity.SetTag("messaging.system", "opentail");
        activity.SetTag("messaging.destination.name", job.Queue);
        activity.SetTag("messaging.operation.type", "process");
        activity.SetTag("messaging.message.id", job.Id);
        activity.SetTag("opentail.job.attempt", job.Attempt);
        if (job.ConcurrencyKey is { } key) activity.SetTag("opentail.job.concurrency_key", key);
        return activity;
    }

    internal static void Failed(Activity? activity, string reason) => activity?.SetStatus(ActivityStatusCode.Error, reason);
}
