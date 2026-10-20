namespace OpenTail.Connectivity.Tests;

public sealed class SiteCrawlConnectorTests
{
    // Builds an HttpClient backed by a fake handler that returns the given pages.
    private static HttpClient BuildClient(Dictionary<string, string> pages)
    {
        var handler = new FakeHttpHandler(pages);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task DiscoverAsync_stays_on_same_domain()
    {
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] =
                "<html><body><a href='http://example.com/page1'>p1</a>" +
                "<a href='http://other.com/evil'>evil</a></body></html>",
            ["http://example.com/page1"] = "<html><body>Page 1</body></html>",
        };

        var connector = new SiteCrawlConnector(BuildClient(pages), "http://example.com/", "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.All(items, i => Assert.StartsWith("http://example.com", i.Id));
        Assert.DoesNotContain(items, i => i.Id.Contains("other.com"));
    }

    [Fact]
    public async Task DiscoverAsync_respects_max_pages()
    {
        // Start page links to 10 sub-pages
        var links = string.Concat(Enumerable.Range(1, 10)
            .Select(n => $"<a href='http://example.com/p{n}'>p{n}</a>"));
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] = $"<html><body>{links}</body></html>",
        };
        for (var i = 1; i <= 10; i++)
            pages[$"http://example.com/p{i}"] = $"<html><body>Page {i}</body></html>";

        var options = new WebCrawlOptions(MaxPages: 3);
        var connector = new SiteCrawlConnector(BuildClient(pages), "http://example.com/", "t", options);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.True(items.Count <= 3);
    }

    [Fact]
    public async Task DiscoverAsync_RespectsMaxDepth()
    {
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] = "<html><body><a href='http://example.com/p1'>p1</a></body></html>",
            ["http://example.com/p1"] = "<html><body><a href='http://example.com/p2'>p2</a></body></html>",
            ["http://example.com/p2"] = "<html><body>Page 2</body></html>",
        };

        var connector = new SiteCrawlConnector(
            BuildClient(pages),
            "http://example.com/",
            "t",
            new WebCrawlOptions(MaxPages: 10, MaxDepth: 1));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Contains(items, i => i.Id == "http://example.com/");
        Assert.Contains(items, i => i.Id == "http://example.com/p1");
        Assert.DoesNotContain(items, i => i.Id == "http://example.com/p2");
    }

    [Fact]
    public async Task DiscoverAsync_ResetsFailedFetchCountBetweenRuns()
    {
        var connector = new SiteCrawlConnector(
            BuildClient(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)),
            "http://example.com/",
            "t",
            new WebCrawlOptions(MaxRetries: 0));

        await foreach (var _ in connector.DiscoverAsync()) { }
        Assert.Equal(1, connector.FailedFetchCount);

        await foreach (var _ in connector.DiscoverAsync()) { }
        Assert.Equal(1, connector.FailedFetchCount);
    }

    [Fact]
    public async Task DiscoverAsync_DoesNotRetryPermanentStatusCodes()
    {
        var calls = 0;
        var client = new HttpClient(new CountingStatusHandler(() =>
        {
            calls++;
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }));
        var connector = new SiteCrawlConnector(
            client,
            "http://example.com/",
            "t",
            new WebCrawlOptions(
                MaxRetries: 3,
                RetryDelay: TimeSpan.FromMilliseconds(1),
                RespectRobotsTxt: false));

        await foreach (var _ in connector.DiscoverAsync()) { }

        Assert.Equal(1, calls);
        Assert.Equal(1, connector.FailedFetchCount);
    }

    [Fact]
    public async Task DiscoverAsync_LimitsHtmlScannedForLinks()
    {
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] = "<html><body>" + new string('x', 128) +
                                      "<a href='http://example.com/late'>late</a></body></html>",
            ["http://example.com/late"] = "<html><body>late</body></html>",
        };

        var connector = new SiteCrawlConnector(
            BuildClient(pages),
            "http://example.com/",
            "t",
            new WebCrawlOptions(MaxPages: 10, MaxLinkExtractionBytes: 64));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.DoesNotContain(items, i => i.Id == "http://example.com/late");
    }

    [Fact]
    public async Task DiscoverAsync_skips_binary_extensions()
    {
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] =
                "<html><body>" +
                "<a href='http://example.com/doc.pdf'>pdf</a>" +
                "<a href='http://example.com/img.jpg'>img</a>" +
                "<a href='http://example.com/about'>about</a>" +
                "</body></html>",
            ["http://example.com/about"] = "<html><body>About us</body></html>",
        };

        var connector = new SiteCrawlConnector(BuildClient(pages), "http://example.com/", "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.DoesNotContain(items, i => i.Id.EndsWith(".pdf"));
        Assert.DoesNotContain(items, i => i.Id.EndsWith(".jpg"));
    }

    [Fact]
    public async Task FetchAsync_UsesCache_DoesNotReissueHttpRequest()
    {
        var fetchCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var pages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["http://example.com/"] = "<html><title>Home</title><body>content</body></html>",
        };
        var handler = new CountingFakeHandler(pages, fetchCount);
        var client = new HttpClient(handler);
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Single(items);

        // FetchAsync should use cache — no new HTTP request
        var beforeFetch = fetchCount.GetValueOrDefault("http://example.com/", 0);
        var bytes = await connector.FetchAsync(items[0]);
        var afterFetch = fetchCount.GetValueOrDefault("http://example.com/", 0);

        Assert.True(bytes.Length > 0);
        Assert.Equal(beforeFetch, afterFetch); // no additional network hit
    }

    private sealed class CountingFakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;
        private readonly Dictionary<string, int> _counts;
        public CountingFakeHandler(Dictionary<string, string> pages, Dictionary<string, int> counts)
        {
            _pages = pages;
            _counts = counts;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri.TrimEnd('/');
            _counts[url] = _counts.GetValueOrDefault(url) + 1;
            if (_pages.TryGetValue(url, out var html) || _pages.TryGetValue(url + "/", out html))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent(html, Encoding.UTF8, "text/html") });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;
        public FakeHttpHandler(Dictionary<string, string> pages) => _pages = pages;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri.TrimEnd('/');
            if (_pages.TryGetValue(url, out var html) ||
                _pages.TryGetValue(url + "/", out html))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                });
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private sealed class CountingStatusHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _responseFactory;

        public CountingStatusHandler(Func<HttpResponseMessage> responseFactory)
            => _responseFactory = responseFactory;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_responseFactory());
    }
}
