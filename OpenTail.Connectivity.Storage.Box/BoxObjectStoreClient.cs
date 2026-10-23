using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Storage.Box;

/// <summary>Read-only access to a caller-selected Box folder tree.</summary>
/// <remarks>
/// Supply an access token obtained and refreshed by the caller. The configured folder ID is the root of this adapter's
/// namespace; folder IDs remain internal while relative keys are derived from Box item names. Folder listings use Box's
/// marker cursors and object-store listings use key-based resume tokens. Subsequent pages reuse one in-process snapshot;
/// resuming after process restart re-discovers the tree from the last key. Discovery is capped at 100,000 items by default;
/// set <c>maxSnapshotItems</c> to adjust the limit.
/// If supplying an <see cref="HttpClient"/>, configure its handler with automatic redirects disabled so the adapter can inspect
/// Box's download redirect and avoid forwarding the access token to the download host.
/// </remarks>
public sealed class BoxObjectStoreClient : IObjectStoreClient, IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.box.com/2.0/");
    private const int PageLimit = 1000;
    private const int DefaultMaxSnapshotItems = 100_000;
    private const int MaxDownloadRedirects = 5;
    private readonly string _rootFolderId;
    private readonly string _accessToken;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly int _maxSnapshotItems;
    private readonly object _snapshotLock = new();
    private ListingSnapshot? _snapshot;

    /// <param name="accountNamespace">A stable, non-secret name for the account, normally its Box user id
    /// (<see cref="GetAccountIdAsync"/>). Required for the root folder: its id is 0 in every account, so without it two accounts
    /// would share item ids and sync state. Other folder ids are unique across Box.</param>
    public BoxObjectStoreClient(string accessToken, string rootFolderId, HttpClient? http = null, int maxSnapshotItems = DefaultMaxSnapshotItems,
        string? accountNamespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnapshotItems, 1);
        ValidateId(rootFolderId, nameof(rootFolderId));
        _accessToken = accessToken;
        _rootFolderId = rootFolderId;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = http is null;
        _maxSnapshotItems = maxSnapshotItems;
        if (rootFolderId == RootFolderId && string.IsNullOrWhiteSpace(accountNamespace))
            throw new ArgumentException("Box's root folder id is 0 in every account: pass the account's id (GetAccountIdAsync) as accountNamespace.",
                nameof(accountNamespace));
        Container = string.IsNullOrWhiteSpace(accountNamespace) ? rootFolderId : accountNamespace.Trim('/') + "/" + rootFolderId;
    }

    private const string RootFolderId = "0";

    /// <summary>The signed-in user's Box id: stable, not secret, and the namespace for the root folder.</summary>
    public static async Task<string> GetAccountIdAsync(string accessToken, HttpClient? http = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var client = http ?? new HttpClient();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoot, "users/me?fields=id"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return GetString(document.RootElement, "id") is { Length: > 0 } id ? id : throw new InvalidDataException("Box returned no user id.");
        }
        finally
        {
            if (http is null) client.Dispose();
        }
    }

    public string Scheme => "box";

    public string Container { get; }

    public async Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ValidateKey(prefix, allowEmpty: true, nameof(prefix));
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (resumeToken is null)
        {
            lock (_snapshotLock)
                _snapshot = null;
        }

        var cursor = ParseCursor(resumeToken);
        ListingSnapshot? snapshot = null;
        if (cursor is { } parsed)
        {
            lock (_snapshotLock)
                if (_snapshot is { } cached && cached.Id == parsed.Id && cached.Prefix == prefix)
                    snapshot = cached;
        }

        if (snapshot is null)
        {
            var lastKey = cursor?.LastKey ?? resumeToken;
            if (lastKey is not null)
                ValidateKey(lastKey, allowEmpty: false, nameof(resumeToken));
            var all = await EnumerateAsync(Math.Min(pageSize, PageLimit), cancellationToken).ConfigureAwait(false);
            var matching = all.Where(file => file.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(file => lastKey is null || string.CompareOrdinal(file.Key, lastKey) > 0)
                .OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => file.Info).ToArray();
            return Page(matching, 0, pageSize, prefix, cursor?.Id);
        }

        var start = Array.FindIndex(snapshot.Objects, item => string.CompareOrdinal(item.Key, cursor!.Value.LastKey) > 0);
        if (start < 0)
            start = snapshot.Objects.Length;
        return Page(snapshot.Objects, start, pageSize, prefix, snapshot.Id);
    }

    private ObjectListingPage Page(ObjectInfo[] objects, int start, int pageSize, string prefix, Guid? existingId)
    {
        var page = objects.Skip(start).Take(pageSize).ToArray();
        if (objects.Length <= start + page.Length)
        {
            ClearSnapshot(existingId);
            return new ObjectListingPage(page, null);
        }

        var id = existingId ?? Guid.NewGuid();
        var token = MakeCursor(id, page[^1].Key);
        lock (_snapshotLock)
            _snapshot = new ListingSnapshot(id, prefix, objects);
        return new ObjectListingPage(page, token);
    }

    private void ClearSnapshot(Guid? id)
    {
        if (id is null)
            return;
        lock (_snapshotLock)
            if (_snapshot?.Id == id)
                _snapshot = null;
    }

    private static string MakeCursor(Guid id, string key) => $"v1|{id:N}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";

    private static (Guid Id, string LastKey)? ParseCursor(string? token)
    {
        var parts = token?.Split('|');
        if (parts is not { Length: 3 } || parts[0] != "v1" || !Guid.TryParseExact(parts[1], "N", out var id))
            return null;
        var encoded = parts[2].Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        try
        {
            var key = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            ValidateKey(key, allowEmpty: false, nameof(token));
            return (id, key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("The Box listing cursor is invalid.", nameof(token), ex);
        }
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, allowEmpty: false, nameof(key));
        var item = await ResolveKeyAsync(key, cancellationToken).ConfigureAwait(false);
        return item is { Type: "file" } ? item.ToInfo(key) : null;
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, allowEmpty: false, nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);

        var file = await ResolveKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (file is not { Type: "file" })
            throw new ObjectNotFoundException(key);

        using var response = await SendDownloadAsync(file.Id, maxBytes, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ObjectNotFoundException(key);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return new ObjectContent([], Truncated: false, ContentType: response.Content.Headers.ContentType?.ToString());
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var limit = maxBytes + 1;
        var chunk = new byte[81920];
        while (buffer.Length < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(chunk.Length, limit - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }

        var truncated = buffer.Length > maxBytes;
        var bytes = buffer.ToArray();
        if (truncated)
            Array.Resize(ref bytes, checked((int)maxBytes));
        return new ObjectContent(bytes, truncated, response.Content.Headers.ContentType?.ToString(),
            response.Content.Headers.ContentEncoding.Count == 0 ? null : string.Join(",", response.Content.Headers.ContentEncoding));
    }

    public void Dispose()
    {
        lock (_snapshotLock)
            _snapshot = null;
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<List<DiscoveredFile>> EnumerateAsync(int pageLimit, CancellationToken cancellationToken)
    {
        var files = new List<DiscoveredFile>();
        var pending = new Stack<FolderNode>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var discoveredCount = 0;
        pending.Push(new FolderNode(_rootFolderId, ""));

        while (pending.TryPop(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(folder.Id))
                continue;

            string? marker = null;
            var seenMarkers = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var page = await ListFolderPageAsync(folder.Id, marker, pageLimit, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (discoveredCount >= _maxSnapshotItems)
                        throw new InvalidOperationException($"Box listing exceeded the configured limit of {_maxSnapshotItems} discovered items.");
                    discoveredCount++;
                    if (item.Type == "folder")
                    {
                        pending.Push(new FolderNode(item.Id, JoinKey(folder.Key, item.Name)));
                    }
                    else if (item.Type == "file")
                    {
                        var key = JoinKey(folder.Key, item.Name);
                        var info = item.ToInfo(key);
                        files.Add(new DiscoveredFile(key, item.Id, info));
                    }
                }

                if (string.IsNullOrEmpty(page.NextMarker))
                    break;
                if (!seenMarkers.Add(page.NextMarker))
                    throw new InvalidDataException("Box returned a repeated folder listing marker.");
                marker = page.NextMarker;
            }
        }

        return files;
    }

    private async Task<BoxItem?> ResolveKeyAsync(string key, CancellationToken cancellationToken)
    {
        var segments = key.Split('/');
        var folderId = _rootFolderId;
        for (var index = 0; index < segments.Length; index++)
        {
            var item = await FindChildAsync(folderId, segments[index], cancellationToken).ConfigureAwait(false);
            if (item is null)
                return null;
            if (index == segments.Length - 1)
                return item;
            if (item.Type != "folder")
                return null;
            folderId = item.Id;
        }
        return null;
    }

    private async Task<BoxItem?> FindChildAsync(string folderId, string name, CancellationToken cancellationToken)
    {
        string? marker = null;
        var seenMarkers = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var page = await ListFolderPageAsync(folderId, marker, PageLimit, cancellationToken).ConfigureAwait(false);
            var found = page.Entries.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (found is not null)
                return found;
            if (string.IsNullOrEmpty(page.NextMarker))
                return null;
            if (!seenMarkers.Add(page.NextMarker))
                throw new InvalidDataException("Box returned a repeated folder listing marker.");
            marker = page.NextMarker;
        }
    }

    private async Task<FolderPage> ListFolderPageAsync(string folderId, string? marker, int pageLimit, CancellationToken cancellationToken)
    {
        var markerQuery = marker is null ? "" : "&marker=" + Uri.EscapeDataString(marker);
        var uri = new Uri(ApiRoot, $"folders/{Uri.EscapeDataString(folderId)}/items?limit={pageLimit.ToString(CultureInfo.InvariantCulture)}&usemarker=true{markerQuery}&fields=id%2Ctype%2Cname%2Csize%2Cmodified_at%2Cetag%2Csha1");
        using var request = CreateApiRequest(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var entriesElement = root.TryGetProperty("entries", out var entries) ? entries : default;
        var result = new List<BoxItem>();
        if (entriesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entriesElement.EnumerateArray())
            {
                var type = GetString(entry, "type");
                if (type is not ("file" or "folder"))
                    continue;
                var id = GetString(entry, "id") ?? throw new InvalidDataException("Box returned an item without an ID.");
                ValidateId(id, "id");
                var name = GetString(entry, "name") ?? throw new InvalidDataException("Box returned an item without a name.");
                ValidateName(name);
                var size = entry.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) ? Math.Max(0, parsedSize) : 0;
                var modified = DateTimeOffset.TryParse(GetString(entry, "modified_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedModified)
                    ? parsedModified : DateTimeOffset.UnixEpoch;
                result.Add(new BoxItem(id, type, name, size, modified, GetString(entry, "etag"), GetString(entry, "sha1")));
            }
        }

        var nextMarker = GetString(root, "next_marker");
        return new FolderPage(result, nextMarker);
    }

    private async Task<HttpResponseMessage> SendDownloadAsync(string fileId, long maxBytes, CancellationToken cancellationToken)
    {
        var uri = new Uri(ApiRoot, $"files/{Uri.EscapeDataString(fileId)}/content");
        var rangeEnd = maxBytes;
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, rangeEnd);
            if (uri.Host.Equals(ApiRoot.Host, StringComparison.OrdinalIgnoreCase))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            if (redirectCount >= MaxDownloadRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HttpRequestException("Box returned an invalid or excessive download redirect.");
            }

            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            response.Dispose();
            if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps ||
                !(target.Host.Equals("dl.boxcloud.com", StringComparison.OrdinalIgnoreCase) || target.Host.EndsWith(".boxcloud.com", StringComparison.OrdinalIgnoreCase)) ||
                !string.IsNullOrEmpty(target.UserInfo))
                throw new HttpRequestException("Box redirected the download outside its trusted HTTPS download hosts.");
            uri = target;
        }
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var status = response.StatusCode;
        var error = status switch
        {
            HttpStatusCode.Unauthorized => "Box rejected the access token (401); obtain a fresh token and retry.",
            HttpStatusCode.Forbidden => "Box denied access to this item (403); check the token scopes and folder permissions.",
            _ => $"Box API request failed with {(int)status} ({status}).",
        };
        throw new BoxApiException(error, status);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string JoinKey(string parent, string name) => parent.Length == 0 ? name : parent + "/" + name;

    private static void ValidateId(string id, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, parameterName);
        if (id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new ArgumentException("Box IDs may contain only ASCII letters, digits, '-' and '_'.", parameterName);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("Box returned an item with an unsafe name.");
    }

    private static void ValidateKey(string key, bool allowEmpty, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.Length == 0 && allowEmpty)
            return;
        if ((!allowEmpty && key.Length == 0) || key.StartsWith('/') || key.Contains('\\') ||
            key.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Box keys must be relative slash-separated paths without empty, '.' or '..' segments.", parameterName);
    }

    private sealed record FolderNode(string Id, string Key);
    private sealed record FolderPage(IReadOnlyList<BoxItem> Entries, string? NextMarker);
    private sealed record BoxItem(string Id, string Type, string Name, long Size, DateTimeOffset ModifiedAt, string? ETag, string? Sha1)
    {
        public ObjectInfo ToInfo(string key) => new(key, Size, ModifiedAt, ETag ?? Sha1);
    }

    private sealed record DiscoveredFile(string Key, string Id, ObjectInfo Info);
    private sealed record ListingSnapshot(Guid Id, string Prefix, ObjectInfo[] Objects);
}

public sealed class BoxApiException(string message, HttpStatusCode statusCode) : HttpRequestException(message, null, statusCode)
{
}
