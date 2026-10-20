using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Validation;

namespace OpenTail.Connectivity.Files.Pipeline;

public sealed class ConnectivityPipeline
{
    private readonly ISourceConnector _source;
    private readonly ITextExtractor _extractor;
    private readonly IReadOnlyList<IConnectivityExporter> _exporters;
    private readonly PipelineOptions _options;
    private readonly string _sourceType;

    public ConnectivityPipeline(
        ISourceConnector source,
        ITextExtractor extractor,
        IReadOnlyList<IConnectivityExporter> exporters,
        PipelineOptions? options = null,
        string sourceType = "file")
    {
        _source = source;
        _extractor = extractor;
        _exporters = exporters;
        _options = options ?? new PipelineOptions();
        _sourceType = string.IsNullOrWhiteSpace(sourceType) ? "file" : sourceType.Trim().ToLowerInvariant();
    }

    public async Task<IngestionReport> RunAsync(CancellationToken cancellationToken = default)
    {
        if (_options.SyncStore is { } store && _source is IIncrementalSourceConnector incremental)
            return await RunIncrementalAsync(incremental, store, cancellationToken);

        var run = new Run(_options);
        var feedback = _source as ISourceItemFeedback;
        await foreach (var item in _source.DiscoverAsync(cancellationToken))
        {
            if (run.CapReached)
            {
                feedback?.NotProcessed(item); // discovered, but the cap stopped the run before it
                break;
            }

            run.Discovered++;
            var outcome = await ProcessAsync(item, run, exporterFailuresStop: false, cancellationToken);
            if (outcome is Outcome.Rejected or Outcome.FetchFailed) feedback?.NotProcessed(item);
            if (run.CapReached) break;
        }

        foreach (var exporter in _exporters)
            await exporter.FlushAsync(cancellationToken);

        if (_options.VersionStore is { } vStore)
        {
            foreach (var (docId, ver) in run.PendingVersions)
                await vStore.SetAsync(docId, ver, cancellationToken);
            run.PendingVersions.Clear();
            if (vStore is FileItemVersionStore fvs)
                await fvs.FlushAsync(cancellationToken);
        }

        return run.Report();
    }

