namespace OpenTail.Connectivity.Files.Pipeline;

/// <param name="SyncStore">With an <see cref="IIncrementalSourceConnector"/>, runs incrementally and commits its cursor here.</param>
/// <param name="Sync">How often an incremental run commits.</param>
/// <param name="Transformers">Run in order on each document before validation (redaction, enrichment); the hash is taken after them.</param>
public sealed record PipelineOptions(
    bool DeduplicateByContentHash = false,
    IReadOnlyDictionary<string, string>? ExtraMetadata = null,
    int? MaxItemsProcessed = null,
    int MaxDedupHashes = 100_000,
    ISyncCursorStore? SyncStore = null,
    SyncOptions? Sync = null,
    IReadOnlyList<IDocumentTransformer>? Transformers = null,
    IItemVersionStore? VersionStore = null);
