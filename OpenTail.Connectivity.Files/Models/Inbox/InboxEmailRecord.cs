namespace OpenTail.Connectivity.Files.Models;

public sealed record InboxEmailRecord(
    string MessageId,
    string Subject,
    string From,
    string To,
    string Cc,
    DateTimeOffset Date,
    string Body,
    string? BodyHtml,
    IReadOnlyList<string> AttachmentNames,
    string Folder,
    string TenantContext,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyList<AttachmentMetadata>? Attachments = null,
    string? ThreadId = null,
    string? ProviderMessageId = null)
{
    // MessageId is the RFC 5322 Message-ID (or a stable fallback): shown to agents, matched on, used for replies.
    // ProviderMessageId is the provider's own id (Gmail and Graph API ids, the IMAP stable id): opaque, only passed back to that
    // provider's fetchers, together with AttachmentMetadata.AttachmentId.
}
