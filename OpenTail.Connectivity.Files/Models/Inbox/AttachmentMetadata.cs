namespace OpenTail.Connectivity.Files.Models;

public sealed record AttachmentMetadata(
    string Name,
    string? AttachmentId,
    string ContentType,
    long SizeBytes);
