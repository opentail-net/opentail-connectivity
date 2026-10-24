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
        var sync = _options.Sync;
        var sourceRefs = sync?.CollectSourceRefs == true
            ? new SourceRefCollector(sync.MaxReportedRefs, run.Issues, subject: null)
            : null;
        var discoveryCompleted = true;
        await foreach (var item in _source.DiscoverAsync(cancellationToken))
        {
            if (run.CapReached)
            {
                feedback?.NotProcessed(item); // discovered, but the cap stopped the run before it
                discoveryCompleted = false;
                break;
            }

            run.Discovered++;
            sourceRefs?.Add(item.Id);
            var outcome = await ProcessAsync(item, run, exporterFailuresStop: false, sourceRefs, cancellationToken);
            if (outcome is Outcome.Rejected or Outcome.FetchFailed or Outcome.ExporterFailed or Outcome.ExportedSomeRejected) feedback?.NotProcessed(item);
            if (outcome == Outcome.ExporterFailed) discoveryCompleted = false;
            if (run.CapReached)
            {
                discoveryCompleted = false;
                break;
            }
        }

        if (_source is IReportsIncompleteDiscovery { IncompleteReason: { } incomplete } && !cancellationToken.IsCancellationRequested)
        {
            discoveryCompleted = false;
            run.Errors.Add(incomplete);
            run.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "DISCOVERY_INCOMPLETE",
                $"{incomplete} Items after that point were not read, so the seen source refs are not reported.", null));
        }

        if (discoveryCompleted && sourceRefs is not null)
        {
            run.SeenSourceRefs = sourceRefs.Refs;
            run.SourceRefsTruncated = sourceRefs.Truncated;
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
        if (source.ReportsDeletions && _options.Expander is not null && _options.VersionStore is null)
            throw new InvalidOperationException(
                "This connector reports deletions and an expander is set, but there is no version store: it holds each archive's entry list, without which a deleted archive's entries would stay.");

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
        var sourceRefs = sync.CollectSourceRefs
            ? new SourceRefCollector(sync.MaxReportedRefs, run.Issues, scope)
            : null;

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
        // An item that failed after the last committed checkpoint is on the list and will also come again from the stream; it is
        // fetched once per run, by whichever reaches it first.
        var retriedThisRun = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in retries.ToList())
        {
            if (run.CapReached)
            {
                stoppedByCap = true;
                goto stop;
            }

            retriedThisRun.Add(entry.Item.Id);
            // No refs here: the item comes again from a full run's stream, which adds them.
            var retried = await ProcessAsync(entry.Item, run, exporterFailuresStop: true, sourceRefs: null, ct);
            unflushed |= NeedsFlush(retried);
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
                    if (run.FullRun) sourceRefs?.Add(upsert.Item.Id);
                    if (retriedThisRun.Contains(upsert.Item.Id))
                    {
                        run.Skipped++; // the retry pass already fetched its current content this run
                        if (run.FullRun && sourceRefs is not null)
                        {
                            // The retry pass ran without a source-ref collector, so revalidate before treating the item as accounted for.
                            if (_options.Expander?.ValidateSourceId(upsert.Item) is { } sourceIdError)
                            {
                                sourceRefs.Remove(upsert.Item.Id);
                                sourceRefs.MarkIncomplete(upsert.Item.Id,
                                    "A previously retried source item was rejected because its ID conflicts with the expander's child-ID namespace.");
                            }
                            else if (run.ExpandedChildren.TryGetValue(upsert.Item.Id, out var children) && children is not null)
                                ArchiveRefs(upsert.Item.Id, children, sourceRefs);
                            else if (run.ExpandedChildren.ContainsKey(upsert.Item.Id))
                                await ArchiveFailedAsync(upsert.Item, DocumentId(_sourceType, upsert.Item.TenantContext, upsert.Item.Id), run, sourceRefs, ct);
                        }
                        break;
                    }

                    var outcome = await ProcessAsync(upsert.Item, run, exporterFailuresStop: true, run.FullRun ? sourceRefs : null, ct);
                    unflushed |= NeedsFlush(outcome);
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
                    try
                    {
                        // An expanded archive was never a document itself, but its entries are.
                        var parentId = DocumentId(_sourceType, tenant, d.SourceRef);
                        if (_options.Expander is not null && _options.VersionStore is { } manifests
                            && await manifests.GetAsync(ManifestKey(parentId), ct) is { } manifest)
                        {
                            if (ReadManifestOrNull(manifest) is { } children)
                                foreach (var child in children)
                                    await DeleteDocumentAsync(child, tenant, d.Reason, run, ct);
                            else
                                ManifestDamaged(run, d.SourceRef);
                            await manifests.RemoveAsync(ManifestKey(parentId), ct);
                        }

                        await DeleteDocumentAsync(d.SourceRef, tenant, d.Reason, run, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        run.Failed++;
                        run.Errors.Add($"{d.SourceRef}: {ex.Message}");
                        return run.Report();
                    }

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
                    sourceRefs?.Reset();
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
            run.SeenSourceRefs = sourceRefs?.Refs;
            run.SourceRefsTruncated = sourceRefs?.Truncated ?? false;
        }

        return run.Report();
    }

    /// <summary><see cref="ExportedSomeRejected"/>: an archive with documents to flush, and entries that failed.</summary>
    private enum Outcome { Exported, Skipped, Rejected, FetchFailed, ExporterFailed, ExportedSomeRejected }

    private static bool NeedsFlush(Outcome outcome) => outcome is Outcome.Exported or Outcome.ExportedSomeRejected;

    /// <summary>One item, from fetch to export. Counts it in <paramref name="run"/>. Adds an archive's child ids to <paramref name="sourceRefs"/>.</summary>
    private async Task<Outcome> ProcessAsync(SourceItem item, Run run, bool exporterFailuresStop, SourceRefCollector? sourceRefs, CancellationToken ct)
    {
        var configuredExpander = _options.Expander;
        if (configuredExpander?.ValidateSourceId(item) is { } sourceIdError)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {sourceIdError}");
            run.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "SOURCE_ID_RESERVED", sourceIdError, item.Id));
            sourceRefs?.Remove(item.Id);
            sourceRefs?.MarkIncomplete(item.Id, "A source item was rejected because its ID conflicts with the expander's child-ID namespace.");
            return Outcome.Rejected;
        }

        if (run.TenantContext.Length == 0) run.TenantContext = item.TenantContext;

        string? itemVersion = null;
        if (item.Metadata is not null && item.Metadata.TryGetValue(SourceKeys.Version, out var v))
            itemVersion = v;

        var docId = DocumentId(_sourceType, item.TenantContext, item.Id);
        var expander = configuredExpander is { } e && e.CanExpand(item) ? e : null;

        var ingestedWhole = false; // a version but no child list: an archive exported as one document before expansion was set
        if (_options.VersionStore is { } versionStore && itemVersion is not null)
        {
            var stored = await versionStore.GetAsync(docId, ct).ConfigureAwait(false);
            var manifest = expander is null ? null : ReadManifestOrNull(await versionStore.GetAsync(ManifestKey(docId), ct).ConfigureAwait(false));
            ingestedWhole = expander is not null && stored is not null && manifest is null;
            // An archive is unchanged only with a child list to stand for it; without one it has never been expanded.
            if (stored is not null && string.Equals(stored, itemVersion, StringComparison.Ordinal) && (expander is null || manifest is not null))
            {
                run.Skipped++;
                run.Unchanged++;
                // Unchanged archives' entries still exist, so a deletion comparison must see them.
                if (manifest is not null)
                {
                    run.ExpandedChildren[item.Id] = manifest;
                    if (sourceRefs is not null) ArchiveRefs(item.Id, manifest, sourceRefs);
                }
                return Outcome.Skipped;
            }
        }

        byte[] bytes;
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
            if (expander is not null) await ArchiveFailedAsync(item, docId, run, sourceRefs, ct);
            return Outcome.FetchFailed;
        }

        return expander is null
            ? await ExportAsync(item, bytes, itemVersion, run, exporterFailuresStop, ct)
            : await ExpandAsync(expander, item, bytes, docId, itemVersion, ingestedWhole, run, exporterFailuresStop, sourceRefs, ct);
    }

    /// <summary>
    /// An archive is not a document: its children stand in its place in the seen refs, so an index entry for the archive itself
    /// (from before expansion) is found deleted by comparison.
    /// </summary>
    private static void ArchiveRefs(string archiveRef, IEnumerable<string> children, SourceRefCollector sourceRefs)
    {
        sourceRefs.Remove(archiveRef);
        foreach (var child in children) sourceRefs.Add(child);
    }

    /// <summary>
    /// An archive that could not be fetched or expanded this run. Its last child list stands in for the children, or, without one,
    /// the run's refs are marked incomplete: absent children must never look deleted.
    /// </summary>
    private async Task ArchiveFailedAsync(SourceItem item, string docId, Run run, SourceRefCollector? sourceRefs, CancellationToken ct)
    {
        var manifest = _options.VersionStore is { } store ? ReadManifestOrNull(await store.GetAsync(ManifestKey(docId), ct)) : null;
        run.ExpandedChildren[item.Id] = null;
        if (sourceRefs is null) return;
        if (manifest is null) sourceRefs.MarkIncomplete(item.Id, "The archive could not be read and its entries are not known.");
        else foreach (var child in manifest) sourceRefs.Add(child); // the archive's own ref stays: it may still be an old document
    }

    /// <summary>
    /// An archive: every child goes through extraction and export as an item of its own. The archive's version is recorded only
    /// when no child failed (as for any item), its child list always, and children gone since the last list are deleted.
    /// </summary>
    private async Task<Outcome> ExpandAsync(IItemExpander expander, SourceItem item, byte[] bytes, string docId, string? itemVersion,
        bool ingestedWhole, Run run, bool exporterFailuresStop, SourceRefCollector? sourceRefs, CancellationToken ct)
    {
        ItemExpansion expansion;
        try
        {
            expansion = expander.Expand(item, bytes, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            run.Failed++;
            run.Errors.Add($"{item.DisplayName}: {ex.Message}");
            await ArchiveFailedAsync(item, docId, run, sourceRefs, ct);
            return Outcome.Rejected;
        }

        foreach (var refused in expansion.Refused)
        {
            run.Skipped++;
            run.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "ARCHIVE_ENTRY_REFUSED",
                $"Entry not expanded: {refused.Reason}.", item.Id + "!/" + refused.Path));
        }

        var childIds = expansion.Items.Select(i => i.Item.Id).ToList();
        run.ExpandedChildren[item.Id] = childIds;
        if (sourceRefs is not null) ArchiveRefs(item.Id, childIds, sourceRefs);
        if (_options.VersionStore is { } manifests)
        {
            var current = new HashSet<string>(childIds, StringComparer.Ordinal);
            var stored = await manifests.GetAsync(ManifestKey(docId), ct);
            var previous = stored is null ? [] : ReadManifestOrNull(stored);
            if (previous is null) ManifestDamaged(run, item.Id);
            var gone = (previous ?? []).Where(id => !current.Contains(id)).ToList();
            // Exported as one document before expansion was set: that document goes, its children replace it.
            if (ingestedWhole) gone.Add(item.Id);
            foreach (var sourceRef in gone)
            {
                try
                {
                    await DeleteDocumentAsync(sourceRef, item.TenantContext, DeletionReason.Deleted, run, ct);
                }
                catch (Exception ex) when (!exporterFailuresStop && ex is not OperationCanceledException)
                {
                    run.Failed++;
                    run.Errors.Add($"{sourceRef}: {ex.Message}");
                    return Outcome.ExporterFailed;
                }
            }
        }

        var anyRejected = false;
        foreach (var child in expansion.Items)
        {
            run.Discovered++;
            var outcome = await ExportAsync(child.Item, child.Content, itemVersion: null, run, exporterFailuresStop, ct);
            if (outcome == Outcome.ExporterFailed) return outcome;
            anyRejected |= outcome == Outcome.Rejected;
        }

        if (_options.VersionStore is not null)
        {
            run.PendingVersions.Add((ManifestKey(docId), JsonSerializer.Serialize(childIds)));
            if (itemVersion is not null && !anyRejected) run.PendingVersions.Add((docId, itemVersion));
        }

        // Exported either way: there is a child list to flush even when no child produced a document.
        return anyRejected ? Outcome.ExportedSomeRejected : Outcome.Exported;
    }

    /// <summary>Extraction to export, for an item whose content is fetched.</summary>
    private async Task<Outcome> ExportAsync(SourceItem item, byte[] bytes, string? itemVersion, Run run, bool exporterFailuresStop, CancellationToken ct)
    {
        TextExtractionResult extraction;
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

        if (!extraction.Success && extraction.NotText)
        {
            run.Skipped++;
            run.Issues.Add(new ValidationIssue(ValidationSeverity.Warning, "NOT_TEXT",
                "The file has no text to read (binary, or an image without OCR): " + extraction.ErrorMessage, item.Id));
            return Outcome.Skipped;
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

        if (run.IsDuplicate(doc.TenantContext, doc.ContentHash))
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
            return Outcome.ExporterFailed;
        }

        // Remembered only once every exporter has it, so a failed export never hides a later copy.
        run.RememberHash(doc.TenantContext, doc.ContentHash);
        if (itemVersion is not null)
            run.PendingVersions.Add((doc.Id, itemVersion));

        run.Processed(doc.Body?.Length ?? 0);
        return Outcome.Exported;
    }

    private async Task DeleteDocumentAsync(string sourceRef, string tenant, DeletionReason reason, Run run, CancellationToken ct)
    {
        var deletion = new DocumentDeletion(
            Id: DocumentId(_sourceType, tenant, sourceRef),
            SourceRef: sourceRef,
            SourceType: _sourceType,
            TenantContext: tenant,
            Reason: reason,
            DetectedAt: DateTimeOffset.UtcNow);

        if (_options.VersionStore is { } versionStore)
            await versionStore.RemoveAsync(deletion.Id, ct);

        foreach (var exporter in _exporters)
        {
            if (exporter is IDeletionExporter deletionExporter)
                await deletionExporter.DeleteAsync(deletion, ct);
        }

        run.Deleted++;
    }

    /// <summary>Where an archive's child ids are kept in the version store: beside its version, never a document id (those are hex).</summary>
    private static string ManifestKey(string documentId) => documentId + "!entries";

    /// <summary>A damaged child list cannot say which entries the archive had, so they may remain downstream: reported as an error,
    /// never read as an empty list. A full run's seen refs leave them out, so a consumer that reconciles with them removes them.</summary>
    private static void ManifestDamaged(Run run, string archiveRef)
    {
        var message = $"The archive's list of entries is damaged, so entries of its earlier version may remain downstream; their ids start with '{archiveRef}!/'.";
        run.Errors.Add($"{archiveRef}: {message}");
        run.Issues.Add(new ValidationIssue(ValidationSeverity.Error, "ARCHIVE_MANIFEST_DAMAGED", message, archiveRef));
    }

    /// <summary>Null when there is no list or it is damaged: then nothing can be inferred from it.</summary>
    private static List<string>? ReadManifestOrNull(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class SourceRefCollector(int maxReportedRefs, List<ValidationIssue> issues, string? subject)
    {
        private bool warningAdded;

        public List<string> Refs { get; } = [];

        public bool Truncated { get; private set; }

        public void Add(string sourceRef)
        {
            if (Refs.Count < maxReportedRefs)
            {
                Refs.Add(sourceRef);
                return;
            }

            Truncated = true;
            if (warningAdded) return;
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "SOURCE_REFS_TRUNCATED",
                "The list of seen source refs was truncated and must not be used for deletions.", subject));
            warningAdded = true;
        }

        /// <summary>Takes back a ref just added; it is normally the last one.</summary>
        public void Remove(string sourceRef)
        {
            var at = Refs.LastIndexOf(sourceRef);
            if (at >= 0) Refs.RemoveAt(at);
        }

        /// <summary>Some items' refs are unknown this run, so absence proves nothing: reported like a truncated list.</summary>
        public void MarkIncomplete(string because, string reason)
        {
            Truncated = true;
            issues.Add(new ValidationIssue(ValidationSeverity.Warning, "SOURCE_REFS_INCOMPLETE",
                $"{reason} The list of seen source refs must not be used for deletions.", because));
        }

        public void Reset()
        {
            Refs.Clear();
            Truncated = false;
        }
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
        /// <summary>Archives handled this run: their children, or null when fetching or expanding failed.</summary>
        public Dictionary<string, List<string>?> ExpandedChildren { get; } = new(StringComparer.Ordinal);
        public string? SyncScope { get; set; }
        public bool FullRun { get; set; }
        public bool SyncCommitted { get; set; }

        public bool CapReached => options.MaxItemsProcessed is { } cap && processed >= cap;

        // Keyed by tenant and hash: the same body in two tenants is two documents.
        private static string HashKey(string tenant, string hash) => tenant + "\n" + hash;

        public bool IsDuplicate(string tenant, string hash) => seenHashes?.Contains(HashKey(tenant, hash)) == true;

        public void RememberHash(string tenant, string hash)
        {
            if (seenHashes is null) return;
            var key = HashKey(tenant, hash);
            if (!seenHashes.Add(key)) return;
            seenHashOrder.Enqueue(key);
            while (seenHashes.Count > Math.Max(1, options.MaxDedupHashes) && seenHashOrder.Count > 0)
                seenHashes.Remove(seenHashOrder.Dequeue());
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
