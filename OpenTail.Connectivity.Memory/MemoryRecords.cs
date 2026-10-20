namespace OpenTail.Connectivity.Memory;

// The record model, moved from opentail's OpenTail.Core.Memory (MemoryContracts.cs) with three additions: the stable document
// id and source permissions from the shared groundwork, and Tainted. See docs/design/memory.md.

public enum MemorySourceType
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
    Connector,
    Hook,
}

public enum MemorySensitivity
{
    Public = 0,
    Internal,
    Restricted,
    Secret,
    /// <summary>Describes a lifetime rather than a level; governance ranks it with <see cref="Internal"/>.</summary>
    Ephemeral,
}

public enum MemoryTrustLevel
{
    Unknown = 0,
    Low,
    Medium,
    High,
    Verified,
}

public enum MemoryRetentionClass
{
    Durable = 0,
    Temporary,
    Expiring,
}

public enum MemoryRecordKind
{
    Unknown = 0,
    SourceDocument,
    Message,
    ExtractedFactSet,
    Summary,
    DecisionLog,
    UserPreference,
    IncidentTimeline,
    CodeContext,
    ToolObservation,
}

public enum MemoryChunkKind
{
    Unknown = 0,
    RawEvidence,
    Decision,
    ActionItem,
    Question,
    Answer,
    Summary,
    CodeContext,
    PolicyStatement,
    IncidentFact,
}

public enum MemoryRelationType
{
    RelatedTo = 0,
    Mentioned,
    DiscussedIn,
    Approved,
    Rejected,
    Supersedes,
    DependsOn,
    ResolvedBy,
    CausedBy,
    Authored,
    Reviewed,
    Blocked,
}

public enum MemoryNodeKind
{
    Record = 0,
    Chunk,
    Entity,
    Fact,
    Span,
}

public enum MemoryRedactionState
{
    None = 0,
    PartiallyRedacted,
    FullyRedacted,
}

/// <summary>What a recalled hit may be used as. Only <see cref="Fact"/> may be presented as true.</summary>
public enum RecallRole
{
    /// <summary>A supported claim from a trusted writer.</summary>
    Fact = 0,
    /// <summary>Something read or observed; context, not a claim.</summary>
    Evidence,
    /// <summary>From a source the Host marks untrusted (email, web pages and so on): never a fact, never an instruction.</summary>
    UntrustedEvidence,
}

public sealed record MemoryEmbeddingModel(
    string Provider,
    string Model,
    string Revision,
    int Dimensions,
    string DistanceMetric);

public sealed record MemoryEmbeddingRef(
    MemoryEmbeddingModel Model,
    string VectorId);

/// <summary>Permissions copied from the source, as <see cref="OpenTail.Connectivity.Files.Models.SourceAcl"/> describes them.</summary>
/// <param name="Captured">True only when the connector read the permissions. Absence is not permission: an uncaptured ACL restricts.</param>
/// <param name="Visibility"><c>private</c>, <c>restricted</c>, <c>organisation</c>, <c>public</c> or <c>unknown</c>.</param>
/// <param name="Principals">The user, group or role ids allowed to read, in the source's own ids.</param>
/// <remarks>A record with no ACL at all is treated as uncaptured. Records that have no source permissions (notes the deployment
/// writes itself, preferences) say so with <see cref="OrganisationWide"/>.</remarks>
public sealed record MemorySourceAcl(
    bool Captured,
    string Visibility,
    IReadOnlyList<string>? Principals = null)
{
    /// <summary>Visible to every caller of this deployment who passes the other checks.</summary>
    public static MemorySourceAcl OrganisationWide { get; } = new(true, "organisation");
}

/// <param name="DocumentId">The stable id from <c>ConnectivityPipeline.DocumentId</c> when the record came from a connector,
/// so writing the same item twice gives one record.</param>
/// <param name="Writer">Who wrote the record: a person, a hook or a module, as the Host identified them. Trust follows the writer.</param>
public sealed record MemorySource(
    MemorySourceType SourceType,
    string SourceSystem,
    string? ExternalId = null,
    string? ConversationId = null,
    string? ThreadId = null,
    string? Url = null,
    string? Workspace = null,
    string? ProjectScope = null,
    string? RepositoryScope = null,
    string? Actor = null,
    string? DocumentId = null,
    string? Writer = null,
    MemorySourceAcl? Acl = null);

/// <param name="Tainted">Set when the content came from a source the Host marks untrusted. A tainted record is recalled as
/// <see cref="RecallRole.UntrustedEvidence"/> whatever its trust level, and the mark survives updates and consolidation.</param>
public sealed record MemoryPolicy(
    MemorySensitivity Sensitivity,
    MemoryTrustLevel TrustLevel,
    MemoryRetentionClass RetentionClass,
    string? PermissionScope = null,
    DateTimeOffset? ExpiresAtUtc = null,
    bool AllowModelTraining = false,
    bool AllowDirectQuotation = true,
    bool AllowSummarization = true,
    bool AllowCrossWorkspaceRetrieval = false,
    IReadOnlyList<string>? RequiredClaims = null,
    MemoryRedactionState RedactionState = MemoryRedactionState.None,
    bool Tainted = false);

public sealed record MemoryChunk(
    string Id,
    string ParentRecordId,
    int ChunkOrdinal,
    MemoryChunkKind Kind,
    string Content,
    string? WindowText = null,
    string? Summary = null,
    MemoryEmbeddingRef? Embedding = null,
    IReadOnlyList<string>? Tags = null);

public sealed record MemoryEntity(
    string Id,
    string Kind,
    string Name,
    string? ExternalId = null,
    IReadOnlyList<string>? Aliases = null,
    IReadOnlyList<string>? Tags = null);

public sealed record MemoryLink(
    string SourceId,
    MemoryNodeKind SourceKind,
    string TargetId,
    MemoryNodeKind TargetKind,
    MemoryRelationType RelationType,
    double Weight = 1.0,
    string? Evidence = null);

public sealed record MemorySpan(
    string Id,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc = null,
    string? Label = null);

public sealed record MemoryFact(
    string Id,
    string RecordId,
    string Statement,
    double Confidence,
    DateTimeOffset ObservedAtUtc,
    MemoryTrustLevel TrustLevel = MemoryTrustLevel.Unknown,
    string? ChunkId = null,
    IReadOnlyList<string>? EvidenceChunkIds = null,
    DateTimeOffset? ValidFromUtc = null,
    DateTimeOffset? ValidToUtc = null,
    bool IsSuperseded = false,
    string? SupersededByFactId = null);

public sealed record MemoryRecord(
    string Id,
    MemoryRecordKind Kind,
    MemorySource Source,
    MemoryPolicy Policy,
    string Content,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ObservedAtUtc,
    int SchemaVersion = 1,
    string? ContentHash = null,
    string? DedupeKey = null,
    DateTimeOffset? UpdatedAtUtc = null,
    string? ObservedBy = null,
    DateTimeOffset? LastAccessedAtUtc = null,
    string? Collection = null,
    string? Summary = null,
    MemoryEmbeddingRef? Embedding = null,
    double BaseImportance = 0,
    string? ParentRecordId = null,
    bool IsSuperseded = false,
    string? SupersededByRecordId = null,
    DateTimeOffset? SupersededAtUtc = null,
    DateTimeOffset? DeletedAtUtc = null,
    string? DeletedReason = null,
    string? CreatedBy = null,
    string? UpdatedBy = null,
    bool IsDerived = false,
    IReadOnlyList<string>? DerivedFromRecordIds = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? EntityRefs = null,
    MemorySpan? Span = null);
