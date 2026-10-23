using OpenTail.Connectivity.Social.Reddit;

namespace OpenTail.Connectivity.Social.Reddit;

/// <summary>
/// Ingests posts from a subreddit via the public Reddit JSON API.
/// No API key required for read-only access (rate limit: ~60 req/min unauthenticated).
/// </summary>
public sealed class RedditConnector : ISourceConnector
{
    private const string BaseUrl = "https://www.reddit.com";
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _subreddit;
    private readonly string? _keywords;
    private readonly SocialConnectorOptions _opts;
    private readonly bool _ownsHttp;

    // Cache populated during DiscoverAsync so FetchAsync doesn't re-request
    private readonly Dictionary<string, RedditPost> _postCache = [];

    public RedditConnector(
        string subreddit,
        SocialConnectorOptions? options = null,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(subreddit))
            throw new ArgumentException("Subreddit must not be empty.", nameof(subreddit));

        var normalizedSubreddit = subreddit.Trim();
        if (normalizedSubreddit.StartsWith('/'))
            normalizedSubreddit = normalizedSubreddit[1..];
        if (normalizedSubreddit.StartsWith("r/", StringComparison.OrdinalIgnoreCase))
            normalizedSubreddit = normalizedSubreddit[2..];
        if (string.IsNullOrWhiteSpace(normalizedSubreddit))
            throw new ArgumentException("Subreddit must not be empty.", nameof(subreddit));
        _subreddit = normalizedSubreddit;
        _opts = options ?? new SocialConnectorOptions();
        _keywords = _opts.Keywords?.Count > 0 ? string.Join(" ", _opts.Keywords) : null;

        if (http is null)
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenTail/1.0 (+https://opentail.dev)");
            _ownsHttp = true;
        }
        else
        {
            _http = http;
            _ownsHttp = false;
        }
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? after = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            var batch = Math.Min(100, _opts.MaxItems - collected);
            var url = BuildUrl(batch, after);

            RedditListingResponse? listing = null;
            try
            {
                var response = await _http.GetAsync(url, cancellationToken);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                listing = JsonSerializer.Deserialize<RedditListingResponse>(json, _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Never a quiet end: the run would look complete with the rest of the results missing.
                throw new InvalidOperationException("Reddit discovery failed.", ex);
            }

            if (listing?.Data.Children is not { Count: > 0 }) yield break;

            foreach (var child in listing.Data.Children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var post = child.Data;
                if (post.Stickied) continue;
                if (_opts.Since.HasValue && post.CreatedAt < _opts.Since.Value) continue;

                var id = $"reddit:{_subreddit}:{post.Id}";
                _postCache[id] = post;
                var body = $"{post.Title}\n{post.Selftext}";

                yield return new SourceItem(
                    Id: id,
                    DisplayName: post.Title,
                    Extension: ".reddit-post",
                    SizeBytes: Encoding.UTF8.GetByteCount(body),
                    Modified: post.CreatedAt,
                    TenantContext: _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            after = listing.Data.After;
            if (after is null) yield break;

            if (_opts.RequestDelayMs > 0)
                await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_postCache.Remove(item.Id, out var post))
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(post));

        // Fallback: return minimal JSON so the pipeline doesn't fail
        var minimal = JsonSerializer.SerializeToUtf8Bytes(new { id = item.Id, title = item.DisplayName });
        return Task.FromResult(minimal);
    }

    private string BuildUrl(int limit, string? after)
    {
        var sb = new StringBuilder();
        if (_keywords is not null)
            sb.Append($"{BaseUrl}/r/{_subreddit}/search.json?q={Uri.EscapeDataString(_keywords)}&sort=new&restrict_sr=1&limit={limit}");
        else
            sb.Append($"{BaseUrl}/r/{_subreddit}/new.json?limit={limit}");

        if (after is not null)
            sb.Append($"&after={Uri.EscapeDataString(after)}");

        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
