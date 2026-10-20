using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Storage.S3;

/// <summary>Writes to one S3 bucket. This type is separate from the read-only <see cref="S3ObjectStoreClient"/>.</summary>
public sealed class S3ObjectStoreWriter : IObjectStoreWriter
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public S3ObjectStoreWriter(IAmazonS3 s3, string bucket)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        _s3 = s3;
        _bucket = bucket;
    }

    public string Scheme => "s3";
    public string Container => _bucket;

    public async Task<ObjectInfo> PutAsync(string key, Stream content, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        long? contentLength = content.CanSeek ? content.Length - content.Position : null;
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = content,
            ContentType = contentType,
            IfNoneMatch = overwrite ? null : "*",
        };
        try
        {
            var response = await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
            var size = contentLength ?? (await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false)).ContentLength;
            return new ObjectInfo(key, size, DateTimeOffset.UtcNow, response.ETag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ObjectExistsException(key);
        }
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (IsMissing(ex))
        {
            return false;
        }

        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private static bool IsMissing(AmazonS3Exception exception) =>
        exception.StatusCode == HttpStatusCode.NotFound || exception.ErrorCode is "NoSuchKey" or "NotFound";
}
