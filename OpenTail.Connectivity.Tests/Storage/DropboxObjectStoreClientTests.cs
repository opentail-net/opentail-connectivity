using System.Net;
using System.Text;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.Dropbox;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class DropboxObjectStoreClientTests
{
    private const string Token = "test-access-token";

    [Fact]
    public async Task Listing_maps_files_and_ignores_folders_and_deleted_entries()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"entries":[
              {".tag":"file","name":"one.txt","path_display":"/root/One.txt","size":42,"server_modified":"2026-10-01T10:00:00Z","content_hash":"hash-one"},
              {".tag":"folder","name":"folder","path_display":"/root/folder"},
              {".tag":"deleted","name":"gone.txt","path_display":"/root/gone.txt"}],
             "cursor":"cursor-1","has_more":false}
            """));
        using var http = new HttpClient(handler);
        using var client = Client(http, "/root");

        var page = await client.ListAsync("", null, 50);

        var info = Assert.Single(page.Objects);
        Assert.Equal("One.txt", info.Key);
        Assert.Equal(42, info.Size);
        Assert.Equal("hash-one", info.ETag);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), info.LastModified);
        Assert.Null(page.NextResumeToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.dropboxapi.com/2/files/list_folder", request.Uri.ToString());
        Assert.Equal(Token, request.BearerToken);
        Assert.Contains("\"path\":\"/root\"", request.BodyText);
        Assert.Contains("\"limit\":50", request.BodyText);
    }

    [Fact]
    public async Task Listing_continues_with_cursor_and_returns_the_next_cursor_only_when_more()
    {
        var handler = new StubHandler(request => request.Uri.AbsolutePath.EndsWith("/continue", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """
                {"entries":[],"cursor":"cursor-2","has_more":false}
                """)
            : Json(HttpStatusCode.OK, """
                {"entries":[],"cursor":"cursor-1","has_more":true}
                """));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        var first = await client.ListAsync("", null, 10);
        var second = await client.ListAsync("", first.NextResumeToken, 10);

        Assert.Equal("cursor-1", first.NextResumeToken);
        Assert.Null(second.NextResumeToken);
        Assert.Equal(new[] { "/2/files/list_folder", "/2/files/list_folder/continue" }, handler.Requests.Select(r => r.Uri.AbsolutePath));
        Assert.Contains("\"cursor\":\"cursor-1\"", handler.Requests[1].BodyText);
    }

    [Fact]
    public async Task Prefix_filter_is_case_insensitive_and_preserves_display_case()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"entries":[
              {".tag":"file","path_display":"/root/Reports/Q1.TXT","size":1,"server_modified":"2026-10-01T00:00:00Z","content_hash":"1"},
              {".tag":"file","path_display":"/root/other.txt","size":1,"server_modified":"2026-10-01T00:00:00Z","content_hash":"2"}],
             "cursor":"c","has_more":false}
            """));
        using var http = new HttpClient(handler);
        using var client = Client(http, "/root");

        var page = await client.ListAsync("reports/", null, 10);

        Assert.Equal(["Reports/Q1.TXT"], page.Objects.Select(o => o.Key));
    }

    [Fact]
    public async Task Head_returns_null_for_path_not_found()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict,
            "{\"error_summary\":\"path/not_found/..\"}"));
        using var http = new HttpClient(handler);
        using var client = Client(http, "/root");

        Assert.Null(await client.HeadAsync("missing.txt"));
        Assert.Equal("/root/missing.txt", handler.Requests[0].Body.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Download_uses_escaped_dropbox_argument_and_caps_the_body()
    {
        var handler = new StubHandler(request =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello world")),
            };
            response.Content.Headers.ContentType = new("text/plain");
            return response;
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, "/root");

        var content = await client.ReadAsync("caf\u00e9.txt", 5);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://content.dropboxapi.com/2/files/download", request.Uri.ToString());
        Assert.Contains("\\u00E9", request.DropboxArg, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/root/caf\u00e9.txt", request.DropboxArgJson.RootElement.GetProperty("path").GetString());
        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal("text/plain", content.ContentType);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder\\secret.txt")]
    public async Task Refuses_non_relative_keys_and_prefixes(string bad)
    {
        using var client = Client(new HttpClient(new StubHandler(_ => throw new Xunit.Sdk.XunitException("Network should not be called."))), "/root");

        await Assert.ThrowsAsync<ArgumentException>(() => client.HeadAsync(bad));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync(bad, 10));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ListAsync(bad, null, 10));
    }

    [Fact]
    public async Task Read_throws_object_not_found_for_path_not_found()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.Conflict,
            "{\"error_summary\":\"path/not_found/..\"}"));
        using var http = new HttpClient(handler);
        using var client = Client(http);

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => client.ReadAsync("missing.txt", 10));
    }

    [Fact]
    public async Task Refresh_token_helper_caches_until_one_minute_before_expiry()
    {
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var refreshes = 0;
        var handler = new StubHandler(_ =>
        {
            refreshes++;
            return Json(HttpStatusCode.OK, $"{{\"access_token\":\"token-{refreshes}\",\"expires_in\":3600}}");
        });
        using var http = new HttpClient(handler);
        var token = DropboxTokens.FromRefreshToken("app", "secret", "refresh", http, () => now);

        Assert.Equal("token-1", await token(CancellationToken.None));
        now = now.AddMinutes(58);
        Assert.Equal("token-1", await token(CancellationToken.None));
        now = now.AddMinutes(2);
        Assert.Equal("token-2", await token(CancellationToken.None));
        Assert.Equal(2, refreshes);
        Assert.Contains("grant_type=refresh_token", handler.Requests[0].BodyText);
    }

    [Fact]
    public async Task Rate_limit_waits_for_retry_after_then_retries()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var throttled = new HttpResponseMessage((HttpStatusCode)429);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return throttled;
            }
            return Json(HttpStatusCode.OK, "{\"entries\":[],\"cursor\":\"c\",\"has_more\":false}");
        });
        using var http = new HttpClient(handler);
        using var client = new DropboxObjectStoreClient(_ => Task.FromResult(Token), "", http,
            (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        await client.ListAsync("", null, 10);

        Assert.Equal([TimeSpan.FromSeconds(2)], waits);
        Assert.Equal(2, calls);
    }

    private static DropboxObjectStoreClient Client(HttpClient http, string root = "") =>
        new(_ => Task.FromResult(Token), root, http, delay: null);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class SeenRequest(HttpRequestMessage request)
    {
        public HttpMethod Method { get; } = request.Method;
        public Uri Uri { get; } = request.RequestUri!;
        public string BodyText { get; } = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";
        public System.Text.Json.JsonDocument Body { get; } = JsonDocument.Parse(
            request.Content?.Headers.ContentType?.MediaType == "application/json"
                ? request.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                : "{}");
        public string? BearerToken { get; } = request.Headers.Authorization?.Parameter;
        public string? DropboxArg { get; } = request.Headers.TryGetValues("Dropbox-API-Arg", out var values) ? values.Single() : null;
        public System.Text.Json.JsonDocument DropboxArgJson { get; } = JsonDocument.Parse(
            request.Headers.TryGetValues("Dropbox-API-Arg", out var values) ? values.Single() : "{}");
    }

    private sealed class StubHandler(Func<SeenRequest, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new SeenRequest(request);
            Requests.Add(seen);
            return Task.FromResult(respond(seen));
        }
    }
}
