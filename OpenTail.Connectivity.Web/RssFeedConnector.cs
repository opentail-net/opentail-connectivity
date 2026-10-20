using System.Text.Json;
using System.Xml.Linq;

namespace OpenTail.Connectivity.Web;

public sealed class RssFeedConnector : ISourceConnector
{
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";

    private readonly HttpClient _client;
    private readonly string _feedUrl;
    private readonly string _tenant;
    private readonly int _maxItems;
    private readonly DateTimeOffset? _since;
    private readonly Dictionary<string, byte[]> _itemCache = new();

    public RssFeedConnector(HttpClient client, string feedUrl, string tenant, int maxItems = 100, DateTimeOffset? since = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(feedUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        _client = client;
        _feedUrl = feedUrl;
        _tenant = tenant;
        _maxItems = maxItems;
        _since = since;
    }

    public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _itemCache.Clear();

        var xml = await _client.GetStringAsync(_feedUrl, cancellationToken);
        var doc = XDocument.Parse(xml);
        var root = doc.Root!;

        var entries = root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase)
            ? ParseRss(root)
            : ParseAtom(root);

        var count = 0;
        foreach (var (id, displayName, modified, payload) in entries)
        {
            if (count >= _maxItems) break;
            if (_since.HasValue && modified < _since.Value) continue;

            cancellationToken.ThrowIfCancellationRequested();

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            _itemCache[id] = bytes;
            count++;

            yield return new SourceItem(
                Id: id,
                DisplayName: displayName,
                Extension: "rss-item",
                SizeBytes: bytes.Length,
                Modified: modified,
                TenantContext: _tenant);
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
        _itemCache.TryGetValue(item.Id, out var bytes)
            ? Task.FromResult(bytes)
            : Task.FromResult(Array.Empty<byte>());

    private static IEnumerable<(string id, string displayName, DateTimeOffset modified, Dictionary<string, string> payload)> ParseRss(XElement rss)
    {
        var channel = rss.Element("channel");
        if (channel is null) yield break;

        foreach (var item in channel.Elements("item"))
        {
            var link = (item.Element("link")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(link)) continue;

            var title = (item.Element("title")?.Value ?? link).Trim();
            var description = (item.Element("description")?.Value ?? string.Empty).Trim();
            var author = (item.Element("author")?.Value
                ?? item.Element("{http://purl.org/dc/elements/1.1/}creator")?.Value
                ?? string.Empty).Trim();
            var pubDate = item.Element("pubDate")?.Value;
            var modified = ParseDate(pubDate);
            var categories = string.Join("; ", item.Elements("category")
                .Select(c => c.Value?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)));

            yield return (link, title, modified, new Dictionary<string, string>
            {
                ["link"] = link,
                ["title"] = title,
                ["description"] = description,
                ["author"] = author,
                ["categories"] = categories,
                ["published"] = modified.ToString("O"),
            });
        }
    }

    private static IEnumerable<(string id, string displayName, DateTimeOffset modified, Dictionary<string, string> payload)> ParseAtom(XElement feed)
    {
        foreach (var entry in feed.Elements(AtomNs + "entry"))
        {
            var link = (entry.Elements(AtomNs + "link")
                .FirstOrDefault(l => l.Attribute("rel")?.Value != "enclosure")
                ?.Attribute("href")?.Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(link)) continue;

            var title = (entry.Element(AtomNs + "title")?.Value ?? link).Trim();
            var summary = (entry.Element(AtomNs + "summary")?.Value
                ?? entry.Element(AtomNs + "content")?.Value
                ?? string.Empty).Trim();
            var author = (entry.Element(AtomNs + "author")?.Element(AtomNs + "name")?.Value
                ?? string.Empty).Trim();
            var published = entry.Element(AtomNs + "published")?.Value
                ?? entry.Element(AtomNs + "updated")?.Value;
            var modified = ParseDate(published);
            var categories = string.Join("; ", entry.Elements(AtomNs + "category")
                .Select(c => c.Attribute("term")?.Value?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)));

            yield return (link, title, modified, new Dictionary<string, string>
            {
                ["link"] = link,
                ["title"] = title,
                ["summary"] = summary,
                ["author"] = author,
                ["categories"] = categories,
                ["published"] = modified.ToString("O"),
            });
        }
    }

    private static readonly string[] Rfc2822Formats =
    [
        "dd MMM yyyy HH:mm:ss zzz",
        "d MMM yyyy HH:mm:ss zzz",
    ];

    private static DateTimeOffset ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DateTimeOffset.UtcNow;
        var s = raw.Trim();
        // ISO 8601 (Atom feeds)
        if (DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var dt))
            return dt;
        // RFC 2822 (RSS 2.0 pubDate): "Mon, 01 Jan 2026 10:00:00 +0000"
        // Normalize +0000 → +00:00, then strip leading day-of-week ("Mon, ")
        var normalized = Regex.Replace(s, @"([+-])(\d{2})(\d{2})$", "$1$2:$3");
        var stripped = Regex.Replace(normalized, @"^\w{3},\s*", "");
        if (DateTimeOffset.TryParseExact(stripped, Rfc2822Formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
            return dt;
        return DateTimeOffset.UtcNow;
    }
}
