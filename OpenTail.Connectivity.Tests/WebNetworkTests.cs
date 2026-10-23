using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenTail.Connectivity.Web;

namespace OpenTail.Connectivity.Tests;

/// <summary>The web connectors' network boundary: redirects checked hop by hop, private and local addresses refused unless allowed.</summary>
public sealed class WebNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)] // IPv4-mapped private
    [InlineData("2002:0a00:0001::1", false)] // 6to4 embedding 10.0.0.1
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void Only_public_addresses_count_as_public(string address, bool expected)
        => Assert.Equal(expected, WebNetwork.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task When_the_first_address_does_not_connect_the_next_one_is_tried()
    {
        var tried = new List<IPAddress>();
        var stream = await WebNetwork.ConnectFirstAsync("example.com", [IPAddress.Parse("93.184.216.33"), IPAddress.Parse("93.184.216.34")], 443,
            (address, _, _) =>
            {
                tried.Add(address);
                return tried.Count == 1
                    ? ValueTask.FromException<Stream>(new SocketException((int)SocketError.NetworkUnreachable))
                    : ValueTask.FromResult<Stream>(new MemoryStream());
            }, TestContext.Current.CancellationToken);

        Assert.IsType<MemoryStream>(stream);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), tried[^1]);
    }

    [Fact]
    public async Task When_no_address_connects_the_error_names_the_host()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(async () => await WebNetwork.ConnectFirstAsync("example.com",
            [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("93.184.216.35")], 443,
            (_, _, _) => ValueTask.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)), TestContext.Current.CancellationToken));
        Assert.Contains("example.com", e.Message);
        Assert.Equal(2, Assert.IsType<AggregateException>(e.InnerException).InnerExceptions.Count);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")] // a name that resolves to a private address: the check runs on the address connected to
    public async Task The_crawler_never_connects_to_a_local_address_unless_allowed(string host)
    {
        using var server = new OneShotServer();
        using var crawler = new SiteCrawlConnector($"http://{host}:{server.Port}/", "t", new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0));

        var items = await crawler.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(items);
        Assert.Equal(1, crawler.FailedFetchCount);
        Assert.False(server.WasConnected, "the crawler opened a connection to a local address");
    }

    [Fact]
    public async Task Allowing_private_networks_lets_an_internal_site_be_crawled()
    {
        using var server = new OneShotServer();
        using var crawler = new SiteCrawlConnector($"http://127.0.0.1:{server.Port}/", "t",
            new WebCrawlOptions(RespectRobotsTxt: false, MaxRetries: 0, AllowPrivateNetworks: true));

        var items = await crawler.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Single(items);
        Assert.True(server.WasConnected);
    }

    [Fact]
    public void Create_refuses_handlers_that_cannot_be_guarded()
    {
        // Follows redirects itself.
        Assert.Throws<ArgumentException>(() => SiteCrawlConnector.Create(new SocketsHttpHandler(), "https://example.com/", "t"));
        // Cannot take the address check.
        Assert.Throws<ArgumentException>(() => SiteCrawlConnector.Create(new HttpClientHandler { AllowAutoRedirect = false }, "https://example.com/", "t"));
        // Already connects its own way.
        Assert.Throws<ArgumentException>(() => SiteCrawlConnector.Create(
            new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = (_, _) => throw new InvalidOperationException() }, "https://example.com/", "t"));
        // Behind a delegating handler, too.
        Assert.Throws<ArgumentException>(() => SiteCrawlConnector.Create(new PassThrough { InnerHandler = new SocketsHttpHandler() }, "https://example.com/", "t"));

        using var guarded = SiteCrawlConnector.Create(new SocketsHttpHandler { AllowAutoRedirect = false }, "https://example.com/", "t");
        using var internalSite = SiteCrawlConnector.Create(new HttpClientHandler { AllowAutoRedirect = false }, "https://intranet.example/", "t",
            new WebCrawlOptions(AllowPrivateNetworks: true));
        Assert.Throws<ArgumentException>(() => new SiteCrawlConnector("file:///etc/passwd", "t"));
    }

    [Fact]
    public async Task The_feed_reader_follows_redirects_itself_and_refuses_other_schemes_and_endless_chains()
    {
        var hops = 0;
        var feed = RssFeedConnector.Create(new Stub(request => request.RequestUri!.AbsolutePath switch
        {
            "/old" => Redirect("/feed"),
            "/feed" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Rss, Encoding.UTF8, "application/rss+xml") },
            "/to-file" => Redirect("file:///etc/passwd"),
            _ => Redirect("/loop" + ++hops),
        }), "https://example.com/old", "t");
        Assert.Single(await feed.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        var toFile = RssFeedConnector.Create(new Stub(_ => Redirect("file:///etc/passwd")), "https://example.com/to-file", "t");
        await Assert.ThrowsAsync<InvalidOperationException>(() => toFile.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken).AsTask());

        var loop = RssFeedConnector.Create(new Stub(_ => Redirect("/again")), "https://example.com/start", "t");
        await Assert.ThrowsAsync<InvalidOperationException>(() => loop.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task The_feed_reader_never_connects_to_a_local_address_unless_allowed()
    {
        using var server = new OneShotServer(Rss, "application/rss+xml");
        var feed = new RssFeedConnector($"http://localhost:{server.Port}/feed", "t");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => feed.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.False(server.WasConnected);

        var allowed = new RssFeedConnector($"http://127.0.0.1:{server.Port}/feed", "t", allowPrivateNetworks: true);
        Assert.Single(await allowed.DiscoverAsync(TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    private const string Rss = """
        <?xml version="1.0"?><rss version="2.0"><channel><title>T</title>
        <item><guid>https://example.com/1</guid><link>https://example.com/1</link><title>One</title>
        <description>First item body, long enough to count as content.</description><pubDate>Mon, 20 Oct 2026 09:00:00 GMT</pubDate></item>
        </channel></rss>
        """;

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
    };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class PassThrough : DelegatingHandler;

    /// <summary>A local HTTP server that answers every connection with one page and records whether anyone connected.</summary>
    private sealed class OneShotServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public OneShotServer(string body = "<html><head><title>Inside</title></head><body>internal page</body></html>", string type = "text/html")
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = ServeAsync(body, type);
        }

        public int Port { get; }

        public bool WasConnected { get; private set; }

        private async Task ServeAsync(string body, string type)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    WasConnected = true;
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    var bytes = Encoding.UTF8.GetBytes(body);
                    var head = $"HTTP/1.1 200 OK\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
