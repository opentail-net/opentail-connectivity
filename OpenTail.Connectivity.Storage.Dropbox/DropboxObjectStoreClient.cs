using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Storage.Dropbox;

/// <summary>Read-only object access to a Dropbox folder, using its HTTP API and a caller-supplied access-token function.</summary>
/// <remarks>Dropbox listing order is unspecified; continuation tokens are service cursors, not key-order positions.</remarks>
public sealed class DropboxObjectStoreClient : IObjectStoreClient, IDisposable
{
    private const int MaxPageSize = 2000;
    private static readonly Uri ApiRoot = new("https://api.dropboxapi.com/2/");
    private static readonly Uri ContentRoot = new("https://content.dropboxapi.com/2/");
    private static readonly TimeSpan MaxThrottleWait = TimeSpan.FromMinutes(10);

    private readonly Func<CancellationToken, Task<string>> _accessToken;
    private readonly string _root;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DropboxObjectStoreClient(Func<CancellationToken, Task<string>> accessToken, string root = "", HttpClient? http = null)
        : this(accessToken, root, http, delay: null)
    {
    }

    internal DropboxObjectStoreClient(
        Func<CancellationToken, Task<string>> accessToken,
        string root,
        HttpClient? http,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        _accessToken = accessToken;
        _root = NormalizeRoot(root);
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
        _delay = delay ?? Task.Delay;
    }

    public string Scheme => "dropbox";

    public string Container => _root.Length == 0 ? "/" : _root;

    public async Task<ObjectListingPage> ListAsync(string prefix, string? resumeToken, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ValidateRelativeKey(prefix, nameof(prefix), allowEmpty: true);
        var endpoint = resumeToken is null ? "files/list_folder" : "files/list_folder/continue";
        var body = resumeToken is null
            ? JsonSerializer.Serialize(new { path = _root, recursive = true, limit = Math.Min(pageSize, MaxPageSize) })
            : JsonSerializer.Serialize(new { cursor = resumeToken });
        using var response = await SendAsync(
            token => JsonPost(new Uri(ApiRoot, endpoint), token, body), cancellationToken).ConfigureAwait(false);
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var objects = new List<ObjectInfo>();
        foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (!IsTag(entry, "file"))
                continue;
            var info = MapFile(entry);
            if (info.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                objects.Add(info);
        }
        var hasMore = json.RootElement.GetProperty("has_more").GetBoolean();
        var next = hasMore ? json.RootElement.GetProperty("cursor").GetString() : null;
        if (hasMore && string.IsNullOrEmpty(next))
            throw new InvalidDataException("Dropbox returned has_more without a cursor.");
        return new ObjectListingPage(objects, next);
    }

    public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateRelativeKey(key, nameof(key), allowEmpty: false);
        var body = JsonSerializer.Serialize(new { path = FullPath(key) });
        using var response = await SendAsync(
            token => JsonPost(new Uri(ApiRoot, "files/get_metadata"), token, body), cancellationToken).ConfigureAwait(false);
        if (await IsPathNotFoundAsync(response, cancellationToken).ConfigureAwait(false))
            return null;
        using var json = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return IsTag(json.RootElement, "file") ? MapFile(json.RootElement) : null;
    }

    public async Task<ObjectContent> ReadAsync(string key, long maxBytes, CancellationToken cancellationToken = default)
    {
        ValidateRelativeKey(key, nameof(key), allowEmpty: false);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBytes, Array.MaxLength - 1L);
        var pathArg = JsonSerializer.Serialize(new { path = FullPath(key) });
        using var response = await SendAsync(
            token =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ContentRoot, "files/download"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", pathArg);
                return request;
            }, cancellationToken, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (await IsPathNotFoundAsync(response, cancellationToken).ConfigureAwait(false))
            throw new ObjectNotFoundException(key);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var limit = maxBytes + 1;
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
        var encoding = response.Content.Headers.ContentEncoding.Count == 0
            ? null
            : string.Join(",", response.Content.Headers.ContentEncoding);
        return new ObjectContent(bytes, truncated, contentType, encoding);
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<string, HttpRequestMessage> createRequest,
        CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var waited = TimeSpan.Zero;
        for (var attempt = 0; ; attempt++)
        {
            var token = await _accessToken(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("The Dropbox access-token function returned an empty token.");
            using var request = createRequest(token);
            var response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != (HttpStatusCode)429)
                return response;

            var retryAfter = response.Headers.RetryAfter;
            var wait = retryAfter?.Delta ?? (retryAfter?.Date is { } retryDate
                ? retryDate - DateTimeOffset.UtcNow
                : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt))));
            if (wait <= TimeSpan.Zero)
                wait = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
            response.Dispose();
            if (waited + wait > MaxThrottleWait)
                throw new HttpRequestException($"Dropbox kept rate-limiting the request past the {MaxThrottleWait.TotalMinutes:0}-minute limit.");
            waited += wait;
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> IsPathNotFoundAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if ((int)response.StatusCode != 409)
            return false;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var error = JsonDocument.Parse(body);
            return error.RootElement.TryGetProperty("error_summary", out var summary) &&
                   summary.GetString()?.StartsWith("path/not_found", StringComparison.Ordinal) == true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Dropbox request failed with {(int)response.StatusCode} ({response.StatusCode}): {body}");
    }

    private static HttpRequestMessage JsonPost(Uri uri, string token, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private ObjectInfo MapFile(JsonElement entry)
    {
        var displayPath = entry.GetProperty("path_display").GetString()
            ?? throw new InvalidDataException("Dropbox returned a file without path_display.");
        var key = RelativeKey(displayPath);
        var size = entry.GetProperty("size").GetInt64();
        var modified = entry.GetProperty("server_modified").GetDateTimeOffset();
        var etag = entry.TryGetProperty("content_hash", out var hash) ? hash.GetString() : null;
        return new ObjectInfo(key, size, modified, etag);
    }

    private string RelativeKey(string displayPath)
    {
        var path = displayPath.TrimStart('/');
        if (_root.Length > 0)
        {
            var root = _root.TrimStart('/');
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                (path.Length > root.Length && path[root.Length] != '/'))
                throw new InvalidDataException("Dropbox returned an entry outside the configured root.");
            path = path.Length == root.Length ? "" : path[(root.Length + 1)..];
        }
        ValidateRelativeKey(path, nameof(displayPath), allowEmpty: false);
        return path;
    }

    private string FullPath(string key) => _root.Length == 0 ? "/" + key : _root.TrimEnd('/') + "/" + key;

    private static bool IsTag(JsonElement entry, string tag) =>
        entry.TryGetProperty(".tag", out var property) && property.GetString() == tag;

    private static string NormalizeRoot(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Length == 0 || root == "/")
            return "";
        if (root.Contains('\\') || root.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("Dropbox root cannot contain backslashes or '..' path segments.", nameof(root));
        var segments = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? "" : "/" + string.Join('/', segments);
    }

    private static void ValidateRelativeKey(string key, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(key, parameterName);
        if (!allowEmpty && key.Length == 0)
            throw new ArgumentException("Dropbox key cannot be empty.", parameterName);
        if (key.StartsWith('/') || key.Contains('\\') || key.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("Dropbox keys must be relative and cannot contain backslashes or '..' path segments.", parameterName);
    }
}
