using OpenTail.Connectivity.Social.YouTube;

namespace OpenTail.Connectivity.Social.YouTube;

/// <summary>
/// Ingests YouTube content via Data API v3.
/// Source format: "query:{terms}" | "channel:{channelId}" | "video:{videoId}" (comments)
/// Set YOUTUBE_API_KEY env var or pass via constructor.
/// Free quota: 10 000 units/day.
/// </summary>
public sealed class YouTubeConnector : ISourceConnector, IDisposable
{
    private const string ApiBase = "https://www.googleapis.com/youtube/v3";
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _source;
    private readonly YouTubeMode _mode;
    private readonly SocialConnectorOptions _opts;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, YouTubeItem> _cache = [];

    public YouTubeConnector(
        string apiKey,
        string source,
        SocialConnectorOptions? options = null,
        HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source must not be empty.", nameof(source));

        _apiKey = apiKey;
        _opts = options ?? new SocialConnectorOptions();
        (_mode, _source) = ParseSource(source);

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

    private static (YouTubeMode mode, string value) ParseSource(string source)
    {
        if (source.StartsWith("video:", StringComparison.OrdinalIgnoreCase))
            return (YouTubeMode.VideoComments, source[6..]);
        if (source.StartsWith("channel:", StringComparison.OrdinalIgnoreCase))
            return (YouTubeMode.Channel, source[8..]);
        if (source.StartsWith("query:", StringComparison.OrdinalIgnoreCase))
            return (YouTubeMode.Search, source[6..]);
        return (YouTubeMode.Search, source);
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_mode == YouTubeMode.VideoComments)
        {
            await foreach (var item in DiscoverCommentsAsync(_source, cancellationToken))
                yield return item;
        }
        else
        {
            await foreach (var item in DiscoverVideosAsync(cancellationToken))
                yield return item;
        }
    }

    private async IAsyncEnumerable<SourceItem> DiscoverVideosAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            var batch = Math.Min(50, _opts.MaxItems - collected);
            var url = _mode == YouTubeMode.Channel
                ? $"{ApiBase}/search?part=id,snippet&channelId={Uri.EscapeDataString(_source)}&type=video&maxResults={batch}&order=date"
                  + (pageToken is not null ? $"&pageToken={pageToken}" : "")
                : $"{ApiBase}/search?part=id,snippet&q={Uri.EscapeDataString(_source)}&type=video&maxResults={batch}&order=date"
                  + (pageToken is not null ? $"&pageToken={pageToken}" : "");

            YtSearchResponse? response = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Goog-Api-Key", _apiKey);
                var resp = await _http.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                response = JsonSerializer.Deserialize<YtSearchResponse>(
                    await resp.Content.ReadAsStringAsync(cancellationToken), _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) 
            { 
                // Never a quiet end: the run would look complete with the rest of the results missing.
                throw new InvalidOperationException("YouTube discovery failed.", ex);
            }

            if (response?.Items is not { Count: > 0 }) yield break;

            // Fetch stats for the video IDs in batch
            var videoIds = response.Items
                .Where(i => i.Id?.VideoId is not null)
                .Select(i => i.Id!.VideoId!)
                .ToList();
            var stats = await FetchVideoStatsAsync(videoIds, cancellationToken);

