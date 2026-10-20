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
    string? ThreadId = null);
