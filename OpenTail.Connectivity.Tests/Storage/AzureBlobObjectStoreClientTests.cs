using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using OpenTail.Connectivity.Storage;
using OpenTail.Connectivity.Storage.AzureBlob;

namespace OpenTail.Connectivity.Tests.Storage;

/// <summary>The Azure Blob adapter tests. Per ST2 spec: Azure SDK clients are mockable with virtual methods.</summary>
public sealed class AzureBlobObjectStoreClientTests
{
    [Fact]
    public void Scheme_is_azblob()
    {
        var container = new FakeBlobContainerClient("test-container");
        var client = new AzureBlobObjectStoreClient(container);

        Assert.Equal("azblob", client.Scheme);
    }

    [Fact]
    public void Container_name_from_client()
    {
        var container = new FakeBlobContainerClient("my-bucket");
        var client = new AzureBlobObjectStoreClient(container);

        Assert.Equal("my-bucket", client.Container);
    }

    [Fact]
    public async Task Lists_blobs_and_maps_properties()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("file1.txt", 100, AccessTier.Hot);
        container.AddBlob("file2.txt", 200, AccessTier.Cool);
        container.AddBlob("file3.txt", 300, AccessTier.Cold);
        container.AddBlob("file4.txt", 400, AccessTier.Archive, ArchiveStatus.RehydratePendingToHot);

        var client = new AzureBlobObjectStoreClient(container);
        var page = await client.ListAsync("", null, 10);

        Assert.Equal(4, page.Objects.Count);
        Assert.Equal("file1.txt", page.Objects[0].Key);
        Assert.Equal(100, page.Objects[0].Size);
        Assert.Equal("Hot", page.Objects[0].StorageClass);
        Assert.False(page.Objects[0].CostsExtraToRead);

        Assert.Equal("file2.txt", page.Objects[1].Key);
        Assert.Equal("Cool", page.Objects[1].StorageClass);
        Assert.True(page.Objects[1].CostsExtraToRead);

        Assert.Equal("Cold", page.Objects[2].StorageClass);
        Assert.True(page.Objects[2].CostsExtraToRead);

