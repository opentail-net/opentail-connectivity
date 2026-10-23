namespace OpenTail.Connectivity.Queues;

/// <summary>
/// Shared state for a queue that cannot keep it itself (RabbitMQ cannot remove a message from the middle of a queue, count what
/// other consumers hold, or remember a resource's failures): cancelled job ids, concurrency slots and circuits. Every worker of a
/// queue must share one store, or a cancel, a limit or an open circuit reaches only some of them.
/// </summary>
/// <remarks>
/// <para><b>Time.</b> Slots and rate windows are measured on <b>the store's clock</b> (callers pass durations, not times), so workers
/// on machines whose clocks disagree still agree on when a slot lapses. Circuits carry times the caller computed
/// (<see cref="CircuitRecord.OpenUntilMs"/>); skew between workers only shifts a cooldown by that much.</para>
/// <para>The contract is checked by <see cref="QueueControlConformance"/>.</para>
/// </remarks>
public interface IQueueControl
{
    /// <summary>Marks a job cancelled. The mark is kept for the store's retention, then forgotten (by then the job is past its
    /// attempts or age anyway).</summary>
    Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default);

    Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default);

    /// <summary>Takes a slot for <paramref name="key"/> when fewer than <paramref name="limit"/> live slots are held. The slot is
    /// named by the lease token and lapses <paramref name="lease"/> from now (the store's clock) unless renewed, so a crashed worker
    /// cannot hold it for good.</summary>
    Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Extends a live slot to <paramref name="lease"/> from now. False when the slot has lapsed or was released: a lapsed
    /// slot is never revived, because another worker may already hold its place. The caller has then lost its lease.</summary>
    Task<bool> RenewSlotAsync(string queue, string key, string token, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Frees the slot. Releasing one already gone does nothing.</summary>
    Task ReleaseSlotAsync(string queue, string key, string token, CancellationToken ct = default);

    /// <summary>Applies <paramref name="change"/> to the key's circuit atomically (null removes it) and returns the new value. A
    /// store may call <paramref name="change"/> more than once (a retry after a concurrent write), so it must not have side effects.</summary>
    Task<CircuitRecord?> UpdateCircuitAsync(string queue, string key, Func<CircuitRecord?, CircuitRecord?> change, CancellationToken ct = default);

    /// <summary>Records a start for <paramref name="key"/> when fewer than <see cref="JobRateLimit.Count"/> started within the last
    /// <see cref="JobRateLimit.Per"/> (a sliding window on the store's clock); false, recording nothing, when the window is full.</summary>
    Task<bool> TryTakeStartAsync(string queue, string key, JobRateLimit rate, CancellationToken ct = default);

    /// <summary>Keys with unexpired slots, a circuit or recent starts: how many slots each holds, its circuit, and its starts within
    /// the last hour (rate windows longer than that are counted up to an hour).</summary>
    Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit, int Starts)>> KeysAsync(string queue, CancellationToken ct = default);
}

/// <summary>Queue control in process memory: for one process, or tests.</summary>
/// <param name="retention">How long cancellations are kept. Default 30 days, as <c>SqliteQueueControl</c>.</param>
public sealed class InMemoryQueueControl(TimeProvider? time = null, TimeSpan? retention = null) : IQueueControl
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _retention = retention ?? TimeSpan.FromDays(30);
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Queue, string Id), DateTimeOffset> _cancelled = [];
    private readonly Dictionary<(string Queue, string Key), Dictionary<string, DateTimeOffset>> _slots = [];
    private readonly Dictionary<(string Queue, string Key), CircuitRecord> _circuits = [];
    private readonly Dictionary<(string Queue, string Key), List<DateTimeOffset>> _starts = [];

    public Task MarkCancelledAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ForgetOldCancellations();
            _cancelled[(queue, id)] = _time.GetUtcNow();
        }
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ForgetOldCancellations();
            return Task.FromResult(_cancelled.ContainsKey((queue, id)));
        }
    }

    private void ForgetOldCancellations()
    {
        var cut = _time.GetUtcNow() - _retention;
        foreach (var old in _cancelled.Where(c => c.Value <= cut).Select(c => c.Key).ToList())
            _cancelled.Remove(old);
    }

    public Task<bool> TryTakeSlotAsync(string queue, string key, int limit, string token, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var slots = Live(queue, key);
            if (slots.Count >= limit) return Task.FromResult(false);
            slots[token] = _time.GetUtcNow() + lease;
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewSlotAsync(string queue, string key, string token, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var slots = Live(queue, key); // drops lapsed slots first, so a lapsed one is never revived
            if (!slots.ContainsKey(token)) return Task.FromResult(false);
            slots[token] = _time.GetUtcNow() + lease;
            return Task.FromResult(true);
        }
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

    public Task<bool> TryTakeStartAsync(string queue, string key, JobRateLimit rate, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_starts.TryGetValue((queue, key), out var starts))
                _starts[(queue, key)] = starts = [];
            starts.RemoveAll(t => t <= now - rate.Per);
            if (starts.Count >= rate.Count) return Task.FromResult(false);
            starts.Add(now);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<(string Key, int Running, CircuitRecord? Circuit, int Starts)>> KeysAsync(string queue, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var hourAgo = _time.GetUtcNow().AddHours(-1);
            var keys = _slots.Keys.Where(k => k.Queue == queue).Select(k => k.Key)
                .Concat(_circuits.Keys.Where(k => k.Queue == queue).Select(k => k.Key))
                .Concat(_starts.Keys.Where(k => k.Queue == queue).Select(k => k.Key))
                .Distinct(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<(string, int, CircuitRecord?, int)>>(keys
                .Select(k => (k, Live(queue, k).Count, _circuits.GetValueOrDefault((queue, k)), _starts.GetValueOrDefault((queue, k))?.Count(t => t > hourAgo) ?? 0))
                .Where(k => k.Item2 > 0 || k.Item3 is not null || k.Item4 > 0).ToList());
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
