using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenTail.Connectivity.Storage.WebDav;

/// <summary>Read-only access to a WebDAV collection using Depth 1 PROPFIND and bounded GET requests.</summary>
/// <remarks>
/// The root is a collection URL ending at the allowed namespace. Credentials are sent only to that origin and paths below
/// that root, including when redirects are followed. Prefer <see cref="Create"/> with an <see cref="HttpMessageHandler"/>: it
/// refuses a handler that follows redirects itself. A supplied <see cref="HttpClient"/> cannot be inspected; if its handler
/// follows a redirect, the response is refused (fail closed), but the redirected request has already been sent. WebDAV has no
/// snapshot cursor. One in-progress sorted listing is cached so subsequent pages do not traverse the tree again; a token
/// resumed after process restart falls back to a new traversal from its last key. Discovery is capped at 100,000 items by
/// default; set <c>maxSnapshotItems</c> to adjust the limit.
/// </remarks>
public sealed class WebDavObjectStoreClient : IObjectStoreClient, IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private const int MaxRedirects = 5;
    private const int DefaultMaxSnapshotItems = 100_000;
    private const int DefaultMaxListingResponseBytes = 16 * 1024 * 1024;
    private const int MaxErrorBodyBytes = 1024;
    private readonly Uri _root;
    private readonly string _rootPath;
    private readonly string _authorization;
    private readonly HttpClient _http;
    private bool _ownsHttp;
    private readonly int _maxSnapshotItems;
    private readonly int _maxListingResponseBytes;
    private readonly object _snapshotLock = new();
    private ListingSnapshot? _snapshot;

    public WebDavObjectStoreClient(
        Uri root,
        string username,
        string password,
        HttpClient? http = null,
        bool allowInsecureHttp = false,
        int maxSnapshotItems = DefaultMaxSnapshotItems,
        int maxListingResponseBytes = DefaultMaxListingResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSnapshotItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxListingResponseBytes, 1);
        if (username.Contains(':'))
            throw new ArgumentException("A WebDAV Basic-auth username cannot contain a colon.", nameof(username));
        if (!root.IsAbsoluteUri || root.Scheme != Uri.UriSchemeHttps && !(allowInsecureHttp && root.Scheme == Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(root.UserInfo) || !string.IsNullOrEmpty(root.Query) || !string.IsNullOrEmpty(root.Fragment))
            throw new ArgumentException("The WebDAV root must be an absolute HTTPS collection URL without user info, query or fragment. HTTP requires allowInsecureHttp.", nameof(root));

        _root = EnsureTrailingSlash(root);
        _rootPath = _root.AbsolutePath;
        _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _ownsHttp = http is null;
        _maxSnapshotItems = maxSnapshotItems;
        _maxListingResponseBytes = maxListingResponseBytes;
        Container = _root.Authority + _root.AbsolutePath.TrimEnd('/');
    }

    /// <summary>
    /// Builds a client on <paramref name="handler"/>, refusing a handler that follows redirects itself (an
    /// <see cref="HttpClientHandler"/> or <see cref="SocketsHttpHandler"/>, also behind <see cref="DelegatingHandler"/>s, with
    /// <c>AllowAutoRedirect</c> on). The client owns the handler.
    /// </summary>
    public static WebDavObjectStoreClient Create(
        Uri root,
        string username,
        string password,
        HttpMessageHandler handler,
        bool allowInsecureHttp = false,
        int maxSnapshotItems = DefaultMaxSnapshotItems,
        int maxListingResponseBytes = DefaultMaxListingResponseBytes)
    {
        ArgumentNullException.ThrowIfNull(handler);
        for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
        {
            if (current is HttpClientHandler { AllowAutoRedirect: true } or SocketsHttpHandler { AllowAutoRedirect: true })
                throw new ArgumentException("The WebDAV handler must have AllowAutoRedirect = false so each redirect can be checked.", nameof(handler));
        }
        var client = new WebDavObjectStoreClient(root, username, password, new HttpClient(handler, disposeHandler: true),
            allowInsecureHttp, maxSnapshotItems, maxListingResponseBytes);
        client._ownsHttp = true;
        return client;
    }

    public string Scheme => "webdav";

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
            var allObjects = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
            var matching = allObjects.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Where(item => lastKey is null || string.CompareOrdinal(item.Key, lastKey) > 0)
                .OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
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
        var hasMore = objects.Length > start + page.Length;
        if (!hasMore)
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
            throw new ArgumentException("The WebDAV listing cursor is invalid.", nameof(token), ex);
        }
    }

    private sealed record ListingSnapshot(Guid Id, string Prefix, ObjectInfo[] Objects);

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, allowEmpty: false, nameof(key));
        var uri = BuildUri(key);
        using var response = await SendAsync(HttpMethodPropfind, uri, depth: "0", body: PropFindBody, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var document = await ReadMultiStatusAsync(response, _maxListingResponseBytes, cancellationToken).ConfigureAwait(false);
        var ownResponse = document.Root?.Elements(Dav + "response")
            .FirstOrDefault(element => RelativeKey(ReadHref(element, response.RequestMessage?.RequestUri ?? uri)) == key);
        return ownResponse is null ? null : Map(ownResponse, response.RequestMessage?.RequestUri ?? uri);
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateKey(key, allowEmpty: false, nameof(key));
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);

        var uri = BuildUri(key);
        using var response = await SendAsync(HttpMethod.Get, uri, depth: null, body: null, cancellationToken, maxBytes).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new ObjectNotFoundException(key);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) == true && range.Length == 0)
                return new ObjectContent([], Truncated: false, ContentType: response.Content.Headers.ContentType?.ToString());
        }
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
        var contentType = response.Content.Headers.ContentType?.ToString();
        var contentEncoding = response.Content.Headers.ContentEncoding.Count == 0
            ? null
            : string.Join(",", response.Content.Headers.ContentEncoding);
        return new ObjectContent(bytes, truncated, contentType, contentEncoding);
    }

    public void Dispose()
    {
        lock (_snapshotLock)
            _snapshot = null;
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<List<ObjectInfo>> EnumerateAsync(CancellationToken cancellationToken)
    {
        var objects = new List<ObjectInfo>();
        var discoveredCount = 0;
        var pending = new Queue<Uri>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(_root);

        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(directory.AbsoluteUri))
                continue;
            using var response = await SendAsync(HttpMethodPropfind, directory, depth: "1", PropFindBody, cancellationToken).ConfigureAwait(false);
            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var document = await ReadMultiStatusAsync(response, _maxListingResponseBytes, cancellationToken).ConfigureAwait(false);
            foreach (var entry in document.Root?.Elements(Dav + "response") ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                var href = ReadHref(entry, response.RequestMessage?.RequestUri ?? directory);
                var key = RelativeKey(href);
                if (key.Length == 0)
                    continue;
                if (discoveredCount >= _maxSnapshotItems)
                    throw new InvalidOperationException($"WebDAV listing exceeded the configured limit of {_maxSnapshotItems} discovered items.");
                discoveredCount++;

                if (IsCollection(entry, href))
                {
                    pending.Enqueue(EnsureTrailingSlash(href));
                    continue;
                }
                objects.Add(Map(entry, response.RequestMessage?.RequestUri ?? directory));
            }
        }

        return objects;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri initialUri,
        string? depth,
        string? body,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        var uri = initialUri;
        for (var redirect = 0; ; redirect++)
        {
            ValidateUri(uri);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(_authorization);
            if (depth is not null)
                request.Headers.TryAddWithoutValidation("Depth", depth);
            if (maxBytes is { } bytes)
                request.Headers.Range = new RangeHeaderValue(0, bytes);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var effectiveUri = response.RequestMessage?.RequestUri ?? uri;
            try
            {
                if (effectiveUri != uri)
                    throw new InvalidOperationException(
                        "The supplied HttpClient followed a redirect itself. Disable automatic redirects on its handler, or use WebDavObjectStoreClient.Create with a handler.");
                ValidateUri(effectiveUri);
            }
            catch
            {
                response.Dispose();
                throw;
            }

            if (!IsRedirect(response.StatusCode))
                return response;
            if (redirect >= MaxRedirects || response.Headers.Location is null)
            {
                response.Dispose();
                throw new HttpRequestException("The WebDAV server returned an invalid or excessive redirect.");
            }

            var target = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(effectiveUri, response.Headers.Location);
            var status = response.StatusCode;
            response.Dispose();
            ValidateUri(target);
            if (status == HttpStatusCode.SeeOther && method != HttpMethod.Get)
                throw new HttpRequestException("The WebDAV server redirected PROPFIND with 303; the method cannot be preserved safely.");
            uri = target;
        }
    }

    private void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !SameOrigin(_root, uri) || !uri.AbsolutePath.StartsWith(_rootPath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new HttpRequestException("The WebDAV response referred outside the configured origin or root path.");
        _ = RelativeKey(uri);
    }

    private string RelativeKey(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (!SameOrigin(_root, uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            !path.StartsWith(_rootPath, StringComparison.Ordinal))
            throw new HttpRequestException("The WebDAV server returned an href outside the configured root.");
        var relative = path[_rootPath.Length..].TrimEnd('/');
        if (relative.Length == 0)
            return "";
        var decoded = new List<string>();
        foreach (var escapedSegment in relative.Split('/'))
        {
            var segment = Uri.UnescapeDataString(escapedSegment);
            if (segment is "." or ".." || segment.Contains('/') || segment.Contains('\\') ||
                segment.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
                segment.Contains("%5c", StringComparison.OrdinalIgnoreCase))
                throw new HttpRequestException("The WebDAV server returned a path with an unsafe encoded segment.");
            decoded.Add(segment);
        }
        return string.Join('/', decoded);
    }

    private Uri BuildUri(string key) => new(_root, string.Join('/', key.Split('/').Select(Uri.EscapeDataString)));

    // The whole PROPFIND body is buffered (bounded) before XML parsing, so a hostile or
    // runaway server cannot exhaust memory before the discovered-item limit applies.
    private static async Task<XDocument> ReadMultiStatusAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long declared && declared > maxBytes)
            throw new InvalidDataException($"WebDAV listing response is too large ({declared} bytes; limit {maxBytes}).");
        var (body, truncated) = await ReadUpToAsync(response.Content, maxBytes, cancellationToken).ConfigureAwait(false);
        if (truncated)
            throw new InvalidDataException($"WebDAV listing response exceeded the limit of {maxBytes} bytes.");
        using var stream = new MemoryStream(body, writable: false);
        var settings = new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(stream, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private ObjectInfo Map(XElement response, Uri requestUri)
    {
        var key = RelativeKey(ReadHref(response, requestUri));
        var lengthText = Property(response, Dav + "getcontentlength")?.Value;
        var size = long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSize) ? Math.Max(parsedSize, 0) : 0;
        var modifiedText = Property(response, Dav + "getlastmodified")?.Value;
        var modified = DateTimeOffset.TryParse(modifiedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedModified)
            ? parsedModified
            : DateTimeOffset.UnixEpoch;
        var etag = Property(response, Dav + "getetag")?.Value;
        return new ObjectInfo(key, size, modified, etag);
    }

    private static IEnumerable<XElement> SuccessfulProperties(XElement response) => response.Elements(Dav + "propstat")
        .Where(propstat => IsSuccessStatus(propstat.Element(Dav + "status")?.Value))
        .Select(propstat => propstat.Element(Dav + "prop"))
        .OfType<XElement>();

    private static bool IsCollection(XElement response, Uri href) =>
        SuccessfulProperties(response).Any(properties => properties.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null) ||
        href.AbsolutePath.EndsWith("/", StringComparison.Ordinal);

    private static XElement? Property(XElement response, XName name) =>
        SuccessfulProperties(response).Select(properties => properties.Element(name)).FirstOrDefault(element => element is not null);

    private static bool IsSuccessStatus(string? status)
    {
        var parts = status?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: >= 2 } && parts[1] == "200";
    }

    private static Uri ReadHref(XElement response, Uri requestUri)
    {
        var href = response.Element(Dav + "href")?.Value
            ?? throw new InvalidDataException("The WebDAV server returned a response without an href.");
        var uri = new Uri(requestUri, href);
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new HttpRequestException("The WebDAV server returned an href with a query or fragment.");
        return uri;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var (bytes, truncated) = await ReadUpToAsync(response.Content, MaxErrorBodyBytes, cancellationToken).ConfigureAwait(false);
        var body = Encoding.UTF8.GetString(bytes) + (truncated ? "... (truncated)" : "");
        throw new HttpRequestException($"WebDAV request failed with {(int)response.StatusCode} ({response.StatusCode}): {body}");
    }

    // Reads at most maxBytes; Truncated is true when the stream had more. Does not trust Content-Length.
    private static async Task<(byte[] Bytes, bool Truncated)> ReadUpToAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var remaining = maxBytes + 1L - buffer.Length;
            if (remaining <= 0)
                break;
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            buffer.Write(chunk, 0, read);
        }
        return buffer.Length > maxBytes ? (buffer.GetBuffer()[..maxBytes], true) : (buffer.ToArray(), false);
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.Host.Equals(right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
        HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static Uri EnsureTrailingSlash(Uri uri) => uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
        ? uri
        : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    private static void ValidateKey(string key, bool allowEmpty, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (key.Length == 0 && allowEmpty)
            return;
        var segments = key.Split('/');
        if (allowEmpty && segments.Length > 1 && segments[^1].Length == 0)
            segments = segments[..^1];
        if ((!allowEmpty && key.Length == 0) || key.StartsWith('/') || key.Contains('\\') || segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("WebDAV keys must be relative slash-separated paths without empty, '.' or '..' segments.", parameterName);
    }

    private static readonly HttpMethod HttpMethodPropfind = new("PROPFIND");
    private const string PropFindBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:getetag/><d:getcontenttype/></d:prop></d:propfind>""";
}
