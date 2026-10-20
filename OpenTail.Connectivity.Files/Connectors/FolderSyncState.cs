namespace OpenTail.Connectivity.Files.Connectors;

public sealed record FolderSyncState(
    string FolderPath,
    Dictionary<string, DateTimeOffset> FileTimestamps,
    DateTimeOffset UpdatedAt);
