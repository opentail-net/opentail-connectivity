// Moved from opentail's OpenTail.Core.Memory (MemoryOperatorControls.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public enum MemoryOperatorActionKind
{
    Pin = 0,
    Unpin,
    Forget,
    Archive,
    ReEmbed,
    MergeDuplicates,
    RelabelEntity,
    MarkStale,
    MarkCanonical,
    BlockSource,
    UnblockSource,
    UpdateRetention,
}

public sealed record MemoryOperatorActionRequest(
    MemoryOperatorActionKind Kind,
    string Actor,
    string? RecordId = null,
    string? SecondaryRecordId = null,
    string? EntityId = null,
    string? SourceSystem = null,
    string? Label = null,
    MemoryRetentionClass? RetentionClass = null,
    string? Note = null,
    string? EmbeddingRevision = null,
    DateTimeOffset? TimestampUtc = null);

public sealed record MemoryOperatorAuditEntry(
    string AuditId,
    DateTimeOffset OccurredAtUtc,
    string Actor,
    MemoryOperatorActionKind Action,
    bool Success,
    string Message,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryOperatorControlSnapshot(
    IReadOnlyDictionary<string, MemoryRecord> Records,
    IReadOnlyDictionary<string, MemoryEntity> Entities,
    IReadOnlySet<string> BlockedSources,
    IReadOnlySet<string> PinnedRecordIds,
    IReadOnlyList<MemoryOperatorAuditEntry> AuditTrail);

public sealed record MemoryOperatorActionResult(
    bool Success,
    string Message,
    MemoryOperatorControlSnapshot Snapshot,
    MemoryOperatorAuditEntry Audit);

public interface IMemoryOperatorControlService
{
    MemoryOperatorControlSnapshot Snapshot();

    MemoryOperatorActionResult Apply(MemoryOperatorActionRequest request);
}
