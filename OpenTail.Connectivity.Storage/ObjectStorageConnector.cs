using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;

namespace OpenTail.Connectivity.Storage;

/// <summary>Ingests objects from any <see cref="IObjectStoreClient"/> using timestamp watermarks.</summary>
public sealed class ObjectStorageConnector : IIncrementalSourceConnector
{
    private const long DefaultMaxFileSizeBytes = 50L * 1024 * 1024;
    private static readonly JsonSerializerOptions CursorJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IObjectStoreClient _client;
    private readonly string _tenantContext;
    private readonly ObjectStorageOptions _options;
    private readonly FileFilter _filter;
    private readonly string _idPrefix;
    private readonly string _containerScope;

    public ObjectStorageConnector(IObjectStoreClient client, string tenantContext, ObjectStorageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _client = client;
        _tenantContext = tenantContext;
        _options = options ?? new ObjectStorageOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PageSize, 1);
        if (_options.WatermarkOverlap < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "WatermarkOverlap cannot be negative.");
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxBytesPerRun ?? 0);

        var filter = _options.Filter ?? new FileFilterOptions();
        _filter = new FileFilter(filter with { MaxFileSizeBytes = filter.MaxFileSizeBytes ?? DefaultMaxFileSizeBytes });
        var container = string.IsNullOrWhiteSpace(_options.Namespace) ? client.Container : _options.Namespace.Trim('/') + "/" + client.Container;
        _idPrefix = $"{client.Scheme}://{container}/";
        _containerScope = container + (_options.Prefix.Length == 0 ? "" : "/" + _options.Prefix.Trim('/'));
        SyncScope = $"storage:{tenantContext}:{client.Scheme}://{container}/{_options.Prefix}";
    }

    public string SyncScope { get; }

    public bool ReportsDeletions => false;

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? resumeToken = null;
        do
        {
            var page = await _client.ListAsync(_options.Prefix, resumeToken, _options.PageSize, cancellationToken).ConfigureAwait(false);
            foreach (var info in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryCreateItem(info, out var item))
                    yield return item;
            }
            resumeToken = page.NextResumeToken;
        } while (resumeToken is not null);
    }

    public async IAsyncEnumerable<SourceChange> GetChangesAsync(
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var state = ReadCursor(cursor);
        var watermark = state.Watermark;
        var maxSeen = state.MaxSeen;
        var resumeToken = state.ResumeToken;
        var lastKey = state.LastKey;
        long bytesThisRun = 0;
        var overlap = _options.WatermarkOverlap ?? TimeSpan.FromHours(1);

        while (true)
        {
            var pageResumeToken = resumeToken;
            var page = await _client.ListAsync(_options.Prefix, pageResumeToken, _options.PageSize, cancellationToken).ConfigureAwait(false);
            foreach (var info in page.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (lastKey is not null && string.CompareOrdinal(info.Key, lastKey) <= 0)
                    continue;
                lastKey = null;
                if (maxSeen is null || info.LastModified > maxSeen)
                    maxSeen = info.LastModified;

                if (!TryCreateItem(info, out var item) || (watermark is { } w && info.LastModified < SubtractOverlap(w, overlap)))
                    continue;

                yield return new SourceChange.Upsert(item);
                bytesThisRun = SaturatingAdd(bytesThisRun, info.Size);
                if (_options.MaxBytesPerRun is { } budget && bytesThisRun > budget)
                {
                    yield return new SourceChange.Checkpoint(WriteCursor(new Cursor(watermark, maxSeen, pageResumeToken, info.Key)));
                    yield break;
                }
            }

            resumeToken = page.NextResumeToken;
            if (resumeToken is not null)
            {
                yield return new SourceChange.Checkpoint(WriteCursor(new Cursor(watermark, maxSeen, resumeToken, null)));
                continue;
            }

            yield return new SourceChange.Checkpoint(WriteCursor(new Cursor(maxSeen, null, null, null)));
            yield break;
        }
    }

    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!string.Equals(item.TenantContext, _tenantContext, StringComparison.Ordinal))
            throw new ArgumentException("The source item does not belong to this tenant.", nameof(item));
        if (!item.Id.StartsWith(_idPrefix, StringComparison.Ordinal))
            throw new ArgumentException("The source item does not belong to this object store.", nameof(item));
        var key = item.Id[_idPrefix.Length..];
        if (key.Length == 0 || !key.StartsWith(_options.Prefix, StringComparison.Ordinal))
            throw new ArgumentException("The source item is outside the configured object-storage prefix.", nameof(item));

        var maxFileSize = _filter.Options.MaxFileSizeBytes ?? DefaultMaxFileSizeBytes;
        var content = await _client.ReadAsync(key, maxFileSize, cancellationToken).ConfigureAwait(false);
        if (content.Truncated)
            throw new InvalidDataException($"'{key}' is larger than the configured object size limit ({maxFileSize} bytes).");

        if (key.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(content.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (bytes, cut) = ObjectStoreReader.Gunzip(content.Bytes, maxFileSize, inputTruncated: false);
                if (cut)
                    throw new InvalidDataException($"'{key}' expands beyond the configured object size limit ({maxFileSize} bytes).");
                return bytes;
            }
            catch (ObjectStoreRefusedException ex)
            {
                throw new InvalidDataException($"'{key}' is not a valid gzip object.", ex);
            }
        }

        return content.Bytes;
    }

    private bool TryCreateItem(ObjectInfo info, out SourceItem item)
    {
        item = default!;
        if (!info.Key.StartsWith(_options.Prefix, StringComparison.Ordinal) || info.IsFolderPlaceholder || info.NeedsRestore ||
            (info.CostsExtraToRead && !_options.ReadColdStorage))
            return false;

        var relativeKey = info.Key[_options.Prefix.Length..];
        var filterPath = relativeKey.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? relativeKey[..^3] : relativeKey;
        if (!_filter.Includes(filterPath, info.Size))
            return false;

        var displayName = info.Key[(info.Key.LastIndexOf('/') + 1)..];
        if (displayName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            displayName = displayName[..^3];
        var extension = Extension(displayName);
        var id = _idPrefix + info.Key;
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceKeys.Url] = id,
            [SourceKeys.Container] = _containerScope,
            [SourceKeys.AclCaptured] = "false",
        };
        if (info.ETag is not null)
        {
            metadata["storage.etag"] = info.ETag;
            metadata[SourceKeys.Version] = info.ETag;
        }
        if (info.StorageClass is not null)
            metadata["storage.class"] = info.StorageClass;

        item = new SourceItem(id, displayName, extension, info.Size, info.LastModified, _tenantContext, metadata);
        return true;
    }

    private static Cursor ReadCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
            return new Cursor(null, null, null, null);
        try
        {
            return JsonSerializer.Deserialize<Cursor>(cursor, CursorJson) ?? throw new JsonException("Cursor was empty.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The object storage cursor is invalid.", nameof(cursor), ex);
        }
    }

    private static string WriteCursor(Cursor cursor) => JsonSerializer.Serialize(cursor, CursorJson);

    private static DateTimeOffset SubtractOverlap(DateTimeOffset value, TimeSpan overlap) =>
        value.UtcTicks < DateTimeOffset.MinValue.UtcTicks + overlap.Ticks ? DateTimeOffset.MinValue : value - overlap;

    private static long SaturatingAdd(long left, long right) => right > long.MaxValue - left ? long.MaxValue : left + right;

    private static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? "" : fileName[(dot + 1)..].ToLowerInvariant();
    }

    private sealed record Cursor(DateTimeOffset? Watermark, DateTimeOffset? MaxSeen, string? ResumeToken, string? LastKey);
}

/// <param name="Namespace">A stable, non-secret name for the account behind the client (for example a Dropbox account id),
/// put before <see cref="IObjectStoreClient.Container"/> in item ids and the sync scope. Set it when two sources could otherwise
/// have the same scheme and container.</param>
public sealed record ObjectStorageOptions(
    string Prefix = "",
    FileFilterOptions? Filter = null,
    TimeSpan? WatermarkOverlap = null,
    long? MaxBytesPerRun = null,
    bool ReadColdStorage = false,
    int PageSize = 1000,
    string? Namespace = null);
