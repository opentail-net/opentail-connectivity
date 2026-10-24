namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// Fetches one attachment's bytes. The reference comes from the same provider's inbox adapter
/// (<see cref="InboxEmailRecord.ProviderMessageId"/>, <see cref="AttachmentMetadata.AttachmentId"/>). Never returns null and never
/// throws for a missing attachment: every outcome is an <see cref="AttachmentFetchStatus"/>. Throws only for cancellation and for
/// failures to reach the provider at all (network, authentication).
/// </summary>
public interface IAttachmentFetcher
{
    Task<AttachmentFetchResult> FetchAsync(AttachmentRef reference, AttachmentFetchLimits limits, CancellationToken ct = default);
}
