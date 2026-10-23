namespace OpenTail.Connectivity.Queues;

/// <summary>Opens a control store over one storage. Called more than once, it opens the same storage again (a second worker); a
/// fresh storage comes from a new call to the factory passed to <see cref="QueueControlConformance.Checks"/>.</summary>
public delegate IQueueControl QueueControlOpener(TimeProvider time);

/// <summary>
/// The contract every <see cref="IQueueControl"/> must keep, independent of any test framework. Pass a factory that makes a new,
/// empty storage, and the cancellation retention the store was configured with. Time is simulated with a manual clock; a store
/// that keeps its own clock (a server) passes <c>realTime: true</c> and the checks wait for real, a few seconds each.
/// </summary>
public static class QueueControlConformance
{
    private const string Q = "conformance";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<WorkQueueConformanceCheck> Checks(Func<QueueControlOpener> newStorage, TimeSpan retention, bool realTime = false)
    {
        ArgumentNullException.ThrowIfNull(newStorage);

        WorkQueueConformanceCheck Check(string name, Func<QueueControlOpener, Clock, Task> run)
            => new(name, true, () => run(newStorage(), new Clock(realTime)));

        return
        [
            Check("a key never holds more slots than its limit, across workers", LimitAsync),
            Check("a lapsed slot frees its place", LapsedFreesAsync),
            Check("a renewed slot keeps its place", RenewKeepsAsync),
            Check("a lapsed slot cannot be renewed", NoRevivalAsync),
            Check("releasing is idempotent, and a released slot cannot be renewed", ReleaseAsync),
            Check("a rate window counts starts and opens again after its length", RateAsync),
            Check("circuit updates from many workers at once are not lost", CircuitContentionAsync),
            Check("a removed circuit is gone and starts again from nothing", CircuitRemovalAsync),
            Check("a cancellation is seen by every worker until the retention ends", (open, clock) => CancellationAsync(open, clock, retention)),
            Check("keys report their slots, circuits and starts, and empty keys are not reported", KeysAsync),
        ];
    }

    private static async Task LimitAsync(QueueControlOpener open, Clock clock)
    {
        var a = open(clock);
        var b = open(clock);
        Ensure(await a.TryTakeSlotAsync(Q, "db", 2, "t1", Lease), "the first slot was refused");
        Ensure(await b.TryTakeSlotAsync(Q, "db", 2, "t2", Lease), "the second slot was refused");
        Ensure(!await a.TryTakeSlotAsync(Q, "db", 2, "t3", Lease), "a third slot was given past a limit of 2");
        Ensure(!await b.TryTakeSlotAsync(Q, "db", 2, "t4", Lease), "another worker got a third slot past a limit of 2");
        await a.ReleaseSlotAsync(Q, "db", "t1");
        Ensure(await b.TryTakeSlotAsync(Q, "db", 2, "t5", Lease), "a released slot did not free its place");
        Ensure(await a.TryTakeSlotAsync(Q, "other", 1, "t6", Lease), "a full key blocked another key");
    }

    private static async Task LapsedFreesAsync(QueueControlOpener open, Clock clock)
    {
        var c = open(clock);
        Ensure(await c.TryTakeSlotAsync(Q, "db", 1, "crashed", Lease), "the slot was refused");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        Ensure(await c.TryTakeSlotAsync(Q, "db", 1, "next", Lease), "a lapsed slot still held its place");
    }

