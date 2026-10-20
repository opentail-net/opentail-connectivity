// Moved from opentail's OpenTail.Core.Memory (MemoryMaintenanceContracts.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public enum MemoryMaintenanceActionKind
{
    None = 0,
    Deduplicate,
    Summarize,
    CollapseCluster,
    RefreshEmbedding,
    RepairOrphan,
    RepairLink,
    RetentionArchive,
    RetentionPrune,
    PinHotMemory,
    WeeklyDecisionDigest,
    ProjectBrief,
    TeamInteractionSummary,
    UnresolvedTopicView,
    IncrementalReindex,
    SemanticMerge
}

public sealed record MemoryMaintenanceRequest(
    IReadOnlyList<MemoryRecord> Records,
    IReadOnlyList<MemoryLink> Links,
    IReadOnlyList<MemoryEntity>? Entities = null,
    DateTimeOffset? AsOfUtc = null,
    string? Workspace = null,
    string? ProjectScope = null,
    string? CurrentEmbeddingVersion = null,
    string? ReplayToken = null,
    bool CrashSafeReplay = false,
    int MaxActions = 200);

public sealed record MemoryMaintenanceAction(
    MemoryMaintenanceActionKind Kind,
    string TargetId,
    string Reason,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryMaintenanceReport(
    DateTimeOffset ExecutedAtUtc,
    IReadOnlyList<MemoryMaintenanceAction> Actions,
    IReadOnlyDictionary<string, int> ActionCounts,
    IReadOnlyList<string> Notes,
    string? ReplayToken = null,
    bool IsIdempotentSafe = true);

public interface IMemoryMaintenanceService
{
    Task<MemoryMaintenanceReport> ExecuteAsync(MemoryMaintenanceRequest request, CancellationToken ct = default);
}
