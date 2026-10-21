using System.Net;
using System.Text;

namespace OpenTail.Connectivity.Web;

/// <summary>Crawls same-origin web pages and extracts links and page content.</summary>
/// <remarks>Redirects are checked before each request when the client disables automatic redirects, as <see cref="CreateHttpClient"/> does.
/// An injected client that follows redirects itself receives only the final-URI check.</remarks>
public sealed class SiteCrawlConnector : ISourceConnector, IDisposable
{
    private const int DefaultMaxLinkExtractionBytes = 1_000_000;

    private static readonly Regex HrefPattern =
        new(@"href\s*=\s*[""']([^""'#?][^""']*)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Extensions that are definitely not pages worth ingesting
    private static readonly HashSet<string> SkipExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".svg", ".ico", ".webp",
        ".css", ".js", ".woff", ".woff2", ".ttf", ".eot",
        ".zip", ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".mp4", ".mp3", ".avi", ".mov",
    };

    private readonly HttpClient _client;
    private readonly Uri _startUri;
    private readonly string _tenantContext;
    private readonly WebCrawlOptions _options;
    private readonly bool _ownsClient;
    private readonly HtmlTextExtractor _extractor = new();
    private readonly Dictionary<string, byte[]> _fetchCache = new(StringComparer.OrdinalIgnoreCase);
    private int _failedFetches;

    public int FailedFetchCount => _failedFetches;

    public SiteCrawlConnector(HttpClient client, string startUrl, string tenantContext, WebCrawlOptions? options = null)
        : this(client, startUrl, tenantContext, options, ownsClient: false)
    {
    }

    public SiteCrawlConnector(string startUrl, string tenantContext, WebCrawlOptions? options = null)
        : this(CreateHttpClient(), startUrl, tenantContext, options, ownsClient: true)
    {
    }

    private SiteCrawlConnector(HttpClient client, string startUrl, string tenantContext, WebCrawlOptions? options, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _client = client;
        _ownsClient = ownsClient;
        _startUri = new Uri(startUrl);
        _tenantContext = tenantContext;
        _options = options ?? new WebCrawlOptions();
    }

