namespace OpenTail.Connectivity.Tests;

public sealed class IncrementalSyncTests : IDisposable
{
    private readonly string _stateFile = Path.Combine(Path.GetTempPath(), "otail-sync-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try { File.Delete(_stateFile); } catch (IOException) { }
    }

    /// <summary>Yields a script of changes. "U:x" upserts item x, "C:n" a checkpoint, "R" a reset, "D:x" deletes x, "D-:x" out of scope. Items in failFetch throw on fetch.</summary>
    private sealed class Scripted(string[] script, params string[] failFetch) : IIncrementalSourceConnector
    {
        public string SyncScope => "test:acme:things";
        public List<string?> CursorsAskedFor { get; } = [];
        public bool ReportsDeletions { get; set; }

        public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CursorsAskedFor.Add(cursor);
            foreach (var step in script)
            {
                await Task.Yield();
                yield return step switch
                {
                    "R" => new SourceChange.Reset("token expired"),
                    _ when step.StartsWith("C:") => new SourceChange.Checkpoint(step[2..]),
                    _ when step.StartsWith("D-:") => new SourceChange.Delete(step[3..], DeletionReason.OutOfScope),
                    _ when step.StartsWith("D:") => new SourceChange.Delete(step[2..], DeletionReason.Deleted),
                    _ => new SourceChange.Upsert(new SourceItem(step[2..], step[2..] + ".txt", "txt", 1, DateTimeOffset.UnixEpoch, "acme")),
                };
            }
        }

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var c in GetChangesAsync(null, cancellationToken))
                if (c is SourceChange.Upsert u) yield return u.Item;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            if (failFetch.Contains(item.Id)) throw new HttpRequestException("timeout");
            return Task.FromResult(Encoding.UTF8.GetBytes(item.Id == "bad" ? "BAD" : "text of " + item.Id + new string('.', 60)));
        }
    }

    private sealed class Extractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content)
        {
            var text = Encoding.UTF8.GetString(content);
            return text == "BAD" ? new(false, "", "cannot read") : new(true, text);
        }
    }

    private sealed class Exporter(List<string> log, string? failOn = null, string? failDeleteOn = null) : IConnectivityExporter, IDeletionExporter
    {
        public List<DocumentDeletion> Deletions { get; } = [];

        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            if (document.SourceRef == failOn) throw new IOException("store down");
            log.Add("export " + document.SourceRef);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default)
        {
            if (deletion.SourceRef == failDeleteOn) throw new IOException("delete failed");
            Deletions.Add(deletion);
            log.Add("delete " + deletion.SourceRef);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            log.Add("flush");
            return Task.CompletedTask;
        }
    }

    private sealed class NonDeletionExporter(List<string> log) : IConnectivityExporter
    {
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            log.Add("export " + document.SourceRef);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default)
        {
            log.Add("flush");
            return Task.CompletedTask;
        }
    }

    /// <summary>Logs saves, so the order of flush and save can be checked.</summary>
    private sealed class LoggingStore(ISyncCursorStore inner, List<string> log) : ISyncCursorStore
    {
        public Task<SyncState?> LoadAsync(string scope, CancellationToken cancellationToken = default) => inner.LoadAsync(scope, cancellationToken);

        public async Task<SyncState> SaveAsync(string scope, string? cursor, long expectedVersion, CancellationToken cancellationToken = default)
        {
            var state = await inner.SaveAsync(scope, cursor, expectedVersion, cancellationToken);
            log.Add("save " + cursor);
            return state;
        }

        public Task ResetAsync(string scope, CancellationToken cancellationToken = default) => inner.ResetAsync(scope, cancellationToken);
    }

    private FileSyncCursorStore Store() => new(_stateFile);

    private Task<IngestionReport> Run(Scripted source, string? failExportOn = null, int every = 1, int? cap = null, IConnectivityExporter? exporter = null, string? failDeleteOn = null, SyncOptions? sync = null) =>
        new ConnectivityPipeline(source, new Extractor(), [exporter ?? new Exporter(_log, failExportOn, failDeleteOn)],
            new PipelineOptions(MaxItemsProcessed: cap, SyncStore: new LoggingStore(Store(), _log), Sync: sync ?? new SyncOptions(CheckpointEveryItems: every)))
        .RunAsync();

    [Fact]
    public async Task Saves_each_checkpoint_only_after_the_exporters_flush_and_resumes_from_it()
    {
        var first = new Scripted(["U:a", "U:b", "C:1", "U:c", "C:2"]);
        var report = await Run(first);

        Assert.Equal(["export a", "export b", "flush", "save 1", "export c", "flush", "save 2"], _log);
        Assert.True(report.SyncCommitted);
        Assert.True(report.FullRun);
        Assert.Equal("test:acme:things", report.SyncScope);
        Assert.Equal(new SyncState("2", 2, default), (await Store().LoadAsync("test:acme:things"))! with { UpdatedAt = default });

        var second = new Scripted(["C:3"]);
        var again = await Run(second);
        Assert.Equal(["2"], second.CursorsAskedFor);
        Assert.False(again.FullRun);
    }

    [Fact]
    public async Task Checkpoints_are_batched_by_item_count()
    {
        await Run(new Scripted(["U:a", "C:1", "U:b", "C:2", "U:c", "C:3"]), every: 500);
        Assert.Equal(["save 3"], _log.Where(l => l.StartsWith("save")));
    }

    [Fact]
    public async Task An_exporter_failure_stops_the_run_and_keeps_the_last_commit()
    {
        await Assert.ThrowsAsync<IOException>(() => Run(new Scripted(["U:a", "C:1", "U:b", "C:2"]), failExportOn: "b"));
        Assert.Equal("1", (await Store().LoadAsync("test:acme:things"))!.Cursor);
    }

    [Fact]
    public async Task A_failed_fetch_holds_the_cursor_before_it_so_the_item_comes_round_again()
    {
        var report = await Run(new Scripted(["U:a", "C:1", "U:b", "C:2", "U:c", "C:3"], failFetch: "b"));

        Assert.Equal("1", (await Store().LoadAsync("test:acme:things"))!.Cursor);
        Assert.Contains("export c", _log);                // the rest of the run still exports
        Assert.Equal("flush", _log[^1]);                  // and is flushed
        Assert.Equal(1, report.ItemsFailed);
        Assert.Contains(report.Errors, e => e.Contains("not committed past this item"));
    }

    [Fact]
    public async Task A_rejected_item_does_not_hold_the_cursor()
    {
        var report = await Run(new Scripted(["U:a", "C:1", "U:bad", "C:2"]));
        Assert.Equal("2", (await Store().LoadAsync("test:acme:things"))!.Cursor);
        Assert.Equal(1, report.ItemsFailed);
    }

    [Fact]
    public async Task The_item_cap_commits_the_last_checkpoint_reached()
    {
        var report = await Run(new Scripted(["U:a", "C:1", "U:b", "C:2", "U:c", "C:3"]), every: 500, cap: 2);
        Assert.Equal(2, report.ItemsProcessed);
        Assert.Equal("2", (await Store().LoadAsync("test:acme:things"))!.Cursor);
        Assert.DoesNotContain("export c", _log);
    }

    [Fact]
    public async Task A_stream_without_a_checkpoint_commits_nothing_but_still_flushes()
    {
        var report = await Run(new Scripted(["U:a"]));
        Assert.False(report.SyncCommitted);
        Assert.Null(await Store().LoadAsync("test:acme:things"));
        Assert.Equal(["export a", "flush"], _log);
    }

    [Fact]
    public async Task A_reset_marks_the_run_full()
    {
        await Store().SaveAsync("test:acme:things", "old", 0);
        var report = await Run(new Scripted(["R", "U:a", "C:9"]));
        Assert.True(report.FullRun);
        Assert.Contains(report.ValidationIssues, i => i.Code == "SYNC_RESET");
        Assert.Equal("9", (await Store().LoadAsync("test:acme:things"))!.Cursor);
    }

    [Fact]
    public async Task A_run_that_lost_a_race_saves_nothing()
    {
        var store = Store();
        await store.SaveAsync("s", "a", 0);
        await store.SaveAsync("s", "b", 1);
        var conflict = await Assert.ThrowsAsync<SyncConflictException>(() => store.SaveAsync("s", "stale", 1));
        Assert.Equal(2, conflict.ActualVersion);
        Assert.Equal("b", (await store.LoadAsync("s"))!.Cursor);
    }

    [Fact]
    public async Task The_store_keeps_scopes_apart_resets_one_and_treats_a_corrupt_file_as_no_state()
    {
        var store = Store();
        await store.SaveAsync("one", "1", 0);
        await store.SaveAsync("two", "2", 0);
        await store.ResetAsync("one");
        Assert.Null(await store.LoadAsync("one"));
        Assert.Equal("2", (await store.LoadAsync("two"))!.Cursor);

        await File.WriteAllTextAsync(_stateFile, "{ not json");
        Assert.Null(await store.LoadAsync("two"));
        Assert.True(store.LastReadWasCorrupt);
    }

    [Fact]
    public async Task Without_a_store_the_connector_runs_as_a_plain_source()
    {
        var source = new Scripted(["U:a", "C:1"]);
        var report = await new ConnectivityPipeline(source, new Extractor(), [new Exporter(_log)]).RunAsync();
        Assert.Equal(1, report.ItemsProcessed);
        Assert.Null(report.SyncScope);
        Assert.Equal([null], source.CursorsAskedFor);
    }

    [Fact]
    public async Task Deletions_are_exported_in_order_before_the_next_checkpoint()
    {
        var exporter = new Exporter(_log);
        var source = new Scripted(["U:a", "D:b", "C:1"]);
        var report = await Run(source, exporter: exporter);

        Assert.Equal(["export a", "delete b", "flush", "save 1"], _log);
        var deletion = Assert.Single(exporter.Deletions);
        Assert.Equal(ConnectivityPipeline.DocumentId("file", "acme", "b"), deletion.Id);
        Assert.Equal("b", deletion.SourceRef);
        Assert.Equal("file", deletion.SourceType);
        Assert.Equal("acme", deletion.TenantContext);
        Assert.Equal(DeletionReason.Deleted, deletion.Reason);
    }

    [Fact]
    public async Task A_deletion_exporter_that_throws_stops_the_run_without_committing_and_records_the_error()
    {
        var source = new Scripted(["U:a", "D:b", "C:1"]);
        var report = await Run(source, failDeleteOn: "b");

        Assert.Null(await Store().LoadAsync("test:acme:things"));
        Assert.Equal(1, report.ItemsFailed);
        Assert.Contains(report.Errors, e => e.Contains("delete failed"));
    }

    [Fact]
    public async Task Reports_deletions_without_a_deletion_exporter_throws_before_reading_changes()
    {
        var source = new Scripted(["D:b", "C:1"]) { ReportsDeletions = true };
        var exporter = new NonDeletionExporter(_log);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(source, exporter: exporter));
        Assert.Empty(source.CursorsAskedFor);
    }

    [Fact]
    public async Task Item_cap_stops_before_deletion()
    {
        var exporter = new Exporter(_log);
        var source = new Scripted(["U:a", "D:b", "C:1"]);
        var report = await Run(source, cap: 1, exporter: exporter);

        Assert.Equal(1, report.ItemsProcessed);
        Assert.Empty(exporter.Deletions);
    }

    [Fact]
    public async Task Items_deleted_counts_deletions()
    {
        var source = new Scripted(["U:a", "D:b", "D-:c", "C:1"]);
        var report = await Run(source);

        Assert.Equal(2, report.ItemsDeleted);
    }

    [Fact]
    public async Task Full_run_with_collect_source_refs_lists_all_seen_refs()
    {
        var source = new Scripted(["U:a", "U:b", "C:1"]);
        var report = await Run(source, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true));

        Assert.Equal(["a", "b"], report.SeenSourceRefs);
        Assert.False(report.SourceRefsTruncated);
    }

    [Fact]
    public async Task Run_resumed_from_stored_cursor_leaves_seen_source_refs_null()
    {
        await Store().SaveAsync("test:acme:things", "1", 0);
        var source = new Scripted(["U:a", "C:2"]);
        var report = await Run(source, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true));

        Assert.Null(report.SeenSourceRefs);
    }

    [Fact]
    public async Task Run_stopped_by_cap_leaves_seen_source_refs_null()
    {
        var source = new Scripted(["U:a", "U:b", "C:1"]);
        var report = await Run(source, cap: 1, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true));

        Assert.Null(report.SeenSourceRefs);
    }

    [Fact]
    public async Task Reset_mid_run_collects_refs_from_reset_on()
    {
        await Store().SaveAsync("test:acme:things", "old", 0);
        var source = new Scripted(["U:ignored", "R", "U:a", "U:b", "C:1"]);
        var report = await Run(source, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true));

        Assert.True(report.FullRun);
        Assert.Equal(["a", "b"], report.SeenSourceRefs);
    }

    [Fact]
    public async Task Rejected_item_is_in_the_seen_refs_list()
    {
        var source = new Scripted(["U:a", "U:bad", "C:1"]);
        var report = await Run(source, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true));

        Assert.Equal(["a", "bad"], report.SeenSourceRefs);
    }

    [Fact]
    public async Task Max_reported_refs_cap_truncates_with_flag_and_warning()
    {
        var source = new Scripted(["U:a", "U:b", "C:1"]);
        var report = await Run(source, sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true, MaxReportedRefs: 1));

        Assert.Equal(["a"], report.SeenSourceRefs);
        Assert.True(report.SourceRefsTruncated);
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_REFS_TRUNCATED" && i.Severity == ValidationSeverity.Warning);
    }
}
