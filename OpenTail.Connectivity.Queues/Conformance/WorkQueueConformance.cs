namespace OpenTail.Connectivity.Queues;

/// <summary>A conformance check failed: the queue breaks a guarantee its users rely on.</summary>
public sealed class WorkQueueConformanceException(string message) : Exception(message);

/// <summary>One conformance check. <see cref="RunAsync"/> throws <see cref="WorkQueueConformanceException"/> on failure.</summary>
/// <param name="AppliesTo">False when the queue does not declare the capability the check needs; skip it then.</param>
public sealed record WorkQueueConformanceCheck(string Name, bool AppliesTo, Func<Task> RunAsync);

/// <summary>Opens a queue over one storage. Called more than once, it opens the same storage again (a second worker, or a
/// restart); a fresh storage comes from a new call to the factory passed to <see cref="WorkQueueConformance.Checks"/>.</summary>
public delegate IWorkQueue WorkQueueOpener(TimeProvider time, WorkQueueOptions options);

/// <summary>
/// The checks every <see cref="IWorkQueue"/> must pass, independent of any test framework. Pass a factory that makes a new,
/// empty storage and returns how to open it. Time is simulated with a manual clock; a queue that cannot use one (a broker) passes
/// <c>realTime: true</c> and the checks wait for real, a few seconds each.
/// </summary>
public static class WorkQueueConformance
{
    private const string Q = "conformance";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<WorkQueueConformanceCheck> Checks(Func<WorkQueueOpener> newStorage, bool realTime = false)
    {
        ArgumentNullException.ThrowIfNull(newStorage);
        var capabilities = Probe(newStorage);

        WorkQueueConformanceCheck Check(string name, bool applies, Func<WorkQueueOpener, Clock, Task> run)
            => new(name, applies, () => run(newStorage(), new Clock(realTime)));

        return
        [
            Check("a leased job goes to one worker and a completed job never comes back", true, LeasedOnceAsync),
            Check("tenant, body, key and headers round trip", true, RoundTripAsync),
            Check("queues are separate", true, SeparateQueuesAsync),
            Check("the oldest due job comes first", true, OldestFirstAsync),
            Check("an expired lease hands the job to another worker and the first loses it", capabilities.LeaseExpiry, LeaseExpiresAsync),
            Check("a lost lease cannot complete, retry, extend or dead-letter", capabilities.LeaseExpiry, LostLeaseAsync),
            Check("extending a lease keeps the job", capabilities.LeaseExpiry, ExtendAsync),
            Check("a delayed job never runs early", capabilities.DelayedDelivery, NotBeforeAsync),
            Check("a retry waits for its delay", capabilities.DelayedDelivery, RetryDelayAsync),
            Check("the same key is one job, even after it finished", capabilities.DeduplicatesKeys, SameKeyAsync),
            Check("keys are per queue", capabilities.DeduplicatesKeys, KeysPerQueueAsync),
            Check("a job past its attempts goes to the dead letters", true, AttemptsLimitAsync),
            Check("a job past its age goes to the dead letters", true, AgeLimitAsync),
            Check("a dead letter can be redriven as a fresh job", true, DeadLetterAndRedriveAsync),
            Check("concurrent workers never share a job", true, ConcurrentWorkersAsync),
            Check("jobs survive closing and reopening the queue", capabilities.Durable, SurvivesReopenAsync),
            Check("invalid requests are refused", true, InvalidRefusedAsync),
        ];
    }

    private static WorkQueueCapabilities Probe(Func<WorkQueueOpener> newStorage)
    {
        var queue = newStorage()(TimeProvider.System, new WorkQueueOptions());
        try { return queue.Capabilities; }
        finally { queue.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static EnqueueRequest Job(string body = "{}", string? key = null, DateTimeOffset? notBefore = null, string queue = Q)
        => new(queue, "acme", body, key, notBefore);

    private static async Task LeasedOnceAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var id = (await q.EnqueueAsync(Job())).Id;
        var first = await q.LeaseAsync(Q, 10, Lease);
        Ensure(first.Count == 1 && first[0].Id == id && first[0].Attempt == 1, "the job was not leased once, as attempt 1");
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a leased job was handed out again while its lease held");
        Ensure(await q.CompleteAsync(first[0]), "completing with a held lease failed");
        await clock.AdvanceAsync(Lease * 2);
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a completed job came back");
    }

    private static async Task RoundTripAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var headers = new Dictionary<string, string> { ["requester"] = "rule:7", ["trace"] = "t-1" };
        await q.EnqueueAsync(new EnqueueRequest(Q, "tenant-b", """{"n":"ünï"}""", "k-1", Headers: headers));
        var job = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(job.Tenant == "tenant-b" && job.Body == """{"n":"ünï"}""" && job.Key == "k-1", "tenant, body or key changed");
        Ensure(job.Headers.Count == 2 && job.Headers["requester"] == "rule:7" && job.Headers["trace"] == "t-1", "headers changed");
    }

