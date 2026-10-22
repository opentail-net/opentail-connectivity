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
        Assert.InRange(retried.Delay!.Value, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(120));
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
        var worker = new WorkQueueWorker(queue, Options(), async (_, ct) =>
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

    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(2, 4_000)]
    [InlineData(4, 16_000)]
    [InlineData(20, 300_000)]
    public void Backoff_doubles_up_to_the_cap_within_twenty_percent(int attempt, int expectedMs)
    {
        for (var i = 0; i < 50; i++)
        {
            var delay = WorkQueueWorker.Backoff(attempt, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));
            Assert.InRange(delay.TotalMilliseconds, expectedMs * 0.8, expectedMs * 1.2);
        }
    }

    [Fact]
    public void A_worker_must_serve_a_tenant()
        => Assert.Throws<ArgumentException>(() => new WorkQueueWorker(new InMemoryWorkQueue(), new WorkQueueWorkerOptions(Q, new HashSet<string>()), (_, _) => Task.CompletedTask));

    private static WorkQueueWorkerOptions Options(int concurrency = 1, TimeSpan? lease = null, TimeSpan? retryBase = null)
        => new(Q, Acme, concurrency, lease ?? TimeSpan.FromMinutes(1), IdleWait: TimeSpan.FromMilliseconds(20), RetryBaseDelay: retryBase);

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
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
