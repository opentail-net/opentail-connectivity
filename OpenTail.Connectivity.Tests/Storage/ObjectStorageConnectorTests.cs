using System.IO.Compression;
using System.Text;
using System.Text.Json;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Export;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Pipeline;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class ObjectStorageConnectorTests
{
    [Fact]
    public async Task First_run_yields_filtered_objects_and_checkpoints_each_page_then_commits_max_timestamp()
    {
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("docs/a.txt", "a", DateTimeOffset.Parse("2026-10-01T10:00:00Z"))
            .Put("docs/b.txt", "b", DateTimeOffset.Parse("2026-10-01T11:00:00Z"))
            .Put("docs/c.txt", "c", DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(Prefix: "docs/", PageSize: 2));

        var changes = await CollectAsync(connector.GetChangesAsync(null));

        Assert.Equal(3, changes.OfType<SourceChange.Upsert>().Count());
        var checkpoints = changes.OfType<SourceChange.Checkpoint>().ToList();
        Assert.Equal(2, checkpoints.Count);
        Assert.Contains("2026-10-01T12:00:00", checkpoints[^1].Cursor);
        Assert.False(connector.ReportsDeletions);
    }

    [Fact]
    public async Task Watermark_overlap_yields_inside_and_boundary_objects_but_not_older_objects()
    {
        var watermark = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("docs/anchor.txt", "a", watermark);
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(Prefix: "docs/"));
        var first = await CollectAsync(connector.GetChangesAsync(null));
        var cursor = first.OfType<SourceChange.Checkpoint>().Last().Cursor;

        client.Put("docs/old.txt", "old", watermark.AddHours(-1).AddTicks(-1));
        client.Put("docs/inside.txt", "inside", watermark.AddMinutes(-30));
        client.Put("docs/boundary.txt", "boundary", watermark);
        var next = await CollectAsync(connector.GetChangesAsync(cursor));

        Assert.Equal(new[] { "anchor.txt", "boundary.txt", "inside.txt" }, next.OfType<SourceChange.Upsert>().Select(c => c.Item.DisplayName).OrderBy(x => x));
    }

    [Fact]
    public async Task Resumed_mid_page_skips_through_last_key_and_continues_after_it()
    {
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("a.txt", "aa", DateTimeOffset.UnixEpoch.AddHours(1))
            .Put("b.txt", "bb", DateTimeOffset.UnixEpoch.AddHours(2))
            .Put("c.txt", "cc", DateTimeOffset.UnixEpoch.AddHours(3));
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(PageSize: 2, MaxBytesPerRun: 3));

        var first = await CollectAsync(connector.GetChangesAsync(null));
        var midCursor = first.OfType<SourceChange.Checkpoint>().Single().Cursor;
        Assert.Equal("b.txt", JsonDocument.Parse(midCursor).RootElement.GetProperty("lastKey").GetString());

        var resumed = await CollectAsync(connector.GetChangesAsync(midCursor));
        Assert.Equal(["c.txt"], resumed.OfType<SourceChange.Upsert>().Select(c => c.Item.DisplayName));
    }

    [Fact]
    public async Task Skips_placeholders_restore_needed_and_cold_objects_unless_enabled()
    {
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("p/ok.txt", "ok")
            .Put("p/dir/", Array.Empty<byte>())
            .Put("p/archive.txt", Encoding.UTF8.GetBytes("archive"), needsRestore: true)
            .Put("p/cold.txt", Encoding.UTF8.GetBytes("cold"), costsExtraToRead: true);

        var defaultConnector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(Prefix: "p/"));
        var defaultItems = await CollectAsync(defaultConnector.DiscoverAsync());
        Assert.Equal(["ok.txt"], defaultItems.Select(i => i.DisplayName));

        var coldConnector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(Prefix: "p/", ReadColdStorage: true));
        var coldItems = await CollectAsync(coldConnector.DiscoverAsync());
        Assert.Equal(new[] { "cold.txt", "ok.txt" }, coldItems.Select(i => i.DisplayName).OrderBy(x => x));
    }

    [Fact]
    public async Task Relative_filter_and_size_cap_are_applied_and_source_metadata_uses_etag_as_version()
    {
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("root/keep/a.txt", "123", DateTimeOffset.UnixEpoch.AddDays(1))
            .Put("root/drop/b.txt", "b", DateTimeOffset.UnixEpoch.AddDays(2))
            .Put("root/keep/large.txt", "12345", DateTimeOffset.UnixEpoch.AddDays(3));
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(
            Prefix: "root/",
            Filter: new FileFilterOptions(IncludePatterns: ["keep/**"], MaxFileSizeBytes: 4)));

        var item = Assert.Single(await CollectAsync(connector.DiscoverAsync()));

        Assert.Equal("memory://bucket/root/keep/a.txt", item.Id);
        Assert.Equal("a.txt", item.DisplayName);
        Assert.Equal("txt", item.Extension);
        Assert.Equal(item.Metadata!["storage.etag"], item.Metadata[SourceKeys.Version]);
        Assert.Equal("bucket/root", item.Metadata[SourceKeys.Container]);
        Assert.Equal("false", item.Metadata[SourceKeys.AclCaptured]);
        Assert.Equal(item.Id, item.Metadata[SourceKeys.Url]);
    }

    [Fact]
    public async Task Byte_budget_stops_after_crossing_object_and_next_run_resumes_after_it()
    {
        var client = new InMemoryObjectStoreClient("bucket")
            .Put("a.txt", "aa", DateTimeOffset.UnixEpoch.AddHours(1))
            .Put("b.txt", "bbb", DateTimeOffset.UnixEpoch.AddHours(2))
            .Put("c.txt", "c", DateTimeOffset.UnixEpoch.AddHours(3));
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(MaxBytesPerRun: 4, PageSize: 10));

        var first = await CollectAsync(connector.GetChangesAsync(null));
        Assert.Equal(new[] { "a.txt", "b.txt" }, first.OfType<SourceChange.Upsert>().Select(c => c.Item.DisplayName));
        var stopped = JsonDocument.Parse(first.OfType<SourceChange.Checkpoint>().Single().Cursor).RootElement;
        Assert.Equal("b.txt", stopped.GetProperty("lastKey").GetString());

        var second = await CollectAsync(connector.GetChangesAsync(first.OfType<SourceChange.Checkpoint>().Single().Cursor));
        Assert.Equal(["c.txt"], second.OfType<SourceChange.Upsert>().Select(c => c.Item.DisplayName));
    }

    [Fact]
    public async Task File_version_store_skips_fetch_for_unchanged_object_and_full_run_collects_all_refs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "otail-object-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var client = new InMemoryObjectStoreClient("bucket")
                .Put("a.txt", "hello", DateTimeOffset.UnixEpoch.AddDays(2));
            var connector = new ObjectStorageConnector(client, "tenant");
            var pipeline = new ConnectivityPipeline(connector, new StubExtractor(), [new RecordingExporter()], new PipelineOptions(
                SyncStore: new FileSyncCursorStore(Path.Combine(dir, "cursor.json")),
                Sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true),
                VersionStore: new FileItemVersionStore(Path.Combine(dir, "versions.json"))));

            var first = await pipeline.RunAsync();
            var second = await pipeline.RunAsync();

            Assert.Single(first.SeenSourceRefs!);
            Assert.Equal(1, second.ItemsUnchanged);
            Assert.Equal(1, client.Calls.Count(c => c == "read:a.txt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Full_discovery_and_incremental_first_run_yield_the_same_ids()
    {
        var client = new InMemoryObjectStoreClient()
            .Put("a.txt", "a")
            .Put("b.txt", "b");
        var connector = new ObjectStorageConnector(client, "tenant");

        var items = await CollectAsync(connector.DiscoverAsync());
        var changes = await CollectAsync(connector.GetChangesAsync(null));
        Assert.Equal(items.Select(i => i.Id).OrderBy(x => x),
            changes.OfType<SourceChange.Upsert>().Select(i => i.Item.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Fetch_throws_for_truncation_and_missing_objects()
    {
        var client = new InMemoryObjectStoreClient().Put("large.txt", "123456");
        var connector = new ObjectStorageConnector(client, "tenant", new ObjectStorageOptions(Filter: new FileFilterOptions(MaxFileSizeBytes: 4)));
        var item = new SourceItem("memory://memory/large.txt", "large.txt", "txt", 6, DateTimeOffset.UnixEpoch, "tenant");

        await Assert.ThrowsAsync<InvalidDataException>(() => connector.FetchAsync(item));
        client.Remove("large.txt");
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => connector.FetchAsync(item));
    }

    [Fact]
    public async Task Fetch_decompresses_gzip_and_rejects_expansion_over_the_size_cap()
    {
        var compressed = Gzip(Encoding.UTF8.GetBytes("hello from gzip"));
        var client = new InMemoryObjectStoreClient().Put("hello.txt.gz", compressed);
        var connector = new ObjectStorageConnector(client, "tenant");
        var item = Assert.Single(await CollectAsync(connector.DiscoverAsync()));

        Assert.Equal("hello.txt", item.DisplayName);
        Assert.Equal("txt", item.Extension);
        Assert.Equal("hello from gzip", Encoding.UTF8.GetString(await connector.FetchAsync(item)));

        var expanded = Gzip(Encoding.UTF8.GetBytes(new string('x', 1000)));
        var cappedClient = new InMemoryObjectStoreClient().Put("large.txt.gz", expanded);
        var capped = new ObjectStorageConnector(cappedClient, "tenant", new ObjectStorageOptions(Filter: new FileFilterOptions(MaxFileSizeBytes: 100)));
        var cappedItem = Assert.Single(await CollectAsync(capped.DiscoverAsync()));
        await Assert.ThrowsAsync<InvalidDataException>(() => capped.FetchAsync(cappedItem));
    }

    [Fact]
    public async Task Connector_passes_source_conformance()
    {
        await SourceConnectorConformance.RunAsync(new InMemoryFixture());
    }

    private static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(input);
        return output.ToArray();
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }

    private static async Task<List<SourceChange>> CollectAsync(IAsyncEnumerable<SourceChange> stream) =>
        await CollectAsync<SourceChange>(stream);

    private sealed class StubExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class RecordingExporter : IConnectivityExporter
    {
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class InMemoryFixture : IConnectorFixture
    {
        private readonly InMemoryObjectStoreClient _objects = new InMemoryObjectStoreClient()
            .Put("a.txt", "alpha", DateTimeOffset.UnixEpoch.AddDays(1))
            .Put("b.txt", "bravo", DateTimeOffset.UnixEpoch.AddDays(2));

        public ISourceConnector Create() => new ObjectStorageConnector(_objects, "tenant");
        public ISourceConnector CreateEmpty() => new ObjectStorageConnector(new InMemoryObjectStoreClient(), "tenant");
        public IReadOnlyList<string> SecretsGiven => [];
        public IReadOnlySet<int>? SkippedChecks => new HashSet<int> { 8 }; // Watermark overlap deliberately replays unchanged objects.

        public Task RemoveOneItemAsync(string sourceRef)
        {
            var key = sourceRef["memory://memory/".Length..];
            _objects.Remove(key);
            return Task.CompletedTask;
        }
    }
}
