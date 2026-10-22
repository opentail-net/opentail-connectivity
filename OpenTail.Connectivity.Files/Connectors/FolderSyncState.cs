namespace OpenTail.Connectivity.Files.Connectors;

/// <param name="FileSizes">Size per path, compared with the timestamp so a rewrite that keeps the old timestamp is still seen
/// when the size changes. Missing in state saved by older versions.</param>
/// <param name="ScanFingerprint">The rules that decided which files were in scope (filter, cutoff, depth). When they change,
/// every file in scope is offered again. Missing in state saved by older versions, which gives one full scan.</param>
public sealed record FolderSyncState(
    string FolderPath,
    Dictionary<string, DateTimeOffset> FileTimestamps,
    DateTimeOffset UpdatedAt,
    Dictionary<string, long>? FileSizes = null,
    string? ScanFingerprint = null);
