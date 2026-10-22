namespace OpenTail.Connectivity.Queues;

/// <summary>
/// Remembers which jobs were cancelled, for queues that cannot remove a message from the middle (RabbitMQ): workers check it
/// before running a job and before settling one. Every worker of a queue must share one store, or a cancel reaches only some.
/// </summary>
public interface ICancelledJobs
{
    Task MarkAsync(string queue, string id, string reason, CancellationToken ct = default);

    Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default);
}

/// <summary>Cancelled jobs in process memory: for one process, or tests.</summary>
public sealed class InMemoryCancelledJobs : ICancelledJobs
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Queue, string Id), string> _cancelled = new();

    public Task MarkAsync(string queue, string id, string reason, CancellationToken ct = default)
    {
        _cancelled[(queue, id)] = reason;
        return Task.CompletedTask;
    }

    public Task<bool> IsCancelledAsync(string queue, string id, CancellationToken ct = default)
        => Task.FromResult(_cancelled.ContainsKey((queue, id)));
}
