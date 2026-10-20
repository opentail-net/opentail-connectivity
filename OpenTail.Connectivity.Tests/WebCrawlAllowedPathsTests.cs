using System.Net;
using System.Net.Http;

namespace OpenTail.Connectivity.Tests;

public sealed class WebCrawlAllowedPathsTests
{
    private static readonly Dictionary<string, string> Pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["https://example.com/"] = """
            <html><head><title>Home</title></head><body>
            <a href="/docs/guide">Docs</a>
            <a href="/blog/post">Blog</a>
            </body></html>
            """,
        ["https://example.com/docs/guide"] = "<html><head><title>Guide</title></head><body>Doc content.</body></html>",
        ["https://example.com/blog/post"] = "<html><head><title>Post</title></head><body>Blog content.</body></html>",
    };

    private static HttpClient MakeClient() => new HttpClient(new MultiPageHandler(Pages));

    [Fact]
    public async Task NoAllowedPaths_FollowsAllLinks()
    {
        // OO.2: without filter, all same-domain links are followed
        var connector = new SiteCrawlConnector(MakeClient(), "https://example.com/",
            "t", new WebCrawlOptions(MaxPages: 10));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task AllowedPaths_RestrictionFiltersLinks()
    {
        // OO.2: with /docs/ restriction, only /docs/* pages are followed
        var connector = new SiteCrawlConnector(MakeClient(), "https://example.com/",
            "t", new WebCrawlOptions(MaxPages: 10, AllowedPathPrefixes: ["/docs/"]));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        // Start URL (/) is always crawled; only /docs/* links are followed from there
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Id == "https://example.com/");
        Assert.Contains(items, i => i.Id == "https://example.com/docs/guide");
        Assert.DoesNotContain(items, i => i.Id == "https://example.com/blog/post");
    }

    private sealed class MultiPageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _pages;
        public MultiPageHandler(Dictionary<string, string> pages) => _pages = pages;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri.TrimEnd('/');
            var key = _pages.Keys.FirstOrDefault(k =>
                string.Equals(k.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase))
                ?? url + "/";
            if (!_pages.TryGetValue(key, out var html))
                html = $"<html><head><title>{url}</title></head><body>Page.</body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html")
            });
        }
    }
}