    private static async Task SeparateQueuesAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job(queue: "conformance-a"));
        Ensure((await q.LeaseAsync("conformance-b", 10, Lease)).Count == 0, "a job leaked into another queue");
        Ensure((await q.LeaseAsync("conformance-a", 10, Lease)).Count == 1, "the job is missing from its queue");
    }

    private static async Task OldestFirstAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job("first"));
        await q.EnqueueAsync(Job("second"));
        Ensure(Single(await q.LeaseAsync(Q, 1, Lease)).Body == "first", "the newer job came before the older one");
    }

    private static async Task LeaseExpiresAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var first = Single(await q.LeaseAsync(Q, 1, Lease));
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        var second = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(second.Id == first.Id && second.Attempt == 2 && second.LeaseToken != first.LeaseToken, "the expired job did not come back as attempt 2 with a new lease");
        Ensure(!await q.CompleteAsync(first), "the worker whose lease expired could still complete the job");
        Ensure(await q.CompleteAsync(second), "the new lease holder could not complete the job");
    }

    private static async Task LostLeaseAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var stale = Single(await q.LeaseAsync(Q, 1, Lease));
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        var current = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(!await q.RetryAsync(stale, TimeSpan.Zero), "a lost lease could retry");
        Ensure(!await q.ExtendAsync(stale, Lease), "a lost lease could extend");
        Ensure(!await q.DeadLetterAsync(stale, "stale"), "a lost lease could dead-letter");
        Ensure((await q.DeadLettersAsync(Q)).Count == 0, "a lost lease's dead-letter took effect");
        Ensure(await q.CompleteAsync(current), "the lease holder lost the job to a stale worker");
    }

    private static async Task ExtendAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var job = Single(await q.LeaseAsync(Q, 1, Lease));
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Ensure(await q.ExtendAsync(job, TimeSpan.FromSeconds(6)), "extending a held lease failed");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "the job was handed out although its lease was extended");
        Ensure(await q.CompleteAsync(job), "completing after an extension failed");
    }

    private static async Task NotBeforeAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job(notBefore: clock.GetUtcNow() + TimeSpan.FromSeconds(4)));
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a delayed job ran early");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 1, "a delayed job did not run once due");
    }

    private static async Task RetryDelayAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        Ensure(await q.RetryAsync(Single(await q.LeaseAsync(Q, 1, Lease)), TimeSpan.FromSeconds(4), "boom"), "retry with a held lease failed");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a retried job ran before its delay");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure(Single(await q.LeaseAsync(Q, 1, Lease)).Attempt == 2, "the retried job did not come back as attempt 2");
    }

    private static async Task SameKeyAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var first = await q.EnqueueAsync(Job(key: "rule-7@2026-10-22T09:00Z"));
        var again = await q.EnqueueAsync(Job(key: "rule-7@2026-10-22T09:00Z"));
        Ensure(!first.Duplicate && again.Duplicate && again.Id == first.Id, "the same key added a second job");
        Ensure(await q.CompleteAsync(Single(await q.LeaseAsync(Q, 10, Lease))), "completing failed");
        Ensure((await q.EnqueueAsync(Job(key: "rule-7@2026-10-22T09:00Z"))).Duplicate, "a finished job's key did not deduplicate");
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a duplicate key ran again");
    }

    private static async Task KeysPerQueueAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job(key: "k", queue: "conformance-a"));
        Ensure(!(await q.EnqueueAsync(Job(key: "k", queue: "conformance-b"))).Duplicate, "a key in one queue blocked the same key in another");
    }

    private static async Task AttemptsLimitAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(MaxAttempts: 2));
        await q.EnqueueAsync(Job());
        for (var i = 0; i < 2; i++)
            Ensure(await q.RetryAsync(Single(await q.LeaseAsync(Q, 1, Lease)), TimeSpan.Zero, "failed " + i), "retry failed");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a job ran past its attempts limit");
        var dead = Single(await q.DeadLettersAsync(Q));
        Ensure(dead.Attempts == 2 && dead.Reason.Contains("attempts", StringComparison.Ordinal) && dead.LastError == "failed 1",
            "the dead letter does not say it ran out of attempts, or lost the last error");
    }

    private static async Task AgeLimitAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(MaxAge: TimeSpan.FromSeconds(3)));
        await q.EnqueueAsync(Job());
        await clock.AdvanceAsync(TimeSpan.FromSeconds(4));
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a job ran past its age limit");
        Ensure(Single(await q.DeadLettersAsync(Q)).Reason.Contains("older", StringComparison.Ordinal), "the dead letter does not say it was too old");
    }

    private static async Task DeadLetterAndRedriveAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job("payload", key: "k"));
        Ensure(await q.DeadLetterAsync(Single(await q.LeaseAsync(Q, 1, Lease)), "cannot parse"), "dead-lettering a held job failed");
        var dead = Single(await q.DeadLettersAsync(Q));
        Ensure(dead.Reason == "cannot parse" && dead.Body == "payload", "the dead letter lost its reason or body");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a dead letter was handed out");
        Ensure(await q.RedriveAsync(Q, dead.Id), "redrive failed");
        Ensure(!await q.RedriveAsync(Q, dead.Id), "the same dead letter was redriven twice");
        var again = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(again.Body == "payload" && again.Attempt == 1 && (await q.DeadLettersAsync(Q)).Count == 0, "the redriven job is not a fresh job");
    }

    private static async Task ConcurrentWorkersAsync(WorkQueueOpener open, Clock clock)
    {
        const int jobs = 60;
        await using var a = open(clock, new WorkQueueOptions());
        await using var b = open(clock, new WorkQueueOptions());
        for (var i = 0; i < jobs; i++)
            await a.EnqueueAsync(Job("job " + i));

        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task WorkAsync(IWorkQueue q)
        {
            while (true)
            {
                var batch = await q.LeaseAsync(Q, 4, TimeSpan.FromMinutes(5));
                if (batch.Count == 0) return;
                foreach (var job in batch)
                {
                    seen.Add(job.Id);
                    await q.CompleteAsync(job);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => WorkAsync(i % 2 == 0 ? a : b))));
        Ensure(seen.Count == jobs && seen.Distinct().Count() == jobs, $"workers took {seen.Count} jobs, {seen.Distinct().Count()} distinct, of {jobs}");
    }

    private static async Task SurvivesReopenAsync(WorkQueueOpener open, Clock clock)
    {
        var first = open(clock, new WorkQueueOptions());
        await first.EnqueueAsync(Job("waiting"));
        await first.EnqueueAsync(Job("in flight"));
        await first.EnqueueAsync(Job("done", key: "done-key"));
        var leased = await first.LeaseAsync(Q, 3, Lease);
        await first.CompleteAsync(leased.Single(j => j.Body == "done"));
        await first.RetryAsync(leased.Single(j => j.Body == "waiting"), TimeSpan.Zero);
        await first.DisposeAsync(); // "in flight" is still leased: its worker died with the process

        await using var second = open(clock, new WorkQueueOptions());
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        var bodies = (await second.LeaseAsync(Q, 10, Lease)).Select(j => j.Body).Order(StringComparer.Ordinal).ToList();
        Ensure(bodies.SequenceEqual(["in flight", "waiting"]), $"after reopening the queue held [{string.Join(", ", bodies)}]");
        Ensure((await second.EnqueueAsync(Job(key: "done-key"))).Duplicate, "a finished job's key was forgotten on reopening");
    }

    private static async Task InvalidRefusedAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(MaxBodyBytes: 1024));
        await Refused(() => q.EnqueueAsync(Job(new string('x', 2000))), "an oversized body");
        await Refused(() => q.EnqueueAsync(Job(queue: "bad name!")), "a queue name with a space");
        await Refused(() => q.EnqueueAsync(new EnqueueRequest(Q, " ", "{}")), "a blank tenant");
        await Refused(() => q.LeaseAsync(Q, 0, Lease), "leasing zero jobs");
    }

    private static async Task Refused(Func<Task> act, string what)
    {
        try { await act(); }
        catch (ArgumentException) { return; }
        throw new WorkQueueConformanceException($"{what} was accepted");
    }

    private static LeasedJob Single(IReadOnlyList<LeasedJob> jobs)
    {
        Ensure(jobs.Count == 1, $"expected one job, got {jobs.Count}");
        return jobs[0];
    }

    private static DeadLetter Single(IReadOnlyList<DeadLetter> dead)
    {
        Ensure(dead.Count == 1, $"expected one dead letter, got {dead.Count}");
        return dead[0];
    }

    private static void Ensure(bool condition, string failure)
    {
        if (!condition) throw new WorkQueueConformanceException(failure);
    }

    /// <summary>A manual clock, or the real one (advancing waits) for queues that cannot take a <see cref="TimeProvider"/>.</summary>
    private sealed class Clock(bool real) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => real ? System.GetUtcNow() : _now;

        public Task AdvanceAsync(TimeSpan by)
        {
            if (real) return Task.Delay(by);
            _now += by;
            return Task.CompletedTask;
        }
    }
}