        Assert.Equal("Archive", page.Objects[3].StorageClass);
        Assert.True(page.Objects[3].NeedsRestore);
        Assert.False(page.Objects[3].CostsExtraToRead);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, page.Objects[3].LastModified);
        Assert.Equal("\"tag\"", page.Objects[3].ETag);
    }

    [Fact]
    public async Task Paging_returns_continuation_token()
    {
        var container = new FakeBlobContainerClient("test");
        for (int i = 1; i <= 15; i++)
            container.AddBlob($"file{i}.txt", 10, AccessTier.Hot);

        var client = new AzureBlobObjectStoreClient(container);

        var page1 = await client.ListAsync("", null, 5);
        Assert.Equal(5, page1.Objects.Count);
        Assert.NotNull(page1.NextResumeToken);

        var page2 = await client.ListAsync("", page1.NextResumeToken, 5);
        Assert.Equal(5, page2.Objects.Count);
        Assert.NotNull(page2.NextResumeToken);
    }

    [Fact]
    public async Task Head_returns_null_for_missing_blob()
    {
        var container = new FakeBlobContainerClient("test");
        var client = new AzureBlobObjectStoreClient(container);

        var info = await client.HeadAsync("nonexistent.txt");

        Assert.Null(info);
    }

    [Fact]
    public async Task Head_returns_blob_properties()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("test.txt", 42, AccessTier.Hot);

        var client = new AzureBlobObjectStoreClient(container);
        var info = await client.HeadAsync("test.txt");

        Assert.NotNull(info);
        Assert.Equal("test.txt", info.Key);
        Assert.Equal(42, info.Size);
        Assert.Equal("Hot", info.StorageClass);
    }

    [Fact]
    public async Task Head_marks_archived_blob_as_needing_restore_while_rehydration_is_pending()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("archived.txt", 42, AccessTier.Archive, ArchiveStatus.RehydratePendingToCool);

        var client = new AzureBlobObjectStoreClient(container);
        var info = await client.HeadAsync("archived.txt");

        Assert.NotNull(info);
        Assert.True(info.NeedsRestore);
    }

    [Fact]
    public async Task Read_returns_content_up_to_max_bytes()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("test.txt", "hello world from azure");

        var client = new AzureBlobObjectStoreClient(container);
        var content = await client.ReadAsync("test.txt", 10);

        Assert.Equal("hello worl", Encoding.UTF8.GetString(content.Bytes));
        Assert.True(content.Truncated);
    }

    [Fact]
    public async Task Read_whole_blob_when_under_max_bytes()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("small.txt", "hello");

        var client = new AzureBlobObjectStoreClient(container);
        var content = await client.ReadAsync("small.txt", 100);

        Assert.Equal("hello", Encoding.UTF8.GetString(content.Bytes));
        Assert.False(content.Truncated);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal("gzip", content.ContentEncoding);
    }

    [Fact]
    public async Task Read_throws_for_missing_blob()
    {
        var container = new FakeBlobContainerClient("test");
        var client = new AzureBlobObjectStoreClient(container);

        await Assert.ThrowsAsync<ObjectNotFoundException>(() => client.ReadAsync("missing.txt", 100));
    }

    [Fact]
    public async Task Read_with_zero_byte_limit_returns_empty_and_detects_nonempty_blob()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("test.txt", "hello");

        var content = await new AzureBlobObjectStoreClient(container).ReadAsync("test.txt", 0);

        Assert.Empty(content.Bytes);
        Assert.True(content.Truncated);
    }

    [Fact]
    public async Task Read_of_empty_blob_returns_empty_content()
    {
        var container = new FakeBlobContainerClient("test");
        container.AddBlob("empty.txt", string.Empty);

        var content = await new AzureBlobObjectStoreClient(container).ReadAsync("empty.txt", 100);

        Assert.Empty(content.Bytes);
        Assert.False(content.Truncated);
    }

    [Fact]
    public async Task Read_rejects_limits_that_cannot_be_represented_with_a_truncation_byte()
    {
        var container = new FakeBlobContainerClient("test");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new AzureBlobObjectStoreClient(container).ReadAsync("test.txt", (long)Array.MaxLength));
    }

    [Fact]
    public void Read_only_methods_check()
    {
        var type = typeof(AzureBlobObjectStoreClient);
        var publicMethods = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(m => m.DeclaringType == type);

        var writeVerbs = new[] { "Upload", "Delete", "Set", "Create", "Copy", "Start", "Put", "Write", "Update" };
        var writeMethods = publicMethods
            .Where(m => writeVerbs.Any(v => m.Name.StartsWith(v, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(writeMethods);
    }

    /// <summary>Minimal fake BlobContainerClient that overrides the methods we use.</summary>
    private sealed class FakeBlobContainerClient : BlobContainerClient
    {
        private readonly string _name;
        private readonly List<FakeBlob> _blobs = [];

        public FakeBlobContainerClient(string name) => _name = name;

        public override string Name => _name;

        public void AddBlob(string name, long size, AccessTier tier, ArchiveStatus? archiveStatus = null)
        {
            _blobs.Add(new FakeBlob(name, size, tier, "test content", archiveStatus));
        }

        public void AddBlob(string name, string content)
        {
            _blobs.Add(new FakeBlob(name, content.Length, AccessTier.Hot, content, null));
        }

        public override AsyncPageable<BlobItem> GetBlobsAsync(
            BlobTraits traits = BlobTraits.None,
            BlobStates states = BlobStates.None,
            string? prefix = null,
            CancellationToken cancellationToken = default)
        {
            var filtered = string.IsNullOrEmpty(prefix)
                ? _blobs
                : _blobs.Where(b => b.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();

            return new FakeAsyncPageable(filtered);
        }

        public override BlobClient GetBlobClient(string blobName)
        {
            return new FakeBlobClient(blobName, _blobs);
        }

        private sealed record FakeBlob(string Name, long Size, AccessTier Tier, string Content, ArchiveStatus? ArchiveStatus);

        private sealed class FakeBlobClient : BlobClient
        {
            private readonly string _name;
            private readonly List<FakeBlob> _blobs;

            public FakeBlobClient(string name, List<FakeBlob> blobs)
            {
                _name = name;
                _blobs = blobs;
            }

            public override Task<Response<BlobProperties>> GetPropertiesAsync(
                BlobRequestConditions? conditions = null,
                CancellationToken cancellationToken = default)
            {
                var blob = _blobs.FirstOrDefault(b => b.Name == _name);
                if (blob == null)
                    throw new RequestFailedException(404, "Not found");

                var properties = BlobsModelFactory.BlobProperties(
                    lastModified: DateTimeOffset.UtcNow,
                    eTag: new ETag("\"etag\""),
                    contentLength: blob.Size,
                    accessTier: blob.Tier.ToString(),
                    archiveStatus: blob.ArchiveStatus?.ToString());

                return Task.FromResult(Response.FromValue(properties, new FakeResponse(200)));
            }

            public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(
                Azure.HttpRange range = default,
                BlobRequestConditions? conditions = null,
                bool rangeGetContentHash = false,
                CancellationToken cancellationToken = default)
            {
                var blob = _blobs.FirstOrDefault(b => b.Name == _name);
                if (blob == null)
                    throw new RequestFailedException(404, "Not found");

                var bytes = Encoding.UTF8.GetBytes(blob.Content);
                if (bytes.Length == 0)
                    throw new RequestFailedException(416, "Range not satisfiable");
                var start = (int)range.Offset;
                var length = range.Length.HasValue
                    ? (int)Math.Min(range.Length.Value, bytes.Length - start)
                    : bytes.Length - start;

                var stream = new MemoryStream(bytes, start, length);
                var result = BlobsModelFactory.BlobDownloadStreamingResult(
                    content: stream,
                    details: BlobsModelFactory.BlobDownloadDetails(
                        contentType: "text/plain",
                        contentEncoding: "gzip"));

                return Task.FromResult(Response.FromValue(result, new FakeResponse(200)));
            }
        }

        private sealed class FakeAsyncPageable : AsyncPageable<BlobItem>
        {
            private readonly List<FakeBlob> _items;

            public FakeAsyncPageable(List<FakeBlob> items) => _items = items;

            public override async IAsyncEnumerable<Page<BlobItem>> AsPages(
                string? continuationToken = null,
                int? pageSizeHint = null)
            {
                var pageSize = pageSizeHint ?? _items.Count;
                var startIndex = 0;

                if (!string.IsNullOrEmpty(continuationToken) && int.TryParse(continuationToken, out var index))
                    startIndex = index;

                while (startIndex < _items.Count)
                {
                    var pageItems = _items.Skip(startIndex).Take(pageSize)
                        .Select(b => BlobsModelFactory.BlobItem(
                            b.Name,
                            false,
                            BlobsModelFactory.BlobItemProperties(
                                accessTierInferred: false,
                                accessTier: b.Tier,
                                lastModified: DateTimeOffset.UtcNow,
                                eTag: new ETag("\"tag\""),
                                contentLength: b.Size,
                                archiveStatus: b.ArchiveStatus)))
                        .ToList();

                    var nextIndex = startIndex + pageItems.Count;
                    var nextToken = nextIndex < _items.Count ? nextIndex.ToString() : null;

                    yield return Page<BlobItem>.FromValues(pageItems, nextToken, new FakeResponse(200));

                    if (nextToken == null)
                        break;

                    startIndex = nextIndex;
                }

                await Task.CompletedTask;
            }
        }

        private sealed class FakeResponse : Response
        {
            public FakeResponse(int status) => Status = status;

            public override int Status { get; }
            public override string ReasonPhrase => "OK";
            public override Stream? ContentStream { get; set; }
            public override string ClientRequestId { get; set; } = string.Empty;

            public override void Dispose() { }

            protected override bool ContainsHeader(string name) => false;
            protected override IEnumerable<Azure.Core.HttpHeader> EnumerateHeaders() => [];
            protected override bool TryGetHeader(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
            {
                value = null;
                return false;
            }
            protected override bool TryGetHeaderValues(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IEnumerable<string>? values)
            {
                values = null;
                return false;
            }
        }
    }
}
