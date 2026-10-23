using OpenTail.Connectivity.Social.Meta;

namespace OpenTail.Connectivity.Social.Meta;

/// <summary>
/// Ingests posts from Facebook Pages or Instagram accounts via Meta Graph API.
/// Source format: "page:{page-id}" for Facebook, "account:{ig-account-id}" for Instagram.
/// Set META_ACCESS_TOKEN env var or pass via constructor.
/// Requires a valid Page access token (Facebook) or User token with instagram_basic scope (Instagram).
/// </summary>
public sealed class MetaConnector : ISourceConnector, IDisposable
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string _accountId;
    private readonly MetaPlatform _platform;
    private readonly SocialConnectorOptions _opts;
    private readonly bool _ownsHttp;
    private readonly string _graphBase;
    private readonly Dictionary<string, MetaPost> _cache = [];
    private readonly Queue<string> _cacheOrder = new();
    private readonly int _maxCacheItems;

    public MetaConnector(
        string accessToken,
        string source,
        SocialConnectorOptions? options = null,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token required.", nameof(accessToken));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source must not be empty.", nameof(source));

        _accessToken = accessToken;
        _opts = options ?? new SocialConnectorOptions();
        _graphBase = BuildGraphBase(_opts.MetaApiVersion);
        _maxCacheItems = Math.Max(1, _opts.MaxCacheItems ?? _opts.MaxItems);
        (_platform, _accountId) = ParseSource(source);

        if (http is null)
        {
            _http = new HttpClient();
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }

    }

    // The token goes in a Bearer header on each request: not in the query string, which would expose it in exception messages,
    // logs and traces, and not on the client's default headers, which a caller's shared client would carry to other accounts.
    private Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
        return _http.SendAsync(request, ct);
    }

    private static (MetaPlatform platform, string id) ParseSource(string source)
    {
        if (source.StartsWith("account:", StringComparison.OrdinalIgnoreCase))
            return (MetaPlatform.Instagram, source[8..]);
        if (source.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            return (MetaPlatform.Facebook, source[5..]);
        return (MetaPlatform.Facebook, source);
    }

    private static string BuildGraphBase(string apiVersion)
    {
        var normalized = string.IsNullOrWhiteSpace(apiVersion)
            ? "v19.0"
            : apiVersion.Trim();
        if (!normalized.StartsWith('v'))
            normalized = "v" + normalized;
        return $"https://graph.facebook.com/{normalized}";
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_platform == MetaPlatform.Instagram)
        {
            await foreach (var item in DiscoverInstagramAsync(cancellationToken))
                yield return item;
        }
        else
        {
            await foreach (var item in DiscoverFacebookAsync(cancellationToken))
                yield return item;
        }
    }

    private async IAsyncEnumerable<SourceItem> DiscoverFacebookAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            var batch = Math.Min(100, _opts.MaxItems - collected);
            var url = $"{_graphBase}/{_accountId}/posts?fields=id,message,created_time,likes.summary(true),shares,comments.summary(true)&limit={batch}"
                + (after is not null ? $"&after={Uri.EscapeDataString(after)}" : "");

            FbPagePostsResponse? response = null;
            try
            {
                var resp = await GetAsync(url, cancellationToken);
                resp.EnsureSuccessStatusCode();
                response = JsonSerializer.Deserialize<FbPagePostsResponse>(
                    await resp.Content.ReadAsStringAsync(cancellationToken), _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Meta Facebook discovery failed.", ex);
            }

            if (response?.Data is not { Count: > 0 }) yield break;

            foreach (var post in response.Data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_opts.Since.HasValue && post.CreatedTime < _opts.Since.Value) continue;
                if (string.IsNullOrWhiteSpace(post.Message)) continue;

                var mp = new MetaPost
                {
                    Id = post.Id, Platform = MetaPlatform.Facebook,
                    Message = post.Message, CreatedAt = post.CreatedTime,
                    Likes = post.Likes?.Summary?.TotalCount ?? 0,
                    Shares = post.Shares?.Count ?? 0,
                    Comments = post.Comments?.Summary?.TotalCount ?? 0,
                    MediaType = "text",
                    Permalink = $"https://www.facebook.com/{post.Id}",
                };

                var id = $"facebook:{post.Id}";
                CachePost(id, mp);
                yield return new SourceItem(id, post.Message.Length > 80 ? post.Message[..80] + "…" : post.Message,
                    ".fb-post", Encoding.UTF8.GetByteCount(post.Message), post.CreatedTime, _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            after = response.Paging?.Cursors?.After;
            if (after is null || response.Paging?.Next is null) yield break;
            if (_opts.RequestDelayMs > 0) await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    private async IAsyncEnumerable<SourceItem> DiscoverInstagramAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            var batch = Math.Min(100, _opts.MaxItems - collected);
            var url = $"{_graphBase}/{_accountId}/media?fields=id,caption,timestamp,like_count,comments_count,media_type,permalink&limit={batch}"
                + (after is not null ? $"&after={Uri.EscapeDataString(after)}" : "");

            IgMediaResponse? response = null;
            try
            {
                var resp = await GetAsync(url, cancellationToken);
                resp.EnsureSuccessStatusCode();
                response = JsonSerializer.Deserialize<IgMediaResponse>(
                    await resp.Content.ReadAsStringAsync(cancellationToken), _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("Meta Instagram discovery failed.", ex);
            }

            if (response?.Data is not { Count: > 0 }) yield break;

            foreach (var media in response.Data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_opts.Since.HasValue && media.Timestamp < _opts.Since.Value) continue;

                var body = media.Caption ?? $"[{media.MediaType ?? "media"}]";
                var mp = new MetaPost
                {
                    Id = media.Id, Platform = MetaPlatform.Instagram,
                    Caption = media.Caption, CreatedAt = media.Timestamp,
                    Likes = media.LikeCount, Comments = media.CommentsCount,
                    MediaType = media.MediaType?.ToLowerInvariant() ?? "image",
                    Permalink = media.Permalink,
                };

                var id = $"instagram:{media.Id}";
                CachePost(id, mp);
                yield return new SourceItem(id, body.Length > 80 ? body[..80] + "…" : body,
                    ".ig-post", Encoding.UTF8.GetByteCount(body), media.Timestamp, _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            after = response.Paging?.Cursors?.After;
            if (after is null || response.Paging?.Next is null) yield break;
            if (_opts.RequestDelayMs > 0) await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(item.Id, out var mp))
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(mp));
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { id = item.Id }));
    }

    private void CachePost(string id, MetaPost post)
    {
        if (!_cache.ContainsKey(id))
            _cacheOrder.Enqueue(id);
        _cache[id] = post;

        while (_cache.Count > _maxCacheItems && _cacheOrder.TryDequeue(out var evictId))
            _cache.Remove(evictId);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
