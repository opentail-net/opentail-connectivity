using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace OpenTail.Connectivity.Storage.S3;

/// <summary>
/// <see cref="IObjectStoreClient"/> over Amazon S3, or an S3-compatible store (MinIO, R2 and others) through the SDK's service URL.
/// Calls only ListObjectsV2, HeadObject and ranged GetObject, so the identity needs <c>s3:ListBucket</c> and <c>s3:GetObject</c>
/// on the one bucket and nothing more.
/// </summary>
public sealed class S3ObjectStoreClient : IObjectStoreClient
{
    private readonly IAmazonS3 s3;

    /// <param name="s3">An SDK client the caller built with its own identity, region and endpoint. See <see cref="CreateSdkClient"/>.</param>
    public S3ObjectStoreClient(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        this.s3 = s3;
        // AWS bucket names are global; an S3-compatible endpoint has its own namespace, so its host is part of the identity.
        Container = Uri.TryCreate(s3.Config?.ServiceURL, UriKind.Absolute, out var endpoint) ? endpoint.Authority + "/" + bucket : bucket;
    }

    public string Scheme => "s3";

    public string Container { get; }

    /// <summary>
    /// Builds an SDK client whose every request goes through <paramref name="httpClient"/>, so a caller with a guarded client (one
    /// that allows only listed hosts) keeps that guard. Without <paramref name="credentials"/> the SDK's ambient identity is used.
    /// </summary>
    public static AmazonS3Client CreateSdkClient(HttpClient httpClient, AmazonS3Config config, AWSCredentials? credentials = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(config);
        config.HttpClientFactory = new FixedHttpClientFactory(httpClient);
        return credentials is null ? new AmazonS3Client(config) : new AmazonS3Client(credentials, config);
    }

    public async Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var response = await s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = Container,
            Prefix = prefix,
            ContinuationToken = resumeToken,
            MaxKeys = Math.Min(pageSize, 1000),
        }, cancellationToken).ConfigureAwait(false);

        var objects = (response.S3Objects ?? []).Select(o => Info(o.Key, o.Size ?? 0, o.LastModified, o.ETag, o.StorageClass?.Value, restore: null)).ToList();
        return new ObjectListingPage(objects, response.IsTruncated == true ? response.NextContinuationToken : null);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var m = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = Container, Key = key }, cancellationToken).ConfigureAwait(false);
            var info = Info(key, m.ContentLength, m.LastModified, m.ETag, m.StorageClass?.Value, m.RestoreExpiration is not null || m.RestoreInProgress == true ? m.RestoreInProgress : null);
            // Intelligent-Tiering's archive tiers need a restore too, whatever the class says.
            return m.ArchiveStatus?.Value is { Length: > 0 } && m.RestoreExpiration is null ? info with { NeedsRestore = true } : info;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        GetObjectResponse response;
        try
        {
            // One byte more than asked tells whether the object is longer; the range keeps a large object from downloading whole.
            response = await s3.GetObjectAsync(new GetObjectRequest { BucketName = Container, Key = key, ByteRange = new ByteRange(0, maxBytes) }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new ObjectNotFoundException(key);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            return new ObjectContent([], false); // S3 refuses any range on an empty object
        }

        using (response)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while (buffer.Length <= maxBytes && (n = await response.ResponseStream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                buffer.Write(chunk, 0, n);

            var truncated = buffer.Length > maxBytes || TotalFromContentRange(response.ContentRange) > maxBytes;
            var bytes = buffer.ToArray();
            if (bytes.LongLength > maxBytes) bytes = bytes[..(int)maxBytes];
            return new ObjectContent(bytes, truncated, response.Headers.ContentType, response.Headers.ContentEncoding);
        }
    }

    /// <summary>The total size in a "bytes 0-99/1234" header, or -1.</summary>
    internal static long TotalFromContentRange(string? contentRange)
    {
        var slash = contentRange?.LastIndexOf('/') ?? -1;
        return slash >= 0 && long.TryParse(contentRange![(slash + 1)..], out var total) ? total : -1;
    }

    /// <param name="restore">null: no restore requested; true: in progress; false: restored copy available.</param>
    private static ObjectInfo Info(string key, long size, DateTime? lastModified, string? etag, string? storageClass, bool? restore)
    {
        var cls = storageClass ?? "STANDARD";
        var archived = cls is "GLACIER" or "DEEP_ARCHIVE";
        return new ObjectInfo(
            key,
            size,
            lastModified is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)) : DateTimeOffset.UnixEpoch,
            etag,
            cls,
            NeedsRestore: archived && restore != false,
            CostsExtraToRead: cls is "GLACIER_IR");
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;

        // The caller owns the client and its handler; the SDK must neither cache-and-dispose it nor build its own.
        public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

        public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
    }
}
