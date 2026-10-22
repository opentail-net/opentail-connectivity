namespace OpenTail.Connectivity.Queues;

/// <summary>
/// Shared state for a queue that cannot keep it itself (RabbitMQ cannot remove a message from the middle of a queue, count what
/// other consumers hold, or remember a resource's failures): cancelled job ids, concurrency slots and circuits. Every worker of a
/// queue must share one store, or a cancel, a limit or an open circuit reaches only some of them.
/// </summary>
public interface IQueueControl
{
    Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default);

    Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default);

    /// <summary>Takes a slot for <paramref name="key"/> when fewer than <paramref name="limit"/> unexpired slots are held. The slot
    /// is named by the lease token and lapses at <paramref name="until"/> unless renewed, so a crashed worker cannot hold it for good.</summary>
    Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, DateTimeOffset until, CancellationToken ct = default);

    Task RenewSlotAsync(string queue, string key, string token, DateTimeOffset until, CancellationToken ct = default);

    Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default);

    /// <summary>Applies <paramref name="change"/> to the key's circuit atomically (null removes it) and returns the new value.</summary>
    Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default);

    /// <summary>Keys with unexpired slots or a circuit: how many slots each holds, and its circuit.</summary>
    Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit)>> KeysAsync(string queue, CancellationToken ct = default);
}

/// <summary>Queue control in process memory: for one process, or tests.</summary>
public sealed class InMemoryQueueControl(TimeProvider? time = null) : IQueueControl
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly HashSet<(string Queue, string Id)> _cancelled = [];
    private readonly Dictionary<(string Queue, string Key), Dictionary<string, DateTimeOffset>> _slots = [];
    private readonly Dictionary<(string Queue, string Key), CircuitRecord> _circuits = [];

    public Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        lock (_gate) _cancelled.Add((queue, id));
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_cancelled.Contains((queue, id)));
    }

    public Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, DateTimeOffset until, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var slots = Live(queue, key);
            if (slots.Count >= limit) return Task.FromResult(false);
            slots[token] = until;
            return Task.FromResult(true);
        }
    }

    public Task RenewSlotAsync(string queue, string key, string token, DateTimeOffset until, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var slots = Live(queue, key);
            if (slots.ContainsKey(token)) slots[token] = until;
        }

        return Task.CompletedTask;
    }

    public Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default)
    {
        lock (_gate) Live(queue, key).Remove(token);
        return Task.CompletedTask;
    }

    public Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var next = change(_circuits.GetValueOrDefault((queue, key)));
            if (next is null) _circuits.Remove((queue, key));
            else _circuits[(queue, key)] = next;
            return Task.FromResult(next);
        }
    }

    public Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit)>> KeysAsync(string queue, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var keys = _slots.Keys.Where(k => k.Queue == queue).Select(k => k.Key)
                .Concat(_circuits.Keys.Where(k => k.Queue == queue).Select(k => k.Key))
                .Distinct(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<(string, int, CircuitRecord?)>>(
                keys.Select(k => (k, Live(queue, k).Count, _circuits.GetValueOrDefault((queue, k)))).Where(k => k.Item2 > 0 || k.Item3 is not null).ToList());
        }
    }

    private Dictionary<string, DateTimeOffset> Live(string queue, string key)
    {
        if (!_slots.TryGetValue((queue, key), out var slots))
            _slots[(queue, key)] = slots = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var now = _time.GetUtcNow();
        foreach (var expired in slots.Where(s => s.Value <= now).Select(s => s.Key).ToList())
            slots.Remove(expired);
        return slots;
    }
}
