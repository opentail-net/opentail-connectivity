namespace OpenTail.Connectivity.Storage;

/// <summary>
/// Read-only access to one bucket or container. Adapters (S3, Azure Blob, GCS) implement it over the cloud's SDK, built by the
/// caller with its own identity and HTTP client. Implementations call only list, head and get.
/// </summary>
public interface IObjectStoreClient
{
    /// <summary>"s3", "azblob", "gs", or "memory".</summary>
    string Scheme { get; }

    /// <summary>The bucket or container name.</summary>
    string Container { get; }

    /// <summary>One page of objects whose keys start with <paramref name="prefix"/>, in key order. <paramref name="resumeToken"/> is null for the first page.</summary>
    Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Up to <paramref name="maxBytes"/> of the object. Stops reading there and sets <see cref="ObjectContent.Truncated"/> rather than
    /// failing. Throws <see cref="ObjectNotFoundException"/> when the object does not exist.
    /// </summary>
    Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>The object's listing data, or null when it does not exist.</summary>
    Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>An object's listing data.</summary>
/// <param name="StorageClass">The provider's class or tier name, as given (for example STANDARD, GLACIER, Cool, ARCHIVE).</param>
/// <param name="NeedsRestore">The object cannot be read until restored (S3 Glacier Flexible Retrieval and Deep Archive, Azure Archive).</param>
/// <param name="CostsExtraToRead">Readable, but each read is charged extra (S3 Glacier Instant Retrieval, GCS Coldline and Archive).</param>
public sealed record ObjectInfo(
    string Key,
    long Size,
    DateTimeOffset LastModified,
    string? ETag = null,
    string? StorageClass = null,
    bool NeedsRestore = false,
    bool CostsExtraToRead = false)
{
    /// <summary>A zero-byte key ending in '/': a "folder" placeholder some tools create, not content.</summary>
    public bool IsFolderPlaceholder => Size == 0 && Key.EndsWith('/');
}

public sealed record ObjectListingPage(IReadOnlyList<ObjectInfo> Objects, string? NextResumeToken);

/// <param name="Truncated">The object is longer than the bytes returned.</param>
public sealed record ObjectContent(byte[] Bytes, bool Truncated, string? ContentType = null, string? ContentEncoding = null);

public sealed class ObjectNotFoundException(string key) : Exception($"Object '{key}' was not found.")
{
    public string Key { get; } = key;
}