    /// <summary>
    /// The incremental path: changes since the stored cursor, committed at checkpoints only after every exporter has flushed. A
    /// failed fetch stops further commits for the rest of the run (the next run repeats from the last commit), so nothing is
    /// skipped. An exporter or stream failure saves nothing more and is rethrown.
    /// </summary>
    private async Task<IngestionReport> RunIncrementalAsync(IIncrementalSourceConnector source, ISyncCursorStore store, CancellationToken ct)
    {
        if (source.ReportsDeletions && !_exporters.OfType<IDeletionExporter>().Any())
            throw new InvalidOperationException("This connector reports deletions but no exporter can apply them.");

        var run = new Run(_options);
        var sync = _options.Sync ?? new SyncOptions();
        var every = sync.CheckpointEvery ?? TimeSpan.FromMinutes(5);
        var scope = source.SyncScope;

        var state = await store.LoadAsync(scope, ct);
        var version = state?.Version ?? 0;
        var (committedCursor, retries) = RetryEnvelope.Read(state?.Cursor);
        var retriesDirty = false;
        run.SyncScope = scope;
        run.FullRun = committedCursor is null;

        string? pending = null;     // the latest checkpoint not yet committed
        var sinceCommit = 0;
        var lastCommit = DateTimeOffset.UtcNow;
        var holdCommits = false;    // a fetch failed and the retry list is full (or a deletion had no tenant): committing would skip it
        var unflushed = true;       // exporters have not been flushed since the last export (or at all)
        var stoppedByCap = false;
        List<string>? collectedRefs = sync.CollectSourceRefs ? [] : null;
        var refsTruncated = false;
        var refsTruncatedWarningAdded = false;

        async Task FlushAsync()
        {
            foreach (var exporter in _exporters) await exporter.FlushAsync(ct);
            unflushed = false;
            if (_options.VersionStore is { } vStore)
            {
                foreach (var (docId, ver) in run.PendingVersions)
                    await vStore.SetAsync(docId, ver, ct);
                run.PendingVersions.Clear();
                if (vStore is FileItemVersionStore fvs)
                    await fvs.FlushAsync(ct);
            }
        }

        async Task CommitAsync()
        {
            if (pending is null || holdCommits) return;
            await FlushAsync(); // never save the cursor before the documents it covers are flushed
            version = (await store.SaveAsync(scope, RetryEnvelope.Write(pending, retries), version, ct)).Version;
            run.SyncCommitted = true;
            committedCursor = pending;
            retriesDirty = false;
            pending = null;
            sinceCommit = 0;
            lastCommit = DateTimeOffset.UtcNow;
        }

        // Tracks a failed fetch so the cursor can move on. False when the list is full: the caller holds the cursor instead.
        bool TrackRetry(SourceItem item)
        {
            var error = run.Errors.Count > 0 ? run.Errors[^1] : null;
            var entry = retries.Find(r => r.Item.Id == item.Id);
            if (entry is null)
            {
                if (retries.Count >= Math.Max(0, sync.MaxRetryItems))
                    return false;
                retries.Add(entry = new RetryEntry { Item = item, FirstFailedAt = DateTimeOffset.UtcNow });
            }

            entry.Attempts++;
            entry.LastError = error;
            retriesDirty = true;
            if (entry.Attempts >= Math.Max(1, sync.MaxItemAttempts))
            {
                retries.Remove(entry);
                run.Errors.Add($"{item.DisplayName}: dropped from the retry list after {entry.Attempts} failed attempts.");
            }
            else
            {
                run.Errors.Add($"{item.DisplayName}: will be retried on the next run (attempt {entry.Attempts} of {Math.Max(1, sync.MaxItemAttempts)}).");
            }

            return true;
        }

        void Untrack(string sourceRef)
        {
            if (retries.RemoveAll(r => r.Item.Id == sourceRef) > 0)
                retriesDirty = true;
        }

        // Items that failed in earlier runs go first: the cursor has already moved past them.
        foreach (var entry in retries.ToList())
        {
            if (run.CapReached)
            {
                stoppedByCap = true;
                goto stop;
            }

            var retried = await ProcessAsync(entry.Item, run, exporterFailuresStop: true, ct);
            unflushed |= retried == Outcome.Exported;
            if (retried == Outcome.FetchFailed)
                TrackRetry(entry.Item); // the list already holds it, so this never fails
            else
                Untrack(entry.Item.Id);
        }

        await foreach (var change in source.GetChangesAsync(committedCursor, ct))
        {
            switch (change)
            {
                case SourceChange.Upsert upsert:
                    if (run.CapReached)
                    {
                        stoppedByCap = true;
                        goto stop;
                    }
                    run.Discovered++;
                    sinceCommit++;
                    if (collectedRefs is not null && run.FullRun)
                    {
                        if (collectedRefs.Count < sync.MaxReportedRefs)
                        {
                            collectedRefs.Add(upsert.Item.Id);
                        }
                        else
                        {
                            refsTruncated = true;
                            if (!refsTruncatedWarningAdded)
                            {
                                run.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "SOURCE_REFS_TRUNCATED", "The list of seen source refs was truncated and must not be used for deletions.", scope));
                                refsTruncatedWarningAdded = true;
                            }
                        }
                    }
                    var outcome = await ProcessAsync(upsert.Item, run, exporterFailuresStop: true, ct);
                    unflushed |= outcome == Outcome.Exported;
                    if (outcome == Outcome.FetchFailed)
                    {
                        if (!TrackRetry(upsert.Item))
                        {
                            holdCommits = true;
                            run.Errors.Add($"{upsert.Item.DisplayName}: the retry list is full, so nothing is committed past this item; the next run starts again from the last commit.");
                        }
                    }
                    else
                    {
                        Untrack(upsert.Item.Id); // the feed brought it again and it went through
                    }
                    break;

                case SourceChange.Delete d:
                    if (run.CapReached)
                    {
                        stoppedByCap = true;
                        goto stop;
                    }
                    sinceCommit++;
                    var tenant = d.TenantContext ?? (run.TenantContext.Length > 0 ? run.TenantContext : null);
                    if (tenant is null)
                    {
                        // Without the tenant the document id is unknown, and a deletion under the wrong id would silently do nothing.
                        run.Failed++;
                        run.Errors.Add($"{d.SourceRef}: a deletion came before any item and without a tenant, so nothing is committed past it.");
                        holdCommits = true;
                        break;
                    }

                    Untrack(d.SourceRef);
                    var deletion = new DocumentDeletion(
                        Id: DocumentId(_sourceType, tenant, d.SourceRef),
                        SourceRef: d.SourceRef,
                        SourceType: _sourceType,
                        TenantContext: tenant,
                        Reason: d.Reason,
                        DetectedAt: DateTimeOffset.UtcNow);

                    if (_options.VersionStore is { } delVs)
                        await delVs.RemoveAsync(deletion.Id, ct);

                    try
                    {
                        foreach (var exporter in _exporters)
                        {
                            if (exporter is IDeletionExporter deletionExporter)
                                await deletionExporter.DeleteAsync(deletion, ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        run.Failed++;
                        run.Errors.Add($"{d.SourceRef}: {ex.Message}");
                        return run.Report();
                    }

                    run.Deleted++;
                    unflushed = true;
                    break;

                case SourceChange.Checkpoint checkpoint:
                    pending = checkpoint.Cursor;
                    if (sinceCommit >= Math.Max(1, sync.CheckpointEveryItems) || DateTimeOffset.UtcNow - lastCommit >= every)
                        await CommitAsync();
                    break;

                case SourceChange.Reset reset:
                    run.FullRun = true;
                    if (retries.Count > 0)
                    {
                        retries.Clear(); // a full listing follows, which brings every item that still exists
                        retriesDirty = true;
                    }
                    collectedRefs?.Clear();
                    refsTruncated = false;
                    run.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "SYNC_RESET", "Full run: " + reset.Reason, scope));
                    break;
            }
        }

    stop:
        // The end of the stream, or the item cap: every item before the latest checkpoint has been handled, so it can be committed.
        await CommitAsync();
        if (unflushed) await FlushAsync(); // documents after the last checkpoint, or held back by a failed fetch
        if (retriesDirty && !holdCommits)
        {
            // The retry list changed with no checkpoint to carry it (retries alone, or no new changes): save it with the cursor
            // already committed.
            version = (await store.SaveAsync(scope, RetryEnvelope.Write(committedCursor, retries), version, ct)).Version;
            run.SyncCommitted = true;
        }

        run.RetriesPending = retries.Count;

        if (sync.CollectSourceRefs && run.FullRun && !stoppedByCap)
        {
            run.SeenSourceRefs = collectedRefs;
            run.SourceRefsTruncated = refsTruncated;
        }

        return run.Report();
    }

    private enum Outcome { Exported, Skipped, Rejected, FetchFailed }

    /// <summary>One item, from fetch to export. Counts it in <paramref name="run"/>.</summary>
    private async Task<Outcome> ProcessAsync(SourceItem item, Run run, bool exporterFailuresStop, CancellationToken ct)
    {
        if (run.TenantContext.Length == 0) run.TenantContext = item.TenantContext;

        string? itemVersion = null;
        if (item.Metadata is not null && item.Metadata.TryGetValue(SourceKeys.Version, out var v))
            itemVersion = v;

        var docId = DocumentId(_sourceType, item.TenantContext, item.Id);

        if (_options.VersionStore is { } versionStore && itemVersion is not null)
        {
            var stored = await versionStore.GetAsync(docId, ct).ConfigureAwait(false);
            if (stored is not null && string.Equals(stored, itemVersion, StringComparison.Ordinal))
            {
                run.Skipped++;
                run.Unchanged++;
                return Outcome.Skipped;
            }
        }

        byte[] bytes;
        TextExtractionResult extraction;
        try
        {
            ct.ThrowIfCancellationRequested();
            bytes = await _source.FetchAsync(item, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {ex.Message}");
            return Outcome.FetchFailed;
        }

        try
        {
            extraction = _extractor.Extract(item.DisplayName, bytes);
        }
        catch (Exception ex)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {ex.Message}");
            return Outcome.Rejected;
        }

        if (!extraction.Success)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {extraction.ErrorMessage}");
            return Outcome.Rejected;
        }

        if (string.IsNullOrWhiteSpace(extraction.Text))
        {
            run.Skipped++;
            run.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "EMPTY_CONTENT", "Extracted text is empty.", item.Id));
            return Outcome.Skipped;
        }

        var doc = Normalise(item, extraction, _options, _sourceType);
        if (_options.Transformers is { Count: > 0 } transformers)
        {
            try
            {
                foreach (var t in transformers) doc = t.Transform(doc);
                // The hash is of what is exported, so it never reflects text a transformer removed.
                doc = doc with { ContentHash = ComputeHash(doc.Body) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Failed++;
                run.Errors.Add($"{item.DisplayName}: a document transformer failed, so the item was not exported: {ex.Message}");
                return Outcome.Rejected;
            }
        }
        var docIssues = DocumentValidator.Validate(doc).ToList();
        if (!run.SeenRefs.Add(item.Id))
            docIssues.Add(new ValidationIssue(ValidationSeverity.Warning, "DUPLICATE_SOURCE_REF",
                "Another item in this run has the same source id, so both get the same document id.", item.Id));
        run.Issues.AddRange(docIssues);

        if (docIssues.Any(i => i.Severity == ValidationSeverity.Error))
        {
            run.Failed++;
            return Outcome.Rejected;
        }

        if (!run.FirstTimeHash(doc.ContentHash))
        {
            run.Skipped++;
            return Outcome.Skipped;
        }

        try
        {
            foreach (var exporter in _exporters)
                await exporter.ExportAsync(doc, ct);
        }
        catch (Exception ex) when (!exporterFailuresStop && ex is not OperationCanceledException)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {ex.Message}");
            return Outcome.Rejected;
        }

        if (itemVersion is not null)
            run.PendingVersions.Add((doc.Id, itemVersion));

        run.Processed(doc.Body?.Length ?? 0);
        return Outcome.Exported;
    }

    /// <summary>Counters and lists for one run.</summary>
    private sealed class Run(PipelineOptions options)
    {
        private readonly HashSet<string>? seenHashes = options.DeduplicateByContentHash ? new(StringComparer.OrdinalIgnoreCase) : null;
        private readonly Queue<string> seenHashOrder = new();
        private readonly string runId = Guid.NewGuid().ToString("N");
        private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        private int minBody = int.MaxValue;
        private int maxBody;
        private long totalBody;
        private int processed;

        public string TenantContext { get; set; } = string.Empty;
        public int Discovered { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Deleted { get; set; }
        public List<string> Errors { get; } = [];
        public List<ValidationIssue> Issues { get; } = [];
        public HashSet<string> SeenRefs { get; } = new(StringComparer.Ordinal);
        public List<string>? SeenSourceRefs { get; set; }
        public bool SourceRefsTruncated { get; set; }
        public int Unchanged { get; set; }
        public int RetriesPending { get; set; }
        public List<(string DocId, string Version)> PendingVersions { get; } = [];
        public string? SyncScope { get; set; }
        public bool FullRun { get; set; }
        public bool SyncCommitted { get; set; }

        public bool CapReached => options.MaxItemsProcessed is { } cap && processed >= cap;

        public bool FirstTimeHash(string hash)
        {
            if (seenHashes is null) return true;
            if (!seenHashes.Add(hash)) return false;
            seenHashOrder.Enqueue(hash);
            while (seenHashes.Count > Math.Max(1, options.MaxDedupHashes) && seenHashOrder.Count > 0)
                seenHashes.Remove(seenHashOrder.Dequeue());
            return true;
        }

        public void Processed(int bodyLength)
        {
            processed++;
            minBody = Math.Min(minBody, bodyLength);
            maxBody = Math.Max(maxBody, bodyLength);
            totalBody += bodyLength;
        }

        public IngestionReport Report() => new(
            RunId: runId,
            TenantContext: TenantContext,
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.UtcNow,
            ItemsDiscovered: Discovered,
            ItemsProcessed: processed,
            ItemsSkipped: Skipped,
            ItemsFailed: Failed,
            DocumentsProduced: processed,
            ValidationIssues: Issues,
            Errors: Errors,
            MinBodyLength: processed > 0 ? minBody : 0,
            MaxBodyLength: maxBody,
            AvgBodyLength: processed > 0 ? Math.Round((double)totalBody / processed, 1) : 0,
            SyncScope: SyncScope,
            FullRun: FullRun,
            SyncCommitted: SyncCommitted,
            ItemsDeleted: Deleted,
            SeenSourceRefs: SeenSourceRefs,
            SourceRefsTruncated: SourceRefsTruncated,
            ItemsUnchanged: Unchanged,
            RetriesPending: RetriesPending);
    }

    private static NormalisedDocument Normalise(SourceItem item, TextExtractionResult extraction, PipelineOptions options, string sourceType)
    {
        var body = extraction.Text.Trim();
        var title = Title(item);
        var hash = ComputeHash(body);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.Metadata is not null)
            foreach (var kv in item.Metadata)
                metadata[kv.Key.StartsWith("source.", StringComparison.OrdinalIgnoreCase) ? kv.Key : "source." + kv.Key] = kv.Value;
        metadata["source.extension"] = item.Extension;
        metadata["source.sizeBytes"] = item.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (extraction.Metadata is not null)
            foreach (var kv in extraction.Metadata)
                metadata[$"extraction.{kv.Key}"] = kv.Value;

        if (options.ExtraMetadata is not null)
            foreach (var kv in options.ExtraMetadata)
                metadata[$"run.{kv.Key}"] = kv.Value;

        return new NormalisedDocument(
            Id: DocumentId(sourceType, item.TenantContext, item.Id),
            Title: title,
            Body: body,
            ContentHash: hash,
            SourceRef: item.Id,
            SourceType: sourceType,
            TenantContext: item.TenantContext,
            SourceModified: item.Modified,
            IngestedAt: DateTimeOffset.UtcNow,
            Metadata: metadata);
    }

    /// <summary>
    /// The display name without its extension, only when it ends with the item's extension: a subject or page title such as
    /// "Release 2.1 notes" keeps its dots.
    /// </summary>
    internal static string Title(SourceItem item)
    {
        var name = item.DisplayName;
        var suffix = "." + item.Extension;
        return item.Extension.Length > 0 && name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : name;
    }

    /// <summary>
    /// The document id: the first 32 hex characters of SHA-256 over source type, tenant and source id. The same item gets the
    /// same id on every run, so a store can update it in place; a changed body changes <c>ContentHash</c>, not the id.
    /// </summary>
    public static string DocumentId(string sourceType, string tenantContext, string sourceRef)
    {
        // Each part is length-prefixed, so no choice of characters inside one part can make two different triples hash alike.
        static string Part(string s) => s.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + s;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Part(sourceType) + Part(tenantContext) + Part(sourceRef)));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    private static string ComputeHash(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
