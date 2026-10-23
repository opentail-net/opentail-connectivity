namespace OpenTail.Connectivity.Web;

/// <param name="AllowPrivateNetworks">Off by default: the crawler connects only to public addresses (checked on the address actually
/// connected to, so DNS rebinding does not help), which matters when an agent or a user supplies the URL. Turn it on to crawl an
/// internal site.</param>
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
    int MaxResponseBytes = 5 * 1024 * 1024,
    bool AllowPrivateNetworks = false);
