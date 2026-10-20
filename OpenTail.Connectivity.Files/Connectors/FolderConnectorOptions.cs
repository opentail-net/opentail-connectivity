namespace OpenTail.Connectivity.Files.Connectors;

public sealed record FolderConnectorOptions(
    IReadOnlyList<string>? IncludePatterns = null,
    IReadOnlyList<string>? ExcludePatterns = null,
    long? MaxFileSizeBytes = null,
    bool IncludeNoisyDirectories = false,
    DateTimeOffset? ModifiedSince = null,
    IReadOnlyList<string>? ExtraExtensions = null,
    int? MaxDepth = null,
    IReadOnlyList<string>? SupportedExtensions = null,
    IReadOnlyList<string>? NoisyDirectories = null,
    int MaxPendingDirectories = 10_000);
