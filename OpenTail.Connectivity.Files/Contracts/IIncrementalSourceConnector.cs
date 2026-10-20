namespace OpenTail.Connectivity.Files.Contracts;

/// <summary>
/// A source that streams changes since a cursor, with checkpoints the pipeline commits after the exporters have flushed. Run
/// through <see cref="Pipeline.ConnectivityPipeline"/> with <see cref="Pipeline.PipelineOptions.SyncStore"/> set; without a store
/// the pipeline uses <see cref="ISourceConnector.DiscoverAsync"/> as for any source.
/// </summary>
/// <remarks>
/// This is the smaller core of the sync design (docs/design/sync-and-deletions.md): upserts, checkpoints and resets. Deletions
/// and a retry list come with the first connector that has a change feed.
/// </remarks>
public interface IIncrementalSourceConnector : ISourceConnector
{
    /// <summary>What this connector reads, stable across runs, for example "sql:sqlite:crm/tickets" or "graph-drive:acme:b!xyz".</summary>
    string SyncScope { get; }

    /// <summary>
    /// Changes since <paramref name="cursor"/>; null means from the beginning (a full run). A connector promises that resuming
    /// from a <see cref="SourceChange.Checkpoint"/> cursor skips nothing that came after it.
    /// </summary>
    IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, CancellationToken cancellationToken = default);

    /// <summary>Whether this connector can yield <see cref="SourceChange.Delete"/> changes.</summary>
    bool ReportsDeletions => false;
}

/// <summary>One step of a change stream.</summary>
public abstract record SourceChange
{
    private SourceChange() { }

    /// <summary>An item is new or changed: fetch, extract and export it.</summary>
    public sealed record Upsert(SourceItem Item) : SourceChange;

    /// <summary>An item no longer exists at the source, or no longer matches the connector's filter.</summary>
    public sealed record Delete(string SourceRef, Models.DeletionReason Reason) : SourceChange;

    /// <summary>A cursor that is valid once everything yielded before it has been handled. Opaque to everyone but the connector.</summary>
    public sealed record Checkpoint(string Cursor) : SourceChange;

    /// <summary>The stored cursor was rejected (expired token, schema change); a full listing follows.</summary>
    public sealed record Reset(string Reason) : SourceChange;
}

/// <summary>Stored sync state for one scope. <see cref="Version"/> increases with every save.</summary>
public sealed record SyncState(string? Cursor, long Version, DateTimeOffset UpdatedAt);

/// <summary>Holds one cursor per scope.</summary>
public interface ISyncCursorStore
{
    Task<SyncState?> LoadAsync(string scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves <paramref name="cursor"/> as version <paramref name="expectedVersion"/> + 1. Throws <see cref="SyncConflictException"/>
    /// if the stored version is not <paramref name="expectedVersion"/> (0 when there is none): another run committed meanwhile.
    /// </summary>
    Task<SyncState> SaveAsync(string scope, string? cursor, long expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Forgets the scope, so the next run is a full run.</summary>
    Task ResetAsync(string scope, CancellationToken cancellationToken = default);
}

public sealed class SyncConflictException(string scope, long expected, long actual)
    : Exception($"Sync scope '{scope}' is at version {actual}, not {expected}: another run committed meanwhile. Nothing was saved.")
{
    public string Scope { get; } = scope;
    public long ExpectedVersion { get; } = expected;
    public long ActualVersion { get; } = actual;
}

/// <summary>How often an incremental run commits.</summary>
/// <param name="CheckpointEveryItems">Commit at the first checkpoint after this many changes since the last commit.</param>
/// <param name="CheckpointEvery">Or after this long since the last commit, whichever comes first. Default 5 minutes.</param>
public sealed record SyncOptions(int CheckpointEveryItems = 500, TimeSpan? CheckpointEvery = null);
