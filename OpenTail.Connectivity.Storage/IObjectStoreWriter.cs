namespace OpenTail.Connectivity.Storage;

/// <summary>Writes to one bucket or container. Kept separate from <see cref="IObjectStoreClient"/> on purpose.</summary>
public interface IObjectStoreWriter
{
    string Scheme { get; }
    string Container { get; }

    /// <summary>Writes an object. Without <paramref name="overwrite"/>, an existing key throws <see cref="ObjectExistsException"/>.</summary>
    Task<ObjectInfo> PutAsync(
        string key,
        Stream content,
        string? contentType = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the object. False means that the key did not exist.</summary>
    Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class ObjectExistsException(string key) : Exception($"Object '{key}' already exists.")
{
    public string Key { get; } = key;
}

/// <summary>Restricts writes to configured key prefixes and a byte budget before forwarding them.</summary>
public sealed class ObjectStoreWriterGuard : IObjectStoreWriter
{
    private readonly IObjectStoreWriter _inner;
    private readonly IReadOnlyList<string> _prefixes;
    private readonly long _maxBytes;

    public ObjectStoreWriterGuard(IObjectStoreWriter inner, IReadOnlyList<string> prefixes, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(prefixes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);
        if (prefixes.Count == 0)
            throw new ArgumentException("At least one allowed prefix is required.", nameof(prefixes));
        _inner = inner;
        _prefixes = prefixes;
        _maxBytes = maxBytes;
    }

    public string Scheme => _inner.Scheme;
    public string Container => _inner.Container;

    public async Task<ObjectInfo> PutAsync(
        string key,
        Stream content,
        string? contentType = null,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        if (!_prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            throw Refused(key, "is outside the allowed prefixes");

        using var copy = new MemoryStream();
        var buffer = new byte[81920];
        var limit = _maxBytes + 1;
        while (copy.Length < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, limit - copy.Length);
            var read = await content.ReadAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (read > _maxBytes - copy.Length)
                throw Refused(key, $"exceeds the {_maxBytes}-byte write limit");
            await copy.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        copy.Position = 0;
        return await _inner.PutAsync(key, copy, contentType, overwrite, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        if (!_prefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal)))
            throw Refused(key, "is outside the allowed prefixes");
        return _inner.DeleteAsync(key, cancellationToken);
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.StartsWith('/') || key.Split('/').Any(segment => segment == ".."))
            throw Refused(key, "must be a relative key without '..' segments");
    }

    private static ObjectStoreRefusedException Refused(string key, string reason) => new($"Key '{key}' {reason}.");
}

/// <summary>A writer backed by an <see cref="InMemoryObjectStoreClient"/>.</summary>
public sealed class InMemoryObjectStoreWriter(InMemoryObjectStoreClient client) : IObjectStoreWriter
{
    private readonly InMemoryObjectStoreClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public string Scheme => _client.Scheme;
    public string Container => _client.Container;

    public async Task<ObjectInfo> PutAsync(string key, Stream content, string? contentType = null, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return _client.PutFromWriter(key, buffer.ToArray(), contentType, overwrite);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_client.RemoveFromWriter(key));
    }
}
