namespace OpenTail.Connectivity.Files.Models;

public sealed record OutboundEmail(
    IReadOnlyList<string> To,
    string Subject,
    string Body,
    string BodyFormat = "html",
    IReadOnlyList<string>? Cc = null,
    string? InReplyToMessageId = null,
    string? ThreadId = null,
    IReadOnlyList<AttachmentContent>? Attachments = null);
