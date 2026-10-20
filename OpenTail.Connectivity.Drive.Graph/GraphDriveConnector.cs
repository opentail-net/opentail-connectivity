using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace OpenTail.Connectivity.Drive.Graph;

/// <summary>
/// Reads one SharePoint library or OneDrive through Graph's delta query: the first run lists everything, later runs read only the
/// changes, deletions included. Read-only, and refuses a token that grants write access.
/// </summary>
/// <remarks>
/// One code path: delta with no token lists the drive, the same query with a token returns changes since. A checkpoint follows
/// every page, holding the next link (or, after the last page, the delta link), so a long first run resumes where it stopped.
/// Deleted files and files that no longer pass the filter are reported as deletions. Folders produce no document; OneNote
/// notebooks (<c>package</c>) and shortcuts to other drives (<c>remoteItem</c>) are skipped. Delta results carry no paths, so
/// filter patterns match file names only. The same item may appear more than once in a feed; the stable ids make the repeat an
/// overwrite. Download URLs are fetched per file and downloaded without the Graph token.
/// </remarks>
public sealed class GraphDriveConnector : IIncrementalSourceConnector
{
    private const string Scope = "https://graph.microsoft.com/.default";

    // A token with any of these may write. The grant is the control; this refuses to run with one rather than trusting it.
    private static readonly string[] WritePermissions =
    [
        "Files.ReadWrite", "Files.ReadWrite.All", "Files.ReadWrite.AppFolder", "Sites.ReadWrite.All", "Sites.Manage.All",
        "Sites.FullControl.All",
    ];

    private const string Select =
        "id,name,size,lastModifiedDateTime,webUrl,file,folder,deleted,package,remoteItem,createdBy,parentReference";

    private readonly TokenCredential _credential;
    private readonly GraphDriveTarget _target;
    private readonly string _tenantContext;
    private readonly GraphDriveOptions _options;
    private readonly HttpClient _graph;
    private readonly HttpClient _download;
    private readonly FileFilter _filter;
    private readonly Uri _baseUri;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private AccessToken? _token;
    private string? _driveId;

    /// <param name="graphClient">For Graph calls; the connector adds the token per request. Tests pass a stubbed one.</param>
    /// <param name="downloadClient">For pre-authenticated download URLs on other hosts. Must not carry an Authorization header.</param>
    public GraphDriveConnector(
        TokenCredential credential,
        GraphDriveTarget target,
        string tenantContext,
        GraphDriveOptions? options = null,
        HttpClient? graphClient = null,
        HttpClient? downloadClient = null)
        : this(credential, target, tenantContext, options, graphClient, downloadClient, delay: null)
    {
    }

