using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;

namespace OpenTail.Connectivity.Drive.Google;

/// <summary>
/// Reads one Google Drive (a shared drive or a user's My Drive) through the Drive changes feed: the first run lists the drive,
/// later runs read changes, deletions included. Read-only, and refuses a token with a Drive scope broader than read-only.
/// </summary>
/// <remarks>
/// The changes feed cannot list what already exists, so a first run takes a start token <b>first</b>, lists the files, then switches
/// to the feed from that token. Changes made during the listing are replayed next run: repeats, never losses. The cursor records
/// the phase. A checkpoint follows every page. Trashed and removed files become deletions; so do files that fail the filter, and
/// files that leave the target (moved to another drive, or no longer owned by the user for My Drive), and files outside the root
/// folder. The cursor keeps a map of folder id to parent and name for building paths; a folder that moves or is renamed while
/// paths matter (a root folder or patterns) makes it list the drive again. Folders, shortcuts and
/// Google types other than Docs, Sheets and Slides produce nothing. Docs and Slides are exported as text, Sheets as .xlsx. Every
/// request carries the shared-drive flags: without them shared-drive files are silently invisible.
/// </remarks>
public sealed class GoogleDriveConnector : IIncrementalSourceConnector
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string TokenInfo = "https://oauth2.googleapis.com/tokeninfo";
    private const string Folder = "application/vnd.google-apps.folder";
    private const string GooglePrefix = "application/vnd.google-apps.";
    private const string FileFields = "id,name,mimeType,size,modifiedTime,webViewLink,md5Checksum,version,trashed,driveId,ownedByMe,owners(emailAddress),parents";

    private static readonly Dictionary<string, string> DefaultExports = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] = "text/plain",
        ["application/vnd.google-apps.presentation"] = "text/plain",
        ["application/vnd.google-apps.spreadsheet"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
    };

    private static readonly Dictionary<string, string> ExportExtensions = new(StringComparer.Ordinal)
    {
        ["text/plain"] = "txt",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "xlsx",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "docx",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "pptx",
        ["application/pdf"] = "pdf",
        ["text/csv"] = "csv",
        ["text/html"] = "html",
        ["text/markdown"] = "md",
    };

    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly GoogleDriveTarget _target;
    private readonly string _tenantContext;
    private readonly GoogleDriveOptions _options;
    private readonly HttpClient _http;
    private readonly FileFilter _filter;
    private readonly IReadOnlyDictionary<string, string> _exports;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string? _checkedToken;
    private string? _container;
    private readonly string? _rootFolderId;
    private bool _rootChecked;

    /// <param name="credential">Already scoped; see <see cref="GoogleDriveCredentials"/>.</param>
    public GoogleDriveConnector(GoogleCredential credential, GoogleDriveTarget target, string tenantContext,
        GoogleDriveOptions? options = null, HttpClient? http = null)
        : this(TokenFrom(credential ?? throw new ArgumentNullException(nameof(credential))), target, tenantContext, options, http, delay: null)
    {
    }

    internal GoogleDriveConnector(Func<CancellationToken, Task<string>> token, GoogleDriveTarget target, string tenantContext,
        GoogleDriveOptions? options, HttpClient? http, Func<TimeSpan, CancellationToken, Task>? delay)
    {
        _token = token;
        _target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _tenantContext = tenantContext;
        _options = options ?? new GoogleDriveOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.PageSize, 1);
        _http = http ?? new HttpClient();
        var filter = _options.Filter ?? new FileFilterOptions();
        _filter = new FileFilter(filter with { MaxFileSizeBytes = filter.MaxFileSizeBytes ?? 50L * 1024 * 1024 });
        _exports = _options.ExportFormats ?? DefaultExports;
        _delay = delay ?? Task.Delay;
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxTrackedFolders, 1);
        _rootFolderId = string.IsNullOrWhiteSpace(_options.RootFolderId) ? null : _options.RootFolderId.Trim();
    }

    public string SyncScope => $"gdrive:{_tenantContext}:{_target.Key}";

    public bool ReportsDeletions => true;

    public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await CheckRootFolderAsync(cancellationToken).ConfigureAwait(false);
        var state = cursor is null ? null : JsonSerializer.Deserialize<DriveCursor>(cursor);
        var throttled = TimeSpan.Zero;
        var account = await AccountAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null && account is not null && !string.Equals(state.Account, account, StringComparison.OrdinalIgnoreCase))
        {
            // Another user's change token and folder map, or a cursor from before accounts were recorded: never reused.
            yield return new SourceChange.Reset("the sync state belongs to another account; listing the drive again");
            state = null;
        }
        if (state is not null && !string.Equals(state.Options, OptionsFingerprint, StringComparison.Ordinal))
        {
            // The file rules or export formats changed: unchanged files may now be in, out or exported differently.
            yield return new SourceChange.Reset("the file filter or export formats changed; listing the drive again");
            state = null;
        }

        var folders = new Dictionary<string, FolderEntry>(StringComparer.Ordinal);
        foreach (var (id, folder) in state?.Folders ?? [])
            folders[id] = folder;

        if (state is null or { Phase: "list" })
        {
            // The start token comes before the listing, so nothing changed during the listing is missed.
            var startToken = state?.StartToken ?? (await GetAsync("changes/startPageToken?" + DriveQuery(forList: false), cancellationToken).ConfigureAwait(false))
                ["startPageToken"]?.GetValue<string>() ?? throw new InvalidDataException("Drive returned no start page token.");
            var pageToken = state?.ListPageToken;
            while (true)
            {
                var query = $"files?{DriveQuery(forList: true)}&q={Uri.EscapeDataString(ListFilter())}&pageSize={Math.Min(_options.PageSize, 1000)}"
                            + $"&fields={Uri.EscapeDataString("nextPageToken,files(" + FileFields + ")")}"
                            + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
                var page = await GetAsync(query, cancellationToken).ConfigureAwait(false);
                var relist = false;
                foreach (var file in page["files"]?.AsArray() ?? [])
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (file is null) continue;
                    if (TrackFolder(file, folders))
                    {
                        relist = true;
                        break;
                    }

                    if (await MapFileAsync(file, folders, cancellationToken).ConfigureAwait(false) is { } change)
                        yield return change;
                }

                if (relist)
                {
                    await foreach (var change in RelistAsync(cancellationToken).ConfigureAwait(false))
                        yield return change;
                    yield break;
                }

                pageToken = page["nextPageToken"]?.GetValue<string>();
                if (pageToken is null)
                {
                    // The listing is complete. The feed from the start token, read next run, replays what changed meanwhile.
                    yield return Checkpoint(new DriveCursor("changes", PageToken: startToken), folders);
                    yield break;
                }

                yield return Checkpoint(new DriveCursor("list", StartToken: startToken, ListPageToken: pageToken), folders);
            }
        }

        var token = state.PageToken ?? throw new InvalidDataException("A changes-phase cursor has no page token.");
        while (true)
        {
            var query = $"changes?{DriveQuery(forList: false)}&includeRemoved=true&pageToken={Uri.EscapeDataString(token)}&pageSize={Math.Min(_options.PageSize, 1000)}"
                        + $"&fields={Uri.EscapeDataString("nextPageToken,newStartPageToken,changes(fileId,removed,changeType,file(" + FileFields + "))")}";
            var (page, rejected) = await GetChangesPageAsync(query, cancellationToken).ConfigureAwait(false);
            if (rejected)
            {
                // Drive does not document how a bad token is reported; any 404/410, or a 400 about the page token, starts over.
                yield return new SourceChange.Reset("the Drive change token was rejected; listing the drive again");
                await foreach (var change in GetChangesAsync(null, cancellationToken).ConfigureAwait(false))
                    yield return change;
                yield break;
            }

            foreach (var change in page!["changes"]?.AsArray() ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change is null || change["changeType"]?.GetValue<string>() is { } type && type != "file")
                    continue;
                var fileId = change["fileId"]?.GetValue<string>();
                if (string.IsNullOrEmpty(fileId))
                    continue;
                if (change["removed"]?.GetValue<bool>() == true || change["file"] is null)
                {
                    folders.Remove(fileId);
                    yield return new SourceChange.Delete(fileId, DeletionReason.Deleted, _tenantContext); // deleted, or access lost
                    continue;
                }

                if (TrackFolder(change["file"]!, folders))
                {
                    await foreach (var again in RelistAsync(cancellationToken).ConfigureAwait(false))
                        yield return again;
                    yield break;
                }

                if (await MapFileAsync(change["file"]!, folders, cancellationToken).ConfigureAwait(false) is { } mapped)
                    yield return mapped;
            }

            if (page["nextPageToken"]?.GetValue<string>() is { } next)
            {
                yield return Checkpoint(new DriveCursor("changes", PageToken: next), folders);
                token = next;
                continue;
            }

            var fresh = page["newStartPageToken"]?.GetValue<string>() ?? throw new InvalidDataException("A last changes page had no newStartPageToken.");
            yield return Checkpoint(new DriveCursor("changes", PageToken: fresh), folders);
            yield break;
        }
    }

    /// <summary>Files under a moved or renamed folder are not reported again, so which of them are in scope is unknown.</summary>
    private async IAsyncEnumerable<SourceChange> RelistAsync([EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SourceChange.Reset("a folder moved or was renamed; listing the drive again");
        await foreach (var change in GetChangesAsync(null, ct).ConfigureAwait(false))
            yield return change;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A run from no cursor is the listing alone, which is the full set.
        await foreach (var change in GetChangesAsync(null, cancellationToken).ConfigureAwait(false))
            if (change is SourceChange.Upsert upsert)
                yield return upsert.Item;
    }

    /// <summary>Downloads a binary file, or exports a Google Doc, Sheet or Slides file, capped at the size limit.</summary>
    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        // Read the file's own metadata rather than trusting the item: the target is an access boundary for fetches too.
        var file = await GetAsync($"files/{Uri.EscapeDataString(item.Id)}?supportsAllDrives=true&fields=mimeType,driveId,ownedByMe,parents,name",
            cancellationToken, notFound: true).ConfigureAwait(false);
        var inTarget = _target switch
        {
            GoogleDriveTarget.SharedDrive s => file["driveId"]?.GetValue<string>() == s.DriveId,
            _ => file["ownedByMe"]?.GetValue<bool>() == true,
        };
        if (!inTarget)
            throw new InvalidOperationException($"{item.DisplayName} is not in the configured drive.");
        // Walked with fresh lookups, not the cursor's map: a folder moved out since the last run must not still count as inside.
        if (_rootFolderId is not null
            && await RelativePathAsync(file, new Dictionary<string, FolderEntry>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidOperationException($"{item.DisplayName} is not in the configured folder.");
        var mime = file["mimeType"]?.GetValue<string>() ?? string.Empty;
        var path = _exports.TryGetValue(mime, out var exportAs)
            ? $"files/{Uri.EscapeDataString(item.Id)}/export?mimeType={Uri.EscapeDataString(exportAs)}"
            : $"files/{Uri.EscapeDataString(item.Id)}?alt=media&supportsAllDrives=true";

        using var response = await SendAsync(new Uri(Api + path), cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("The file no longer exists in the drive.");
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

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

    private async Task<SourceChange?> MapFileAsync(JsonNode file, Dictionary<string, FolderEntry> folders, CancellationToken ct)
    {
        var id = file["id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            return null;
        if (file["trashed"]?.GetValue<bool>() == true)
            return new SourceChange.Delete(id, DeletionReason.Deleted, _tenantContext);

        var mime = file["mimeType"]?.GetValue<string>() ?? string.Empty;
        if (mime == Folder)
            return null;
        // Left the target: moved to another shared drive, or (My Drive) no longer the user's own file.
        var outside = _target switch
        {
            GoogleDriveTarget.SharedDrive s => file["driveId"]?.GetValue<string>() is { } d && d != s.DriveId,
            _ => file["ownedByMe"]?.GetValue<bool>() == false,
        };
        if (outside)
            return new SourceChange.Delete(id, DeletionReason.OutOfScope, _tenantContext);

        var exported = _exports.TryGetValue(mime, out var exportAs);
        if (!exported && mime.StartsWith(GooglePrefix, StringComparison.Ordinal))
            return null; // shortcuts (not followed), Forms, Drawings, Sites, Maps

        var name = file["name"]?.GetValue<string>() ?? id;
        var extension = exported
            ? ExportExtensions.GetValueOrDefault(exportAs!, "txt")
            : Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var displayName = exported ? name + "." + extension : name;
        var size = exported ? 0 : long.TryParse(file["size"]?.GetValue<string>(), out var s2) ? s2 : 0;
        var path = await RelativePathAsync(file, folders, ct, displayName).ConfigureAwait(false);
        if (path is null || !_filter.Includes(path, size))
            return new SourceChange.Delete(id, DeletionReason.OutOfScope, _tenantContext);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["gdrive.fileId"] = id,
            ["gdrive.mimeType"] = mime,
            [SourceKeys.AclCaptured] = "false",
            [SourceKeys.Container] = await ContainerAsync(ct).ConfigureAwait(false),
        };
        if (exported) metadata["gdrive.exportedAs"] = exportAs!;
        if (file["webViewLink"]?.GetValue<string>() is { Length: > 0 } link) metadata[SourceKeys.Url] = link;
        if (file["owners"]?.AsArray().FirstOrDefault()?["emailAddress"]?.GetValue<string>() is { Length: > 0 } owner) metadata[SourceKeys.Owner] = owner;
        // md5 changes with content for binary files; Google files have none, and their version number changes on every edit.
        if (file["md5Checksum"]?.GetValue<string>() is { Length: > 0 } md5)
        {
            metadata["gdrive.md5"] = md5;
            metadata[SourceKeys.Version] = md5;
        }
        else if (file["version"]?.GetValue<string>() is { Length: > 0 } version)
        {
            metadata[SourceKeys.Version] = "v" + version;
        }

        var modified = file["modifiedTime"]?.GetValue<string>() is { } m
            && DateTimeOffset.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
        return new SourceChange.Upsert(new SourceItem(id, displayName, extension, size, modified, _tenantContext, metadata));
    }

    /// <summary>
    /// Keeps the folder map current. Returns true when a known folder moved or was renamed while paths decide what is in scope.
    /// </summary>
    private bool TrackFolder(JsonNode file, Dictionary<string, FolderEntry> folders)
    {
        if (file["mimeType"]?.GetValue<string>() != Folder || file["id"]?.GetValue<string>() is not { Length: > 0 } id)
            return false;
        if (file["trashed"]?.GetValue<bool>() == true)
        {
            folders.Remove(id);
            return false;
        }

        var folder = AsFolder(file);
        var moved = folders.TryGetValue(id, out var known) && known != folder && PathsMatter;
        folders[id] = folder;
        EnsureWithinFolderCap(folders);
        return moved;
    }

    /// <summary>
    /// The path below the root folder (or the drive root), or null when the file is not below it. Unknown folders are looked up
    /// one request each and remembered in <paramref name="folders"/>.
    /// </summary>
    private async Task<string?> RelativePathAsync(JsonNode file, Dictionary<string, FolderEntry> folders, CancellationToken ct, string? name = null)
    {
        var segments = new List<string> { name ?? file["name"]?.GetValue<string>() ?? string.Empty };
        var current = file["parents"]?.AsArray().FirstOrDefault()?.GetValue<string>();
        var sharedDrive = (_target as GoogleDriveTarget.SharedDrive)?.DriveId;
        for (var depth = 0; depth < 1_000; depth++)
        {
            if (current is not null && current == _rootFolderId)
                return string.Join('/', Enumerable.Reverse(segments));
            if (current is null || current == sharedDrive)
                return _rootFolderId is null ? string.Join('/', Enumerable.Reverse(segments)) : null; // reached the drive root
            if (!folders.TryGetValue(current, out var folder))
            {
                JsonNode found;
                try
                {
                    found = await GetAsync($"files/{Uri.EscapeDataString(current)}?supportsAllDrives=true&fields=id,name,parents", ct, notFound: true)
                        .ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    return null; // the parent is gone or out of reach; a later change reports the file's move
                }

                folders[current] = folder = AsFolder(found);
                EnsureWithinFolderCap(folders);
            }

            if (folder.Name is null)
                return _rootFolderId is null ? string.Join('/', Enumerable.Reverse(segments)) : null; // My Drive's root has no parent
            segments.Add(folder.Name);
            current = folder.ParentId;
        }

        throw new InvalidDataException("Folder parents nest more than 1,000 deep or form a loop.");
    }

    private static FolderEntry AsFolder(JsonNode node) => node["parents"]?.AsArray().FirstOrDefault()?.GetValue<string>() is { } parent
        ? new FolderEntry(parent, node["name"]?.GetValue<string>() ?? string.Empty)
        : new FolderEntry(null, null);

    private void EnsureWithinFolderCap(Dictionary<string, FolderEntry> folders)
    {
        if (folders.Count > _options.MaxTrackedFolders)
            throw new InvalidOperationException(
                $"The drive has more than {_options.MaxTrackedFolders} folders to track. Set RootFolderId to a narrower folder, or raise MaxTrackedFolders.");
    }

    /// <summary>Whether a folder's path can change which files are in scope.</summary>
    private bool PathsMatter => _rootFolderId is not null
                                || _filter.Options.IncludePatterns is { Count: > 0 }
                                || _filter.Options.ExcludePatterns is { Count: > 0 };

    /// <summary>The root folder must exist, be a folder, and (for a shared drive) be in that drive.</summary>
    private async Task CheckRootFolderAsync(CancellationToken ct)
    {
        if (_rootFolderId is null || _rootChecked)
            return;
        JsonNode folder;
        try
        {
            folder = await GetAsync($"files/{Uri.EscapeDataString(_rootFolderId)}?supportsAllDrives=true&fields=id,mimeType,driveId,trashed", ct, notFound: true)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"The root folder '{_rootFolderId}' does not exist or is not shared with this account.");
        }

        if (folder["mimeType"]?.GetValue<string>() != Folder || folder["trashed"]?.GetValue<bool>() == true)
            throw new InvalidOperationException($"'{_rootFolderId}' is not a folder (or is in the trash).");
        if (_target is GoogleDriveTarget.SharedDrive s && folder["driveId"]?.GetValue<string>() != s.DriveId)
            throw new InvalidOperationException($"The root folder '{_rootFolderId}' is not in the configured shared drive.");
        _rootChecked = true;
    }

    private async Task<string> ContainerAsync(CancellationToken ct)
    {
        if (_container is not null)
            return _container;
        _container = _target switch
        {
            GoogleDriveTarget.SharedDrive s => (await GetAsync($"drives/{Uri.EscapeDataString(s.DriveId)}?fields=name", ct).ConfigureAwait(false))["name"]?.GetValue<string>()
                                               ?? "shared:" + s.DriveId,
            _ => "mydrive:" + ((await GetAsync("about?fields=user(emailAddress)", ct).ConfigureAwait(false))["user"]?["emailAddress"]?.GetValue<string>() ?? "me"),
        };
        return _container;
    }

    // The shared-drive flags go on every request; forgetting them hides shared-drive files without an error.
    private string DriveQuery(bool forList) => _target switch
    {
        GoogleDriveTarget.SharedDrive s => $"supportsAllDrives=true&includeItemsFromAllDrives=true&driveId={Uri.EscapeDataString(s.DriveId)}"
                                           + (forList ? "&corpora=drive" : string.Empty),
        _ => "supportsAllDrives=true" + (forList ? "&corpora=user" : "&restrictToMyDrive=true"),
    };

    private string ListFilter() => _target is GoogleDriveTarget.MyDrive ? "trashed = false and 'me' in owners" : "trashed = false";

    private SourceChange.Checkpoint Checkpoint(DriveCursor cursor, Dictionary<string, FolderEntry> folders)
        => new(JsonSerializer.Serialize(cursor with { Options = OptionsFingerprint, Folders = folders, Account = _account }));

    private string? _account;

    /// <summary>My Drive's signed-in user (null for a shared drive, whose id already names it). Checked against the target's
    /// <see cref="GoogleDriveTarget.MyDrive.Account"/> when one is given.</summary>
    private async Task<string?> AccountAsync(CancellationToken ct)
    {
        if (_target is not GoogleDriveTarget.MyDrive myDrive) return null;
        var container = await ContainerAsync(ct).ConfigureAwait(false);
        _account = container["mydrive:".Length..];
        if (!string.IsNullOrWhiteSpace(myDrive.Account) && !string.Equals(myDrive.Account.Trim(), _account, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The token signs in as {_account}, not {myDrive.Account.Trim()}.");
        return _account;
    }

    // "paths": patterns match paths since folder scoping; older cursors (name-only, no folder map) list the drive once more.
    internal string OptionsFingerprint => FileFilter.Hash(
        _filter.Fingerprint,
        "paths",
        "root=" + _rootFolderId,
        string.Join('\n', _exports.Select(e => e.Key + "=" + e.Value).Order(StringComparer.Ordinal)));

    private async Task<(JsonNode? Page, bool Rejected)> GetChangesPageAsync(string query, CancellationToken ct)
    {
        using var response = await SendAsync(new Uri(Api + query), ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return (null, true);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest && body.Contains("pageToken", StringComparison.OrdinalIgnoreCase))
            return (null, true);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode, body);
        return (JsonNode.Parse(body), false);
    }

    private async Task<JsonNode> GetAsync(string relative, CancellationToken ct, bool notFound = false)
    {
        using var response = await SendAsync(new Uri(Api + relative), ct).ConfigureAwait(false);
        if (notFound && response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException("The file no longer exists in the drive.");
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))!;
    }

    /// <summary>Sends with the Drive token, backing off on 429 and 403 rate-limit errors up to the run's ceiling.</summary>
    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var ceiling = _options.MaxThrottleWait ?? TimeSpan.FromMinutes(10);
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await CheckedTokenAsync(ct).ConfigureAwait(false));
            var response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
            if (!await IsRateLimitedAsync(response, ct).ConfigureAwait(false))
                return response;

            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            response.Dispose();
            if (waited + wait > ceiling)
                throw new HttpRequestException($"Drive kept rate-limiting the run past the {ceiling.TotalMinutes:0}-minute limit; the next run continues from the last checkpoint.");
            waited += wait;
            await _delay(wait, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> IsRateLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == (HttpStatusCode)429)
            return true;
        if (response.StatusCode != HttpStatusCode.Forbidden)
            return false;
        await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return body.Contains("rateLimitExceeded", StringComparison.Ordinal) || body.Contains("userRateLimitExceeded", StringComparison.Ordinal);
    }

    /// <summary>The token, checked once against Google's token-info endpoint: any Drive scope other than read-only is refused.</summary>
    private async Task<string> CheckedTokenAsync(CancellationToken ct)
    {
        var token = await _token(ct).ConfigureAwait(false);
        if (token == _checkedToken)
            return token;

        using var request = new HttpRequestMessage(HttpMethod.Get, TokenInfo + "?access_token=" + Uri.EscapeDataString(token));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var scopes = (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))?["scope"]?.GetValue<string>() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var broad = scopes.Where(s => s.StartsWith("https://www.googleapis.com/auth/drive", StringComparison.Ordinal)
                                          && !s.EndsWith(".readonly", StringComparison.Ordinal)).ToList();
            if (broad.Count > 0)
                throw new InvalidOperationException(
                    $"The Google token grants more than read-only Drive access ({string.Join(", ", broad)}). Use {GoogleDriveCredentials.ReadOnlyScope}.");
        }
        // Token info unavailable (some proxies and test hosts): the service-account helper scopes to read-only by construction.

        _checkedToken = token;
        return token;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        throw Failure(response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
    }

    private static HttpRequestException Failure(HttpStatusCode status, string body)
    {
        string? reason = null;
        try { reason = JsonNode.Parse(body)?["error"]?["errors"]?[0]?["reason"]?.GetValue<string>(); }
        catch (JsonException) { }
        return new HttpRequestException($"Google Drive returned {(int)status}{(reason is null ? "" : " (" + reason + ")")}.", null, status);
    }

    private static Func<CancellationToken, Task<string>> TokenFrom(GoogleCredential credential)
        => ct => ((ITokenAccess)credential).GetAccessTokenForRequestAsync(Api, ct);

    /// <param name="Folders">Folder id to parent and name, for building paths.</param>
    private sealed record DriveCursor(string Phase, string? StartToken = null, string? ListPageToken = null, string? PageToken = null,
        string? Options = null, Dictionary<string, FolderEntry>? Folders = null, string? Account = null);

    /// <summary>A folder's parent id and name; both null for My Drive's root. Short property names keep the cursor small.</summary>
    private sealed record FolderEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("p")] string? ParentId,
        [property: System.Text.Json.Serialization.JsonPropertyName("n")] string? Name);
}
