using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.S3;

namespace OpenTail.Connectivity.Tests.Storage;

/// <summary>The adapter against recorded-style S3 responses, through a caller-supplied HttpClient. No network.</summary>
public sealed class S3ObjectStoreClientTests
{
    private static (S3ObjectStoreClient Client, StubHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var sdk = S3ObjectStoreClient.CreateSdkClient(
            new HttpClient(handler),
            new AmazonS3Config { ServiceURL = "https://s3.example.test", ForcePathStyle = true, AuthenticationRegion = "eu-west-1" },
            new BasicAWSCredentials("AKIDEXAMPLE", "secret-example"));
        return (new S3ObjectStoreClient(sdk, "acme-exports"), handler);
    }

    [Fact]
    public async Task Lists_a_page_through_the_supplied_http_client()
    {
        var (client, handler) = Create(_ => Xml("""
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>acme-exports</Name><Prefix>finance/</Prefix><KeyCount>2</KeyCount><MaxKeys>2</MaxKeys><IsTruncated>true</IsTruncated>
              <NextContinuationToken>token-2</NextContinuationToken>
              <Contents><Key>finance/a.csv</Key><LastModified>2026-10-01T10:00:00.000Z</LastModified><ETag>"abc"</ETag><Size>12</Size><StorageClass>STANDARD</StorageClass></Contents>
              <Contents><Key>finance/old.csv</Key><LastModified>2020-01-01T00:00:00.000Z</LastModified><ETag>"def"</ETag><Size>34</Size><StorageClass>DEEP_ARCHIVE</StorageClass></Contents>
            </ListBucketResult>
            """));

        var page = await client.ListAsync("finance/", null, 2);

        Assert.Equal("token-2", page.NextResumeToken);
        Assert.Equal(["finance/a.csv", "finance/old.csv"], page.Objects.Select(o => o.Key));
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero), page.Objects[0].LastModified);
        Assert.Equal(12, page.Objects[0].Size);
        Assert.False(page.Objects[0].NeedsRestore);
        Assert.True(page.Objects[1].NeedsRestore);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("list-type=2", request.Uri.Query);
        Assert.Contains("prefix=finance/", Uri.UnescapeDataString(request.Uri.Query));
        Assert.Contains("AWS4-HMAC-SHA256", request.Authorization);
    }

    [Fact]
    public async Task Reads_with_a_range_and_reports_truncation_from_the_content_range()
    {
        var (client, handler) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")) };
            r.Content.Headers.ContentType = new("text/plain");
            r.Content.Headers.ContentRange = new(0, 4, 1000);
            return r;
        });

        var content = await client.ReadAsync("finance/a.csv", 4);

        Assert.Equal("hell", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal("bytes=0-4", Assert.Single(handler.Requests).Range);
    }

    [Fact]
    public async Task Reads_a_short_object_whole()
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hi")) };
            r.Content.Headers.ContentRange = new(0, 1, 2);
            return r;
        });
        var content = await client.ReadAsync("finance/a.csv", 100);
        Assert.Equal("hi", Encoding.UTF8.GetString(content.Bytes));
        Assert.False(content.Truncated);
    }

    [Fact]
    public async Task An_empty_object_reads_as_empty()
    {
        var (client, _) = Create(_ => Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange"));
        var content = await client.ReadAsync("finance/empty.csv", 100);
        Assert.Empty(content.Bytes);
        Assert.False(content.Truncated);
    }

    [Fact]
    public async Task A_missing_object_is_null_on_head_and_not_found_on_read()
    {
        var (client, _) = Create(r => r.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Error(HttpStatusCode.NotFound, "NoSuchKey"));
        Assert.Null(await client.HeadAsync("finance/none.csv"));
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => client.ReadAsync("finance/none.csv", 10));
    }

    [Theory]
    [InlineData(null, null, null, false, false)]
    [InlineData("GLACIER", null, null, true, false)]
    [InlineData("DEEP_ARCHIVE", "ongoing-request=\"true\"", null, true, false)]
    [InlineData("GLACIER", "ongoing-request=\"false\", expiry-date=\"Wed, 23 Dec 2026 00:00:00 GMT\"", null, false, false)]
    [InlineData("GLACIER_IR", null, null, false, true)]
    [InlineData("INTELLIGENT_TIERING", null, "ARCHIVE_ACCESS", true, false)]
    public async Task Head_maps_storage_classes(string? storageClass, string? restore, string? archiveStatus, bool needsRestore, bool costsExtra)
    {
        var (client, _) = Create(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            r.Content.Headers.ContentLength = 42;
            r.Content.Headers.LastModified = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero);
            r.Headers.ETag = new("\"etag\"");
            if (storageClass is not null) r.Headers.Add("x-amz-storage-class", storageClass);
            if (restore is not null) r.Headers.Add("x-amz-restore", restore);
            if (archiveStatus is not null) r.Headers.Add("x-amz-archive-status", archiveStatus);
            return r;
        });

        var info = await client.HeadAsync("finance/a.csv");

        Assert.NotNull(info);
        Assert.Equal(42, info.Size);
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.Zero), info.LastModified);
        Assert.Equal(storageClass ?? "STANDARD", info.StorageClass);
        Assert.Equal(needsRestore, info.NeedsRestore);
        Assert.Equal(costsExtra, info.CostsExtraToRead);
    }

    [Fact]
    public async Task Works_with_the_reader_end_to_end()
    {
        var (client, handler) = Create(r =>
        {
            if (r.Method == HttpMethod.Head)
            {
                var h = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                h.Content.Headers.ContentLength = 11;
                return h;
            }
            var g = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("month,total")) };
            g.Content.Headers.ContentRange = new(0, 10, 11);
            return g;
        });
        var reader = new ObjectStoreReader(client, new ObjectStoreReaderOptions { Prefixes = ["finance/"] });

        var text = await reader.ReadTextAsync("finance/a.csv");

        Assert.Equal("month,total", text.Text);
        Assert.Equal([HttpMethod.Head, HttpMethod.Get], handler.Requests.Select(r => r.Method));
        Assert.All(handler.Requests, r => Assert.Equal("s3.example.test", r.Uri.Host));
    }

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body.Trim(), Encoding.UTF8, "application/xml") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code) =>
        new(status) { Content = new StringContent($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{code}</Message></Error>", Encoding.UTF8, "application/xml") };

    internal sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Range, string? Authorization);

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Range?.ToString(),
                request.Headers.TryGetValues("Authorization", out var auth) ? string.Join(",", auth) : null));
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
