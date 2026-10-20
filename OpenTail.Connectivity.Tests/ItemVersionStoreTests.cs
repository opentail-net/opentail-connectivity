using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Export;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Pipeline;
using OpenTail.Connectivity.Web;

namespace OpenTail.Connectivity.Tests;

public sealed class ItemVersionStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-ver-" + Guid.NewGuid().ToString("N"));

    public ItemVersionStoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class VersionConnector(Func<IEnumerable<SourceItem>> getItems) : ISourceConnector
    {
        public List<string> FetchedIds { get; } = [];

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var item in getItems()) yield return item;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            FetchedIds.Add(item.Id);
            return Task.FromResult(Encoding.UTF8.GetBytes("Content of " + item.Id + new string('.', 50)));
        }
    }

    private sealed class StubExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class RecordingExporter(string? failOn = null) : IConnectivityExporter
    {
        public List<string> ExportedIds { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
        {
            if (document.SourceRef == failOn) throw new IOException("export failed");
            ExportedIds.Add(document.SourceRef);
            return Task.CompletedTask;
        }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Pipeline_skips_unchanged_version_and_fetches_new_version()
    {
        var storeFile = Path.Combine(_dir, "versions.json");
        var versionStore = new FileItemVersionStore(storeFile);

        string currentVersion = "v1";
        var connector = new VersionConnector(() =>
        [
            new SourceItem("item1", "item1.txt", "txt", 10, DateTimeOffset.UtcNow, "acme",
                Metadata: new Dictionary<string, string> { [SourceKeys.Version] = currentVersion })
        ]);

        var options = new PipelineOptions(VersionStore: versionStore);
        var pipeline = new ConnectivityPipeline(connector, new StubExtractor(), [new RecordingExporter()], options);

        // Run 1: v1
        var report1 = await pipeline.RunAsync();
        Assert.Equal(1, report1.ItemsProcessed);
        Assert.Equal(0, report1.ItemsUnchanged);
        Assert.Equal(["item1"], connector.FetchedIds);

        // Run 2: v1 again
        connector.FetchedIds.Clear();
        var report2 = await pipeline.RunAsync();
        Assert.Equal(0, report2.ItemsProcessed);
        Assert.Equal(1, report2.ItemsSkipped);
        Assert.Equal(1, report2.ItemsUnchanged);
        Assert.Empty(connector.FetchedIds);

        // Run 3: v2
        currentVersion = "v2";
        connector.FetchedIds.Clear();
        var report3 = await pipeline.RunAsync();
        Assert.Equal(1, report3.ItemsProcessed);
        Assert.Equal(0, report3.ItemsUnchanged);
        Assert.Equal(["item1"], connector.FetchedIds);
    }

    [Fact]
    public async Task Item_without_version_is_always_fetched()
    {
        var storeFile = Path.Combine(_dir, "versions.json");
        var versionStore = new FileItemVersionStore(storeFile);

        var connector = new VersionConnector(() =>
        [
            new SourceItem("item1", "item1.txt", "txt", 10, DateTimeOffset.UtcNow, "acme")
        ]);

        var options = new PipelineOptions(VersionStore: versionStore);
        var pipeline = new ConnectivityPipeline(connector, new StubExtractor(), [new RecordingExporter()], options);

        var report1 = await pipeline.RunAsync();
        Assert.Equal(["item1"], connector.FetchedIds);

        connector.FetchedIds.Clear();
        var report2 = await pipeline.RunAsync();
        Assert.Equal(["item1"], connector.FetchedIds);
        Assert.Equal(0, report2.ItemsUnchanged);
    }

    [Fact]
    public async Task Failed_export_does_not_store_the_version()
    {
        var storeFile = Path.Combine(_dir, "versions.json");
        var versionStore = new FileItemVersionStore(storeFile);

        var connector = new VersionConnector(() =>
        [
            new SourceItem("item1", "item1.txt", "txt", 10, DateTimeOffset.UtcNow, "acme",
                Metadata: new Dictionary<string, string> { [SourceKeys.Version] = "v1" })
        ]);

        var options = new PipelineOptions(VersionStore: versionStore);
        var exporter = new RecordingExporter(failOn: "item1");
        var pipeline = new ConnectivityPipeline(connector, new StubExtractor(), [exporter], options);

        var report = await pipeline.RunAsync();
        Assert.Equal(1, report.ItemsFailed);

        var docId = ConnectivityPipeline.DocumentId("file", "acme", "item1");
        Assert.Null(await versionStore.GetAsync(docId));
    }

    [Fact]
    public async Task FileItemVersionStore_round_trips_across_instances()
    {
        var path = Path.Combine(_dir, "roundtrip.json");
        var store1 = new FileItemVersionStore(path);
        await store1.SetAsync("doc1", "ver1");
        await store1.SetAsync("doc2", "ver2");
        await store1.FlushAsync();

        var store2 = new FileItemVersionStore(path);
        Assert.Equal("ver1", await store2.GetAsync("doc1"));
        Assert.Equal("ver2", await store2.GetAsync("doc2"));
        Assert.Null(await store2.GetAsync("doc3"));

        await store2.RemoveAsync("doc1");
        await store2.FlushAsync();

        var store3 = new FileItemVersionStore(path);
        Assert.Null(await store3.GetAsync("doc1"));
        Assert.Equal("ver2", await store3.GetAsync("doc2"));
    }

    [Fact]
    public async Task Rss_sets_the_version_from_guid_and_updated()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0">
              <channel>
                <title>Test Feed</title>
                <item>
                  <title>Post 1</title>
                  <link>https://example.com/1</link>
                  <guid>guid-123</guid>
                  <pubDate>Mon, 01 Jan 2026 00:00:00 GMT</pubDate>
                </item>
              </channel>
            </rss>
            """;

        using var handler = new StaticHttpHandler(xml);
        using var client = new HttpClient(handler);
        var connector = new RssFeedConnector(client, "https://example.com/feed", "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var item1 = Assert.Single(items);
        Assert.NotNull(item1.Metadata);
        Assert.True(item1.Metadata.TryGetValue(SourceKeys.Version, out var version));
        Assert.Equal(ExpectedRssVersion("guid-123", "https://example.com/1", "Post 1", "", "", "Mon, 01 Jan 2026 00:00:00 GMT"), version);
    }

    [Fact]
    public async Task Rss_version_changes_when_description_changes_and_is_stable_for_identical_items()
    {
        var original = RssFeed("Before");
        var same = RssFeed("Before");
        var edited = RssFeed("After");

        var originalVersion = await ReadRssVersionAsync(original);
        var sameVersion = await ReadRssVersionAsync(same);
        var editedVersion = await ReadRssVersionAsync(edited);

        Assert.Equal(originalVersion, sameVersion);
        Assert.NotEqual(originalVersion, editedVersion);
    }

    [Fact]
    public async Task Pipeline_exports_Rss_item_again_when_description_changes()
    {
        var description = "Before";
        using var handler = new MutableRssHttpHandler(() => RssFeed(description));
        using var client = new HttpClient(handler);
        var connector = new RssFeedConnector(client, "https://example.com/feed", "tenant");
        var store = new FileItemVersionStore(Path.Combine(_dir, "rss-versions.json"));
        var exporter = new RecordingExporter();
        var pipeline = new ConnectivityPipeline(connector, new RssFeedTextExtractor(), [exporter], new PipelineOptions(VersionStore: store));

        await pipeline.RunAsync();
        description = "After";
        var secondReport = await pipeline.RunAsync();

        Assert.Equal(["https://example.com/1", "https://example.com/1"], exporter.ExportedIds);
        Assert.Equal(0, secondReport.ItemsUnchanged);
    }

    [Fact]
    public async Task Site_crawl_sets_version_from_etag()
    {
        using var handler = new ETagHttpHandler("<html><head><title>Hello</title></head><body>World</body></html>", "\"etag-987\"");
        using var client = new HttpClient(handler);
        using var connector = new SiteCrawlConnector(client, "https://example.com/", "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var item1 = Assert.Single(items);
        Assert.NotNull(item1.Metadata);
        Assert.True(item1.Metadata.TryGetValue(SourceKeys.Version, out var version));
        Assert.Equal("\"etag-987\"", version);
    }

    private sealed class StaticHttpHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/xml")
            };
            return Task.FromResult(res);
        }
    }

    private static string RssFeed(string description) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <item>
              <title>Post 1</title>
              <link>https://example.com/1</link>
              <description>{{description}}</description>
              <content:encoded><![CDATA[full content]]></content:encoded>
              <guid>guid-123</guid>
              <pubDate>Mon, 01 Jan 2026 00:00:00 GMT</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private static async Task<string?> ReadRssVersionAsync(string xml)
    {
        using var handler = new StaticHttpHandler(xml);
        using var client = new HttpClient(handler);
        var connector = new RssFeedConnector(client, "https://example.com/feed", "tenant");
        var item = Assert.Single(await connector.DiscoverAsync().ToListAsync());
        Assert.NotNull(item.Metadata);
        Assert.True(item.Metadata.TryGetValue(SourceKeys.Version, out var version));
        return version;
    }

    private static string ExpectedRssVersion(string guid, string link, string title, string description, string content, string pubDate)
    {
        var fields = string.Join('\u001f', guid, link, title, description, content, pubDate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fields))).ToLowerInvariant()[..32];
    }

    private sealed class MutableRssHttpHandler(Func<string> getContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(getContent(), Encoding.UTF8, "application/xml")
            });
    }

    private sealed class ETagHttpHandler(string html, string etag) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            };
            res.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
            return Task.FromResult(res);
        }
    }
}
