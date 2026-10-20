using System.Net;
using System.Text;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.Box;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class BoxObjectStoreClientTests
{
    [Fact]
    public async Task Listing_recurses_with_marker_pages_and_resumes_in_sorted_key_order()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.Uri.AbsolutePath;
            var marker = Query(request.Uri, "marker");
            return path.EndsWith("/folders/root/items", StringComparison.Ordinal) && marker is null
                ? Json("""{"next_marker":"page-2","entries":[{"id":"folder1","type":"folder","name":"nested"},{"id":"f1","type":"file","name":"z.txt","size":3,"etag":"v1","modified_at":"2024-01-01T00:00:00Z"}]}""")
                : path.EndsWith("/folders/root/items", StringComparison.Ordinal)
                    ? Json("""{"next_marker":null,"entries":[{"id":"f2","type":"file","name":"a space.txt","size":4,"etag":"v2","modified_at":"2024-01-02T00:00:00Z"}]}""")
                    : Json("""{"next_marker":null,"entries":[{"id":"f3","type":"file","name":"report.txt","size":7,"etag":"v3","modified_at":"2024-01-03T00:00:00Z"}]}""");
        });
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var first = await client.ListAsync("", null, 2);
        var second = await client.ListAsync("", first.NextResumeToken, 2);
        Assert.Equal(3, handler.Requests.Count);
        var replay = await client.ListAsync("", null, 2);

        Assert.Equal(["a space.txt", "nested/report.txt"], first.Objects.Select(item => item.Key));
        Assert.StartsWith("v1|", first.NextResumeToken);
        Assert.Equal(["z.txt"], second.Objects.Select(item => item.Key));
        Assert.Null(second.NextResumeToken);
        Assert.Equal(first.Objects.Select(item => item.Key), replay.Objects.Select(item => item.Key));
        Assert.Equal("v2", first.Objects[0].ETag);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("GET", request.Method.Method);
            Assert.Equal("Bearer test-token", request.Authorization);
            Assert.Contains("limit=2", request.Uri.Query);
            Assert.Contains("usemarker=true", request.Uri.Query);
        });
        Assert.Contains(handler.Requests, request => request.Uri.Query.Contains("marker=page-2", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath.EndsWith("/folders/folder1/items", StringComparison.Ordinal));
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task Head_resolves_relative_key_and_reflects_changed_file_version()
    {
        var version = "10";
        var handler = new StubHandler(_ => Json($"{{\"entries\":[{{\"id\":\"f1\",\"type\":\"file\",\"name\":\"note.txt\",\"size\":4,\"etag\":\"{version}\",\"modified_at\":\"2024-01-01T00:00:00Z\"}}]}}"));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        Assert.Equal("10", (await client.HeadAsync("note.txt"))?.ETag);
        version = "11";
        Assert.Equal("11", (await client.HeadAsync("note.txt"))?.ETag);
        Assert.Null(await client.HeadAsync("deleted.txt"));
    }

    [Fact]
    public async Task Missing_etag_and_hash_do_not_claim_the_file_id_as_a_version()
    {
        var handler = new StubHandler(_ => Json("""{"entries":[{"id":"file-id","type":"file","name":"unknown-version.txt","size":1}]}"""));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var info = await client.HeadAsync("unknown-version.txt");

        Assert.NotNull(info);
        Assert.Null(info.ETag);
    }

    [Fact]
    public async Task Full_discovery_reflects_moved_and_deleted_paths()
    {
        var moved = false;
        var handler = new StubHandler(_ => moved
            ? Json("""{"entries":[{"id":"f1","type":"file","name":"renamed.txt","size":1,"etag":"2"}]}""")
            : Json("""{"entries":[{"id":"f1","type":"file","name":"old.txt","size":1,"etag":"1"}]}"""));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        Assert.Equal("old.txt", Assert.Single((await client.ListAsync("", null, 10)).Objects).Key);
        moved = true;
        var next = await client.ListAsync("", null, 10);
        Assert.Equal("renamed.txt", Assert.Single(next.Objects).Key);
        Assert.Null(await client.HeadAsync("old.txt"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "fresh token")]
    [InlineData(HttpStatusCode.Forbidden, "permissions")]
    public async Task Authentication_expiry_and_permission_failures_are_actionable(HttpStatusCode status, string message)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var error = await Assert.ThrowsAsync<BoxApiException>(() => client.ListAsync("", null, 10));
        Assert.Equal(status, error.StatusCode);
        Assert.Contains(message, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_uses_range_follows_box_download_redirect_without_bearer_and_caps_ignored_range()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("/folders/root/items", StringComparison.Ordinal)
            ? Json("""{"entries":[{"id":"folder1","type":"folder","name":"nested"},{"id":"other","type":"folder","name":"unrelated"}]}""")
            : request.Uri.AbsolutePath.EndsWith("/folders/folder1/items", StringComparison.Ordinal)
                ? Json("""{"entries":[{"id":"f1","type":"file","name":"report.txt","size":11,"etag":"v1"}]}""")
                : request.Uri.Host == "api.box.com"
                    ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://dl.boxcloud.com/download/file") } }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world")) });
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var content = await client.ReadAsync("nested/report.txt", 5);

        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal("bytes=0-5", handler.Requests[2].Range);
        Assert.Equal("Bearer test-token", handler.Requests[2].Authorization);
        Assert.Null(handler.Requests[3].Authorization);
        Assert.Equal("f1", handler.Requests[2].Uri.Segments[^2].TrimEnd('/'));
        Assert.Equal(4, handler.Requests.Count);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath.Contains("/folders/other/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_returns_empty_on_unsatisfiable_range_for_empty_file()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("/folders/root/items", StringComparison.Ordinal)
            ? Json("""{"entries":[{"id":"f1","type":"file","name":"empty.txt","size":0,"etag":"v1"}]}""")
            : request.Uri.Host == "api.box.com"
                ? new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://dl.boxcloud.com/download/empty") } }
                : new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var result = await client.ReadAsync("empty.txt", 20);

        Assert.Empty(result.Bytes);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Read_rejects_download_redirect_to_untrusted_host()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("/folders/root/items", StringComparison.Ordinal)
            ? Json("""{"entries":[{"id":"f1","type":"file","name":"file.txt","size":1}]}""")
            : new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/file") } });
        using var http = new HttpClient(handler);
        using var client = Client(http);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadAsync("file.txt", 10));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Invalid_root_and_relative_keys_are_rejected()
    {
        using var http = new HttpClient(new StubHandler(_ => Json("{}")));
        using var client = Client(http);
        Assert.Throws<ArgumentException>(() => new BoxObjectStoreClient("token", "../outside", http));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync("../outside", null, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync("/absolute.txt", 10));
    }

    [Fact]
    public async Task Listing_over_snapshot_limit_fails_with_configured_limit_message()
    {
        var handler = new StubHandler(_ => Json("""{"entries":[{"id":"f1","type":"file","name":"a.txt"},{"id":"f2","type":"file","name":"b.txt"}]}"""));
        using var http = new HttpClient(handler);
        using var client = new BoxObjectStoreClient("token", "root", http, maxSnapshotItems: 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListAsync("", null, 1));

        Assert.Contains("limit of 1", error.Message, StringComparison.Ordinal);
    }

    private static BoxObjectStoreClient Client(HttpClient http) => new("test-token", "root", http);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string? Query(Uri uri, string key)
    {
        var part = uri.Query.TrimStart('?').Split('&').FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal));
        return part is null ? null : Uri.UnescapeDataString(part[(key.Length + 1)..]);
    }

    private sealed record SeenRequest(Uri Uri, HttpMethod Method, string? Authorization, string? Range);

    private sealed class StubHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new SeenRequest(request.RequestUri!, request.Method, request.Headers.Authorization?.ToString(), request.Headers.Range?.ToString());
            Requests.Add(seen);
            return Task.FromResult(respond(seen));
        }
    }
}
