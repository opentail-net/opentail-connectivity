// Moved from opentail's OpenTail.Core.Memory (MemoryContracts.cs, retrieval types); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public enum MemoryRetrievalDisposition
{
    Allowed = 0,
    Redacted,
    Summarized,
    Denied,
}

public enum MemoryRetrievalMode
{
    Auto = 0,
    Semantic,
    Keyword,
    Hybrid,
    Graph,
    Timeline,
    EntityCentric,
}

public sealed record MemoryRetrievalRequest(
    string Query,
    int TopK = 5,
    string? Workspace = null,
    string? ProjectScope = null,
    string? RepositoryScope = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    IReadOnlyList<MemorySourceType>? PreferredSources = null,
    IReadOnlyList<string>? EntityHints = null,
    MemoryRetrievalMode RetrievalMode = MemoryRetrievalMode.Auto,
    string? PermissionScope = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<MemoryRecordKind>? RecordKinds = null,
    IReadOnlyList<MemoryChunkKind>? ChunkKinds = null,
    MemorySensitivity? MaxSensitivity = null,
    MemoryTrustLevel? MinTrustLevel = null,
    bool IncludeSuperseded = false,
    bool IncludeDerived = true,
    [property: Obsolete("Deleted records are never recalled.")] bool IncludeDeleted = false,
    bool IncludeRawEvidence = true,
    bool IncludeSummaries = true,
    bool EnableCondenseQuestion = false,
    string? ChatHistory = null);

public sealed record MemoryRetrievalEvidence(
    string ChunkId,
    MemoryChunkKind Kind,
    string Content,
    string? Summary = null,
    string? SourceUrl = null,
    string? Citation = null,
    MemoryRetrievalDisposition Disposition = MemoryRetrievalDisposition.Allowed);

public sealed record MemoryRetrievalHit(
    MemoryRecord Record,
    double Score,
    string Reason,
    MemoryRetrievalDisposition Disposition = MemoryRetrievalDisposition.Allowed,
    IReadOnlyList<MemoryRetrievalEvidence>? Evidence = null,
    IReadOnlyDictionary<string, double>? ScoreBreakdown = null,
    IReadOnlyList<string>? RelatedRecordIds = null,
    IReadOnlyList<string>? RelatedEntityIds = null);

public sealed record MemoryRetrievalResponse(
    string Query,
    DateTimeOffset RetrievedAtUtc,
    IReadOnlyList<MemoryRetrievalHit> Hits,
    MemoryRetrievalMode EffectiveMode = MemoryRetrievalMode.Auto,
    string? RequestId = null,
    IReadOnlyList<string>? Notes = null);
