// Moved from opentail's OpenTail.Core.Memory (MemoryIngestionContracts.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public enum MemoryIngestionSourceType
{
    Unknown = 0,
    Slack,
    GitHub,
    LocalDocs,
    WorkflowRun,
    CrewHandoff,
    Diagnostic,
    ToolExecution,
    UserNote,
}

public enum MemoryIngestionEventKind
{
    Unknown = 0,
    Message,
    Comment,
    Issue,
    PullRequest,
    Review,
    Commit,
    WorkflowEvent,
    DiagnosticEvent,
    ToolEvent,
    Document,
}

public enum MemoryIngestionStage
{
    Capture = 0,
    Normalize,
    Extract,
    Chunk,
    Embed,
    Store,
    Link,
    Audit,
}

public sealed record MemoryRawCaptureEnvelope(
    string EnvelopeId,
    MemoryIngestionSourceType SourceType,
    string SourceSystem,
    MemoryIngestionEventKind EventKind,
    string ExternalId,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ObservedAtUtc,
    string RawPayload,
    string? Title = null,
    string? Status = null,
    string? PayloadFormat = null,
    string? Workspace = null,
    string? WorkspaceId = null,
    string? ProjectScope = null,
    string? RepositoryScope = null,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    int? IssueNumber = null,
    int? PullRequestNumber = null,
    string? CommentId = null,
    string? ReviewCommentId = null,
    string? CommitSha = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Assignees = null,
    IReadOnlyList<string>? LinkedExternalIds = null,
    string? ConversationId = null,
    string? ThreadId = null,
    string? ThreadRootExternalId = null,
    string? Actor = null,
    string? SenderId = null,
    string? ChannelId = null,
    string? ChannelName = null,
    string? ReplyToExternalId = null,
    DateTimeOffset? EditedAtUtc = null,
    string? SourceUrl = null,
    string? Permalink = null,
    string? ContentHash = null,
    string? DedupeKey = null,
    IReadOnlyList<string>? Mentions = null,
    IReadOnlyList<string>? AttachmentUrls = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryNormalizedEnvelope(
    string NormalizedId,
    MemoryIngestionSourceType SourceType,
    string SourceSystem,
    MemoryIngestionEventKind EventKind,
    string ExternalId,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset ObservedAtUtc,
    string Content,
    string? Title = null,
    string? Status = null,
    string? Summary = null,
    string? Workspace = null,
    string? WorkspaceId = null,
    string? ProjectScope = null,
    string? RepositoryScope = null,
    string? RepositoryOwner = null,
    string? RepositoryName = null,
    int? IssueNumber = null,
    int? PullRequestNumber = null,
    string? CommentId = null,
    string? ReviewCommentId = null,
    string? CommitSha = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Assignees = null,
    IReadOnlyList<string>? LinkedExternalIds = null,
    string? ConversationId = null,
    string? ThreadId = null,
    string? ThreadRootExternalId = null,
    string? Actor = null,
    string? SenderId = null,
    string? ChannelId = null,
    string? ChannelName = null,
    string? ReplyToExternalId = null,
    DateTimeOffset? EditedAtUtc = null,
    string? SourceUrl = null,
    string? Permalink = null,
    string? ContentHash = null,
    string? DedupeKey = null,
    IReadOnlyList<string>? Mentions = null,
    IReadOnlyList<string>? AttachmentUrls = null,
    MemoryPolicy? Policy = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record MemoryIngestionAuditEvent(
    string AuditId,
    string EnvelopeId,
    MemoryIngestionStage Stage,
    DateTimeOffset TimestampUtc,
    bool Success,
    string? Message = null,
    IReadOnlyDictionary<string, string>? Diagnostics = null);

public interface IMemoryIngestionNormalizer
{
    Task<MemoryNormalizedEnvelope> NormalizeAsync(
        MemoryRawCaptureEnvelope capture,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionPipeline
{
    Task<MemoryIngestionResult> IngestAsync(
        MemoryRawCaptureEnvelope capture,
        CancellationToken cancellationToken = default);
}

public sealed record MemoryIngestionResult(
    bool Success,
    string? RecordId = null,
    string? ChunkGroupId = null,
    string? DedupeKey = null,
    bool IsDuplicate = false,
    bool IsReplay = false,
    bool WasQueued = false,
    string? ReplayToken = null,
    int? BatchSize = null,
    IReadOnlyList<MemoryIngestionAuditEvent>? AuditTrail = null,
    string? Error = null);

public sealed record MemoryIngestionQueueOptions(
    int Capacity = 128,
    int MaxBatchSize = 32,
    TimeSpan? DequeueInterval = null,
    bool DropOldestOnOverflow = false);
