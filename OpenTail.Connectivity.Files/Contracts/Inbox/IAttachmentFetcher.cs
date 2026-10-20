namespace OpenTail.Connectivity.Files.Contracts;

public interface IAttachmentFetcher
{
    Task<AttachmentContent?> FetchAsync(string messageId, string attachmentId, CancellationToken ct = default);
}
