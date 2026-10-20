namespace OpenTail.Connectivity.Files.Contracts;

public interface IPendingSendStore
{
    Task SaveAsync(PendingSendRecord record, CancellationToken ct = default);
    Task<PendingSendRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PendingSendRecord>> GetPendingAsync(CancellationToken ct = default);
    Task UpdateStatusAsync(string id, string status, string? note = null, CancellationToken ct = default);

    /// <summary>
    /// Moves the record from <paramref name="from"/> to <paramref name="to"/> only if it is in <paramref name="from"/> now, and
    /// saves that before returning. False when it is in another state or missing. Exactly one of several concurrent calls for the
    /// same transition succeeds. A failed save throws and leaves the record as it was.
    /// </summary>
    Task<bool> TryTransitionAsync(string id, string from, string to, string? note = null, CancellationToken ct = default);
}
