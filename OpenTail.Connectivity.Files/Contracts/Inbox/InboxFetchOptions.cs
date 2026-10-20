namespace OpenTail.Connectivity.Files.Contracts;

public sealed record InboxFetchOptions(
    string Folder = "INBOX",
    int MaxMessages = 100,
    DateTimeOffset? Since = null,
    string? SyncToken = null);