    private static async Task RenewKeepsAsync(QueueControlOpener open, Clock clock)
    {
        var c = open(clock);
        Ensure(await c.TryTakeSlotAsync(Q, "db", 1, "held", Lease), "the slot was refused");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(1));
        Ensure(await c.RenewSlotAsync(Q, "db", "held", TimeSpan.FromSeconds(4)), "renewing a live slot failed");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(0.5));
        Ensure(!await c.TryTakeSlotAsync(Q, "db", 1, "other", Lease), "a renewed slot lost its place at its old expiry");
    }

    private static async Task NoRevivalAsync(QueueControlOpener open, Clock clock)
    {
        var a = open(clock);
        var b = open(clock);
        Ensure(await a.TryTakeSlotAsync(Q, "db", 1, "stale", Lease), "the slot was refused");
        await clock.AdvanceAsync(Lease + TimeSpan.FromSeconds(1));
        Ensure(!await a.RenewSlotAsync(Q, "db", "stale", Lease), "a lapsed slot was renewed");
        Ensure(await b.TryTakeSlotAsync(Q, "db", 1, "fresh", Lease), "after a refused renewal the place was still held");
        Ensure(!await a.RenewSlotAsync(Q, "db", "stale", Lease), "a lapsed slot was renewed while another worker held the place");
        Ensure(!await a.TryTakeSlotAsync(Q, "db", 1, "third", Lease), "the limit of 1 was exceeded");
    }

    private static async Task ReleaseAsync(QueueControlOpener open, Clock clock)
    {
        var c = open(clock);
        Ensure(await c.TryTakeSlotAsync(Q, "db", 1, "t", Lease), "the slot was refused");
        await c.ReleaseSlotAsync(Q, "db", "t");
        await c.ReleaseSlotAsync(Q, "db", "t");
        await c.ReleaseSlotAsync(Q, "db", "never-held");
        Ensure(!await c.RenewSlotAsync(Q, "db", "t", Lease), "a released slot was renewed");
    }

    private static async Task RateAsync(QueueControlOpener open, Clock clock)
    {
        var a = open(clock);
        var b = open(clock);
        var rate = new JobRateLimit(2, TimeSpan.FromSeconds(4));
        Ensure(await a.TryTakeStartAsync(Q, "api", rate), "the first start was refused");
        Ensure(await b.TryTakeStartAsync(Q, "api", rate), "the second start was refused");
        Ensure(!await a.TryTakeStartAsync(Q, "api", rate), "a third start fitted a window of 2");
        Ensure(await a.TryTakeStartAsync(Q, "other", rate), "a full window blocked another key");
        await clock.AdvanceAsync(TimeSpan.FromSeconds(5));
        Ensure(await b.TryTakeStartAsync(Q, "api", rate), "the window did not open again after its length");
    }

    private static async Task CircuitContentionAsync(QueueControlOpener open, Clock clock)
    {
        var workers = new[] { open(clock), open(clock), open(clock), open(clock) };
        const int perWorker = 10;
        await Task.WhenAll(workers.Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWorker; i++)
                await w.UpdateCircuitAsync(Q, "db", c => (c ?? new CircuitRecord(0, 0, 0, null, 0)) with { Failures = (c?.Failures ?? 0) + 1 });
        })));
        var final = await workers[0].UpdateCircuitAsync(Q, "db", c => c);
        Ensure(final?.Failures == workers.Length * perWorker, $"concurrent updates were lost: {final?.Failures} of {workers.Length * perWorker} recorded");
    }

    private static async Task CircuitRemovalAsync(QueueControlOpener open, Clock clock)
    {
        var a = open(clock);
        var b = open(clock);
        await a.UpdateCircuitAsync(Q, "db", _ => new CircuitRecord(5, 1000, 2000, "probe", 3000));
        Ensure(await b.UpdateCircuitAsync(Q, "db", _ => null) is null, "removing a circuit did not return null");
        CircuitRecord? seen = new(99, 0, 0, null, 0);
        await a.UpdateCircuitAsync(Q, "db", c => { seen = c; return c; });
        Ensure(seen is null, "a removed circuit was still there");
        Ensure((await a.KeysAsync(Q)).All(k => k.Key != "db" || k.Circuit is null), "a removed circuit was still reported");
        var fresh = await b.UpdateCircuitAsync(Q, "db", c => (c ?? new CircuitRecord(0, 0, 0, null, 0)) with { Failures = (c?.Failures ?? 0) + 1 });
        Ensure(fresh?.Failures == 1, "a recreated circuit carried the removed one's state");
    }

    private static async Task CancellationAsync(QueueControlOpener open, Clock clock, TimeSpan retention)
    {
        var a = open(clock);
        var b = open(clock);
        await a.MarkCancelledAsync(Q, "job-1", "owner asked");
        Ensure(await b.IsCancelledAsync(Q, "job-1"), "another worker did not see the cancellation");
        Ensure(!await b.IsCancelledAsync(Q, "job-2"), "a job never cancelled reads as cancelled");
        Ensure(!await b.IsCancelledAsync("other", "job-1"), "a cancellation crossed queues");
        await clock.AdvanceAsync(retention + TimeSpan.FromSeconds(1));
        Ensure(!await b.IsCancelledAsync(Q, "job-1"), "a cancellation was kept past the retention");
    }

    private static async Task KeysAsync(QueueControlOpener open, Clock clock)
    {
        var c = open(clock);
        await c.TryTakeSlotAsync(Q, "slot-key", 5, "t", Lease);
        await c.UpdateCircuitAsync(Q, "circuit-key", _ => new CircuitRecord(1, 0, 0, null, 0));
        await c.TryTakeStartAsync(Q, "rate-key", new JobRateLimit(5, TimeSpan.FromMinutes(1)));
        var keys = (await c.KeysAsync(Q)).ToDictionary(k => k.Key, StringComparer.Ordinal);
        Ensure(keys.TryGetValue("slot-key", out var slot) && slot.Running == 1, "a held slot was not reported");
        Ensure(keys.TryGetValue("circuit-key", out var circuit) && circuit.Circuit?.Failures == 1, "a circuit was not reported");
        Ensure(keys.TryGetValue("rate-key", out var rate) && rate.Starts == 1, "a recent start was not reported");

        await c.ReleaseSlotAsync(Q, "slot-key", "t");
        await c.UpdateCircuitAsync(Q, "circuit-key", _ => null);
        var left = await c.KeysAsync(Q);
        Ensure(left.All(k => k.Key != "slot-key" && k.Key != "circuit-key"), "a key with nothing left was still reported: " +
            string.Join(", ", left.Select(k => k.Key)));
    }

    private static void Ensure(bool condition, string failure)
    {
        if (!condition) throw new WorkQueueConformanceException(failure);
    }

    private sealed class Clock(bool real) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => real ? System.GetUtcNow() : _now;

        public Task AdvanceAsync(TimeSpan by)
        {
            if (real) return Task.Delay(by);
            _now += by;
            return Task.CompletedTask;
        }
    }
}
