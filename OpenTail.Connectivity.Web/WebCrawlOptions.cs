namespace OpenTail.Connectivity.Web;

public sealed record WebCrawlOptions(
    int MaxPages = 50,
    TimeSpan? RequestDelay = null,
    bool RespectRobotsTxt = true,
    int MaxRetries = 1,
    TimeSpan? RetryDelay = null,
    bool UseSitemap = false,
    IReadOnlyList<string>? AllowedPathPrefixes = null,
    int MaxDepth = 10,
    int MaxLinkExtractionBytes = 1_000_000,
    int MaxResponseBytes = 5 * 1024 * 1024);
