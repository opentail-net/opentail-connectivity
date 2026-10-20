namespace OpenTail.Connectivity.Memory;

/// <summary>Kinds of search a backend can run itself.</summary>
[Flags]
public enum MemorySearchKinds
{
    None = 0,
    Keyword = 1,
    Vector = 2,
    /// <summary>The backend fuses keyword and vector results itself.</summary>
    Hybrid = 4,
    /// <summary>The backend can follow links between records and entities.</summary>
    Graph = 8,
}

/// <summary>What a backend declares it can do. The layers above adapt; the conformance suite checks the claims are true.</summary>
/// <param name="FiltersBeforeRanking">The backend applies <see cref="MemoryFilter"/> inside the search, before top-k. Required for any
/// deployment with more than one principal: filtering afterwards makes recall depend on how many forbidden records rank higher.</param>
/// <param name="TemporalValidity">Superseded records and facts outside their validity window are not returned as current.</param>
/// <param name="EmbeddingModelId">The embedding model behind stored vectors, or null when the backend embeds itself or has none.
/// Vectors from one model are useless to another: switching means a re-index from the records.</param>
public sealed record MemoryBackendCapabilities(
    MemorySearchKinds Search,
    bool FiltersBeforeRanking,
    bool TemporalValidity,
    bool DeleteBySource,
    string? EmbeddingModelId = null);

/// <summary>Who is asking and what they may see. Every field is typed; nothing is inferred from record text.</summary>
/// <param name="Principals">The caller's user, group and role ids, matched against a record's captured source ACL.</param>
/// <param name="Scopes">Permission scopes the caller holds, matched against <see cref="MemoryPolicy.PermissionScope"/>.</param>
/// <param name="Claims">Claims the caller holds, matched against <see cref="MemoryPolicy.RequiredClaims"/>.</param>
public sealed record MemoryFilter(
    IReadOnlyList<string>? Principals = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? Claims = null,
    IReadOnlyList<string>? Collections = null,
    string? Workspace = null,
    string? ProjectScope = null,
    string? RepositoryScope = null,
    MemorySensitivity? MaxSensitivity = null,
    MemoryTrustLevel? MinTrustLevel = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    bool IncludeSuperseded = false,
    bool IncludeDerived = true,
    bool IncludeTainted = true);

public sealed record MemorySearch(
    string Query,
    MemorySearchKinds Kind,
    int TopK,
    MemoryFilter Filter);

/// <summary>What to delete. Exactly one of the fields is set.</summary>
public sealed record MemoryDeletion(
    string? RecordId = null,
    string? DocumentId = null,
    string? Collection = null,
    string? Reason = null)
{
    public static MemoryDeletion ById(string recordId, string? reason = null) => new(RecordId: recordId, Reason: reason);

    public static MemoryDeletion BySource(string documentId, string? reason = null) => new(DocumentId: documentId, Reason: reason);

    public static MemoryDeletion ByCollection(string collection, string? reason = null) => new(Collection: collection, Reason: reason);
}

/// <summary>A raw hit from a backend, before governance and ranking.</summary>
/// <param name="Kind">The kind of search that produced it, so fusion knows what it is merging.</param>
public sealed record BackendHit(
    MemoryRecord Record,
    string ChunkId,
    MemoryChunkKind ChunkKind,
    string Text,
    double Score,
    MemorySearchKinds Kind);

/// <summary>A governed hit: what callers and agents receive. For a hit that is not <see cref="MemoryRetrievalDisposition.Allowed"/>,
/// <see cref="Record"/> is the copy <see cref="MemoryRedaction.Redact"/> makes, never the stored record.</summary>
/// <param name="Text">The matched text, or for a <see cref="MemoryRetrievalDisposition.Redacted"/> or
/// <see cref="MemoryRetrievalDisposition.Summarized"/> hit the record's summary when its policy allows one, else empty.</param>
/// <param name="Role">What the hit may be used as. See <see cref="MemoryRecall.RoleOf"/>.</param>
/// <param name="Reasons">Why it was returned (and, for a redacted hit, what was withheld).</param>
public sealed record MemoryHit(
    MemoryRecord Record,
    string ChunkId,
    string Text,
    double Score,
    RecallRole Role,
    IReadOnlyList<string> Reasons,
    MemoryRetrievalDisposition Disposition = MemoryRetrievalDisposition.Allowed);

/// <summary>A store of memory records. Implementations: SQLite, files, Postgres through VectorData, hosted services.</summary>
public interface IMemoryBackend
{
    MemoryBackendCapabilities Capabilities { get; }

    /// <summary>Insert or replace a record and its chunks. A record with the same id replaces the old one entirely.</summary>
    Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default);

    Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default);

    /// <summary>Delete what <paramref name="deletion"/> names; returns the number of records removed. A deleted record must never
    /// come back from search.</summary>
    Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default);

    /// <summary>Search. When <see cref="MemoryBackendCapabilities.FiltersBeforeRanking"/> is true the filter is applied inside the
    /// search; otherwise the caller must not pass a filter that restricts by principal or scope.</summary>
    Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default);
}