    internal GraphDriveConnector(
        TokenCredential credential,
        GraphDriveTarget target,
        string tenantContext,
        GraphDriveOptions? options,
        HttpClient? graphClient,
        HttpClient? downloadClient,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _tenantContext = tenantContext;
        _options = options ?? new GraphDriveOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PageSize, 1);
        _graph = graphClient ?? new HttpClient();
        _download = downloadClient ?? new HttpClient();
        if (_download.DefaultRequestHeaders.Authorization is not null)
            throw new ArgumentException("The download client must not carry an Authorization header: download URLs are on other hosts.", nameof(downloadClient));
        var filter = _options.Filter ?? new FileFilterOptions();
        _filter = new FileFilter(filter with { MaxFileSizeBytes = filter.MaxFileSizeBytes ?? 50L * 1024 * 1024 });
        _baseUri = new Uri(_options.GraphBaseUrl.TrimEnd('/') + "/");
        _delay = delay ?? Task.Delay;
    }

    public string SyncScope => $"graph-drive:{_tenantContext}:{_target.Key}";

    public bool ReportsDeletions => true;

    public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var driveId = await ResolveDriveAsync(cancellationToken).ConfigureAwait(false);
        var saved = cursor is null ? null : JsonSerializer.Deserialize<DeltaCursor>(cursor);
        Uri link;
        if (saved is null)
        {
            link = FreshDelta(driveId);
        }
        else if (!string.Equals(saved.DriveId, driveId, StringComparison.Ordinal))
        {
            // The target now resolves to another drive (a library recreated under the same name): its old token means nothing.
            yield return new SourceChange.Reset("the target resolves to a different drive");
            link = FreshDelta(driveId);
        }
        else
        {
            link = OnGraph(saved.Link);
        }

        var throttled = TimeSpan.Zero;
        while (true)
        {
            using var response = await SendAsync(link, throttledSoFar: throttled, cancellationToken).ConfigureAwait(false);
            throttled = response.Throttled;
            if (response.Message.StatusCode == HttpStatusCode.Gone)
            {
                // The token expired or the service needs a fresh enumeration; Location starts one.
                var restart = response.Message.Headers.Location is { } location
                    ? OnGraph(location.IsAbsoluteUri ? location.ToString() : new Uri(link, location).ToString())
                    : FreshDelta(driveId);
                yield return new SourceChange.Reset("graph delta token expired; listing the drive again");
                link = restart;
                continue;
            }

            await EnsureSuccessAsync(response.Message, cancellationToken).ConfigureAwait(false);
            var page = JsonNode.Parse(await response.Message.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            foreach (var node in page["value"]?.AsArray() ?? [])
            {
                if (node is null) continue;
                if (Map(node, driveId) is { } change)
                    yield return change;
            }

            var next = page["@odata.nextLink"]?.GetValue<string>();
            var delta = page["@odata.deltaLink"]?.GetValue<string>();
            var resumeAt = next ?? delta ?? throw new InvalidDataException("A Graph delta page had neither a next link nor a delta link.");
            yield return new SourceChange.Checkpoint(JsonSerializer.Serialize(new DeltaCursor(driveId, OnGraph(resumeAt).ToString())));
            if (next is null)
                yield break;
            link = OnGraph(next);
        }
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in GetChangesAsync(null, cancellationToken).ConfigureAwait(false))
            if (change is SourceChange.Upsert upsert)
                yield return upsert.Item;
    }

    /// <summary>Gets a fresh download URL for the item, then downloads it without the Graph token, capped at the size limit.</summary>
    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (driveId, itemId) = SplitId(item.Id);
        for (var attempt = 0; ; attempt++)
        {
            var url = await DownloadUrlAsync(driveId, itemId, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _download.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0)
                continue; // the URL expired between listing and download: get a fresh one, once
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Downloading {item.DisplayName} failed with {(int)response.StatusCode}.");

            var max = _filter.Options.MaxFileSizeBytes ?? long.MaxValue;
            if (response.Content.Headers.ContentLength is { } length && length > max)
                throw new InvalidDataException($"{item.DisplayName} is larger than the {max}-byte limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > max)
                    throw new InvalidDataException($"{item.DisplayName} grew past the {max}-byte limit while downloading.");
                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private SourceChange? Map(JsonNode node, string driveId)
    {
        var itemId = node["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(itemId))
            return null;
        var id = driveId + ":" + itemId;

        if (node["deleted"] is not null)
            return node["folder"] is not null ? null : new SourceChange.Delete(id, DeletionReason.Deleted, _tenantContext);
        if (node["folder"] is not null || node["package"] is not null || node["remoteItem"] is not null || node["file"] is null)
            return null; // folders hold no text; notebooks and shortcuts to other drives are not followed

        var name = node["name"]?.GetValue<string>() ?? itemId;
        var size = node["size"]?.GetValue<long>() ?? 0;
        if (!_filter.Includes(name, size))
            return new SourceChange.Delete(id, DeletionReason.OutOfScope, _tenantContext); // grew too large, or renamed to an excluded type

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["graph.driveId"] = driveId,
            ["graph.itemId"] = itemId,
            [SourceKeys.AclCaptured] = "false",
        };
        if (node["webUrl"]?.GetValue<string>() is { Length: > 0 } webUrl) metadata[SourceKeys.Url] = webUrl;
        metadata[SourceKeys.Container] = _target switch
        {
            GraphDriveTarget.SiteLibrary s => s.SiteUrl.TrimEnd('/') + "/" + s.LibraryName,
            GraphDriveTarget.UserOneDrive u => "onedrive:" + u.UserPrincipalName,
            _ => "drive:" + driveId,
        };
        var createdBy = node["createdBy"]?["user"];
        if ((createdBy?["email"]?.GetValue<string>() ?? createdBy?["id"]?.GetValue<string>()) is { Length: > 0 } owner)
            metadata[SourceKeys.Owner] = owner;
        // cTag is left out of delta results on OneDrive for Business and SharePoint; the content hash is what changes with content.
        var hashes = node["file"]?["hashes"];
        if ((hashes?["quickXorHash"]?.GetValue<string>() ?? hashes?["sha256Hash"]?.GetValue<string>()) is { Length: > 0 } hash)
            metadata[SourceKeys.Version] = hash;

        var modified = node["lastModifiedDateTime"]?.GetValue<string>() is { } m
            && DateTimeOffset.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
        var extension = Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        return new SourceChange.Upsert(new SourceItem(id, name, extension, size, modified, _tenantContext, metadata));
    }

    private async Task<string> ResolveDriveAsync(CancellationToken ct)
    {
        if (_driveId is not null)
            return _driveId;

        _driveId = _target switch
        {
            GraphDriveTarget.Drive d => d.DriveId,
            GraphDriveTarget.UserOneDrive u => await IdAtAsync($"users/{Uri.EscapeDataString(u.UserPrincipalName)}/drive?$select=id", ct).ConfigureAwait(false),
            GraphDriveTarget.SiteLibrary s => await LibraryAsync(s, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException(_target.GetType().Name),
        };
        return _driveId;
    }

    private async Task<string> LibraryAsync(GraphDriveTarget.SiteLibrary target, CancellationToken ct)
    {
        var site = new Uri(target.SiteUrl);
        var siteId = await IdAtAsync($"sites/{site.Host}:{site.AbsolutePath.TrimEnd('/')}?$select=id", ct).ConfigureAwait(false);
        var drives = await GetJsonAsync($"sites/{siteId}/drives?$select=id,name", ct).ConfigureAwait(false);
        return drives["value"]?.AsArray()
                   .FirstOrDefault(d => string.Equals(d?["name"]?.GetValue<string>(), target.LibraryName, StringComparison.OrdinalIgnoreCase))?["id"]
                   ?.GetValue<string>()
               ?? throw new InvalidOperationException($"The site has no document library named '{target.LibraryName}'.");
    }

    private async Task<string> IdAtAsync(string relative, CancellationToken ct)
        => (await GetJsonAsync(relative, ct).ConfigureAwait(false))["id"]?.GetValue<string>()
           ?? throw new InvalidDataException("Graph returned no id for the drive target.");

    private async Task<string> DownloadUrlAsync(string driveId, string itemId, CancellationToken ct)
    {
        var item = await GetJsonAsync($"drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}?$select=id,@microsoft.graph.downloadUrl", ct, notFound: true)
            .ConfigureAwait(false);
        return item["@microsoft.graph.downloadUrl"]?.GetValue<string>()
               ?? throw new InvalidDataException("Graph returned no download URL for the item.");
    }

    private async Task<JsonNode> GetJsonAsync(string relative, CancellationToken ct, bool notFound = false)
    {
        using var response = await SendAsync(new Uri(_baseUri, relative), TimeSpan.Zero, ct).ConfigureAwait(false);
        if (notFound && response.Message.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("The item no longer exists in the drive.");
        await EnsureSuccessAsync(response.Message, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Message.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Sends with the Graph token, waiting out 429/503 <c>Retry-After</c> up to the run's ceiling.</summary>
    private async Task<Sent> SendAsync(Uri uri, TimeSpan throttledSoFar, CancellationToken ct)
    {
        var ceiling = _options.MaxThrottleWait ?? TimeSpan.FromMinutes(10);
        var throttled = throttledSoFar;
        while (true)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct).ConfigureAwait(false));
            var response = await _graph.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is not ((HttpStatusCode)429 or HttpStatusCode.ServiceUnavailable))
                return new Sent(response, throttled);

            var wait = response.Headers.RetryAfter?.Delta
                       ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(10));
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            response.Dispose();
            request.Dispose();
            if (throttled + wait > ceiling)
                throw new HttpRequestException($"Graph kept throttling the run past the {ceiling.TotalMinutes:0}-minute limit; the next run continues from the last checkpoint.");
            throttled += wait;
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is { } cached && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        var token = await _credential.GetTokenAsync(new TokenRequestContext([Scope]), ct).ConfigureAwait(false);
        RefuseWriteAccess(token.Token);
        _token = token;
        return token.Token;
    }

    /// <summary>Reads the token's <c>roles</c> (application) and <c>scp</c> (delegated) claims and refuses any write permission.</summary>
    internal static void RefuseWriteAccess(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return; // not a JWT (some test or proxy credentials); nothing to read
        JsonNode? claims;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            claims = JsonNode.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return;
        }

        var granted = new List<string>();
        if (claims?["roles"] is JsonArray roles)
            granted.AddRange(roles.Select(r => r?.GetValue<string>() ?? string.Empty));
        if (claims?["scp"]?.GetValue<string>() is { } scp)
            granted.AddRange(scp.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var write = granted.Where(g => WritePermissions.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
        if (write.Count > 0)
            throw new InvalidOperationException(
                $"The Graph token grants write access ({string.Join(", ", write)}). This connector only reads: grant Sites.Selected (read) or Files.Read.All instead.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var code = JsonNodeOrNull(body)?["error"]?["code"]?.GetValue<string>();
        throw new HttpRequestException($"Graph returned {(int)response.StatusCode}{(code is null ? "" : " (" + code + ")")}.", null, response.StatusCode);
    }

    private static JsonNode? JsonNodeOrNull(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }

    private Uri FreshDelta(string driveId)
        => new(_baseUri, $"drives/{Uri.EscapeDataString(driveId)}/root/delta?$select={Select}&$top={_options.PageSize}");

    /// <summary>A stored or returned link must stay on the Graph host, so a tampered state file cannot send the token elsewhere.</summary>
    private Uri OnGraph(string link)
    {
        var uri = new Uri(link, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A delta link points outside the Graph host; refusing to send the token there.");
        return uri;
    }

    private static (string DriveId, string ItemId) SplitId(string id)
    {
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == id.Length - 1)
            throw new ArgumentException("Not a Graph drive item id (<driveId>:<itemId>).", nameof(id));
        return (id[..colon], id[(colon + 1)..]);
    }

    private sealed record DeltaCursor(string DriveId, string Link);

    private readonly record struct Sent(HttpResponseMessage Message, TimeSpan Throttled) : IDisposable
    {
        public void Dispose() => Message.Dispose();
    }
}
