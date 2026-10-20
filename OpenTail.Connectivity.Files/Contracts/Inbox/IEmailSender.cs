namespace OpenTail.Connectivity.Files.Contracts;

public interface IEmailSender
{
    /// <summary>Returns the provider-native message ID of the sent message.</summary>
    Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default);

    Task DeleteAsync(string messageId, CancellationToken ct = default);
    Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default);
    Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default);
}
