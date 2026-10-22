using System.Net;
using System.Text;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.WebDav;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class WebDavObjectStoreClientTests
{
    private const string Root = "https://dav.example/remote.php/dav/files/alice/library/";
    private const string Auth = "Basic " + "YWxpY2U6YXBwLXBhc3M=";

    [Fact]
    public async Task Listing_recurses_decodes_escaped_paths_and_resumes_by_sorted_key()
    {
        var handler = new StubHandler(request =>
        {
            var path = Uri.UnescapeDataString(request.Uri.AbsolutePath);
            if (path.EndsWith("/folder/", StringComparison.Ordinal))
                return Xml(Multi(
                    Collection("/remote.php/dav/files/alice/library/folder/"),
                    File("/remote.php/dav/files/alice/library/folder/b.txt", 2, "etag-b")));
            return Xml(Multi(
                Collection("/remote.php/dav/files/alice/library/"),
                File("/remote.php/dav/files/alice/library/z.txt", 3, "etag-z"),
                Collection("/remote.php/dav/files/alice/library/folder/"),
                File("/remote.php/dav/files/alice/library/a%20space.txt", 1, "etag-a")));
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var first = await client.ListAsync("", null, 2);
        var second = await client.ListAsync("", first.NextResumeToken, 2);
        Assert.Equal(2, handler.Requests.Count);
        var replay = await client.ListAsync("", null, 2);

        Assert.Equal(["a space.txt", "folder/b.txt"], first.Objects.Select(item => item.Key));
        Assert.StartsWith("v1|", first.NextResumeToken);
        Assert.Equal(["z.txt"], second.Objects.Select(item => item.Key));
        Assert.Null(second.NextResumeToken);
        Assert.Equal(first.Objects.Select(item => item.Key), replay.Objects.Select(item => item.Key));
        Assert.Equal("etag-a", first.Objects[0].ETag);
        Assert.Equal(DateTimeOffset.Parse("2024-01-01T00:00:00Z"), first.Objects[0].LastModified);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("PROPFIND", request.Method.Method);
            Assert.Equal("1", request.Depth);
            Assert.Equal(Auth, request.Authorization);
        });
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Head_uses_depth_zero_and_maps_metadata()
    {
        var handler = new StubHandler(_ => Xml(Multi(File("/remote.php/dav/files/alice/library/report.txt", 7, "etag-report"))));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var info = await client.HeadAsync("report.txt");

        Assert.NotNull(info);
        Assert.Equal("report.txt", info.Key);
        Assert.Equal(7, info.Size);
        Assert.Equal("etag-report", info.ETag);
        Assert.Equal("0", Assert.Single(handler.Requests).Depth);
    }

    [Fact]
    public async Task Missing_properties_use_safe_defaults()
    {
        var handler = new StubHandler(_ => Xml(Multi(
            Collection("/remote.php/dav/files/alice/library/"),
            "<d:response><d:href>/remote.php/dav/files/alice/library/unknown.txt</d:href>" +
            "<d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>")));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var item = Assert.Single((await client.ListAsync("", null, 10)).Objects);

        Assert.Equal("unknown.txt", item.Key);
        Assert.Equal(0, item.Size);
        Assert.Equal(DateTimeOffset.UnixEpoch, item.LastModified);
        Assert.Null(item.ETag);
    }

    [Fact]
    public async Task Head_returns_null_on_404_and_authentication_failure_is_reported()
    {
        var missing = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var missingHttp = new HttpClient(missing);
        using var missingClient = Client(missingHttp);
        Assert.Null(await missingClient.HeadAsync("missing.txt"));

        var denied = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var deniedHttp = new HttpClient(denied);
        using var deniedClient = Client(deniedHttp);
        await Assert.ThrowsAsync<HttpRequestException>(() => deniedClient.ListAsync("", null, 10));
    }

    [Fact]
    public async Task Get_escapes_file_path_sends_range_and_caps_response_even_if_server_ignores_range()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world")),
        });
        handler.ResponseFactory = request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world")),
            };
            response.Content.Headers.ContentType = new("text/plain");
            return response;
        };
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var content = await client.ReadAsync("folder/a space.txt", 5);

        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal("text/plain", content.ContentType);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/remote.php/dav/files/alice/library/folder/a%20space.txt", request.Uri.AbsolutePath);
        Assert.Equal("bytes=0-5", request.Range);
        Assert.Equal(Auth, request.Authorization);
    }

    [Fact]
    public async Task Empty_file_range_returns_empty_content()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0);
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var content = await client.ReadAsync("empty.txt", 10);

        Assert.Empty(content.Bytes);
        Assert.False(content.Truncated);
    }

    [Fact]
    public async Task Same_origin_redirect_is_followed_but_cross_origin_redirect_is_rejected()
    {
        var sameOrigin = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("a.txt", StringComparison.Ordinal)
            ? Redirect("b.txt")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using (var http = new HttpClient(sameOrigin))
        using (var client = Client(http))
        {
            var result = await client.ReadAsync("a.txt", 10);
            Assert.Equal("ok", Encoding.UTF8.GetString(result.Bytes));
            Assert.Equal(2, sameOrigin.Requests.Count);
            Assert.All(sameOrigin.Requests, request => Assert.Equal(Auth, request.Authorization));
        }

        var crossOrigin = new StubHandler(_ => Redirect("https://evil.example/remote.php/dav/files/alice/library/a.txt"));
        using var crossHttp = new HttpClient(crossOrigin);
        using var crossClient = Client(crossHttp);
        await Assert.ThrowsAsync<HttpRequestException>(() => crossClient.ReadAsync("a.txt", 10));
        Assert.Single(crossOrigin.Requests);
    }

    [Fact]
    public async Task Refuses_href_path_traversal_and_invalid_keys()
    {
        var handler = new StubHandler(_ => Xml(Multi(
            Collection("/remote.php/dav/files/alice/library/"),
            File("/remote.php/dav/files/alice/library/%2e%2e/private.txt", 1, "etag"))));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("", null, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync("../private.txt", 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.HeadAsync("/absolute.txt"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync("folder\\file.txt", 10));
    }

    [Fact]
    public void Http_roots_require_explicit_insecure_transport_opt_in()
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        Assert.Throws<ArgumentException>(() => new WebDavObjectStoreClient(new Uri("http://dav.example/library/"), "alice", "pass", http));
        using var allowed = new WebDavObjectStoreClient(new Uri("http://dav.example/library/"), "alice", "pass", http, allowInsecureHttp: true);
        Assert.Equal("webdav", allowed.Scheme);
    }

    [Fact]
    public async Task Listing_over_snapshot_limit_fails_with_configured_limit_message()
    {
        var handler = new StubHandler(_ => Xml(Multi(
            Collection("/remote.php/dav/files/alice/library/"),
            File("/remote.php/dav/files/alice/library/a.txt", 1, "a"),
            File("/remote.php/dav/files/alice/library/b.txt", 1, "b"))));
        using var http = new HttpClient(handler);
        using var client = new WebDavObjectStoreClient(new Uri(Root), "alice", "pass", http, maxSnapshotItems: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync("", null, 1));

        Assert.Contains("limit of 1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_within_byte_limit_parses()
    {
        var xml = Multi(File("/remote.php/dav/files/alice/library/a.txt", 1, "e"));
        using var http = new HttpClient(new StubHandler(_ => Xml(xml)));
        using var client = new WebDavObjectStoreClient(new Uri(Root), "alice", "app-pass", http,
            maxListingResponseBytes: Encoding.UTF8.GetByteCount(xml));

        var page = await client.ListAsync("", null, 10);

        Assert.Equal(["a.txt"], page.Objects.Select(item => item.Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Oversized_listing_is_rejected_with_or_without_content_length(bool declareLength)
    {
        var xml = Multi(File("/remote.php/dav/files/alice/library/a.txt", 1, "e"));
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)207)
        {
            Content = declareLength
                ? new StringContent(xml, Encoding.UTF8, "application/xml")
                : new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(xml))),
        }));
        using var client = new WebDavObjectStoreClient(new Uri(Root), "alice", "app-pass", http,
            maxListingResponseBytes: Encoding.UTF8.GetByteCount(xml) - 1);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => client.ListAsync("", null, 10));

        Assert.StartsWith("WebDAV listing response", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("multistatus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_body_is_truncated()
    {
        var big = new string('x', 100_000);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new UnknownLengthStream(Encoding.UTF8.GetBytes(big))),
        }));
        using var client = Client(http);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.ListAsync("", null, 10));

        Assert.True(error.Message.Length < 2_000);
        Assert.EndsWith("(truncated)", error.Message, StringComparison.Ordinal);
    }

    // A stream that cannot report its length, so HttpContent has no Content-Length.
    private sealed class UnknownLengthStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private static WebDavObjectStoreClient Client(HttpClient http) =>
        new(new Uri(Root), "alice", "app-pass", http);

    private static HttpResponseMessage Xml(string xml) => new((HttpStatusCode)207)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
    };

    private static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.TemporaryRedirect)
    {
        Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) },
    };

    private static string Multi(params string[] responses) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:multistatus xmlns:d=\"DAV:\">" + string.Concat(responses) + "</d:multistatus>";

    private static string Collection(string href) =>
        "<d:response><d:href>" + href + "</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>" +
        "<d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static string File(string href, long size, string etag) =>
        "<d:response><d:href>" + href + "</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>" + size +
        "</d:getcontentlength><d:getlastmodified>Mon, 01 Jan 2024 00:00:00 GMT</d:getlastmodified><d:getetag>" + etag +
        "</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private sealed record SeenRequest(Uri Uri, HttpMethod Method, string? Depth, string? Range, string? Authorization);

    private sealed class StubHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];
        public Func<SeenRequest, HttpResponseMessage> ResponseFactory { get; set; } = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new SeenRequest(
                request.RequestUri!,
                request.Method,
                request.Headers.TryGetValues("Depth", out var depths) ? depths.Single() : null,
                request.Headers.Range?.ToString(),
                request.Headers.Authorization?.ToString());
            Requests.Add(seen);
            var response = ResponseFactory(seen);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
