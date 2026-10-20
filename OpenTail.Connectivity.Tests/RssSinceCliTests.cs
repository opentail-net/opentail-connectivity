using System.Net;

namespace OpenTail.Connectivity.Tests;

public sealed class RssSinceCliTests
{
    private static readonly string FeedXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Test Feed</title>
            <item>
              <title>Old Item</title>
              <link>https://example.com/old</link>
              <description>An older item that predates the cutoff date.</description>
              <pubDate>Mon, 01 Jan 2026 10:00:00 +0000</pubDate>
            </item>
            <item>
              <title>New Item</title>
              <link>https://example.com/new</link>
              <description>A newer item that is on or after the cutoff date.</description>
              <pubDate>Wed, 15 Jan 2026 10:00:00 +0000</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private static HttpClient MakeClient() =>
        new HttpClient(new FeedStubHandler(FeedXml));

    [Fact]
    public async Task Since_FiltersOldItems_ThroughPipeline()
    {
        var outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "docs.jsonl");

        var since = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var connector = new RssFeedConnector(MakeClient(), "http://feed.test/rss", "test", since: since);
        var extractor = new RssFeedTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        try
        {
            var pipeline = new ConnectivityPipeline(connector, extractor, [exporter]);
            var report = await pipeline.RunAsync();
            Assert.Equal(1, report.ItemsProcessed);
        }
        finally
        {
            await exporter.DisposeAsync();
            Directory.Delete(outDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoSince_AllItemsIncluded_ThroughPipeline()
    {
        var outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "docs.jsonl");

        var connector = new RssFeedConnector(MakeClient(), "http://feed.test/rss", "test");
        var extractor = new RssFeedTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        try
        {
            var pipeline = new ConnectivityPipeline(connector, extractor, [exporter]);
            var report = await pipeline.RunAsync();
            Assert.Equal(2, report.ItemsProcessed);
        }
        finally
        {
            await exporter.DisposeAsync();
            Directory.Delete(outDir, recursive: true);
        }
    }

    private sealed class FeedStubHandler : HttpMessageHandler
    {
        private readonly string _xml;
        public FeedStubHandler(string xml) => _xml = xml;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_xml, System.Text.Encoding.UTF8, "application/rss+xml")
            });
    }
}
