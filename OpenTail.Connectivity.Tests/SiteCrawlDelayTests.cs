namespace OpenTail.Connectivity.Tests;

public sealed class SiteCrawlDelayTests
{
    private static HttpClient MakeClient(params (string url, string html)[] pages)
    {
        var dict = pages.ToDictionary(p => p.url, p => p.html, StringComparer.OrdinalIgnoreCase);
        return new HttpClient(new DelayTestHandler(dict));
    }

    [Fact]
    public async Task ZeroDelay_CrawlsNormally()
    {
        var client = MakeClient(
            ("http://site.test/", "<html><body>Page one</body></html>"));
        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 5, RequestDelay: null));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
    }

    [Fact]
    public async Task PositiveDelay_InsertedBetweenPages()
    {
        // Two pages linked together; with a 1ms delay both should still be crawled
        var client = MakeClient(
            ("http://site.test/", "<html><body><a href='http://site.test/p2'>p2</a></body></html>"),
            ("http://site.test/p2", "<html><body>Page two</body></html>"));
        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 5, RequestDelay: TimeSpan.FromMilliseconds(1)));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task CancellationDuringDelay_Propagates()
    {
        // Large delay that exceeds our short timeout — cancellation should propagate
        var client = MakeClient(
            ("http://site.test/", "<html><body><a href='http://site.test/p2'>p2</a></body></html>"),
            ("http://site.test/p2", "<html><body>Page two</body></html>"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var connector = new SiteCrawlConnector(client, "http://site.test/", "tenant",
            new WebCrawlOptions(MaxPages: 10, RequestDelay: TimeSpan.FromSeconds(30)));

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in connector.DiscoverAsync(cts.Token)) { }
        });

        Assert.NotNull(ex);
        Assert.True(ex is OperationCanceledException or TaskCanceledException);
    }

    private sealed class DelayTestHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;
        public DelayTestHandler(Dictionary<string, string> pages) => _pages = pages;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri.TrimEnd('/');
            if (_pages.TryGetValue(url, out var html) || _pages.TryGetValue(url + "/", out html))
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    { Content = new StringContent(html, Encoding.UTF8, "text/html") });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
