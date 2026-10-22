using System.Runtime.CompilerServices;
using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Storage.Dropbox;

/// <summary>Ingests a Dropbox folder through its list-folder cursor, including deletions and filter changes.</summary>
/// <remarks>
/// Item ids and the sync scope include the account's namespace, so two accounts with the same tenant and root path never share
/// checkpoint state or overwrite each other's documents downstream. Use the account's Dropbox id
/// (<see cref="DropboxObjectStoreClient.GetAccountIdAsync"/>), which is stable and not secret.
/// </remarks>
public sealed class DropboxConnector : IIncrementalSourceConnector
{
    private const long DefaultMaxFileSizeBytes = 50L * 1024 * 1024;
    private readonly DropboxObjectStoreClient _client;
    private readonly string _tenantContext;
    private readonly int _pageSize;
    private readonly FileFilter _filter;
    private readonly ObjectStorageConnector _fetcher;
    private readonly string _idPrefix;
    private readonly string _container;

    /// <param name="accountNamespace">A stable, non-secret name for the Dropbox account, normally its account id
    /// (<c>dbid:...</c>, from <see cref="DropboxObjectStoreClient.GetAccountIdAsync"/>). Required: the root path alone does not
    /// tell accounts apart.</param>
    public DropboxConnector(
        DropboxObjectStoreClient client,
        string tenantContext,
        string accountNamespace,
        FileFilterOptions? filter = null,
        int pageSize = 1000)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountNamespace);
        if (accountNamespace.Trim('/').Length == 0)
            throw new ArgumentException("The account namespace must name the account.", nameof(accountNamespace));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _client = client;
        _tenantContext = tenantContext;
        _pageSize = pageSize;
        var effectiveFilter = filter ?? new FileFilterOptions();
        _filter = new FileFilter(effectiveFilter with { MaxFileSizeBytes = effectiveFilter.MaxFileSizeBytes ?? DefaultMaxFileSizeBytes });
        // The fetcher builds the same prefix from the same namespace, so it accepts exactly the ids this connector hands out.
        _fetcher = new ObjectStorageConnector(client, tenantContext, new ObjectStorageOptions(Filter: effectiveFilter, Namespace: accountNamespace));
        _container = accountNamespace.Trim('/') + "/" + client.Container;
        _idPrefix = $"dropbox://{_container}/";
        SyncScope = $"dropbox:{tenantContext}:{_container}";
    }

    public string SyncScope { get; }

    public bool ReportsDeletions => true;

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? cursor = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _client.ListFolderPageAsync(cursor, _pageSize, cancellationToken).ConfigureAwait(false);
            foreach (var entry in page.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Tag == "file" && entry.Info is { } info && TryCreateItem(info, out var item))
                    yield return item;
            }
            if (!page.HasMore)
                yield break;
            cursor = page.Cursor;
        }
    }

    public async IAsyncEnumerable<SourceChange> GetChangesAsync(
        string? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var currentCursor = cursor;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DropboxListPage? page = null;
            DropboxCursorResetException? cursorReset = null;
            try
            {
                page = await _client.ListFolderPageAsync(currentCursor, _pageSize, cancellationToken).ConfigureAwait(false);
            }
            catch (DropboxCursorResetException ex)
            {
                cursorReset = ex;
            }
            if (cursorReset is not null)
            {
                yield return new SourceChange.Reset(cursorReset.Message);
                currentCursor = null;
                continue;
            }
            var currentPage = page ?? throw new InvalidOperationException("Dropbox returned no listing page.");

            foreach (var entry in currentPage.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Tag == "deleted")
                {
                    if (entry.Key is not null)
                        yield return new SourceChange.Delete(SourceRef(entry.Key), DeletionReason.Deleted, _tenantContext);
                    continue;
                }

                if (entry.Tag != "file" || entry.Info is not { } info)
                    continue;
                if (TryCreateItem(info, out var item))
                    yield return new SourceChange.Upsert(item);
                else
                    yield return new SourceChange.Delete(SourceRef(info.Key), DeletionReason.OutOfScope, _tenantContext);
            }

            yield return new SourceChange.Checkpoint(currentPage.Cursor);
            if (!currentPage.HasMore)
                yield break;
            currentCursor = currentPage.Cursor;
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
        _fetcher.FetchAsync(item, cancellationToken);

    private bool TryCreateItem(ObjectInfo info, out SourceItem item)
    {
        item = default!;
        var filterPath = info.Key.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ? info.Key[..^3] : info.Key;
        if (!_filter.Includes(filterPath, info.Size))
            return false;

        var displayName = info.Key[(info.Key.LastIndexOf('/') + 1)..];
        if (displayName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            displayName = displayName[..^3];
        var id = SourceRef(info.Key);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SourceKeys.Url] = id,
            [SourceKeys.Container] = _container,
            [SourceKeys.AclCaptured] = "false",
        };
        if (info.ETag is not null)
        {
            metadata["storage.etag"] = info.ETag;
            metadata[SourceKeys.Version] = info.ETag;
        }

        item = new SourceItem(id, displayName, Extension(displayName), info.Size, info.LastModified, _tenantContext, metadata);
        return true;
    }

    private string SourceRef(string key) => _idPrefix + key;

    private static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 || dot == fileName.Length - 1 ? "" : fileName[(dot + 1)..].ToLowerInvariant();
    }
}
