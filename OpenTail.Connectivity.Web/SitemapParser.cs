using System.Xml.Linq;

namespace OpenTail.Connectivity.Web;

internal static class SitemapParser
{
    private const int MaxSitemapIndexDepth = 5;

    /// <summary>
    /// Fetches sitemap.xml (or sitemap_index.xml) from baseUri/sitemap.xml and returns
    /// up to maxUrls page URLs from the same domain. Returns empty list on any failure.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchUrlsAsync(
        HttpClient client,
        Uri baseUri,
        int maxUrls,
        int maxResponseBytes,
        CancellationToken cancellationToken = default)
    {
        var sitemapUri = new Uri(baseUri, "/sitemap.xml");
        var results = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await FetchSitemapAsync(client, sitemapUri, baseUri, results, visited,
                maxUrls, maxResponseBytes, depth: 0, cancellationToken);
        }
        catch (Exception ex)
        { 
            System.Diagnostics.Debug.WriteLine($"Sitemap fetch failed for {sitemapUri}: {ex.Message}");
            /* fallback to link-crawling */ 
        }

        return results;
    }

    private static async Task FetchSitemapAsync(
        HttpClient client, Uri sitemapUri, Uri requiredOrigin,
        List<string> results, HashSet<string> visited,
        int maxUrls, int maxResponseBytes, int depth, CancellationToken ct)
    {
        if (depth > MaxSitemapIndexDepth || results.Count >= maxUrls) return;

        string xml;
        try
        {
            using var response = await WebContentReader.GetWithinOriginAsync(client, sitemapUri, requiredOrigin, ct);
            if (response is null) return;
            response.EnsureSuccessStatusCode();
            var bytes = await WebContentReader.ReadBoundedAsync(response.Content, maxResponseBytes, ct);
            if (bytes is null) return;
            xml = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        { 
            System.Diagnostics.Debug.WriteLine($"Failed to download sitemap {sitemapUri}: {ex.Message}");
            return; 
        }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        { 
            System.Diagnostics.Debug.WriteLine($"Failed to parse sitemap XML from {sitemapUri}: {ex.Message}");
            return; 
        }

        var root = doc.Root;
        if (root is null) return;

        var rootName = root.Name.LocalName;

        if (rootName.Equals("sitemapindex", StringComparison.OrdinalIgnoreCase))
        {
            // Sitemap index — follow nested sitemaps
            XNamespace ns = root.GetDefaultNamespace();
            foreach (var sitemapEl in root.Elements(ns + "sitemap"))
            {
                ct.ThrowIfCancellationRequested();
                var loc = sitemapEl.Element(ns + "loc")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(loc)) continue;
                if (!Uri.TryCreate(loc, UriKind.Absolute, out var nestedUri)) continue;
                if (!SiteCrawlConnector.HasSameOrigin(nestedUri, requiredOrigin)) continue;
                await FetchSitemapAsync(client, nestedUri, requiredOrigin, results, visited,
                    maxUrls, maxResponseBytes, depth + 1, ct);
                if (results.Count >= maxUrls) break;
            }
        }
        else if (rootName.Equals("urlset", StringComparison.OrdinalIgnoreCase))
        {
            // Regular sitemap — extract <url><loc> entries
            XNamespace ns = root.GetDefaultNamespace();
            foreach (var urlEl in root.Elements(ns + "url"))
            {
                ct.ThrowIfCancellationRequested();
                var loc = urlEl.Element(ns + "loc")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(loc)) continue;
                if (!Uri.TryCreate(loc, UriKind.Absolute, out var pageUri)) continue;
                if (!SiteCrawlConnector.HasSameOrigin(pageUri, requiredOrigin)) continue;
                if (!visited.Add(pageUri.AbsoluteUri)) continue;
                results.Add(pageUri.AbsoluteUri);
                if (results.Count >= maxUrls) break;
            }
        }
    }
}
