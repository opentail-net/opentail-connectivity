namespace OpenTail.Connectivity.Files.Models;

public sealed record PendingSendRecord(
    string Id,
    OutboundEmail Email,
    DateTimeOffset QueuedAt,
    string Status = "pending",
    string? Note = null,
    DateTimeOffset? ProcessedAt = null);
