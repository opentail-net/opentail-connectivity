namespace OpenTail.Connectivity.Tests;

public sealed class SiteCrawlRetryTests
{
    [Fact]
    public async Task FirstFailureThenSuccess_PageCrawled()
    {
        // Handler fails on first call, succeeds on second
        var callCount = 0;
        var handler = new LambdaHandler(_ =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("transient failure");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>Retry success</body></html>", Encoding.UTF8, "text/html")
            };
        });

        var connector = new SiteCrawlConnector(new HttpClient(handler), "http://test.site/", "t",
            new WebCrawlOptions(MaxRetries: 1, RetryDelay: TimeSpan.FromMilliseconds(1)));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
        Assert.Equal(2, callCount); // first failed, second succeeded
    }

    [Fact]
    public async Task AllRetriesExhausted_PageSkipped()
    {
        var handler = new LambdaHandler(_ => throw new HttpRequestException("always fails"));

        var connector = new SiteCrawlConnector(new HttpClient(handler), "http://test.site/", "t",
            new WebCrawlOptions(MaxRetries: 2, RetryDelay: TimeSpan.FromMilliseconds(1)));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Empty(items); // page should be skipped after all retries
    }

    [Fact]
    public async Task ZeroRetries_OriginalBehavior()
    {
        var handler = new LambdaHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body>OK</body></html>", Encoding.UTF8, "text/html")
            });

        var connector = new SiteCrawlConnector(new HttpClient(handler), "http://test.site/", "t",
            new WebCrawlOptions(MaxRetries: 0));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
