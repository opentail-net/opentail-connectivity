namespace OpenTail.Connectivity.Files.Models;

/// <summary>
/// Which attachment to fetch: the provider's own message id (<see cref="InboxEmailRecord.ProviderMessageId"/>) and the
/// attachment reference from <see cref="AttachmentMetadata.AttachmentId"/>. Both are opaque and only ever passed back to the
/// fetcher of the provider that produced them.
/// </summary>
public sealed record AttachmentRef(string ProviderMessageId, string AttachmentId);

/// <summary>Limits a fetch must keep: <see cref="MaxBytes"/> of decoded content, checked before and during the download.</summary>
public sealed record AttachmentFetchLimits(long MaxBytes)
{
    public long MaxBytes { get; } = MaxBytes > 0 ? MaxBytes : throw new ArgumentOutOfRangeException(nameof(MaxBytes), "must be > 0");
}

public enum AttachmentFetchStatus
{
    /// <summary>The bytes are in <see cref="AttachmentFetchResult.Content"/>.</summary>
    Fetched,
    /// <summary>The message or the attachment is not there (deleted, moved, or a reference from another mailbox or folder).</summary>
    Missing,
    /// <summary>The attachment is not a file: a link to a cloud file, a contact or an event.</summary>
    NotAFile,
    /// <summary>Larger than <see cref="AttachmentFetchLimits.MaxBytes"/>, declared or counted while downloading.</summary>
    TooLarge,
    /// <summary>The provider's answer could not be used (bad encoding, no content).</summary>
    Failed
}

/// <summary>
/// The outcome of a fetch. <see cref="Content"/> is set only for <see cref="AttachmentFetchStatus.Fetched"/>.
/// <see cref="Reason"/> is short and safe to show: never content, never credentials.
/// </summary>
public sealed record AttachmentFetchResult(AttachmentFetchStatus Status, AttachmentContent? Content, string? Reason)
{
    public static AttachmentFetchResult Fetched(AttachmentContent content) => new(AttachmentFetchStatus.Fetched, content, null);
    public static AttachmentFetchResult Missing(string reason) => new(AttachmentFetchStatus.Missing, null, reason);
    public static AttachmentFetchResult NotAFile(string reason) => new(AttachmentFetchStatus.NotAFile, null, reason);
    public static AttachmentFetchResult TooLarge(long maxBytes) =>
        new(AttachmentFetchStatus.TooLarge, null, $"The attachment is larger than the limit of {maxBytes} bytes.");
    public static AttachmentFetchResult Failed(string reason) => new(AttachmentFetchStatus.Failed, null, reason);
}