    public static HttpClient CreateHttpClient(TimeSpan? timeout = null)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        });
        if (timeout is { } value)
            client.Timeout = value;
        return client;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _client.Dispose();
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _failedFetches = 0;
        _fetchCache.Clear();

        var robots = _options.RespectRobotsTxt
            ? await RobotsTxtParser.FetchAsync(_client, _startUri, MaxResponseBytes, cancellationToken)
            : new RobotsTxtParser();

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(Uri Uri, int Depth)>();

        if (_options.UseSitemap)
        {
            // Seed queue from sitemap; fall back to home page on failure
            var sitemapUrls = await SitemapParser.FetchUrlsAsync(
                _client, _startUri, _options.MaxPages, MaxResponseBytes, cancellationToken);
            if (sitemapUrls.Count > 0)
            {
                foreach (var url in sitemapUrls)
                {
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var sitemapUri)) continue;
                    if (!visited.Add(sitemapUri.AbsoluteUri)) continue;
                    if (!WithinPathPrefixes(sitemapUri))
                        continue;
                    queue.Enqueue((sitemapUri, 0));
                }
            }
            else
            {
                queue.Enqueue((_startUri, 0));
                visited.Add(_startUri.AbsoluteUri);
            }
        }
        else
        {
            queue.Enqueue((_startUri, 0));
            visited.Add(_startUri.AbsoluteUri);
        }

        while (queue.Count > 0 && visited.Count <= _options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (uri, depth) = queue.Dequeue();

            if (!robots.IsAllowed(uri.AbsolutePath))
                continue;

            var (html, etag) = await FetchHtmlWithRetryAsync(uri, cancellationToken);
            if (html is null) { _failedFetches++; continue; }

            _fetchCache[uri.AbsoluteUri] = System.Text.Encoding.UTF8.GetBytes(html);

            var title = HtmlTextExtractor.ExtractTitle(html) ?? uri.AbsolutePath;

            // The connector fetches during discovery, so source.version saves extraction and export, not the download.
            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrWhiteSpace(etag))
            {
                metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [SourceKeys.Version] = etag
                };
            }

            yield return WebSourceItemFactory.Create(uri.AbsoluteUri, title, _tenantContext, metadata);

            // Enqueue same-domain links
            if (depth >= _options.MaxDepth)
                continue;

            foreach (var link in ExtractLinks(html, uri))
            {
                if (visited.Contains(link.AbsoluteUri) || visited.Count >= _options.MaxPages)
                    continue;
                if (!WithinPathPrefixes(link))
                    continue;
                visited.Add(link.AbsoluteUri);
                queue.Enqueue((link, depth + 1));
            }

            if (_options.RequestDelay.HasValue)
                await Task.Delay(_options.RequestDelay.Value, cancellationToken);
        }
    }

    public async Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
    {
        if (_fetchCache.Remove(item.Id, out var cached))
            return cached;

        if (!Uri.TryCreate(item.Id, UriKind.Absolute, out var uri) || !HasSameOrigin(uri, _startUri))
            throw new InvalidOperationException("The requested page is outside the crawl origin.");

        using var response = await WebContentReader.GetWithinOriginAsync(_client, uri, _startUri, cancellationToken, WithinPathPrefixes)
            ?? throw new InvalidOperationException("The requested page could not be fetched within the crawl origin.");
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri ?? uri;
        if (!HasSameOrigin(finalUri, _startUri))
            throw new InvalidOperationException("The requested page redirected outside the crawl origin.");
        var bytes = await WebContentReader.ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken);
        if (bytes is null)
            throw new InvalidOperationException("The requested page exceeds the configured response size limit.");
        return Encoding.UTF8.GetBytes(DecodeHtml(bytes, response.Content.Headers.ContentType?.CharSet));
    }

    private async Task<(string? Html, string? ETag)> FetchHtmlWithRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        var retryDelay = _options.RetryDelay ?? TimeSpan.FromMilliseconds(500);
        for (var attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            try
            {
                using var response = await WebContentReader.GetWithinOriginAsync(_client, uri, _startUri, cancellationToken, WithinPathPrefixes);
                if (response is null)
                    return (null, null);
                if (response.IsSuccessStatusCode)
                {
                    var finalUri = response.RequestMessage?.RequestUri ?? uri;
                    if (!HasSameOrigin(finalUri, _startUri))
                        return (null, null);
                    var bytes = await WebContentReader.ReadBoundedAsync(response.Content, MaxResponseBytes, cancellationToken);
                    var etag = response.Headers.ETag?.Tag ?? (response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null);
                    return (bytes is null ? null : DecodeHtml(bytes, response.Content.Headers.ContentType?.CharSet), etag);
                }

                if (!IsTransient(response.StatusCode) || attempt >= _options.MaxRetries)
                    return (null, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException) when (attempt < _options.MaxRetries)
            {
                await Task.Delay(retryDelay, cancellationToken);
                continue;
            }
            catch (HttpRequestException)
            {
                return (null, null);
            }

            await Task.Delay(retryDelay, cancellationToken);
        }

        return (null, null);
    }

    private static bool IsTransient(System.Net.HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return statusCode == System.Net.HttpStatusCode.RequestTimeout ||
               statusCode == (System.Net.HttpStatusCode)429 ||
               code >= 500;
    }

    /// <summary>Uses the declared charset; missing or unsupported declarations fall back to UTF-8.</summary>
    private static Encoding GetResponseEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return Encoding.UTF8;

        charset = charset.Trim().Trim('"', '\'');
        if (charset.Length == 0)
            return Encoding.UTF8;

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(charset) ?? Encoding.UTF8;
            }
            catch (Exception providerException) when (providerException is ArgumentException or NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }
    }

    private static string DecodeHtml(byte[] bytes, string? charset)
    {
        var html = GetResponseEncoding(charset).GetString(bytes);
        return html.Length > 0 && html[0] == '\uFEFF' ? html[1..] : html;
    }

    private IEnumerable<Uri> ExtractLinks(string html, Uri baseUri)
    {
        var maxBytes = _options.MaxLinkExtractionBytes > 0
            ? _options.MaxLinkExtractionBytes
            : DefaultMaxLinkExtractionBytes;
        var boundedHtml = TrimToUtf8ByteLimit(html, maxBytes);

        foreach (Match m in HrefPattern.Matches(boundedHtml))
        {
            var href = m.Groups[1].Value.Trim();
            if (string.IsNullOrWhiteSpace(href)) continue;

            // Skip anchors, mailto, javascript
            if (href.StartsWith('#') || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(baseUri, href, out var abs)) continue;
            if (!HasSameOrigin(abs, _startUri)) continue;
            if (SkipExtensions.Contains(Path.GetExtension(abs.AbsolutePath))) continue;

            yield return new Uri(abs.GetLeftPart(UriPartial.Path)); // strip query/fragment
        }
    }

    private bool WithinPathPrefixes(Uri uri)
        => _options.AllowedPathPrefixes is not { Count: > 0 } prefixes
           || prefixes.Any(prefix => uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    internal static bool HasSameOrigin(Uri a, Uri b)
    {
        if (Uri.Compare(a, b, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
            return true;

        if (!a.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !b.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase))
            return false;

        return a.Port == b.Port || (a.IsDefaultPort && b.IsDefaultPort);
    }

    private int MaxResponseBytes => _options.MaxResponseBytes > 0 ? _options.MaxResponseBytes : 5 * 1024 * 1024;

    private static string TrimToUtf8ByteLimit(string value, int maxBytes)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(value) <= maxBytes)
            return value;

        var low = 0;
        var high = value.Length;
        while (low < high)
        {
            var mid = low + ((high - low + 1) / 2);
            if (System.Text.Encoding.UTF8.GetByteCount(value.AsSpan(0, mid)) <= maxBytes)
                low = mid;
            else
                high = mid - 1;
        }

        return value[..low];
    }
}
