using OpenTail.Connectivity.Queues;
using OpenTail.Connectivity.Queues.Sqlite;

namespace OpenTail.Connectivity.Tests.Queues;

/// <summary>Every work queue in this repository runs the full conformance suite.</summary>
public sealed class WorkQueueConformanceTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var queue in Storages.Keys)
            foreach (var check in WorkQueueConformance.Checks(Storages[queue]))
                data.Add(queue, check.Name);
        return data;
    }

    private static readonly Dictionary<string, Func<WorkQueueOpener>> Storages = new(StringComparer.Ordinal)
    {
        ["in-memory"] = static () =>
        {
            InMemoryWorkQueue? shared = null; // a second open is a second worker on the same queue
            return (time, options) => shared ??= new InMemoryWorkQueue(options, time);
        },
        ["sqlite"] = static () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"ot-queue-{Guid.NewGuid():N}.db");
            return (time, options) => new SqliteWorkQueue(path, options, time);
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Queue_PassesCheck(string queue, string check)
    {
        var c = WorkQueueConformance.Checks(Storages[queue]).Single(x => x.Name == check);
        Assert.SkipUnless(c.AppliesTo, $"{queue} does not declare the capability this check needs");
        await c.RunAsync();
    }

    [Theory]
    [InlineData("an expired lease hands the job to another worker and the first loses it")]
    [InlineData("a lost lease cannot complete, retry, extend or dead-letter")]
    public async Task Suite_CatchesAQueueThatIgnoresLostLeases(string check)
    {
        var c = WorkQueueConformance.Checks(() => (time, options) => new IgnoresLeaseTokens(new InMemoryWorkQueue(options, time)))
            .Single(x => x.Name == check);
        await Assert.ThrowsAsync<WorkQueueConformanceException>(c.RunAsync);
    }

    [Fact]
    public async Task Sqlite_workers_in_separate_instances_share_one_file_without_sharing_jobs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ot-queue-{Guid.NewGuid():N}.db");
        await using var producer = new SqliteWorkQueue(path);
        for (var i = 0; i < 40; i++)
            await producer.EnqueueAsync(new EnqueueRequest("work", "acme", "job " + i));

        var taken = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await using var worker = new SqliteWorkQueue(path);
            while ((await worker.LeaseAsync("work", 3, TimeSpan.FromMinutes(1))) is { Count: > 0 } batch)
                foreach (var job in batch)
                {
                    taken.Add(job.Id);
                    Assert.True(await worker.CompleteAsync(job));
                }
        })));

        Assert.Equal(40, taken.Distinct().Count());
        Assert.Equal(40, taken.Count);
    }

    /// <summary>Lets any caller finish a job whatever lease it holds: the bug the lease checks exist for.</summary>
    private sealed class IgnoresLeaseTokens(InMemoryWorkQueue inner) : IWorkQueue
    {
        public WorkQueueCapabilities Capabilities => inner.Capabilities;
        public Task<EnqueueResult> EnqueueAsync(EnqueueRequest request, CancellationToken ct = default) => inner.EnqueueAsync(request, ct);
        public Task<IReadOnlyList<LeasedJob>> LeaseAsync(string queue, int max, TimeSpan lease, CancellationToken ct = default) => inner.LeaseAsync(queue, max, lease, ct);
        public Task<bool> CompleteAsync(LeasedJob job, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> RetryAsync(LeasedJob job, TimeSpan delay, string? error = null, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeadLetterAsync(LeasedJob job, string reason, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ExtendAsync(LeasedJob job, TimeSpan lease, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<DeadLetter>> DeadLettersAsync(string queue, int max = 100, CancellationToken ct = default) => inner.DeadLettersAsync(queue, max, ct);
        public Task<bool> RedriveAsync(string queue, string id, CancellationToken ct = default) => inner.RedriveAsync(queue, id, ct);
        public Task<bool> DeleteDeadLetterAsync(string queue, string id, CancellationToken ct = default) => inner.DeleteDeadLetterAsync(queue, id, ct);
        public Task<bool> CancelAsync(string queue, string id, string reason, CancellationToken ct = default) => inner.CancelAsync(queue, id, reason, ct);
        public Task<bool> RunNowAsync(string queue, string id, CancellationToken ct = default) => inner.RunNowAsync(queue, id, ct);
        public Task<IReadOnlyList<PendingJob>> PendingAsync(string queue, int max = 100, CancellationToken ct = default) => inner.PendingAsync(queue, max, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
