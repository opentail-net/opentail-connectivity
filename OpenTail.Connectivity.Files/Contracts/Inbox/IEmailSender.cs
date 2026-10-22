namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>Sends mail. Changing existing messages is <see cref="IMailboxManager"/>, so a consumer that only sends gets only this.</summary>
public interface IEmailSender
{
    /// <summary>Returns the provider-native message ID of the sent message.</summary>
    Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default);
}

/// <summary>Changes messages already in the mailbox. The provider senders implement both interfaces.</summary>
public interface IMailboxManager
{
    Task DeleteAsync(string messageId, CancellationToken ct = default);
    Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default);
    Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default);
}
