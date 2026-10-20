using System.Net;

namespace OpenTail.Connectivity.Tests;

public sealed class SiteCrawlBoundaryTests
{
    [Fact]
    public async Task DiscoverAsync_does_not_follow_links_on_another_port()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Html(request.RequestUri.AbsolutePath == "/" ? "<a href='http://example.com:8081/other'>other</a>" : "other");
        }));
        var connector = new SiteCrawlConnector(client, "http://example.com:8080/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        await connector.DiscoverAsync().ToListAsync();

        Assert.DoesNotContain("http://example.com:8081/other", calls);
    }

    [Fact]
    public async Task DiscoverAsync_counts_oversized_pages_as_failed()
    {
        using var client = new HttpClient(new BoundaryHandler(_ => Html(new string('x', 5 * 1024 * 1024 + 1))));
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Empty(items);
        Assert.Equal(1, connector.FailedFetchCount);
    }

    [Fact]
    public async Task DiscoverAsync_drops_responses_redirected_off_origin()
    {
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            var response = Html("redirected");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://elsewhere.test/final");
            return response;
        }));
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Empty(items);
        Assert.Equal(1, connector.FailedFetchCount);
    }

    [Theory]
    [InlineData("http://example.com/", "https://example.com/")]
    [InlineData("http://example.com:8080/", "https://example.com:8080/")]
    public async Task DiscoverAsync_accepts_https_upgrade_on_the_same_host_and_port(string startUrl, string finalUrl)
    {
        using var client = new HttpClient(new BoundaryHandler(_ =>
        {
            var response = Html("upgraded");
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl);
            return response;
        }));
        var connector = new SiteCrawlConnector(client, startUrl, "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Single(items);
        Assert.Equal(0, connector.FailedFetchCount);
    }

    [Fact]
    public async Task FetchAsync_rejects_off_origin_cache_miss()
    {
        var calls = 0;
        using var client = new HttpClient(new BoundaryHandler(_ => { calls++; return Html("body"); }));
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t");

        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.FetchAsync(
            new SourceItem("http://elsewhere.test/page", "page", "html", 0, DateTimeOffset.UtcNow, "t")));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_request_off_origin_redirect_target()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Redirect("http://site/", "http://internal/private");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Empty(items);
        Assert.Equal(1, connector.FailedFetchCount);
        Assert.DoesNotContain(calls, uri => new Uri(uri).Host == "internal");
    }

    [Fact]
    public async Task FetchAsync_does_not_request_off_origin_redirect_target()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Redirect("http://site/page", "http://internal/private");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t");

        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.FetchAsync(
            new SourceItem("http://site/page", "page", "html", 0, DateTimeOffset.UtcNow, "t")));
        Assert.DoesNotContain(calls, uri => new Uri(uri).Host == "internal");
    }

    [Fact]
    public async Task Sitemap_does_not_request_off_origin_redirect_target()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/sitemap.xml"
                ? Redirect(request.RequestUri.AbsoluteUri, "http://internal/sitemap.xml")
                : Html("home");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, UseSitemap: true, MaxRetries: 0));

        await connector.DiscoverAsync().ToListAsync();

        Assert.DoesNotContain(calls, uri => new Uri(uri).Host == "internal");
    }

    [Fact]
    public async Task Robots_does_not_request_off_origin_redirect_target()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/robots.txt"
                ? Redirect(request.RequestUri.AbsoluteUri, "http://internal/robots.txt")
                : Html("home");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t",
            new WebCrawlOptions(RespectRobotsTxt: true, MaxRetries: 0));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Single(items);
        Assert.DoesNotContain(calls, uri => new Uri(uri).Host == "internal");
    }

    [Fact]
    public async Task FetchAsync_follows_same_origin_redirect()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/old"
                ? Redirect(request.RequestUri.AbsoluteUri, "/new")
                : Html("new content");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t");

        var bytes = await connector.FetchAsync(new SourceItem("http://site/old", "old", "html", 0, DateTimeOffset.UtcNow, "t"));

        Assert.Equal("new content", Encoding.UTF8.GetString(bytes));
        Assert.Equal(["http://site/old", "http://site/new"], calls);
    }

    [Fact]
    public async Task FetchAsync_stops_after_five_redirects()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            var n = int.Parse(request.RequestUri.AbsolutePath.TrimStart('/'));
            return Redirect(request.RequestUri.AbsoluteUri, $"/{n + 1}");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t");

        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.FetchAsync(
            new SourceItem("http://site/0", "page", "html", 0, DateTimeOffset.UtcNow, "t")));

        Assert.Equal(6, calls.Count);
    }

    [Fact]
    public async Task FetchAsync_follows_same_host_http_to_https_upgrade()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.Scheme == Uri.UriSchemeHttp
                ? Redirect(request.RequestUri.AbsoluteUri, "https://site/page")
                : Html("secure content");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t");

        var bytes = await connector.FetchAsync(new SourceItem("http://site/page", "page", "html", 0, DateTimeOffset.UtcNow, "t"));

        Assert.Equal("secure content", Encoding.UTF8.GetString(bytes));
        Assert.Equal(["http://site/page", "https://site/page"], calls);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_follow_redirect_outside_allowed_path_prefixes()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/docs/a"
                ? Redirect(request.RequestUri.AbsoluteUri, "/admin/x")
                : Html("admin page");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/docs/a", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0, AllowedPathPrefixes: ["/docs/"]));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Empty(items);
        Assert.Equal(1, connector.FailedFetchCount);
        Assert.DoesNotContain(calls, uri => new Uri(uri).AbsolutePath == "/admin/x");
    }

    [Fact]
    public async Task FetchAsync_does_not_follow_redirect_outside_allowed_path_prefixes()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Redirect(request.RequestUri.AbsoluteUri, "/admin/x");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/docs/", "t",
            new WebCrawlOptions(AllowedPathPrefixes: ["/docs/"]));

        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.FetchAsync(
            new SourceItem("http://site/docs/a", "a", "html", 0, DateTimeOffset.UtcNow, "t")));
        Assert.DoesNotContain(calls, uri => new Uri(uri).AbsolutePath == "/admin/x");
    }

    [Fact]
    public async Task FetchAsync_follows_redirect_inside_allowed_path_prefixes()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/docs/old"
                ? Redirect(request.RequestUri.AbsoluteUri, "/docs/new")
                : Html("new docs content");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/docs/", "t",
            new WebCrawlOptions(AllowedPathPrefixes: ["/docs/"]));

        var bytes = await connector.FetchAsync(new SourceItem("http://site/docs/old", "old", "html", 0, DateTimeOffset.UtcNow, "t"));

        Assert.Equal("new docs content", Encoding.UTF8.GetString(bytes));
        Assert.Equal(["http://site/docs/old", "http://site/docs/new"], calls);
    }

    [Fact]
    public async Task FetchAsync_follows_any_same_origin_redirect_without_path_prefixes()
    {
        var calls = new List<string>();
        using var client = new HttpClient(new BoundaryHandler(request =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return request.RequestUri.AbsolutePath == "/docs/a"
                ? Redirect(request.RequestUri.AbsoluteUri, "/admin/x")
                : Html("admin content");
        }));
        var connector = new SiteCrawlConnector(client, "http://site/", "t");

        var bytes = await connector.FetchAsync(new SourceItem("http://site/docs/a", "a", "html", 0, DateTimeOffset.UtcNow, "t"));

        Assert.Equal("admin content", Encoding.UTF8.GetString(bytes));
        Assert.Equal(["http://site/docs/a", "http://site/admin/x"], calls);
    }

    [Fact]
    public async Task DiscoverAsync_caps_sitemap_responses()
    {
        using var client = new HttpClient(new BoundaryHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/sitemap.xml" => Html("<urlset><url><loc>http://example.com/listed</loc></url></urlset>" + new string(' ', 200)),
            "/" => Html("home"),
            _ => Html("listed"),
        }));
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, UseSitemap: true, MaxRetries: 0, MaxResponseBytes: 100, AllowedPathPrefixes: ["/docs/"]));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Equal("http://example.com/", Assert.Single(items).Id);
    }

    [Fact]
    public async Task DiscoverAsync_caps_robots_responses()
    {
        using var client = new HttpClient(new BoundaryHandler(request => request.RequestUri!.AbsolutePath == "/robots.txt"
            ? Html("User-agent: *\nDisallow: /\n" + new string(' ', 200))
            : Html("home")));
        var connector = new SiteCrawlConnector(client, "http://example.com/", "t",
            new WebCrawlOptions(RespectRobotsTxt: true, MaxRetries: 0, MaxResponseBytes: 100));

        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Single(items);
        Assert.Equal("http://example.com/", items[0].Id);
    }

    private static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Redirect(string requestUri, string location) => new(HttpStatusCode.Found)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri),
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) }
    };

    private sealed class BoundaryHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(callback(request));
    }
}
