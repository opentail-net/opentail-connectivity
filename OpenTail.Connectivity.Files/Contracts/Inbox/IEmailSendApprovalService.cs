namespace OpenTail.Connectivity.Files.Contracts;

public interface IEmailSendApprovalService
{
    Task<PendingSendRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<PendingSendRecord>> GetPendingAsync(CancellationToken ct = default);
    Task<string> ApproveAsync(string id, CancellationToken ct = default);
    Task RejectAsync(string id, string? reason = null, CancellationToken ct = default);

    /// <summary>
    /// Puts a <see cref="PendingSendStatus.Unknown"/> or <see cref="PendingSendStatus.Failed"/> send back to pending, after a
    /// person has checked that it was not delivered.
    /// </summary>
    Task RequeueAsync(string id, CancellationToken ct = default);
}
