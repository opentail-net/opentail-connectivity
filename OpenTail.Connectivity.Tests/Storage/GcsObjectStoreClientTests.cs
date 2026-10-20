using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Google;
using Google.Api.Gax;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.Gcs;
using GcsObject = Google.Apis.Storage.v1.Data.Object;
using GcsObjects = Google.Apis.Storage.v1.Data.Objects;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class GcsObjectStoreClientTests
{
    [Fact]
    public async Task Listing_uses_bucket_prefix_page_token_and_maps_object_metadata()
    {
        var storage = new StubStorageClient();
        storage.Pages.Enqueue(new GcsPage([Item("docs/b.txt", 20, "2", "STANDARD"), Item("docs/a.txt", 10, "1", "NEARLINE")], "next"));
        storage.Pages.Enqueue(new GcsPage([Item("docs/c.txt", 30, "3", "ARCHIVE")], null));
        var client = new GcsObjectStoreClient(storage, "bucket");

        var first = await client.ListAsync("docs/", null, 2);
        var second = await client.ListAsync("docs/", first.NextResumeToken, 2);

        Assert.Equal("gs", client.Scheme);
        Assert.Equal("bucket", client.Container);
        Assert.Equal(["docs/a.txt", "docs/b.txt"], first.Objects.Select(x => x.Key));
        Assert.Equal("next", first.NextResumeToken);
        Assert.Null(second.NextResumeToken);
        Assert.Equal("1", first.Objects[0].ETag);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T10:00:00Z"), first.Objects[0].LastModified);
        Assert.Equal("NEARLINE", first.Objects[0].StorageClass);
        Assert.True(first.Objects[0].CostsExtraToRead);
        Assert.False(first.Objects[0].NeedsRestore);
        Assert.False(first.Objects[1].CostsExtraToRead);
        Assert.True(second.Objects[0].CostsExtraToRead);
        Assert.Equal("bucket", storage.ListCalls[0].Bucket);
        Assert.Equal("docs/", storage.ListCalls[0].Prefix);
        Assert.Equal(2, storage.ListCalls[0].Options.PageSize);
        Assert.Null(storage.ListCalls[0].Options.PageToken);
        Assert.Equal("next", storage.ListCalls[1].Options.PageToken);
    }

    [Fact]
    public async Task Head_returns_null_when_object_is_missing()
    {
        var storage = new StubStorageClient { HeadException = new GoogleApiException("storage", "missing") { HttpStatusCode = HttpStatusCode.NotFound } };

        Assert.Null(await new GcsObjectStoreClient(storage, "bucket").HeadAsync("missing.txt"));
        Assert.Equal(("bucket", "missing.txt"), storage.HeadCall);
    }

    [Fact]
    public async Task Generic_connector_uses_GCS_generation_as_source_version()
    {
        var storage = new StubStorageClient();
        storage.Pages.Enqueue(new GcsPage([Item("report.txt", 5, "987654321", "STANDARD")], null));
        var source = new ObjectStorageConnector(new GcsObjectStoreClient(storage, "bucket"), "tenant");

        var item = Assert.Single(await source.DiscoverAsync().ToListAsync());

        Assert.Equal("987654321", item.Metadata![SourceKeys.Version]);
        Assert.Equal("987654321", item.Metadata["storage.etag"]);
    }

    [Fact]
    public async Task Read_requests_one_extra_byte_caps_content_and_preserves_content_metadata()
    {
        var storage = new StubStorageClient
        {
            DownloadBytes = Encoding.UTF8.GetBytes("hello world"),
            DownloadResult = new GcsObject { ContentType = "text/plain", ContentEncoding = "gzip" },
        };
        var client = new GcsObjectStoreClient(storage, "bucket");

        var content = await client.ReadAsync("docs/a.txt", 5);

        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal("gzip", content.ContentEncoding);
        Assert.Equal(("bucket", "docs/a.txt"), storage.DownloadCall);
        Assert.Equal(0, storage.DownloadOptions!.Range.Ranges.Single().From);
        Assert.Equal(5, storage.DownloadOptions.Range.Ranges.Single().To);
        Assert.Equal(DownloadValidationMode.Automatic, storage.DownloadOptions.DownloadValidationMode);
    }

    [Fact]
    public async Task Read_returns_short_body_without_truncation_and_missing_throws()
    {
        var storage = new StubStorageClient { DownloadBytes = Encoding.UTF8.GetBytes("ok") };
        var client = new GcsObjectStoreClient(storage, "bucket");

        var content = await client.ReadAsync("small.txt", 5);
        Assert.Equal("ok", Encoding.UTF8.GetString(content.Bytes));
        Assert.False(content.Truncated);

        storage.DownloadException = new GoogleApiException("storage", "missing") { HttpStatusCode = HttpStatusCode.NotFound };
        var missing = await Assert.ThrowsAsync<OpenTail.Connectivity.Storage.ObjectNotFoundException>(() => client.ReadAsync("missing.txt", 5));
        Assert.Equal("missing.txt", missing.Key);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/../secret.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder\\secret.txt")]
    public async Task Refuses_keys_that_escape_the_bucket(string key)
    {
        var client = new GcsObjectStoreClient(new StubStorageClient(), "bucket");

        await Assert.ThrowsAsync<ArgumentException>(() => client.HeadAsync(key));
        await Assert.ThrowsAsync<ArgumentException>(() => client.ReadAsync(key, 1));
    }

    private static GcsObject Item(string name, ulong size, string generation, string storageClass) => new()
    {
        Name = name,
        Size = size,
        Generation = long.Parse(generation, System.Globalization.CultureInfo.InvariantCulture),
        ETag = "etag-" + generation,
        StorageClass = storageClass,
        UpdatedDateTimeOffset = DateTimeOffset.Parse("2026-10-01T10:00:00Z"),
    };

    private sealed record GcsPage(IReadOnlyList<GcsObject> Items, string? NextPageToken);

    private sealed class StubPages(GcsPage page) : PagedAsyncEnumerable<GcsObjects, GcsObject>
    {
        public override Task<Page<GcsObject>> ReadPageAsync(int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Page<GcsObject>(page.Items, page.NextPageToken));
    }

    private sealed class StubStorageClient : StorageClient
    {
        public Queue<GcsPage> Pages { get; } = new();
        public List<(string Bucket, string? Prefix, ListObjectsOptions Options)> ListCalls { get; } = [];
        public GcsObject? HeadResult { get; set; } = Item("head.txt", 4, "42", "STANDARD");
        public GoogleApiException? HeadException { get; set; }
        public (string Bucket, string Name)? HeadCall { get; private set; }
        public (string Bucket, string Name)? DownloadCall { get; private set; }
        public DownloadObjectOptions? DownloadOptions { get; private set; }
        public byte[] DownloadBytes { get; set; } = [];
        public GcsObject DownloadResult { get; set; } = new();
        public GoogleApiException? DownloadException { get; set; }

        public override PagedAsyncEnumerable<GcsObjects, GcsObject> ListObjectsAsync(string bucket, string? prefix = null, ListObjectsOptions? options = null)
        {
            ListCalls.Add((bucket, prefix, options!));
            return new StubPages(Pages.Dequeue());
        }

        public override Task<GcsObject> GetObjectAsync(string bucket, string objectName, GetObjectOptions? options = null, CancellationToken cancellationToken = default)
        {
            HeadCall = (bucket, objectName);
            if (HeadException is not null)
                throw HeadException;
            return Task.FromResult(HeadResult!);
        }

        public override Task<GcsObject> DownloadObjectAsync(
            string bucket,
            string objectName,
            Stream destination,
            DownloadObjectOptions? options = null,
            CancellationToken cancellationToken = default,
            IProgress<Google.Apis.Download.IDownloadProgress>? progress = null)
        {
            DownloadCall = (bucket, objectName);
            DownloadOptions = options;
            if (DownloadException is not null)
                throw DownloadException;
            var range = options?.Range?.Ranges.Single();
            var length = range is null ? DownloadBytes.Length : (int)Math.Min(DownloadBytes.Length, range.To!.Value - range.From!.Value + 1);
            destination.Write(DownloadBytes, 0, length);
            return Task.FromResult(DownloadResult);
        }
    }
}
