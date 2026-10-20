using System.Security.Cryptography;
using System.Text;

namespace OpenTail.Connectivity.Storage;

/// <summary>An <see cref="IObjectStoreClient"/> over a dictionary, for tests and samples. Keys sort ordinally, as cloud listings do.</summary>
public sealed class InMemoryObjectStoreClient(string container = "memory") : IObjectStoreClient
{
    private readonly SortedDictionary<string, (ObjectInfo Info, byte[] Bytes, string? ContentType, string? ContentEncoding)> objects = new(StringComparer.Ordinal);

    public string Scheme => "memory";

    public string Container { get; } = container;

    /// <summary>Calls made, in order ("list:prefix", "head:key", "read:key"), so tests can check nothing was called.</summary>
    public List<string> Calls { get; } = [];

    public InMemoryObjectStoreClient Put(
        string key,
        byte[] bytes,
        DateTimeOffset? lastModified = null,
        string? storageClass = null,
        bool needsRestore = false,
        bool costsExtraToRead = false,
        string? contentType = null,
        string? contentEncoding = null)
    {
        var info = new ObjectInfo(key, bytes.LongLength, lastModified ?? DateTimeOffset.UnixEpoch, $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..16]}\"",
            storageClass, needsRestore, costsExtraToRead);
        objects[key] = (info, bytes, contentType, contentEncoding);
        return this;
    }

    public InMemoryObjectStoreClient Put(string key, string text, DateTimeOffset? lastModified = null) => Put(key, Encoding.UTF8.GetBytes(text), lastModified);

    public bool Remove(string key) => objects.Remove(key);

    internal ObjectInfo PutFromWriter(string key, byte[] bytes, string? contentType, bool overwrite)
    {
        lock (objects)
        {
            if (!overwrite && objects.ContainsKey(key))
                throw new ObjectExistsException(key);
            var info = new ObjectInfo(
                key,
                bytes.LongLength,
                DateTimeOffset.UtcNow,
                $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..16]}\"");
            objects[key] = (info, bytes, contentType, null);
            return info;
        }
    }

    internal bool RemoveFromWriter(string key)
    {
        lock (objects)
            return objects.Remove(key);
    }

    public Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        Calls.Add("list:" + prefix);
        // The resume token is the last key returned, like S3's StartAfter.
        var page = objects.Values
            .Select(o => o.Info)
            .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(o => resumeToken is null || string.CompareOrdinal(o.Key, resumeToken) > 0)
            .Take(pageSize + 1)
            .ToList();
        var more = page.Count > pageSize;
        if (more) page.RemoveAt(page.Count - 1);
        return Task.FromResult(new ObjectListingPage(page, more ? page[^1].Key : null));
    }

    public Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        Calls.Add("read:" + key);
        if (!objects.TryGetValue(key, out var o)) throw new ObjectNotFoundException(key);
        var truncated = o.Bytes.LongLength > maxBytes;
        var bytes = truncated ? o.Bytes[..(int)maxBytes] : o.Bytes;
        return Task.FromResult(new ObjectContent(bytes, truncated, o.ContentType, o.ContentEncoding));
    }

    public Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        Calls.Add("head:" + key);
        return Task.FromResult(objects.TryGetValue(key, out var o) ? o.Info : null);
    }
}
