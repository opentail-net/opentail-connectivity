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

    /// <param name="capabilities">The queue's capabilities, when known without opening one (a broker that is not reachable while
    /// tests are being discovered); otherwise a queue is opened to read them.</param>
    public static IReadOnlyList<WorkQueueConformanceCheck> Checks(
        Func<WorkQueueOpener> newStorage, bool realTime = false, WorkQueueCapabilities? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(newStorage);
        capabilities ??= Probe(newStorage);

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
            Check("a cancelled waiting or delayed job never runs", capabilities.Cancellation, CancelWaitingAsync),
            Check("cancelling a running job revokes its lease", capabilities.Cancellation, CancelRunningAsync),
            Check("a cancelled job's key still deduplicates", capabilities.Cancellation && capabilities.DeduplicatesKeys, CancelKeepsKeyAsync),
            Check("run now makes a delayed job due", capabilities.DelayedDelivery, RunNowAsync),
            Check("pending lists waiting and delayed jobs, with source, without bodies, without using up attempts", true, PendingAsync),
            Check("pending lists running jobs", capabilities.ListsRunningJobs, PendingRunningAsync),
            Check("a dead letter can be deleted", true, DeleteDeadLetterAsync),
            Check("a concurrency key never runs more than its limit, across workers", capabilities.ConcurrencyLimits, KeyLimitAsync),
            Check("a full key does not hold up other keys", capabilities.ConcurrencyLimits, FullKeyDoesNotBlockAsync),
            Check("an expired lease frees its slot", capabilities.ConcurrencyLimits && capabilities.LeaseExpiry, ExpiredLeaseFreesSlotAsync),
            Check("prefix limits and the default limit apply", capabilities.ConcurrencyLimits, PrefixAndDefaultLimitAsync),
            Check("skipped keys are not leased", capabilities.ConcurrencyLimits, SkippedKeysAsync),
            Check("an open circuit releases nothing and uses no attempts, then lets one through", capabilities.ConcurrencyLimits, CircuitAsync),
            Check("failures that are not the resource's do not open the circuit", capabilities.ConcurrencyLimits, OrdinaryFailuresAsync),
            Check("the maximum run time is carried, or the default given", true, MaxRunTimeAsync),
            Check("dead letters past their retention are removed", true, DeadLetterRetentionAsync),
            Check("looking at dead letters and pending jobs many times destroys nothing", true, LookingDestroysNothingAsync),
            Check("a job that keeps crashing its worker ends in the dead letters", capabilities.Durable && capabilities.LeaseExpiry, CrashLoopAsync),
            Check("a long run of jobs for one full resource does not hide other resources", capabilities.ConcurrencyLimits, NoStarvationAsync),
            Check("a key scoped until finished can be used again once the job finished", capabilities.DeduplicatesKeys, UntilFinishedAsync),
            Check("a key scoped while pending lets a new job queue while the first runs", capabilities.DeduplicatesKeys, WhilePendingAsync),
            Check("a rate limit caps how many start per window", capabilities.ConcurrencyLimits, RateLimitAsync),
            Check("a job held back by an open circuit uses none of its key's rate limit", capabilities.ConcurrencyLimits, RateNotChargedWhileHeldAsync),
            Check("the enqueuer's trace context travels with the job", true, TraceContextAsync),
            Check("a redrive can be delayed", capabilities.DelayedDelivery, DelayedRedriveAsync),
            Check("a running job's heartbeat progress is kept", capabilities.ListsRunningJobs, ProgressAsync),
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
        await q.EnqueueAsync(new EnqueueRequest(Q, "tenant-b", """{"n":"ünï"}""", "k-1", Headers: headers, Source: "rule:7"));
        var job = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(job.Tenant == "tenant-b" && job.Body == """{"n":"ünï"}""" && job.Key == "k-1", "tenant, body or key changed");
        Ensure(job.Source == "rule:7", "the source was lost");
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
        if (second.Capabilities.DeduplicatesKeys)
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

    private static async Task CancelWaitingAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var waiting = (await q.EnqueueAsync(Job("waiting"))).Id;
        var delayed = (await q.EnqueueAsync(Job("delayed", notBefore: clock.GetUtcNow() + TimeSpan.FromSeconds(4)))).Id;
        Ensure(await q.CancelAsync(Q, waiting, "operator") && await q.CancelAsync(Q, delayed, "operator"), "cancelling an unfinished job failed");
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a cancelled job ran");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a cancelled delayed job ran once due");
        Ensure((await q.DeadLettersAsync(Q)).Count == 0, "a cancelled job went to the dead letters");
    }

    private static async Task CancelRunningAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var job = Single(await q.LeaseAsync(Q, 1, Lease));
        Ensure(await q.CancelAsync(Q, job.Id, "operator"), "cancelling a running job failed");
        Ensure(!await q.ExtendAsync(job, Lease), "the worker could still extend a cancelled job's lease");
        Ensure(!await q.CompleteAsync(job), "the worker could still complete a cancelled job");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "a cancelled running job came back after its lease");
    }

    private static async Task CancelKeepsKeyAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var first = await q.EnqueueAsync(Job(key: "nightly@2026-10-22"));
        Ensure(await q.CancelAsync(Q, first.Id, "operator"), "cancelling failed");
        Ensure((await q.EnqueueAsync(Job(key: "nightly@2026-10-22"))).Duplicate, "a cancelled job's key let the same job in again");
    }

    private static async Task RunNowAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var id = (await q.EnqueueAsync(Job(notBefore: clock.GetUtcNow() + TimeSpan.FromMinutes(1)))).Id;
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a delayed job ran early");
        Ensure(await q.RunNowAsync(Q, id), "run now did not find the delayed job");
        Ensure(Single(await q.LeaseAsync(Q, 1, Lease)).Id == id, "the job did not become due");
        Ensure(!await q.RunNowAsync(Q, "no-such-job"), "run now found a job that does not exist");
    }

    private static async Task PendingAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var waiting = (await q.EnqueueAsync(new EnqueueRequest(Q, "acme", "12345", "k", Source: "schedule:nightly"))).Id;
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(20));
        var delayed = (await q.EnqueueAsync(Job(notBefore: clock.GetUtcNow() + TimeSpan.FromMinutes(1)))).Id;
        var pending = await q.PendingAsync(Q);
        Ensure(pending.Count == 2, $"pending listed {pending.Count} jobs, not 2");
        var w = pending.Single(p => p.Id == waiting);
        Ensure(w.State == PendingState.Waiting && w.Source == "schedule:nightly" && w.Key == "k" && w.BodyBytes == 5 && w.Tenant == "acme",
            "the waiting job was listed wrongly");
        Ensure(pending.Single(p => p.Id == delayed).State == PendingState.Delayed, "the delayed job was not listed as delayed");
        Ensure(Single(await q.LeaseAsync(Q, 1, Lease)).Attempt == 1, "listing used up an attempt");
    }

    private static async Task PendingRunningAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var job = Single(await q.LeaseAsync(Q, 1, Lease));
        var row = (await q.PendingAsync(Q)).SingleOrDefault(p => p.Id == job.Id);
        Ensure(row is { State: PendingState.Running, Attempts: 1 }, "the running job was not listed as running");
    }

    private static async Task DeleteDeadLetterAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        Ensure(await q.DeadLetterAsync(Single(await q.LeaseAsync(Q, 1, Lease)), "bad"), "dead-lettering failed");
        var id = Single(await q.DeadLettersAsync(Q)).Id;
        Ensure(await q.DeleteDeadLetterAsync(Q, id), "deleting the dead letter failed");
        Ensure((await q.DeadLettersAsync(Q)).Count == 0, "the deleted dead letter is still listed");
        Ensure(!await q.DeleteDeadLetterAsync(Q, id), "a dead letter was deleted twice");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a deleted dead letter ran");
    }

    private static EnqueueRequest Keyed(string key, string body = "{}") => new(Q, "acme", body, ConcurrencyKey: key);

    private static async Task KeyLimitAsync(WorkQueueOpener open, Clock clock)
    {
        var options = new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:a"] = 2 });
        await using var a = open(clock, options);
        await using var b = open(clock, options);
        for (var i = 0; i < 6; i++)
            await a.EnqueueAsync(Keyed("db:a"));
        var first = await a.LeaseAsync(Q, 5, TimeSpan.FromMinutes(5));
        Ensure(first.Count == 2, $"the first worker got {first.Count} jobs for a key limited to 2");
        Ensure((await b.LeaseAsync(Q, 5, TimeSpan.FromMinutes(5))).Count == 0, "a second worker went over the key's limit");
        Ensure(await a.CompleteAsync(first[0]), "completing failed");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5)); // a broker parks jobs over the limit for a few seconds
        Ensure((await b.LeaseAsync(Q, 5, TimeSpan.FromMinutes(5))).Count == 1, "finishing a job did not free its slot for another worker");
    }

    private static async Task FullKeyDoesNotBlockAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:a"] = 1 }));
        for (var i = 0; i < 3; i++)
            await q.EnqueueAsync(Keyed("db:a"));
        await q.EnqueueAsync(Keyed("db:b"));
        await q.EnqueueAsync(Job("no key"));
        var jobs = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5));
        Ensure(jobs.Count == 3 && jobs.Count(j => j.ConcurrencyKey == "db:a") == 1 && jobs.Any(j => j.ConcurrencyKey == "db:b") && jobs.Any(j => j.ConcurrencyKey is null),
            $"leased [{string.Join(", ", jobs.Select(j => j.ConcurrencyKey ?? "none"))}], not one of each");
    }

    private static async Task ExpiredLeaseFreesSlotAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:a"] = 1 }));
        await q.EnqueueAsync(Keyed("db:a"));
        await q.EnqueueAsync(Keyed("db:a"));
        Ensure((await q.LeaseAsync(Q, 5, Lease)).Count == 1, "the limit of 1 was not kept");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        Ensure((await q.LeaseAsync(Q, 5, Lease)).Count == 1, "a lapsed lease still held its slot");
    }

    private static async Task PrefixAndDefaultLimitAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:*"] = 1 }, DefaultConcurrencyLimit: 2));
        for (var i = 0; i < 3; i++)
        {
            await q.EnqueueAsync(Keyed("db:x"));
            await q.EnqueueAsync(Keyed("erp:y"));
        }

        var jobs = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5));
        Ensure(jobs.Count(j => j.ConcurrencyKey == "db:x") == 1 && jobs.Count(j => j.ConcurrencyKey == "erp:y") == 2,
            "the prefix limit (1) or the default limit (2) was not applied");
    }

    private static async Task SkippedKeysAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Keyed("db:a"));
        await q.EnqueueAsync(Keyed("db:b"));
        var jobs = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5), ["db:a"]);
        Ensure(jobs.Count == 1 && jobs[0].ConcurrencyKey == "db:b", "a skipped key was leased");
    }

    private static async Task CircuitAsync(WorkQueueOpener open, Clock clock)
    {
        var options = new WorkQueueOptions(CircuitFailures: 2, CircuitCooldown: TimeSpan.FromSeconds(3));
        await using var q = open(clock, options);
        for (var i = 0; i < 4; i++)
            await q.EnqueueAsync(Keyed("db:r"));
        for (var i = 0; i < 2; i++)
            Ensure(await q.RetryAsync(Single(await q.LeaseAsync(Q, 1, Lease)), TimeSpan.Zero, "connection refused", resourceFailure: true), "retry failed");
        var state = (await q.ConcurrencyKeysAsync(Q)).SingleOrDefault(k => k.ConcurrencyKey == "db:r");
        Ensure(state is { Circuit: CircuitState.Open }, "two resource failures in a row did not open the circuit");
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "an open circuit released a job");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(6));
        var probe = await q.LeaseAsync(Q, 10, Lease);
        Ensure(probe.Count == 1, $"half-open let {probe.Count} jobs through, not 1");
        Ensure(probe[0].Attempt <= 3, "jobs used attempts while the circuit was open");
        Ensure(await q.CompleteAsync(probe[0]), "completing the probe failed");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        var rest = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5));
        Ensure(rest.Count == 3, $"after the probe succeeded {rest.Count} jobs were released, not the 3 left");
        Ensure(rest.All(j => j.Attempt <= 2), "jobs that waited behind the open circuit used up attempts");
    }

    private static async Task OrdinaryFailuresAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(CircuitFailures: 2));
        await q.EnqueueAsync(Keyed("db:r"));
        for (var i = 0; i < 2; i++)
            Ensure(await q.RetryAsync(Single(await q.LeaseAsync(Q, 1, Lease)), TimeSpan.Zero, "bad input", resourceFailure: false), "retry failed");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 1, "failures that were not the resource's opened the circuit");
    }

    private static async Task MaxRunTimeAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(DefaultMaxRunTime: TimeSpan.FromMinutes(3)));
        await q.EnqueueAsync(new EnqueueRequest(Q, "acme", "own", MaxRunTime: TimeSpan.FromMinutes(7)));
        await q.EnqueueAsync(Job("default"));
        var jobs = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5));
        Ensure(jobs.Single(j => j.Body == "own").MaxRunTime == TimeSpan.FromMinutes(7), "the job's maximum run time was lost");
        Ensure(jobs.Single(j => j.Body == "default").MaxRunTime == TimeSpan.FromMinutes(3), "the queue's default maximum run time was not given");
    }

    private static async Task DeadLetterRetentionAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(DeadLetterRetention: TimeSpan.FromSeconds(3)));
        await q.EnqueueAsync(Job());
        Ensure(await q.DeadLetterAsync(Single(await q.LeaseAsync(Q, 1, Lease)), "bad"), "dead-lettering failed");
        Ensure((await q.DeadLettersAsync(Q)).Count == 1, "the dead letter is missing");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure((await q.DeadLettersAsync(Q)).Count == 0, "a dead letter outlived its retention");
    }

    private static async Task LookingDestroysNothingAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job("dead"));
        Ensure(await q.DeadLetterAsync(Single(await q.LeaseAsync(Q, 1, Lease)), "bad"), "dead-lettering failed");
        await q.EnqueueAsync(Job("delayed", notBefore: clock.GetUtcNow() + TimeSpan.FromMinutes(5)));
        for (var i = 0; i < 25; i++)
        {
            await q.DeadLettersAsync(Q);
            await q.PendingAsync(Q);
        }

        Ensure((await q.DeadLettersAsync(Q)).Count == 1, "looking at the dead letters 25 times lost one");
        Ensure((await q.PendingAsync(Q)).Count(p => p.State == PendingState.Delayed) == 1, "looking at pending jobs 25 times lost the delayed one");
    }

    private static async Task CrashLoopAsync(WorkQueueOpener open, Clock clock)
    {
        var options = new WorkQueueOptions(MaxAttempts: 3);
        await using (var first = open(clock, options))
            await first.EnqueueAsync(Job("poison"));
        for (var crash = 1; crash <= 3; crash++)
        {
            var worker = open(clock, options);
            var job = Single(await worker.LeaseAsync(Q, 1, Lease));
            Ensure(job.Attempt == crash, $"crash {crash}: the job came back as attempt {job.Attempt}");
            await worker.DisposeAsync(); // the worker dies holding the job
            await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        }

        await using var last = open(clock, options);
        Ensure((await last.LeaseAsync(Q, 1, Lease)).Count == 0, "a job that crashed its worker every time ran again past its attempts");
        Ensure((await last.DeadLettersAsync(Q)).Count == 1, "the crash-looping job is not in the dead letters");
    }

    private static async Task NoStarvationAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(ConcurrencyLimits: new Dictionary<string, int> { ["db:a"] = 1 }));
        for (var i = 0; i < 600; i++)
            await q.EnqueueAsync(Keyed("db:a"));
        await q.EnqueueAsync(Keyed("db:b", "behind"));
        Ensure((await q.LeaseAsync(Q, 1, TimeSpan.FromMinutes(5))).Count == 1, "the first db:a job was not leased");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5)); // a broker parks jobs over the limit for a few seconds
        var found = false;
        for (var round = 0; round < 40 && !found; round++)
        {
            found = (await q.LeaseAsync(Q, 1, TimeSpan.FromMinutes(5))).Any(j => j.Body == "behind");
            if (!found) await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        }

        Ensure(found, "a job for a free resource stayed hidden behind 600 jobs for a full one");
    }

    private static async Task UntilFinishedAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var once = new EnqueueRequest(Q, "acme", "{}", Key: "refresh:acme", KeyScope: KeyScope.UntilFinished);
        Ensure(!(await q.EnqueueAsync(once)).Duplicate, "the first job was refused");
        Ensure((await q.EnqueueAsync(once)).Duplicate, "a second job got in while the first waited");
        Ensure(await q.CompleteAsync(Single(await q.LeaseAsync(Q, 1, Lease))), "completing failed");
        Ensure(!(await q.EnqueueAsync(once)).Duplicate, "the key stayed blocked after the job finished");
        Ensure((await q.EnqueueAsync(once with { KeyScope = KeyScope.Retention })).Duplicate, "a retention-scoped request ignored a waiting job");
    }

    private static async Task WhilePendingAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        var debounce = new EnqueueRequest(Q, "acme", "{}", Key: "index:acme", KeyScope: KeyScope.WhilePending);
        await q.EnqueueAsync(debounce);
        Ensure((await q.EnqueueAsync(debounce)).Duplicate, "two pending jobs for the same key");
        var running = Single(await q.LeaseAsync(Q, 1, TimeSpan.FromMinutes(5)));
        Ensure(!(await q.EnqueueAsync(debounce)).Duplicate, "a change made during the run was not queued behind it");
        Ensure((await q.EnqueueAsync(debounce)).Duplicate, "a third job got in while the second waited");
        Ensure(await q.CompleteAsync(running), "the first job could not complete after its key moved on");
    }

    private static async Task RateLimitAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions(RateLimits: new Dictionary<string, JobRateLimit> { ["api:*"] = new(2, TimeSpan.FromSeconds(3)) }));
        for (var i = 0; i < 5; i++)
            await q.EnqueueAsync(Keyed("api:crm"));
        var first = await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5));
        Ensure(first.Count == 2, $"{first.Count} started in the first window, not 2");
        foreach (var j in first) await q.CompleteAsync(j);
        Ensure((await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5))).Count == 0, "finishing early let more start in the same window");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5)); // a broker parks jobs over the limit for a few seconds
        Ensure((await q.LeaseAsync(Q, 10, TimeSpan.FromMinutes(5))).Count == 2, "the next window did not allow 2 more");
    }

    private static async Task RateNotChargedWhileHeldAsync(WorkQueueOpener open, Clock clock)
    {
        var options = new WorkQueueOptions(CircuitFailures: 1, CircuitCooldown: TimeSpan.FromSeconds(3),
            RateLimits: new Dictionary<string, JobRateLimit> { ["api:*"] = new(2, TimeSpan.FromMinutes(5)) });
        await using var q = open(clock, options);
        await q.EnqueueAsync(Keyed("api:crm"));
        // One start, a resource failure: the circuit opens.
        Ensure(await q.RetryAsync(Single(await q.LeaseAsync(Q, 1, Lease)), TimeSpan.Zero, "connection refused", resourceFailure: true), "retry failed");
        for (var i = 0; i < 3; i++)
            Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 0, "an open circuit released a job");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(6));
        // One of the two starts in the window is used; the attempts held back by the circuit must not have used the other.
        Ensure((await q.LeaseAsync(Q, 10, Lease)).Count == 1, "attempts held back by the open circuit used up the rate limit");
    }

    private static async Task TraceContextAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        using (var enqueuer = new System.Diagnostics.Activity("enqueue").SetIdFormat(System.Diagnostics.ActivityIdFormat.W3C).Start())
        {
            await q.EnqueueAsync(Job());
            var job = Single(await q.LeaseAsync(Q, 1, Lease));
            Ensure(job.Headers.GetValueOrDefault(JobHeaders.TraceParent) == enqueuer.Id, "the trace context was not carried with the job");
        }
    }

    private static async Task DelayedRedriveAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        Ensure(await q.DeadLetterAsync(Single(await q.LeaseAsync(Q, 1, Lease)), "bad"), "dead-lettering failed");
        var id = Single(await q.DeadLettersAsync(Q)).Id;
        Ensure(await q.RedriveAsync(Q, id, clock.GetUtcNow() + TimeSpan.FromSeconds(4)), "the delayed redrive failed");
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 0, "a delayed redrive ran early");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure((await q.LeaseAsync(Q, 1, Lease)).Count == 1, "a delayed redrive did not run once due");
    }

    private static async Task ProgressAsync(WorkQueueOpener open, Clock clock)
    {
        await using var q = open(clock, new WorkQueueOptions());
        await q.EnqueueAsync(Job());
        var job = Single(await q.LeaseAsync(Q, 1, TimeSpan.FromMinutes(5)));
        var beat = clock.GetUtcNow();
        Ensure(await q.ExtendAsync(job, TimeSpan.FromMinutes(5), "page 40 of 120", beat), "extending with progress failed");
        var row = (await q.PendingAsync(Q)).Single(p => p.Id == job.Id);
        Ensure(row.Progress == "page 40 of 120" && row.LastHeartbeat?.ToUnixTimeMilliseconds() == beat.ToUnixTimeMilliseconds(), "the progress or heartbeat was not kept");
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
