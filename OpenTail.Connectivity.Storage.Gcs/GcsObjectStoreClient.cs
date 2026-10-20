using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Google;
using Google.Apis.Storage.v1.Data;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace OpenTail.Connectivity.Storage.Gcs;

/// <summary>
/// <see cref="IObjectStoreClient"/> over Google Cloud Storage. Object names are preserved as opaque keys, and generation is
/// used as the stable object version token. Nearline, Coldline and Archive objects remain readable but incur retrieval charges.
/// </summary>
/// <remarks>
/// The caller supplies a <see cref="StorageClient"/> built with its own credentials and HTTP transport. A
/// <see cref="StorageClientBuilder"/> can configure both before construction.
/// </remarks>
public sealed class GcsObjectStoreClient : IObjectStoreClient
{
    private readonly StorageClient _storage;

    public GcsObjectStoreClient(StorageClient storage, string bucket)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        _storage = storage;
        Container = bucket;
    }

    public string Scheme => "gs";

    public string Container { get; }

    public async Task<ObjectListingPage> ListAsync(
        string prefix,
        string? resumeToken,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        var listing = _storage.ListObjectsAsync(Container, prefix, new ListObjectsOptions
        {
            PageSize = pageSize,
            PageToken = resumeToken,
        });
        var page = await listing.ReadPageAsync(pageSize, cancellationToken).ConfigureAwait(false);
        var objects = page.Select(Map).OrderBy(item => item.Key, StringComparer.Ordinal).ToList();
        return new ObjectListingPage(objects, string.IsNullOrEmpty(page.NextPageToken) ? null : page.NextPageToken);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            var item = await _storage.GetObjectAsync(Container, key, cancellationToken: cancellationToken).ConfigureAwait(false);
            return item is null ? null : Map(item);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);

        using var buffer = new MemoryStream();
        GcsObject metadata;
        try
        {
            metadata = await _storage.DownloadObjectAsync(
                Container,
                key,
                buffer,
                new DownloadObjectOptions
                {
                    Range = new RangeHeaderValue(0, maxBytes),
                    DownloadValidationMode = DownloadValidationMode.Automatic,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw new ObjectNotFoundException(key);
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new ObjectContent([], false);
        }

        var truncated = buffer.Length > maxBytes;
        var bytes = buffer.ToArray();
        if (truncated)
            Array.Resize(ref bytes, checked((int)maxBytes));
        return new ObjectContent(bytes, truncated, metadata.ContentType, metadata.ContentEncoding);
    }

    private static ObjectInfo Map(GcsObject item)
    {
        var key = item.Name ?? throw new InvalidDataException("Google Cloud Storage returned an object without a name.");
        var size = item.Size ?? 0;
        var modified = item.UpdatedDateTimeOffset ?? DateTimeOffset.UnixEpoch;
        var version = item.Generation?.ToString(CultureInfo.InvariantCulture) ?? item.ETag;
        var storageClass = item.StorageClass;
        var costsExtraToRead = storageClass is not null &&
            (storageClass.Equals("NEARLINE", StringComparison.OrdinalIgnoreCase) ||
             storageClass.Equals("COLDLINE", StringComparison.OrdinalIgnoreCase) ||
             storageClass.Equals("ARCHIVE", StringComparison.OrdinalIgnoreCase));

        return new ObjectInfo(key, checked((long)size), modified, version, storageClass, NeedsRestore: false, CostsExtraToRead: costsExtraToRead);
    }

}
