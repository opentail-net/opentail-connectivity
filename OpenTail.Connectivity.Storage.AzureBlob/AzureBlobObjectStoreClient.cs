using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace OpenTail.Connectivity.Storage.AzureBlob;

/// <summary>
/// <see cref="IObjectStoreClient"/> over Azure Blob Storage.
/// Calls only list, get properties and download, so the identity needs Storage Blob Data Reader on the container.
/// </summary>
public sealed class AzureBlobObjectStoreClient : IObjectStoreClient
{
    private readonly BlobContainerClient _container;

    /// <param name="container">
    /// A container client the caller built with its own credential and transport.
    /// The caller configures HttpClient through BlobClientOptions when constructing the container client.
    /// </param>
    public AzureBlobObjectStoreClient(BlobContainerClient container)
    {
        ArgumentNullException.ThrowIfNull(container);
        _container = container;
    }

    public string Scheme => "azblob";

    public string Container => _container.Name;

    public async Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var page = _container.GetBlobsAsync(
                traits: BlobTraits.None,
                states: BlobStates.None,
                prefix: prefix,
                cancellationToken: cancellationToken)
            .AsPages(resumeToken, pageSize);

        await using var enumerator = page.GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
            return new ObjectListingPage([], null);

        var firstPage = enumerator.Current;
        var objects = firstPage.Values.Select(MapBlobItem).ToList();
        var nextToken = firstPage.ContinuationToken;

        return new ObjectListingPage(objects, nextToken);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var blob = _container.GetBlobClient(key);
            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return MapProperties(key, properties.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        // ObjectContent uses a byte array, and the extra byte is needed to detect truncation.
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);

        var blob = _container.GetBlobClient(key);

        try
        {
            // Download maxBytes + 1 to detect if the blob is larger
            var range = new Azure.HttpRange(0, maxBytes + 1);
            var response = await blob.DownloadStreamingAsync(
                range: range,
                conditions: null,
                rangeGetContentHash: false,
                cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            using var content = response.Value.Content;
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var limit = maxBytes + 1;
            while (buffer.Length < limit)
            {
                var count = (int)Math.Min(chunk.Length, limit - buffer.Length);
                var read = await content.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }

            var truncated = buffer.Length > maxBytes;
            var bytes = buffer.ToArray();
            if (truncated)
                Array.Resize(ref bytes, checked((int)maxBytes));

            var contentType = response.Value.Details.ContentType;
            var contentEncoding = response.Value.Details.ContentEncoding;

            return new ObjectContent(bytes, truncated, contentType, contentEncoding);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new ObjectNotFoundException(key);
        }
        catch (RequestFailedException ex) when (ex.Status == 416)
        {
            // A range starting at byte zero is unsatisfiable only when the blob is empty.
            return new ObjectContent([], false);
        }
    }

    private static ObjectInfo MapBlobItem(BlobItem item)
    {
        var key = item.Name;
        var size = item.Properties.ContentLength ?? 0;
        var lastModified = item.Properties.LastModified ?? DateTimeOffset.UnixEpoch;
        var etag = item.Properties.ETag?.ToString();
        var storageClass = item.Properties.AccessTier?.ToString();

        // Azure Archive tier needs restoration
        var needsRestore = item.Properties.AccessTier == AccessTier.Archive;

        // Cool and Cold tiers have per-read charges
        var costsExtraToRead = item.Properties.AccessTier == AccessTier.Cool ||
                              item.Properties.AccessTier == AccessTier.Cold;

        return new ObjectInfo(key, size, lastModified, etag, storageClass, needsRestore, costsExtraToRead);
    }

    private static ObjectInfo MapProperties(string key, BlobProperties properties)
    {
        var size = properties.ContentLength;
        var lastModified = properties.LastModified;
        var etag = properties.ETag.ToString();
        var storageClass = properties.AccessTier?.ToString();

        var needsRestore = properties.AccessTier == AccessTier.Archive;

        var costsExtraToRead = properties.AccessTier == AccessTier.Cool ||
                              properties.AccessTier == AccessTier.Cold;

        return new ObjectInfo(key, size, lastModified, etag, storageClass, needsRestore, costsExtraToRead);
    }
}
