using System.Collections.Concurrent;
using OpenTail.Connectivity.Queues;

namespace OpenTail.Connectivity.Tests.Queues;

public sealed class WorkQueueWorkerTests
{
    private const string Q = "work";
    private static readonly HashSet<string> Acme = ["acme"];

    [Fact]
    public async Task Jobs_run_once_each_across_concurrent_handlers_and_are_completed()
    {
        var queue = new InMemoryWorkQueue();
        for (var i = 0; i < 20; i++)
            await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "job " + i), TestContext.Current.CancellationToken);
        var ran = new ConcurrentBag<string>();

        var events = await RunUntilAsync(queue, Options(concurrency: 3), (job, _) => { ran.Add(job.Body); return Task.CompletedTask; },
            e => e.Count(x => x.Kind == WorkerEventKind.Completed) == 20);

        Assert.Equal(20, ran.Distinct().Count());
        Assert.Equal(20, ran.Count);
        Assert.Empty(await queue.LeaseAsync(Q, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(events, e => e.Kind is WorkerEventKind.LeaseLost or WorkerEventKind.QueueError);
    }

    [Fact]
    public async Task A_failing_job_is_retried_after_a_backoff_and_then_completes()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "flaky"), TestContext.Current.CancellationToken);
        var calls = 0;

        var events = await RunUntilAsync(queue, Options(retryBase: TimeSpan.FromMilliseconds(100)),
            (_, _) => Interlocked.Increment(ref calls) == 1 ? throw new IOException("disk full") : Task.CompletedTask,
            e => e.Any(x => x.Kind == WorkerEventKind.Completed));

        var retried = Assert.Single(events, e => e.Kind == WorkerEventKind.Retried);
        Assert.Equal("IOException: disk full", retried.Detail);
        Assert.InRange(retried.Delay!.Value, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));
        Assert.Equal(2, Assert.Single(events, e => e.Kind == WorkerEventKind.Completed).Attempt);
    }

    [Fact]
    public async Task A_permanent_failure_goes_straight_to_the_dead_letters()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "not json"), TestContext.Current.CancellationToken);

        await RunUntilAsync(queue, Options(), (_, _) => throw new PermanentJobFailureException("the body is not JSON"),
            e => e.Any(x => x.Kind == WorkerEventKind.DeadLettered));

        var dead = Assert.Single(await queue.DeadLettersAsync(Q, ct: TestContext.Current.CancellationToken));
        Assert.Equal("the body is not JSON", dead.Reason);
        Assert.Equal(1, dead.Attempts);
    }

    [Fact]
    public async Task A_job_for_a_tenant_the_worker_does_not_serve_never_reaches_the_handler()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "other", "{}"), TestContext.Current.CancellationToken);
        var called = false;

        await RunUntilAsync(queue, Options(), (_, _) => { called = true; return Task.CompletedTask; },
            e => e.Any(x => x.Kind == WorkerEventKind.DeadLettered));

        Assert.False(called);
        Assert.Contains("'other'", Assert.Single(await queue.DeadLettersAsync(Q, ct: TestContext.Current.CancellationToken)).Reason);
    }

    [Fact]
    public async Task A_long_job_keeps_its_lease_and_another_worker_cannot_take_it()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "slow"), TestContext.Current.CancellationToken);
        var stolen = new List<LeasedJob>();

        // Renewed every 300 ms; the handler runs four leases' worth.
        var events = await RunUntilAsync(queue, Options(lease: TimeSpan.FromMilliseconds(600)), async (_, ct) =>
        {
            for (var i = 0; i < 16; i++)
            {
                await Task.Delay(150, ct);
                stolen.AddRange(await queue.LeaseAsync(Q, 1, TimeSpan.FromMinutes(1), ct)); // a second worker trying
            }
        }, e => e.Any(x => x.Kind is WorkerEventKind.Completed or WorkerEventKind.LeaseLost));

        Assert.Empty(stolen);
        Assert.Contains(events, e => e.Kind == WorkerEventKind.Completed);
    }

    [Fact]
    public async Task A_lost_lease_cancels_the_handler_and_its_result_is_ignored()
    {
        var queue = new LosesExtensions(new InMemoryWorkQueue());
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "slow"), TestContext.Current.CancellationToken);
        var cancelled = false;

        var events = await RunUntilAsync(queue, Options(lease: TimeSpan.FromMilliseconds(100)), async (_, ct) =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); }
            catch (OperationCanceledException) { cancelled = true; throw; }
        }, e => e.Any(x => x.Kind == WorkerEventKind.LeaseLost));

        Assert.True(cancelled);
        Assert.DoesNotContain(events, e => e.Kind is WorkerEventKind.Completed or WorkerEventKind.Retried);
    }

    [Fact]
    public async Task Stopping_cancels_running_handlers_and_hands_their_jobs_back_at_once()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "running"), TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource();
        var events = new ConcurrentQueue<WorkerEvent>();
        using var stop = new CancellationTokenSource();
        var worker = new WorkQueueWorker(queue, Options(drain: TimeSpan.Zero), async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, events.Enqueue);

        var run = worker.RunAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal("worker stopped", Assert.Single(events, e => e.Kind == WorkerEventKind.Retried).Detail);
        Assert.Equal(2, Assert.Single(await queue.LeaseAsync(Q, 1, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).Attempt);
    }

    [Fact]
    public async Task A_handler_past_its_maximum_run_time_is_cancelled_and_the_job_retried_as_a_resource_failure()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "slow", ConcurrencyKey: "db:a"), TestContext.Current.CancellationToken);
        var calls = 0;

        var events = await RunUntilAsync(queue, Options(retryBase: TimeSpan.FromMilliseconds(50), maxRun: TimeSpan.FromMilliseconds(150)), async (_, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                await Task.Delay(TimeSpan.FromSeconds(10), ct); // honours the token
        }, e => e.Any(x => x.Kind == WorkerEventKind.Completed));

        Assert.StartsWith("timed out after", Assert.Single(events, e => e.Kind == WorkerEventKind.TimedOut).Detail);
        Assert.Equal(2, Assert.Single(events, e => e.Kind == WorkerEventKind.Completed).Attempt);
        Assert.DoesNotContain(events, e => e.Kind == WorkerEventKind.Zombie);
    }

    [Fact]
    public async Task A_handler_that_ignores_cancellation_is_a_zombie_and_its_key_is_not_leased_until_it_ends()
    {
        var queue = new InMemoryWorkQueue(new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:a"] = 1 }));
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "stubborn", ConcurrencyKey: "db:a"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "next", ConcurrencyKey: "db:a"), TestContext.Current.CancellationToken);
        var calls = 0;

        var events = await RunUntilAsync(queue, Options(concurrency: 2, retryBase: TimeSpan.FromMilliseconds(50), maxRun: TimeSpan.FromMilliseconds(100)),
            async (job, _) =>
            {
                if (job.Body == "stubborn" && Interlocked.Increment(ref calls) == 1)
                    await Task.Delay(800, CancellationToken.None); // ignores its token
            },
            e => e.Count(x => x.Kind == WorkerEventKind.Completed) == 2);

        var zombie = events.FindIndex(e => e.Kind == WorkerEventKind.Zombie && e.Detail != "ended");
        var ended = events.FindIndex(e => e.Kind == WorkerEventKind.Zombie && e.Detail == "ended");
        Assert.True(zombie >= 0 && ended > zombie, "no zombie was reported, or it never ended");
        Assert.All(events.Select((e, i) => (e, i)).Where(x => x.e.Kind == WorkerEventKind.Completed), x => Assert.True(x.i > ended,
            "a job with the zombie's key ran while the zombie still loaded the resource"));
    }

    [Fact]
    public async Task A_resource_failure_counts_towards_the_circuit_and_an_ordinary_one_does_not()
    {
        var queue = new InMemoryWorkQueue(new WorkQueueOptions(CircuitFailures: 3));
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "down", ConcurrencyKey: "db:a"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "bad", ConcurrencyKey: "db:b"), TestContext.Current.CancellationToken);

        await RunUntilAsync(queue, Options(concurrency: 2), (job, _) => job.Body == "down"
                ? throw new ResourceUnavailableException("the server refused the connection")
                : throw new FormatException("not a number"),
            e => e.Count(x => x.Kind == WorkerEventKind.Retried) == 2);

        var keys = (await queue.ConcurrencyKeysAsync(Q, TestContext.Current.CancellationToken)).ToDictionary(k => k.ConcurrencyKey);
        Assert.Equal(1, keys["db:a"].Failures);
        Assert.Equal(0, keys["db:b"].Failures);
    }

    [Fact]
    public async Task Stopping_lets_running_handlers_finish_within_the_drain_time()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "nearly done"), TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource();
        var events = new ConcurrentQueue<WorkerEvent>();
        using var stop = new CancellationTokenSource();
        var worker = new WorkQueueWorker(queue, Options(drain: TimeSpan.FromSeconds(5)), async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(300, ct);
        }, events.Enqueue);

        var run = worker.RunAsync(stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.Kind == WorkerEventKind.Completed);
        Assert.DoesNotContain(events, e => e.Kind == WorkerEventKind.Retried);
    }

    [Fact]
    public async Task Redriving_after_an_outage_releases_the_dead_jobs_through_their_key_limit()
    {
        var queue = new InMemoryWorkQueue(new WorkQueueOptions(MaxAttempts: 1, ConcurrencyLimits: new Dictionary<string, int> { ["db:*"] = 2 }));
        for (var i = 0; i < 6; i++)
            await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "report " + i, ConcurrencyKey: "db:reports"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "other", ConcurrencyKey: "erp:acme"), TestContext.Current.CancellationToken);
        while ((await queue.LeaseAsync(Q, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)) is { Count: > 0 } batch)
            foreach (var j in batch)
                await queue.RetryAsync(j, TimeSpan.Zero, "connection refused", resourceFailure: false, TestContext.Current.CancellationToken);
        await queue.LeaseAsync(Q, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken); // out of attempts: all dead now
        Assert.Equal(7, (await queue.DeadLettersAsync(Q, ct: TestContext.Current.CancellationToken)).Count);

        var filter = new DeadLetterFilter(ConcurrencyKey: "db:*", ReasonContains: "attempts");
        Assert.Equal(6, await WorkQueueOperations.RedriveMatchingAsync(queue, Q, filter, dryRun: true, ct: TestContext.Current.CancellationToken));
        Assert.Equal(6, await WorkQueueOperations.RedriveMatchingAsync(queue, Q, filter, ct: TestContext.Current.CancellationToken));

        Assert.Single(await queue.DeadLettersAsync(Q, ct: TestContext.Current.CancellationToken)); // the erp job stays dead
        Assert.Equal(2, (await queue.LeaseAsync(Q, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken)).Count); // trickles, 2 at a time
    }

    [Fact]
    public async Task Health_reports_long_waits_dead_letter_bursts_stuck_jobs_and_open_circuits()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var queue = new InMemoryWorkQueue(new WorkQueueOptions(CircuitFailures: 1, DefaultMaxRunTime: TimeSpan.FromMinutes(1)), clock);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(QueueHealthStatus.Healthy, (await WorkQueueHealth.CheckAsync(queue, Q, time: clock, ct: ct)).Status);

        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "running"), ct);
        await queue.LeaseAsync(Q, 1, TimeSpan.FromHours(1), ct);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "flaky", ConcurrencyKey: "db:a"), ct);
        await queue.RetryAsync(Assert.Single(await queue.LeaseAsync(Q, 1, TimeSpan.FromHours(1), ct)), TimeSpan.Zero, "down", resourceFailure: true, ct);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "waiting"), ct);
        clock.Advance(TimeSpan.FromMinutes(20));

        var health = await WorkQueueHealth.CheckAsync(queue, Q, new WorkQueueHealthThresholds(DeadPerHour: 0), time: clock, ct: ct);
        Assert.Equal(QueueHealthStatus.Degraded, health.Status);
        Assert.Equal(1, health.StuckRunning);
        Assert.True(health.OldestWaiting >= TimeSpan.FromMinutes(20));
        Assert.Contains(health.Reasons, r => r.Contains("db:a", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_handler_that_keeps_sending_heartbeats_runs_on_and_one_that_stops_is_treated_as_hung()
    {
        var queue = new InMemoryWorkQueue();
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "beating"), TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "silent"), TestContext.Current.CancellationToken);
        var options = Options(concurrency: 2, retryBase: TimeSpan.FromSeconds(30)) with { HeartbeatTimeout = TimeSpan.FromMilliseconds(200) };

        var events = await RunUntilAsync(queue, options, async (job, ct) =>
        {
            if (job.Body == "silent")
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct); // never reports progress
                return;
            }

            for (var i = 0; i < 8; i++)
            {
                await Task.Delay(80, ct);
                JobContext.Current!.Heartbeat($"step {i}");
            }
        }, e => e.Any(x => x.Kind == WorkerEventKind.Completed) && e.Any(x => x.Kind == WorkerEventKind.TimedOut));

        Assert.StartsWith("no heartbeat for", Assert.Single(events, e => e.Kind == WorkerEventKind.TimedOut).Detail);
        Assert.Single(events, e => e.Kind == WorkerEventKind.Completed);
    }

    [Fact]
    public async Task The_handler_runs_inside_the_enqueuers_trace()
    {
        var started = new ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = s => s.Name == JobTrace.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = started.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);
        var queue = new InMemoryWorkQueue();
        System.Diagnostics.ActivityTraceId trace;
        using (var enqueuer = new System.Diagnostics.Activity("enqueue").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start())
        {
            trace = enqueuer.TraceId;
            await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "{}"), TestContext.Current.CancellationToken);
        }

        await RunUntilAsync(queue, Options(), (_, _) => Task.CompletedTask, e => e.Any(x => x.Kind == WorkerEventKind.Completed));

        var processing = Assert.Single(started, a => a.OperationName == "process " + Q);
        Assert.Equal(trace, processing.TraceId);
        Assert.Equal(System.Diagnostics.ActivityKind.Consumer, processing.Kind);
        Assert.Equal(Q, processing.GetTagItem("messaging.destination.name"));
    }

    [Fact]
    public async Task Bulk_redrive_can_spread_jobs_without_a_key_over_a_window()
    {
        var queue = new InMemoryWorkQueue();
        for (var i = 0; i < 5; i++)
            await queue.EnqueueAsync(new EnqueueRequest(Q, "acme", "job " + i), TestContext.Current.CancellationToken);
        foreach (var j in await queue.LeaseAsync(Q, 10, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken))
            await queue.DeadLetterAsync(j, "downstream refused", TestContext.Current.CancellationToken);

        Assert.Equal(5, await WorkQueueOperations.RedriveMatchingAsync(queue, Q, new DeadLetterFilter(), spreadOver: TimeSpan.FromHours(1),
            ct: TestContext.Current.CancellationToken));

        var pending = await queue.PendingAsync(Q, ct: TestContext.Current.CancellationToken);
        Assert.Single(pending, p => p.State == PendingState.Waiting); // the first is due now; the rest follow over the hour
        Assert.Equal(4, pending.Count(p => p.State == PendingState.Delayed));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(2, 4_000)]
    [InlineData(4, 16_000)]
    [InlineData(20, 300_000)]
    public void Backoff_doubles_up_to_the_cap_with_half_of_it_random(int attempt, int expectedMs)
    {
        for (var i = 0; i < 50; i++)
        {
            var delay = WorkQueueWorker.Backoff(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));
            Assert.InRange(delay.TotalMilliseconds, expectedMs * 0.5, expectedMs * 1.0);
        }
    }

    [Fact]
    public void A_worker_must_serve_a_tenant()
        => Assert.Throws<ArgumentException>(() => new WorkQueueWorker(new InMemoryWorkQueue(), new WorkQueueWorkerOptions(Q, new HashSet<string>()), (_, _) => Task.CompletedTask));

    private static WorkQueueWorkerOptions Options(int concurrency = 1, TimeSpan? lease = null, TimeSpan? retryBase = null, TimeSpan? maxRun = null,
        TimeSpan? drain = null)
        => new(Q, Acme, concurrency, lease ?? TimeSpan.FromMinutes(1), IdleWait: TimeSpan.FromMilliseconds(20), RetryBaseDelay: retryBase,
            MaxRunTime: maxRun, ZombieGrace: TimeSpan.FromMilliseconds(100), DrainTime: drain);

    /// <summary>Runs a worker until the events satisfy <paramref name="done"/> (or 10 seconds pass), then stops it.</summary>
    private static async Task<List<WorkerEvent>> RunUntilAsync(
        IWorkQueue queue, WorkQueueWorkerOptions options, Func<LeasedJob, CancellationToken, Task> handler, Func<IReadOnlyList<WorkerEvent>, bool> done)
    {
        var events = new ConcurrentQueue<WorkerEvent>();
        using var stop = new CancellationTokenSource();
        var run = new WorkQueueWorker(queue, options, handler, events.Enqueue).RunAsync(stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done(events.ToList()) && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(done(events.ToList()), "the worker did not get there in time: " + string.Join(", ", events.Select(e => e.Kind)));
        return events.ToList();
    }

    /// <summary>Every extension fails, as when another worker took the job or a broker refused a longer lease.</summary>
    private sealed class LosesExtensions(InMemoryWorkQueue inner) : IWorkQueue
    {
        public WorkQueueCapabilities Capabilities => inner.Capabilities;
        public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default) => inner.EnqueueAsync(request, ct);
        public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default) => inner.LeaseAsync(queue, max, lease, ct);
        public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default) => inner.CompleteAsync(job, ct);
        public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default) => inner.RetryAsync(job, delay, error, ct);
        public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default) => inner.DeadLetterAsync(job, reason, ct);
        public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default) => inner.DeadLettersAsync(queue, max, ct);
        public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default) => inner.RedriveAsync(queue, id, ct);
        public Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default) => inner.DeleteDeadLetterAsync(queue, id, ct);
        public Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default) => inner.CancelAsync(queue, id, reason, ct);
        public Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default) => inner.RunNowAsync(queue, id, ct);
        public Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default) => inner.PendingAsync(queue, max, ct);
        public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, IReadOnlyCollection<string> skip, CancellationToken ct = default) => inner.LeaseAsync(queue, max, lease, skip, ct);
        public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error, bool resourceFailure, CancellationToken ct = default) => inner.RetryAsync(job, delay, error, resourceFailure, ct);
        public Task<IReadOnlyList<ConcurrencyKeyState>> ConcurrencyKeysAsync(string queue, CancellationToken ct = default) => inner.ConcurrencyKeysAsync(queue, ct);
        public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, string? progress, DateTimeOffset? lastHeartbeat, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> RedriveAsync(string queue, string id, DateTimeOffset notBefore, CancellationToken ct = default) => inner.RedriveAsync(queue, id, notBefore, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
