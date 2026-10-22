namespace OpenTail.Connectivity.Queues;

/// <summary>Thrown by a handler for a job that can never succeed (bad input, a refused request): it goes straight to the dead
/// letters instead of being retried.</summary>
public sealed class PermanentJobFailureException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>What a worker did; for logs and metrics.</summary>
/// <param name="Delay">For <see cref="WorkerEventKind.Retried"/>: when the job runs again.</param>
public sealed record WorkerEvent(WorkerEventKind Kind, string Queue, string? JobId, int Attempt, string? Detail = null, TimeSpan? Delay = null, Exception? Error = null);

public enum WorkerEventKind
{
    Completed,
    Retried,
    DeadLettered,
    /// <summary>The lease ran out or could not be extended; the handler was cancelled and the job belongs to another worker now.</summary>
    LeaseLost,
    /// <summary>The queue itself failed (a broker connection, a locked file); the worker waits and tries again.</summary>
    QueueError,
}

/// <param name="Queue">The queue to work.</param>
/// <param name="Tenants">The tenants this worker serves. A job for another tenant goes to the dead letters without running.</param>
/// <param name="Concurrency">Jobs run at once. Default 1.</param>
/// <param name="Lease">Lease per job; renewed at half time while the handler runs. Default 1 minute.</param>
/// <param name="IdleWait">How long to wait when the queue is empty (or failing) before asking again. Default 1 second.</param>
/// <param name="RetryBaseDelay">First retry delay; doubles each attempt, with ±20% jitter. Default 2 seconds.</param>
/// <param name="RetryMaxDelay">Retry delays stop growing here. Default 5 minutes.</param>
public sealed record WorkQueueWorkerOptions(
    string Queue,
    IReadOnlySet<string> Tenants,
    int Concurrency = 1,
    TimeSpan? Lease = null,
    TimeSpan? IdleWait = null,
    TimeSpan? RetryBaseDelay = null,
    TimeSpan? RetryMaxDelay = null)
{
    internal TimeSpan LeaseOrDefault => Lease ?? TimeSpan.FromMinutes(1);
    internal TimeSpan IdleWaitOrDefault => IdleWait ?? TimeSpan.FromSeconds(1);
    internal TimeSpan RetryBaseOrDefault => RetryBaseDelay ?? TimeSpan.FromSeconds(2);
    internal TimeSpan RetryMaxOrDefault => RetryMaxDelay ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// Runs a handler over a queue: leases jobs, runs them, and completes, retries or dead-letters each one. Handlers must be
/// idempotent: delivery is at least once.
/// </summary>
/// <remarks>
/// A handler that returns completes the job. One that throws retries it after a growing delay (until the queue's attempt or age
/// limit sends it to the dead letters); <see cref="PermanentJobFailureException"/> dead-letters it at once. While a handler runs
/// its lease is extended at half time; if the lease is lost the handler's token is cancelled and its result is ignored. Stopping
/// stops leasing, cancels running handlers and hands their jobs back for another worker at once.
/// </remarks>
public sealed class WorkQueueWorker(
    IWorkQueue queue,
    WorkQueueWorkerOptions options,
    Func<LeasedJob, CancellationToken, Task> handler,
    Action<WorkerEvent>? observe = null,
    TimeProvider? time = null)
{
    private readonly IWorkQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly WorkQueueWorkerOptions _options = Checked(options);
    private readonly Func<LeasedJob, CancellationToken, Task> _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>Works until <paramref name="stop"/> is cancelled, then returns once running handlers have finished.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        var running = new List<Task>();
        var queueFailures = 0;
        while (!stop.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            var free = _options.Concurrency - running.Count;
            if (free == 0)
            {
                await Task.WhenAny(running).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<LeasedJob> jobs;
            try
            {
                jobs = await _queue.LeaseAsync(_options.Queue, free, _options.LeaseOrDefault, stop).ConfigureAwait(false);
                queueFailures = 0;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Observe(WorkerEventKind.QueueError, null, 0, ex.Message, error: ex);
                await IdleAsync(Backoff(++queueFailures, _options.IdleWaitOrDefault, _options.RetryMaxOrDefault), stop).ConfigureAwait(false);
                continue;
            }

            foreach (var job in jobs)
                running.Add(Task.Run(() => RunOneAsync(job, stop), CancellationToken.None));
            if (jobs.Count == 0)
            {
                if (running.Count == 0)
                    await IdleAsync(_options.IdleWaitOrDefault, stop).ConfigureAwait(false);
                else
                    await Task.WhenAny(Task.WhenAny(running), IdleAsync(_options.IdleWaitOrDefault, stop)).ConfigureAwait(false);
            }
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>The retry delay before attempt <paramref name="attempt"/> + 1: base × 2^(attempt−1), capped, ±20% jitter.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random? random = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var raw = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, exponent), maxDelay.TotalMilliseconds);
        var jitter = 0.8 + (random ?? Random.Shared).NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(raw * jitter);
    }

    private async Task RunOneAsync(LeasedJob job, CancellationToken stop)
    {
        if (!_options.Tenants.Contains(job.Tenant))
        {
            await SettleAsync(job, q => q.DeadLetterAsync(job, $"tenant '{job.Tenant}' is not served by this worker"),
                WorkerEventKind.DeadLettered, "tenant not served").ConfigureAwait(false);
            return;
        }

        using var lost = new CancellationTokenSource();
        using var both = CancellationTokenSource.CreateLinkedTokenSource(stop, lost.Token);
        using var renewing = new CancellationTokenSource();
        var renewal = RenewAsync(job, lost, renewing.Token);
        Exception? failure = null;
        try
        {
            await _handler(job, both.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            await renewing.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (lost.IsCancellationRequested)
        {
            Observe(WorkerEventKind.LeaseLost, job.Id, job.Attempt, "the lease was lost while the handler ran; its result is ignored");
            return;
        }

        switch (failure)
        {
            case null:
                await SettleAsync(job, q => q.CompleteAsync(job), WorkerEventKind.Completed, null).ConfigureAwait(false);
                break;
            case PermanentJobFailureException permanent:
                await SettleAsync(job, q => q.DeadLetterAsync(job, permanent.Message), WorkerEventKind.DeadLettered, permanent.Message, error: permanent)
                    .ConfigureAwait(false);
                break;
            case OperationCanceledException when stop.IsCancellationRequested:
                // Stopping: hand the job back now rather than let it wait out its lease.
                await SettleAsync(job, q => q.RetryAsync(job, TimeSpan.Zero, "worker stopped"), WorkerEventKind.Retried, "worker stopped", TimeSpan.Zero)
                    .ConfigureAwait(false);
                break;
            default:
                var delay = Backoff(job.Attempt, _options.RetryBaseOrDefault, _options.RetryMaxOrDefault);
                var error = Describe(failure);
                await SettleAsync(job, q => q.RetryAsync(job, delay, error), WorkerEventKind.Retried, error, delay, failure).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Extends the lease at half time; when that fails, cancels the handler through <paramref name="lost"/>.</summary>
    private async Task RenewAsync(LeasedJob job, CancellationTokenSource lost, CancellationToken done)
    {
        var lease = _options.LeaseOrDefault;
        while (!done.IsCancellationRequested)
        {
            await Task.Delay(lease / 2, _time, done).ConfigureAwait(false);
            bool kept;
            try
            {
                // Not cancelled by `done`: an extension under way must finish, or the lease could lapse just as the handler ends.
                kept = await _queue.ExtendAsync(job, lease, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                kept = false; // for example a broker's cap on how long a lease may last
            }

            if (!kept)
            {
                await lost.CancelAsync().ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>Completes, retries or dead-letters; a lost lease or a failing queue is reported, not thrown.</summary>
    private async Task SettleAsync(LeasedJob job, Func<IWorkQueue, Task<bool>> settle, WorkerEventKind kind, string? detail, TimeSpan? delay = null, Exception? error = null)
    {
        try
        {
            if (await settle(_queue).ConfigureAwait(false))
                Observe(kind, job.Id, job.Attempt, detail, delay, error);
            else
                Observe(WorkerEventKind.LeaseLost, job.Id, job.Attempt, $"the lease was gone when the job was to be {kind.ToString().ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            // The job comes back when its lease runs out (or the broker sees the worker gone): at least once still holds.
            Observe(WorkerEventKind.QueueError, job.Id, job.Attempt, ex.Message, error: ex);
        }
    }

    private async Task IdleAsync(TimeSpan wait, CancellationToken stop)
    {
        try { await Task.Delay(wait, _time, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    private void Observe(WorkerEventKind kind, string? jobId, int attempt, string? detail, TimeSpan? delay = null, Exception? error = null)
    {
        try { observe?.Invoke(new WorkerEvent(kind, _options.Queue, jobId, attempt, detail, delay, error)); }
        catch (Exception) { } // an observer must not stop the worker
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().Name}: {ex.Message}";
        return text.Length <= 500 ? text : text[..500];
    }

    private static WorkQueueWorkerOptions Checked(WorkQueueWorkerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        WorkQueueOptions.ValidateQueueName(options.Queue);
        ArgumentNullException.ThrowIfNull(options.Tenants);
        if (options.Tenants.Count == 0)
            throw new ArgumentException("A worker serves at least one tenant.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Concurrency, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.LeaseOrDefault, TimeSpan.Zero);
        return options;
    }
}
