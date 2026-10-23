using OpenTail.Connectivity.Files.Archives;
using static OpenTail.Connectivity.Tests.Archives.ArchiveExpanderTests;

namespace OpenTail.Connectivity.Tests.Archives;

public sealed class ArchivePipelineTests : IDisposable
{
    private readonly string _cursorFile = Path.Combine(Path.GetTempPath(), "otail-arch-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { File.Delete(_cursorFile); } catch (IOException) { }
    }

    /// <summary>Items with content and a version; as a change feed, upserts every item and then deletes <see cref="Deleted"/>.</summary>
    private sealed class Source : IIncrementalSourceConnector
    {
        public Dictionary<string, (byte[] Content, string Version)> Items { get; } = new();
        public List<string> Deleted { get; } = [];
        public List<string> Fetched { get; } = [];
        public HashSet<string> FailFetch { get; } = [];
        public bool ReportsDeletions { get; init; }
        public bool EmitReset { get; set; }
        public string SyncScope => "test:acme:archives";

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var (name, (_, version)) in Items)
                yield return Item(name, new Dictionary<string, string> { [SourceKeys.Version] = version });
        }

        public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (EmitReset) yield return new SourceChange.Reset("test reset");
            await foreach (var item in DiscoverAsync(cancellationToken)) yield return new SourceChange.Upsert(item);
            foreach (var name in Deleted) yield return new SourceChange.Delete(Item(name).Id, DeletionReason.Deleted, "acme");
            yield return new SourceChange.Checkpoint("c" + Guid.NewGuid().ToString("N"));
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            Fetched.Add(item.Id);
            if (FailFetch.Contains(item.DisplayName)) throw new HttpRequestException("timeout");
            return Task.FromResult(Items[item.DisplayName].Content);
        }
    }

    private sealed class Extractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            fileName.EndsWith(".bad", StringComparison.Ordinal) ? new(false, "", "cannot read") : new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Exporter : IConnectivityExporter, IDeletionExporter
    {
        public List<NormalisedDocument> Exported { get; } = [];
        public List<string> Deleted { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            Exported.Add(document);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(DocumentDeletion deletion, CancellationToken cancellationToken = default)
        {
            Deleted.Add(deletion.SourceRef);
            Assert.Equal(ConnectivityPipeline.DocumentId("file", "acme", deletion.SourceRef), deletion.Id);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Versions : IItemVersionStore
    {
        public Dictionary<string, string> Values { get; } = new();
        public Task<string?> GetAsync(string documentId, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(documentId));
        public Task SetAsync(string documentId, string version, CancellationToken ct = default)
        {
            Values[documentId] = version;
            return Task.CompletedTask;
        }
        public Task RemoveAsync(string documentId, CancellationToken ct = default)
        {
            Values.Remove(documentId);
            return Task.CompletedTask;
        }
    }

    private static string Text(string s) => s + new string('.', 60); // past the validator's minimum length

    private static ConnectivityPipeline Pipeline(Source source, Exporter exporter, PipelineOptions options) =>
        new(source, new Extractor(), [exporter], options);

    [Fact]
    public async Task Children_are_exported_the_archive_is_not_and_refused_entries_are_reported()
    {
        var source = new Source();
        source.Items["report.zip"] = (Zip(("q3/sales.txt", Text("sales")), ("../evil.txt", Text("x"))), "v1");
        var exporter = new Exporter();

        var report = await Pipeline(source, exporter, new PipelineOptions(Expander: new ArchiveExpander())).RunAsync();

        var doc = Assert.Single(exporter.Exported);
        Assert.Equal("s3://bucket/report.zip!/q3/sales.txt", doc.SourceRef);
        Assert.Equal("sales", doc.Title);
        Assert.Equal("q3/sales.txt", doc.Metadata[ArchiveExpander.PathKey]);
        var issue = Assert.Single(report.ValidationIssues, i => i.Code == "ARCHIVE_ENTRY_REFUSED");
        Assert.Contains("climbs out", issue.Message);
        Assert.Equal(1, report.ItemsProcessed);
    }

    [Fact]
    public async Task A_plain_source_id_cannot_collide_with_an_archive_child_id()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("report.txt", Text("from archive"))), "v1");
        source.Items["a.zip!/report.txt"] = (Encoding.UTF8.GetBytes(Text("from source")), "v1");
        var exporter = new Exporter();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), Sync: new SyncOptions(CollectSourceRefs: true));

        var report = await Pipeline(source, exporter, options).RunAsync();

        var exported = Assert.Single(exporter.Exported);
        Assert.Equal("s3://bucket/a.zip!/report.txt", exported.SourceRef);
        Assert.Equal(1, report.ItemsFailed);
        Assert.Contains(report.Errors, e => e.Contains("reserved"));
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_ID_RESERVED");
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_REFS_INCOMPLETE");
        Assert.True(report.SourceRefsTruncated);
    }

    [Fact]
    public async Task A_refused_archive_fails_with_its_reason_and_exports_nothing()
    {
        var source = new Source();
        source.Items["bomb.zip"] = (Zip(("zeros.txt", new byte[20 * 1024 * 1024])), "v1");
        var exporter = new Exporter();

        var report = await Pipeline(source, exporter, new PipelineOptions(Expander: new ArchiveExpander())).RunAsync();

        Assert.Empty(exporter.Exported);
        Assert.Equal(1, report.ItemsFailed);
        Assert.Contains(report.Errors, e => e.Contains("archive bomb"));
    }

    [Fact]
    public async Task An_unchanged_archive_is_not_fetched_again_but_its_children_stay_seen()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one")), ("two.txt", Text("two"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions, Sync: new SyncOptions(CollectSourceRefs: true));

        await Pipeline(source, new Exporter(), options).RunAsync();
        var second = await Pipeline(source, new Exporter(), options).RunAsync();

        Assert.Single(source.Fetched);
        Assert.Equal(1, second.ItemsUnchanged);
        // Without the children here, a deletion comparison would delete every entry of every unchanged archive.
        Assert.Contains("s3://bucket/a.zip!/one.txt", second.SeenSourceRefs!);
        Assert.Contains("s3://bucket/a.zip!/two.txt", second.SeenSourceRefs!);
    }

    [Fact]
    public async Task An_entry_removed_from_a_changed_archive_is_deleted()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one")), ("two.txt", Text("two"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions);
        await Pipeline(source, new Exporter(), options).RunAsync();

        source.Items["a.zip"] = (Zip(("one.txt", Text("one, edited"))), "v2");
        var exporter = new Exporter();
        var report = await Pipeline(source, exporter, options).RunAsync();

        Assert.Equal(["s3://bucket/a.zip!/two.txt"], exporter.Deleted);
        Assert.Equal(1, report.ItemsDeleted);
        Assert.Equal("s3://bucket/a.zip!/one.txt", Assert.Single(exporter.Exported).SourceRef);
    }

    [Fact]
    public async Task A_damaged_entry_list_of_a_changed_archive_is_reported_not_read_as_empty()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one")), ("two.txt", Text("two"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions, Sync: new SyncOptions(CollectSourceRefs: true));
        await Pipeline(source, new Exporter(), options).RunAsync();
        Damage(versions);

        source.Items["a.zip"] = (Zip(("one.txt", Text("one, edited"))), "v2");
        var report = await Pipeline(source, new Exporter(), options).RunAsync();

        var issue = Assert.Single(report.ValidationIssues, i => i.Code == "ARCHIVE_MANIFEST_DAMAGED");
        Assert.Contains("'s3://bucket/a.zip!/'", issue.Message);
        Assert.Contains(report.Errors, e => e.Contains("damaged"));
        // two.txt is not seen, so a consumer that reconciles with the seen refs removes it.
        Assert.False(report.SourceRefsTruncated);
        Assert.DoesNotContain("s3://bucket/a.zip!/two.txt", report.SeenSourceRefs!);
    }

    [Fact]
    public async Task A_deleted_archive_with_a_damaged_entry_list_reports_the_entries_it_could_not_delete()
    {
        var source = new Source { ReportsDeletions = true };
        source.Items["a.zip"] = (Zip(("one.txt", Text("one"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions, SyncStore: new FileSyncCursorStore(_cursorFile));
        await Pipeline(source, new Exporter(), options).RunAsync();
        Damage(versions);

        source.Items.Remove("a.zip");
        source.Deleted.Add("a.zip");
        var report = await Pipeline(source, new Exporter(), options).RunAsync();

        Assert.Contains(report.ValidationIssues, i => i.Code == "ARCHIVE_MANIFEST_DAMAGED" && i.Message.Contains("'s3://bucket/a.zip!/'"));
    }

    private static void Damage(Versions versions)
    {
        var key = Assert.Single(versions.Values.Keys, k => k.EndsWith("!entries", StringComparison.Ordinal));
        versions.Values[key] = "{not a list";
    }

    [Fact]
    public async Task A_failed_entry_keeps_the_archive_version_unrecorded_so_it_is_tried_again()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("ok.txt", Text("ok")), ("broken.bad", Text("x"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions);

        var first = await Pipeline(source, new Exporter(), options).RunAsync();
        await Pipeline(source, new Exporter(), options).RunAsync();

        Assert.Equal(1, first.ItemsFailed);
        Assert.Equal(2, source.Fetched.Count); // as for any item that failed to extract
    }

    [Fact]
    public async Task A_deleted_archive_deletes_its_entries_in_an_incremental_run()
    {
        var source = new Source { ReportsDeletions = true };
        source.Items["a.zip"] = (Zip(("one.txt", Text("one")), ("two.txt", Text("two"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions, SyncStore: new FileSyncCursorStore(_cursorFile));
        await Pipeline(source, new Exporter(), options).RunAsync();

        source.Items.Remove("a.zip");
        source.Deleted.Add("a.zip");
        var exporter = new Exporter();
        var report = await Pipeline(source, exporter, options).RunAsync();

        Assert.Equal(["s3://bucket/a.zip!/one.txt", "s3://bucket/a.zip!/two.txt", "s3://bucket/a.zip"], exporter.Deleted);
        Assert.Equal(3, report.ItemsDeleted);
        Assert.Empty(versions.Values); // no version or entry list left behind
    }

    [Fact]
    public async Task A_deleting_connector_with_an_expander_needs_a_version_store()
    {
        var source = new Source { ReportsDeletions = true };
        var options = new PipelineOptions(Expander: new ArchiveExpander(), SyncStore: new FileSyncCursorStore(_cursorFile));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(source, new Exporter(), options).RunAsync());
        Assert.Contains("version store", ex.Message);
    }

    [Fact]
    public async Task Without_an_expander_an_archive_is_extracted_as_before()
    {
        var source = new Source();
        source.Items["a.zip"] = (Encoding.UTF8.GetBytes(Text("whatever the extractor makes of it")), "v1");
        var exporter = new Exporter();

        await Pipeline(source, exporter, new PipelineOptions()).RunAsync();

        Assert.Equal("s3://bucket/a.zip", Assert.Single(exporter.Exported).SourceRef);
    }

    [Fact]
    public async Task An_archive_ingested_whole_before_is_expanded_once_an_expander_is_set()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one"))), "v1");
        var versions = new Versions();
        await Pipeline(source, new Exporter(), new PipelineOptions(VersionStore: versions)).RunAsync();

        // Same version: without a child list the archive has never been expanded, so it must not be skipped.
        var exporter = new Exporter();
        var report = await Pipeline(source, exporter, new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions,
            Sync: new SyncOptions(CollectSourceRefs: true))).RunAsync();

        Assert.Equal("s3://bucket/a.zip!/one.txt", Assert.Single(exporter.Exported).SourceRef);
        Assert.Equal(["s3://bucket/a.zip"], exporter.Deleted); // the old whole-archive document
        Assert.Equal(["s3://bucket/a.zip!/one.txt"], report.SeenSourceRefs!);
        Assert.False(report.SourceRefsTruncated);
    }

    [Fact]
    public async Task An_expanded_archive_is_not_itself_a_seen_ref()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one"))), "v1");
        source.Items["plain.txt"] = (Encoding.UTF8.GetBytes(Text("plain")), "v1");

        var report = await Pipeline(source, new Exporter(), new PipelineOptions(Expander: new ArchiveExpander(),
            Sync: new SyncOptions(CollectSourceRefs: true))).RunAsync();

        Assert.Equal(["s3://bucket/a.zip!/one.txt", "s3://bucket/plain.txt"], report.SeenSourceRefs!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_failed_archive_keeps_its_last_known_children_seen()
    {
        var source = new Source();
        source.Items["a.zip"] = (Zip(("one.txt", Text("one")), ("two.txt", Text("two"))), "v1");
        var versions = new Versions();
        var options = new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: versions, Sync: new SyncOptions(CollectSourceRefs: true));
        await Pipeline(source, new Exporter(), options).RunAsync();

        source.Items["a.zip"] = (Encoding.UTF8.GetBytes("now corrupt"), "v2");
        var corrupt = await Pipeline(source, new Exporter(), options).RunAsync();
        source.Items["a.zip"] = (source.Items["a.zip"].Content, "v3");
        source.FailFetch.Add("a.zip");
        var unreachable = await Pipeline(source, new Exporter(), options).RunAsync();

        foreach (var report in new[] { corrupt, unreachable })
        {
            Assert.Equal(1, report.ItemsFailed);
            Assert.Contains("s3://bucket/a.zip!/one.txt", report.SeenSourceRefs!);
            Assert.Contains("s3://bucket/a.zip!/two.txt", report.SeenSourceRefs!);
            Assert.False(report.SourceRefsTruncated);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_archive_without_a_child_list_marks_the_refs_incomplete(bool failFetch)
    {
        var source = new Source();
        source.Items["a.zip"] = (Encoding.UTF8.GetBytes("not a zip"), "v1");
        if (failFetch) source.FailFetch.Add("a.zip");

        var report = await Pipeline(source, new Exporter(), new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: new Versions(),
            Sync: new SyncOptions(CollectSourceRefs: true))).RunAsync();

        Assert.True(report.SourceRefsTruncated); // absent children must not look deleted
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_REFS_INCOMPLETE" && i.SourceRef == "s3://bucket/a.zip");
    }

    [Fact]
    public async Task A_failed_archive_in_a_full_incremental_run_marks_the_refs_incomplete()
    {
        var source = new Source();
        source.Items["a.zip"] = (Encoding.UTF8.GetBytes("x"), "v1");
        source.FailFetch.Add("a.zip");

        var report = await Pipeline(source, new Exporter(), new PipelineOptions(Expander: new ArchiveExpander(), VersionStore: new Versions(),
            SyncStore: new FileSyncCursorStore(_cursorFile), Sync: new SyncOptions(CollectSourceRefs: true))).RunAsync();

        Assert.True(report.FullRun);
        Assert.True(report.SourceRefsTruncated);
    }

    [Fact]
    public async Task A_retried_invalid_id_stays_incomplete_after_a_full_reset()
    {
        var source = new Source();
        source.Items["a.zip!/report.txt"] = (Encoding.UTF8.GetBytes(Text("payload")), "v1");
        source.FailFetch.Add("a.zip!/report.txt");
        var syncStore = new FileSyncCursorStore(_cursorFile);
        var initialOptions = new PipelineOptions(SyncStore: syncStore);

        var initial = await Pipeline(source, new Exporter(), initialOptions).RunAsync();

        Assert.Equal(1, initial.RetriesPending);
        source.EmitReset = true;
        var migratedOptions = new PipelineOptions(Expander: new ArchiveExpander(), SyncStore: syncStore,
            Sync: new SyncOptions(CollectSourceRefs: true));

        var report = await Pipeline(source, new Exporter(), migratedOptions).RunAsync();

        Assert.True(report.FullRun);
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_ID_RESERVED");
        Assert.Contains(report.ValidationIssues, i => i.Code == "SOURCE_REFS_INCOMPLETE");
        Assert.DoesNotContain("s3://bucket/a.zip!/report.txt", report.SeenSourceRefs!);
        Assert.True(report.SourceRefsTruncated);
    }
}