            foreach (var item in response.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var videoId = item.Id?.VideoId;
                if (videoId is null) continue;
                var snippet = item.Snippet ?? new YtSnippet();
                if (_opts.Since.HasValue && snippet.PublishedAt < _opts.Since.Value) continue;

                YtStatistics? stat = null;
                var statsUnavailable = stats is null || !stats.TryGetValue(videoId, out stat);
                var ytItem = new YouTubeItem
                {
                    ItemId = videoId,
                    Mode = _mode,
                    Title = snippet.Title,
                    Body = snippet.Description ?? snippet.Title,
                    ChannelTitle = snippet.ChannelTitle,
                    PublishedAt = snippet.PublishedAt,
                    LikeCount = ParseLong(stat?.LikeCount),
                    CommentCount = ParseLong(stat?.CommentCount),
                    ViewCount = ParseLong(stat?.ViewCount),
                    StatsUnavailable = statsUnavailable,
                    VideoId = videoId,
                    Url = $"https://www.youtube.com/watch?v={videoId}",
                    Language = snippet.Language,
                };

                var id = $"youtube:video:{videoId}";
                _cache[id] = ytItem;
                yield return new SourceItem(id, snippet.Title, ".yt-video",
                    Encoding.UTF8.GetByteCount(ytItem.Body), snippet.PublishedAt, _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            pageToken = response.NextPageToken;
            if (pageToken is null) yield break;
            if (_opts.RequestDelayMs > 0) await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    private async IAsyncEnumerable<SourceItem> DiscoverCommentsAsync(
        string videoId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        int collected = 0;

        while (collected < _opts.MaxItems)
        {
            var batch = Math.Min(100, _opts.MaxItems - collected);
            var url = $"{ApiBase}/commentThreads?part=snippet&videoId={Uri.EscapeDataString(videoId)}&maxResults={batch}&order=relevance"
                + (pageToken is not null ? $"&pageToken={pageToken}" : "");

            YtCommentThreadResponse? response = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("X-Goog-Api-Key", _apiKey);
                var resp = await _http.SendAsync(req, cancellationToken);
                resp.EnsureSuccessStatusCode();
                response = JsonSerializer.Deserialize<YtCommentThreadResponse>(
                    await resp.Content.ReadAsStringAsync(cancellationToken), _json);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested) 
            { 
                throw new InvalidOperationException("YouTube comment discovery failed.", ex);
            }

            if (response?.Items is not { Count: > 0 }) yield break;

            foreach (var thread in response.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cs = thread.Snippet?.TopLevelComment?.Snippet;
                if (cs is null) continue;
                if (_opts.Since.HasValue && cs.PublishedAt < _opts.Since.Value) continue;

                var ytItem = new YouTubeItem
                {
                    ItemId = thread.Id,
                    Mode = YouTubeMode.VideoComments,
                    Title = $"Comment on video {videoId}",
                    Body = cs.TextDisplay,
                    AuthorName = cs.AuthorDisplayName,
                    PublishedAt = cs.PublishedAt,
                    LikeCount = cs.LikeCount,
                    VideoId = videoId,
                    Url = $"https://www.youtube.com/watch?v={videoId}&lc={thread.Id}",
                };

                var id = $"youtube:comment:{thread.Id}";
                _cache[id] = ytItem;
                yield return new SourceItem(id, $"Comment on {videoId}", ".yt-comment",
                    Encoding.UTF8.GetByteCount(cs.TextDisplay), cs.PublishedAt, _opts.TenantContext);

                collected++;
                if (collected >= _opts.MaxItems) yield break;
            }

            pageToken = response.NextPageToken;
            if (pageToken is null) yield break;
            if (_opts.RequestDelayMs > 0) await Task.Delay(_opts.RequestDelayMs, cancellationToken);
        }
    }

    /// <summary>Null when the call fails: the videos are still worth having, but their counts are unknown, never zero.</summary>
    private async Task<Dictionary<string, YtStatistics>?> FetchVideoStatsAsync(
        List<string> videoIds,
        CancellationToken cancellationToken)
    {
        if (videoIds.Count == 0) return [];
        var ids = string.Join(",", videoIds.Select(Uri.EscapeDataString));
        try
        {
            var url = $"{ApiBase}/videos?part=statistics&id={ids}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Goog-Api-Key", _apiKey);
            var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
            var body = JsonSerializer.Deserialize<YtVideoListResponse>(
                await resp.Content.ReadAsStringAsync(cancellationToken), _json);
            return body?.Items?.ToDictionary(i => i.Id, i => i.Statistics ?? new())
                ?? [];
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            System.Diagnostics.Debug.WriteLine($"YouTube stats fetch failed: {ex.Message}");
            return null;
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_cache.Remove(item.Id, out var yt))
            return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(yt));
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new { id = item.Id, title = item.DisplayName }));
    }

    private static long ParseLong(string? s) =>
        long.TryParse(s, out var v) ? v : 0L;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
