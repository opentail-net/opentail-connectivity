using System.Net;
using System.Text;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Web;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class WebConnectorsConformanceTests
{
    private sealed class RssFixture : IConnectorFixture
    {
        private bool _removed;
        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create()
        {
            var xml = _removed ? """
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0">
                  <channel>
                    <title>Test Feed</title>
                    <item>
                      <title>Post 2</title>
                      <link>https://example.com/2</link>
                      <guid>guid-2</guid>
                      <pubDate>Mon, 01 Jan 2026 01:00:00 GMT</pubDate>
                    </item>
                  </channel>
                </rss>
                """ : """
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0">
                  <channel>
                    <title>Test Feed</title>
                    <item>
                      <title>Post 1</title>
                      <link>https://example.com/1</link>
                      <guid>guid-1</guid>
                      <pubDate>Mon, 01 Jan 2026 00:00:00 GMT</pubDate>
                    </item>
                    <item>
                      <title>Post 2</title>
                      <link>https://example.com/2</link>
                      <guid>guid-2</guid>
                      <pubDate>Mon, 01 Jan 2026 01:00:00 GMT</pubDate>
                    </item>
                  </channel>
                </rss>
                """;

            var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
            return new RssFeedConnector(new HttpClient(handler), "https://example.com/feed", "tenant-rss");
        }

        public ISourceConnector CreateEmpty()
        {
            var xml = """
                <?xml version="1.0" encoding="utf-8"?>
                <rss version="2.0"><channel><title>Empty</title></channel></rss>
                """;
            var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(xml, Encoding.UTF8, "application/xml")
            });
            return new RssFeedConnector(new HttpClient(handler), "https://example.com/empty", "tenant-rss");
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class SiteCrawlFixture : IConnectorFixture
    {
        private bool _removed;
        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create()
        {
            var handler = new StubHandler(req =>
            {
                var path = req.RequestUri?.AbsolutePath;
                if (path == "/robots.txt")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path == "/sitemap.xml")
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (path == "/" || path == "")
                {
                    var html = "<html><head><title>Home</title></head><body><a href=\"/p2\">Page 2</a></body></html>";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
                }
                if (path == "/p2")
                {
                    if (_removed)
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    var html = "<html><head><title>Page 2</title></head><body>Welcome to page two</body></html>";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var opts = new WebCrawlOptions(UseSitemap: false, RespectRobotsTxt: false, MaxPages: 5, MaxDepth: 2);
            return new SiteCrawlConnector(new HttpClient(handler), "https://example.com/", "tenant-crawl", opts);
        }

        public ISourceConnector CreateEmpty()
        {
            var handler = new StubHandler(req => new HttpResponseMessage(HttpStatusCode.NotFound));
            var opts = new WebCrawlOptions(UseSitemap: false, RespectRobotsTxt: false);
            return new SiteCrawlConnector(new HttpClient(handler), "https://example.com/notfound", "tenant-crawl", opts);
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handle(request));
    }

    [Fact]
    public async Task RssFeedConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new RssFixture());
    }

    [Fact]
    public async Task SiteCrawlConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new SiteCrawlFixture());
    }
}
