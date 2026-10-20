namespace OpenTail.Connectivity.Tests;

public sealed class SitemapTests
{
    private static HttpClient MakeClient(Dictionary<string, string> responses)
    {
        var handler = new MapHandler(responses);
        return new HttpClient(handler);
    }

    private static readonly string SimpleSitemap = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>http://site.test/page1</loc></url>
          <url><loc>http://site.test/page2</loc></url>
          <url><loc>http://site.test/page3</loc></url>
        </urlset>
        """;

    private static readonly string SitemapIndex = """
        <?xml version="1.0" encoding="UTF-8"?>
        <sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <sitemap><loc>http://site.test/sitemap-1.xml</loc></sitemap>
        </sitemapindex>
        """;

    private static readonly string NestedSitemap = """
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url><loc>http://site.test/nested/page</loc></url>
        </urlset>
        """;

    [Fact]
    public async Task Sitemap_DiscoversPagesFromSitemap()
    {
        var client = MakeClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://site.test/sitemap.xml"] = SimpleSitemap,
            ["http://site.test/page1"] = "<html><body>Page 1</body></html>",
            ["http://site.test/page2"] = "<html><body>Page 2</body></html>",
            ["http://site.test/page3"] = "<html><body>Page 3</body></html>",
        });

        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 10, UseSitemap: true));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Id.Contains("page1"));
    }

    [Fact]
    public async Task SitemapIndex_FollowsNestedSitemap()
    {
        var client = MakeClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://site.test/sitemap.xml"] = SitemapIndex,
            ["http://site.test/sitemap-1.xml"] = NestedSitemap,
            ["http://site.test/nested/page"] = "<html><body>Nested page</body></html>",
        });

        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 10, UseSitemap: true));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
        Assert.Contains("nested", items[0].Id);
    }

    [Fact]
    public async Task SitemapFetchFailure_FallsBackToLinkCrawl()
    {
        // No sitemap.xml available — should fall back to crawling from home page
        var client = MakeClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://site.test/"] = "<html><body><p>Home page content</p></body></html>",
            // sitemap.xml returns 404 (not in dict → MapHandler returns NotFound)
        });

        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 5, UseSitemap: true));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items); // home page crawled as fallback
    }

    [Fact]
    public async Task MaxPages_RespectedWithSitemap()
    {
        var client = MakeClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://site.test/sitemap.xml"] = SimpleSitemap,
            ["http://site.test/page1"] = "<html><body>1</body></html>",
            ["http://site.test/page2"] = "<html><body>2</body></html>",
            ["http://site.test/page3"] = "<html><body>3</body></html>",
        });

        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 2, UseSitemap: true));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.True(items.Count <= 2);
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses;
        public MapHandler(Dictionary<string, string> responses) => _responses = responses;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri.TrimEnd('/');
            if (_responses.TryGetValue(url, out var body) || _responses.TryGetValue(url + "/", out body))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent(body, Encoding.UTF8, "text/html") });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
