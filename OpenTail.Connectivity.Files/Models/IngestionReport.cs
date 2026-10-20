namespace OpenTail.Connectivity.Files.Models;

/// <param name="SyncScope">Set for incremental runs: the connector's scope.</param>
/// <param name="FullRun">An incremental run that started without a cursor, or was reset by the source.</param>
/// <param name="SyncCommitted">An incremental run saved at least one checkpoint.</param>
public sealed record IngestionReport(
    string RunId,
    string TenantContext,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int ItemsDiscovered,
    int ItemsProcessed,
    int ItemsSkipped,
    int ItemsFailed,
    int DocumentsProduced,
    IReadOnlyList<ValidationIssue> ValidationIssues,
    IReadOnlyList<string> Errors,
    int MinBodyLength = 0,
    int MaxBodyLength = 0,
    double AvgBodyLength = 0,
    string? SyncScope = null,
    bool FullRun = false,
    bool SyncCommitted = false,
    int ItemsDeleted = 0,
    IReadOnlyList<string>? SeenSourceRefs = null,
    bool SourceRefsTruncated = false,
    int ItemsUnchanged = 0);
