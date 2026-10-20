using OpenTail.Connectivity.Social.Twitter;

namespace OpenTail.Connectivity.Social.Twitter;

/// <summary>
/// Ingests tweets via X API v2 recent search (7-day lookback, free tier).
/// Requires a bearer token from developer.twitter.com.
/// Set TWITTER_BEARER_TOKEN env var or pass via constructor.
/// </summary>
public sealed class TwitterConnector : ISourceConnector, IDisposable
{
    private const string BaseUrl = "https://api.twitter.com/2/tweets/search/recent";
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _query;
    private readonly SocialConnectorOptions _opts;
    private readonly bool _ownsHttp;
    private readonly string _bearerToken;
    private readonly Dictionary<string, TwitterTweet> _cache = [];

    public TwitterConnector(
        string bearerToken,
        string query,
        SocialConnectorOptions? options = null,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("Bearer token required.", nameof(bearerToken));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query must not be empty.", nameof(query));

        _query = query;
        _opts = options ?? new SocialConnectorOptions();
        _bearerToken = bearerToken;

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

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? nextToken = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            // Twitter v2 API requires max_results to be between 10 and 100
            var batch = Math.Min(100, Math.Max(10, _opts.MaxItems - collected));
            var url = BuildUrl(batch, nextToken);

            TwitterSearchResponse? response = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bearerToken);
                var resp = await _http.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                response = JsonSerializer.Deserialize<TwitterSearchResponse>(json, _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine($"Twitter search API failed: {ex.Message}");
                yield break;
            }

            if (response?.Data is not { Count: > 0 }) yield break;

            // Build author lookup from expansions
            var users = response.Includes?.Users?.ToDictionary(u => u.Id, u => u)
                ?? new Dictionary<string, TwitterUser>();

            foreach (var tweet in response.Data)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_opts.Since.HasValue && tweet.CreatedAt < _opts.Since.Value) continue;
                if (tweet.AuthorId is not null && users.TryGetValue(tweet.AuthorId, out var user))
                {
                    tweet.AuthorUsername = user.Username;
                    tweet.AuthorName = user.Name;
                }

                var id = $"twitter:{tweet.Id}";
                _cache[id] = tweet;

                yield return new SourceItem(
                    Id: id,
                    DisplayName: tweet.Text.Length > 80 ? tweet.Text[..80] + "…" : tweet.Text,
                    Extension: ".tweet",
                    SizeBytes: Encoding.UTF8.GetByteCount(tweet.Text),
                    Modified: tweet.CreatedAt,
                    TenantContext: _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            nextToken = response.Meta?.NextToken;
            if (nextToken is null) yield break;

            if (_opts.RequestDelayMs > 0)
                await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_cache.Remove(item.Id, out var tweet))
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(tweet));

        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { id = item.Id, text = item.DisplayName }));
    }

    private string BuildUrl(int maxResults, string? nextToken)
    {
        var sb = new StringBuilder(BaseUrl);
        sb.Append($"?query={Uri.EscapeDataString(_query)}&max_results={maxResults}");
        sb.Append("&tweet.fields=id,text,author_id,created_at,public_metrics,entities,lang");
        sb.Append("&expansions=author_id&user.fields=username,name");
        if (nextToken is not null)
            sb.Append($"&next_token={Uri.EscapeDataString(nextToken)}");
        if (_opts.Since.HasValue)
            sb.Append($"&start_time={Uri.EscapeDataString(_opts.Since.Value.ToString("O"))}");
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
