namespace OpenTail.Connectivity.Queues;

/// <summary>Thrown by a handler for a job that can never succeed (bad input, a refused request, a job format it does not know): it
/// goes straight to the dead letters instead of being retried.</summary>
public sealed class PermanentJobFailureException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>Thrown by a handler when the resource it needs is down or overloaded (not the job's fault). It is retried like any
/// failure and counts towards opening the circuit of the job's concurrency key. Timeouts, <see cref="TimeoutException"/>,
/// <see cref="HttpRequestException"/> and socket errors count the same way.</summary>
public sealed class ResourceUnavailableException(string reason, Exception? inner = null) : Exception(reason, inner);

/// <summary>What a worker did; for logs and metrics.</summary>
/// <param name="Delay">For <see cref="WorkerEventKind.Retried"/>: when the job runs again.</param>
public sealed record WorkerEvent(WorkerEventKind Kind, string Queue, string? JobId, int Attempt, string? Detail = null, TimeSpan? Delay = null, Exception? Error = null);

public enum WorkerEventKind
{
    Completed,
    Retried,
    DeadLettered,
    /// <summary>The lease ran out, was revoked (the job was cancelled) or could not be extended; the handler was cancelled and the
    /// job is no longer this worker's.</summary>
    LeaseLost,
    /// <summary>The queue itself failed (a broker connection, a locked file); the worker waits and tries again.</summary>
    QueueError,
    /// <summary>The handler ran past its maximum run time; it was cancelled and the job given back.</summary>
    TimedOut,
    /// <summary>A timed-out handler did not stop when cancelled. It still runs and still loads its resource, so this worker takes no
    /// more jobs with that concurrency key until it ends. Reported again (with <see cref="WorkerEvent.Detail"/> "ended") when it does.</summary>
    Zombie,
}

/// <param name="Queue">The queue to work.</param>
/// <param name="Tenants">The tenants this worker serves. A job for another tenant goes to the dead letters without running.</param>
/// <param name="Concurrency">Jobs run at once in this worker. Limits per resource across all workers are the queue's concurrency
/// keys, not this. Default 1.</param>
/// <param name="Lease">Lease per job; renewed at half time while the handler runs. Default 1 minute.</param>
/// <param name="IdleWait">How long to wait when the queue is empty (or failing) before asking again. Default 1 second.</param>
/// <param name="RetryBaseDelay">First retry delay; doubles each attempt, with ±20% jitter. Default 2 seconds.</param>
/// <param name="RetryMaxDelay">Retry delays stop growing here. Default 5 minutes.</param>
/// <param name="MaxRunTime">For jobs that carry none (<see cref="LeasedJob.MaxRunTime"/>); null for no limit.</param>
/// <param name="ZombieGrace">How long a timed-out handler gets to stop after being cancelled before it counts as a zombie. Default 5 seconds.</param>
/// <param name="DrainTime">On stop, how long running handlers may finish before they are cancelled. Default 10 seconds.</param>
/// <param name="HeartbeatTimeout">When set, a handler must call <see cref="JobContext.Heartbeat"/> (through
/// <see cref="JobContext.Current"/>) at least this often; one that stops is treated as hung, like one past its maximum run time.
/// Catches a handler stuck on a lock long before a generous run-time limit would.</param>
public sealed record WorkQueueWorkerOptions(
    string Queue,
    IReadOnlySet<string> Tenants,
    int Concurrency = 1,
    TimeSpan? Lease = null,
    TimeSpan? IdleWait = null,
    TimeSpan? RetryBaseDelay = null,
    TimeSpan? RetryMaxDelay = null,
    TimeSpan? MaxRunTime = null,
    TimeSpan? ZombieGrace = null,
    TimeSpan? DrainTime = null,
    TimeSpan? HeartbeatTimeout = null)
{
    internal TimeSpan LeaseOrDefault => Lease ?? TimeSpan.FromMinutes(1);
    internal TimeSpan IdleWaitOrDefault => IdleWait ?? TimeSpan.FromSeconds(1);
    internal TimeSpan RetryBaseOrDefault => RetryBaseDelay ?? TimeSpan.FromSeconds(2);
    internal TimeSpan RetryMaxOrDefault => RetryMaxDelay ?? TimeSpan.FromMinutes(5);
    internal TimeSpan ZombieGraceOrDefault => ZombieGrace ?? TimeSpan.FromSeconds(5);
    internal TimeSpan DrainTimeOrDefault => DrainTime ?? TimeSpan.FromSeconds(10);
}

/// <summary>
/// Runs a handler over a queue: leases jobs, runs them, and completes, retries or dead-letters each one. Handlers must be
/// idempotent: delivery is at least once.
/// </summary>
/// <remarks>
/// A handler that returns completes the job. One that throws retries it after a growing delay (until the queue's attempt or age
/// limit sends it to the dead letters); <see cref="PermanentJobFailureException"/> dead-letters it at once, and resource failures
/// (<see cref="ResourceUnavailableException"/>, timeouts, connection errors) also count towards the circuit of the job's
/// concurrency key. While a handler runs its lease is extended at half time; if the lease is lost the handler's token is cancelled
/// and its result is ignored. A handler past its maximum run time is cancelled, its lease no longer renewed, and the job given
/// back; one that will not stop is reported as a zombie: its job keeps its lease (and so its concurrency slot, for every worker
/// sharing the queue) until the handler really ends, and only then is given back. Stopping stops leasing, lets running handlers
/// finish for <see cref="WorkQueueWorkerOptions.DrainTime"/>, then cancels the rest and hands their jobs back, after the zombie
/// grace at the latest: a handler that ignores cancellation cannot keep the worker from stopping.
/// <para>Cancelling a handler stops this process's work only. Work the handler started elsewhere (a report already running on a
/// server) may carry on unless the handler cancels it on its token.</para>
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _zombies = new(StringComparer.Ordinal);

    /// <summary>Zombies' jobs whose leases this worker keeps renewing until their handlers end (or the worker stops).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Task, byte> _holds = new();

    /// <summary>Concurrency keys with a handler that timed out and has not stopped yet.</summary>
    public IReadOnlyCollection<string> ZombieKeys => [.. _zombies.Where(z => z.Value > 0 && z.Key.Length > 0).Select(z => z.Key)];

    /// <summary>Works until <paramref name="stop"/> is cancelled, then drains and returns once running handlers have finished.</summary>
    public async Task RunAsync(CancellationToken stop)
    {
        using var handlersStop = new CancellationTokenSource();
        var running = new List<Task>();
        var queueFailures = 0;
        while (!stop.IsCancellationRequested)
        {
            running.RemoveAll(t => t.IsCompleted);
            var free = _options.Concurrency - running.Count;
            if (free == 0)
            {
                await Task.WhenAny(Task.WhenAny(running), Never(stop)).ConfigureAwait(false);
                continue;
            }

            IReadOnlyList<LeasedJob> jobs;
            try
            {
                var skip = ZombieKeys;
                jobs = skip.Count == 0
                    ? await _queue.LeaseAsync(_options.Queue, free, _options.LeaseOrDefault, stop).ConfigureAwait(false)
                    : await _queue.LeaseAsync(_options.Queue, free, _options.LeaseOrDefault, skip, stop).ConfigureAwait(false);
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
                running.Add(Task.Run(() => RunOneAsync(job, handlersStop.Token), CancellationToken.None));
            if (jobs.Count == 0)
            {
                if (running.Count == 0)
                    await IdleAsync(_options.IdleWaitOrDefault, stop).ConfigureAwait(false);
                else
                    await Task.WhenAny(Task.WhenAny(running), IdleAsync(_options.IdleWaitOrDefault, stop)).ConfigureAwait(false);
            }
        }

        // Drain: let running handlers finish for a while, then cancel what is left and hand those jobs back.
        var all = Task.WhenAll(running);
        await Task.WhenAny(all, Task.Delay(_options.DrainTimeOrDefault, _time)).ConfigureAwait(false);
        await handlersStop.CancelAsync().ConfigureAwait(false);
        await all.ConfigureAwait(false);
        await Task.WhenAll(_holds.Keys).ConfigureAwait(false);
    }

    /// <summary>
    /// The retry delay before attempt <paramref name="attempt"/> + 1: base × 2^(attempt−1), capped, then "equal jitter" (half of it
    /// fixed, half random). After a mass failure the retries spread over the whole second half rather than bunching near the same
    /// moment, and none comes back sooner than half the delay.
    /// </summary>
    public static TimeSpan Backoff(int attempt, TimeSpan baseDelay, TimeSpan maxDelay, Random? random = null)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var raw = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(2, exponent), maxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(raw / 2 + (random ?? Random.Shared).NextDouble() * raw / 2);
    }

    /// <summary>Whether a handler's exception means the resource failed rather than the job.</summary>
    public static bool IsResourceFailure(Exception ex) => ex is ResourceUnavailableException or TimeoutException or HttpRequestException
        or System.Net.Sockets.SocketException;

    private async Task RunOneAsync(LeasedJob job, CancellationToken handlersStop)
    {
        if (!_options.Tenants.Contains(job.Tenant))
        {
            await SettleAsync(job, q => q.DeadLetterAsync(job, $"tenant '{job.Tenant}' is not served by this worker"),
                WorkerEventKind.DeadLettered, "tenant not served").ConfigureAwait(false);
            return;
        }

        using var activity = JobTrace.StartProcessing(job);
        var context = new JobContext(job, _time);
        var maxRun = job.MaxRunTime ?? _options.MaxRunTime;
        string? stopReason = null;
        using var lost = new CancellationTokenSource();
        using var gaveUp = new CancellationTokenSource(); // the run-time limit or a missed heartbeat
        if (maxRun is { } limit)
        {
            gaveUp.CancelAfter(limit);
            stopReason = $"timed out after {limit}";
        }

        using var both = CancellationTokenSource.CreateLinkedTokenSource(handlersStop, lost.Token, gaveUp.Token);
        using var renewing = new CancellationTokenSource();
        using var renewingOrGaveUp = CancellationTokenSource.CreateLinkedTokenSource(renewing.Token, gaveUp.Token);
        var renewal = RenewAsync(job, context, lost, renewingOrGaveUp.Token);
        var heartbeats = _options.HeartbeatTimeout is { } beat
            ? WatchHeartbeatsAsync(context, beat, () => { stopReason = $"no heartbeat for {beat}"; gaveUp.Cancel(); }, renewingOrGaveUp.Token)
            : Task.CompletedTask;
        var handlerTask = Task.Run(async () =>
        {
            JobContext.CurrentContext.Value = context;
            await _handler(job, both.Token).ConfigureAwait(false);
        }, CancellationToken.None);

        // Wait for the handler, or for the worker to give up on it (run-time limit, missed heartbeat) or to stop.
        using var givingUpOrStopping = CancellationTokenSource.CreateLinkedTokenSource(gaveUp.Token, handlersStop);
        await Task.WhenAny(handlerTask, Cancelled(givingUpOrStopping.Token)).ConfigureAwait(false);
        if (!handlerTask.IsCompleted)
        {
            // Cancelled; give it the grace period to stop.
            using var graceCancel = new CancellationTokenSource();
            await Task.WhenAny(handlerTask, Task.Delay(_options.ZombieGraceOrDefault, _time, graceCancel.Token)).ConfigureAwait(false);
            await graceCancel.CancelAsync().ConfigureAwait(false);
        }

        await renewing.CancelAsync().ConfigureAwait(false);
        await renewal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await heartbeats.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (!handlerTask.IsCompleted && !gaveUp.IsCancellationRequested)
        {
            // The worker is stopping and the handler ignores its token: hand the job back now rather than wait for a handler
            // that may never end. The handler is left to the process's end.
            JobTrace.Failed(activity, "worker stopped");
            Observe(WorkerEventKind.Zombie, job.Id, job.Attempt, "the handler did not stop when the worker stopped; its job was handed back");
            await SettleAsync(job, q => q.RetryAsync(job, TimeSpan.Zero, "worker stopped"), WorkerEventKind.Retried, "worker stopped", TimeSpan.Zero)
                .ConfigureAwait(false);
            return;
        }

        if (!handlerTask.IsCompleted)
        {
            // Still running after the grace: keep the job's lease, and with it its concurrency slot for every worker sharing the
            // queue, until the handler really ends; then give the job back. Giving it back now would free the slot while the
            // handler still loads the resource. The key is also skipped by this worker's own leasing.
            var key = job.ConcurrencyKey ?? "";
            _zombies.AddOrUpdate(key, 1, (_, n) => n + 1);
            JobTrace.Failed(activity, stopReason!);
            Observe(WorkerEventKind.Zombie, job.Id, job.Attempt, $"the handler did not stop within {_options.ZombieGraceOrDefault.TotalSeconds:0} s of being cancelled");
            var hold = HoldAsync(job, context, handlerTask, key, stopReason!, handlersStop);
            _holds.TryAdd(hold, 0);
            _ = hold.ContinueWith(t => _holds.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return;
        }

        var failure = handlerTask.Exception?.InnerException ?? (handlerTask.IsCanceled ? new OperationCanceledException() : null);
        if (lost.IsCancellationRequested)
        {
            JobTrace.Failed(activity, "lease lost");
            Observe(WorkerEventKind.LeaseLost, job.Id, job.Attempt, "the lease was lost while the handler ran; its result is ignored");
            return;
        }

        if (gaveUp.IsCancellationRequested && failure is not null)
        {
            JobTrace.Failed(activity, stopReason!);
            await GiveBackAsync(job, stopReason!).ConfigureAwait(false);
            return;
        }

        if (failure is not null)
            JobTrace.Failed(activity, Describe(failure));
        switch (failure)
        {
            case null:
                await SettleAsync(job, q => q.CompleteAsync(job), WorkerEventKind.Completed, null).ConfigureAwait(false);
                break;
            case PermanentJobFailureException permanent:
                await SettleAsync(job, q => q.DeadLetterAsync(job, permanent.Message), WorkerEventKind.DeadLettered, permanent.Message, error: permanent)
                    .ConfigureAwait(false);
                break;
            case OperationCanceledException when handlersStop.IsCancellationRequested:
                // Stopping: hand the job back now rather than let it wait out its lease.
                await SettleAsync(job, q => q.RetryAsync(job, TimeSpan.Zero, "worker stopped"), WorkerEventKind.Retried, "worker stopped", TimeSpan.Zero)
                    .ConfigureAwait(false);
                break;
            default:
                var delay = Backoff(job.Attempt, _options.RetryBaseOrDefault, _options.RetryMaxOrDefault);
                var error = Describe(failure);
                var resource = IsResourceFailure(failure);
                await SettleAsync(job, q => q.RetryAsync(job, delay, error, resource), WorkerEventKind.Retried, error, delay, failure).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Renews a zombie's lease until its handler ends or the worker stops, then gives the job back (a resource failure, like the
    /// timeout that made it a zombie). If the lease is lost meanwhile, the job is someone else's and is left alone.
    /// </summary>
    private async Task HoldAsync(LeasedJob job, JobContext context, Task handlerTask, string key, string reason, CancellationToken workerStop)
    {
        using var done = CancellationTokenSource.CreateLinkedTokenSource(workerStop);
        using var lost = new CancellationTokenSource();
        var renewal = RenewAsync(job, context, lost, done.Token);
        await Task.WhenAny(handlerTask, Cancelled(workerStop), Cancelled(lost.Token)).ConfigureAwait(false);
        await done.CancelAsync().ConfigureAwait(false);
        await renewal.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _zombies.AddOrUpdate(key, 0, (_, n) => Math.Max(0, n - 1));
        Observe(WorkerEventKind.Zombie, job.Id, job.Attempt, handlerTask.IsCompleted ? "ended" : "still running when the worker stopped");
        if (lost.IsCancellationRequested)
        {
            Observe(WorkerEventKind.LeaseLost, job.Id, job.Attempt, "the zombie's lease was lost; the job is no longer this worker's");
            return;
        }
        await GiveBackAsync(job, reason).ConfigureAwait(false);
    }

    private static Task Cancelled(CancellationToken token) => Task.Delay(Timeout.Infinite, token).ContinueWith(_ => { }, TaskScheduler.Default);

    /// <summary>Calls <paramref name="missed"/> once when the handler has not called <see cref="JobContext.Heartbeat"/> for
    /// <paramref name="timeout"/>.</summary>
    private async Task WatchHeartbeatsAsync(JobContext context, TimeSpan timeout, Action missed, CancellationToken done)
    {
        var check = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds / 4, 10, 1000));
        while (!done.IsCancellationRequested)
        {
            await Task.Delay(check, _time, done).ConfigureAwait(false);
            if (_time.GetUtcNow() - context.LastHeartbeat > timeout)
            {
                missed();
                return;
            }
        }
    }

    /// <summary>A timeout or a missed heartbeat is the resource's failure: retried after a backoff, counted towards its key's circuit.</summary>
    private Task GiveBackAsync(LeasedJob job, string reason)
    {
        var delay = Backoff(job.Attempt, _options.RetryBaseOrDefault, _options.RetryMaxOrDefault);
        return SettleAsync(job, q => q.RetryAsync(job, delay, reason, resourceFailure: true), WorkerEventKind.TimedOut, reason, delay);
    }

    /// <summary>Extends the lease at half time until <paramref name="done"/>; when that fails, cancels the handler through <paramref name="lost"/>.</summary>
    private async Task RenewAsync(LeasedJob job, JobContext context, CancellationTokenSource lost, CancellationToken done)
    {
        var lease = _options.LeaseOrDefault;
        while (!done.IsCancellationRequested)
        {
            await Task.Delay(lease / 2, _time, done).ConfigureAwait(false);
            bool kept;
            try
            {
                // Not cancelled by `done`: an extension under way must finish, or the lease could lapse just as the handler ends.
                kept = await _queue.ExtendAsync(job, lease, context.Progress, context.HeartbeatReported ? context.LastHeartbeat : null, CancellationToken.None)
                    .ConfigureAwait(false);
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

    private static Task Never(CancellationToken stop) => Task.Delay(Timeout.Infinite, stop).ContinueWith(_ => { }, TaskScheduler.Default);

    private void Observe(WorkerEventKind kind, string? jobId, int attempt, string? detail, TimeSpan? delay = null, Exception? error = null)
    {
        WorkQueueMetrics.Record(kind, _options.Queue);
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
