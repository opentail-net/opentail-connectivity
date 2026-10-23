using OpenTail.Connectivity.Queues;
using OpenTail.Connectivity.Queues.Sqlite;

namespace OpenTail.Connectivity.Tests.Queues;

/// <summary>Every queue-control store in this repository keeps the <see cref="IQueueControl"/> contract.</summary>
public sealed class QueueControlConformanceTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);

    private static readonly Dictionary<string, Func<QueueControlOpener>> Storages = new(StringComparer.Ordinal)
    {
        ["in-memory"] = static () =>
        {
            InMemoryQueueControl? shared = null; // a second open is a second worker on the same store
            return time => shared ??= new InMemoryQueueControl(time, Retention);
        },
        ["sqlite"] = static () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"ot-control-{Guid.NewGuid():N}.db");
            return time => new SqliteQueueControl(path, Retention, time);
        },
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var store in Storages.Keys)
            foreach (var check in QueueControlConformance.Checks(Storages[store], Retention))
                data.Add(store, check.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Store_KeepsTheContract(string store, string check)
        => await QueueControlConformance.Checks(Storages[store], Retention).Single(x => x.Name == check).RunAsync();

    [Fact]
    public async Task The_suite_catches_a_store_that_revives_lapsed_slots()
    {
        var check = QueueControlConformance.Checks(() =>
        {
            Revives? shared = null;
            return time => shared ??= new Revives(new InMemoryQueueControl(time, Retention));
        }, Retention).Single(x => x.Name == "a lapsed slot cannot be renewed");
        await Assert.ThrowsAsync<WorkQueueConformanceException>(check.RunAsync);
    }

    /// <summary>The bug SqliteQueueControl had: renewing writes the new expiry whether or not the slot had lapsed.</summary>
    private sealed class Revives(InMemoryQueueControl inner) : IQueueControl
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        public Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default) => inner.MarkCancelledAsync(queue, id, reason, ct);
        public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default) => inner.IsCancelledAsync(queue, id, ct);
        public async Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, TimeSpan lease, CancellationToken ct = default)
        {
            var ok = await inner.TryTakeSlotAsync(queue, key, limit, token, lease, ct);
            if (ok) _taken.Add(token);
            return ok;
        }
        public async Task<bool> RenewSlotAsync(string queue, string key, string token, TimeSpan lease, CancellationToken ct = default)
            => await inner.RenewSlotAsync(queue, key, token, lease, ct) || _taken.Contains(token) && await inner.TryTakeSlotAsync(queue, key, int.MaxValue, token, lease, ct);
        public Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default)
        {
            _taken.Remove(token);
            return inner.ReleaseSlotAsync(queue, key, token, ct);
        }
        public Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default)
            => inner.UpdateCircuitAsync(queue, key, change, ct);
        public Task<bool> TryTakeStartAsync(string queue, string key, JobRateLimit rate, CancellationToken ct = default) => inner.TryTakeStartAsync(queue, key, rate, ct);
        public Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit, int Starts)>> KeysAsync(string queue, CancellationToken ct = default)
            => inner.KeysAsync(queue, ct);
    }
}
