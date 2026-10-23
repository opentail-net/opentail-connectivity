using System.Text.Json;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace OpenTail.Connectivity.Web;

/// <summary>Reads an RSS 2.0 or Atom feed: one item per entry, its fields as a small JSON payload.</summary>
/// <remarks>
/// An Atom entry is identified by its <c>id</c> (its alternate link when it has none) and dated by <c>updated</c>
/// (<c>published</c> when it has none); an RSS item by its link and <c>pubDate</c>. An entry with a missing or unreadable date
/// gets <see cref="UnknownDate"/> and is left out when <c>since</c> is set, since nothing shows it is recent. The response is
/// read up to <c>maxResponseBytes</c> (default 10 MB) and parsed with DTDs prohibited.
/// </remarks>
public sealed class RssFeedConnector : ISourceConnector
{
    /// <summary>The <see cref="SourceItem.Modified"/> of an entry without a readable date.</summary>
    public static readonly DateTimeOffset UnknownDate = DateTimeOffset.UnixEpoch;

    private const int DefaultMaxResponseBytes = 10 * 1024 * 1024;
    private static readonly XNamespace AtomNs = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace RssContentNs = "http://purl.org/rss/1.0/modules/content/";

    private readonly HttpClient _client;
    private readonly string _feedUrl;
    private readonly string _tenant;
    private readonly int _maxItems;
    private readonly DateTimeOffset? _since;
    private readonly int _maxResponseBytes;
    private readonly Dictionary<string, byte[]> _itemCache = new();

    /// <summary>Reads <paramref name="feedUrl"/> over <see cref="WebNetwork.CreateHandler"/>: each redirect checked before it is
    /// followed (http and https only, at most 5), and only public addresses unless <paramref name="allowPrivateNetworks"/>.</summary>
    public RssFeedConnector(string feedUrl, string tenant, int maxItems = 100, DateTimeOffset? since = null,
        int maxResponseBytes = DefaultMaxResponseBytes, bool allowPrivateNetworks = false)
        : this(new HttpClient(WebNetwork.CreateHandler(allowPrivateNetworks)), feedUrl, tenant, maxItems, since, maxResponseBytes)
    {
    }

    /// <summary>As the URL constructor, on <paramref name="handler"/>, which is refused if it follows redirects itself; the
    /// public-address check is attached to its <see cref="SocketsHttpHandler"/>.</summary>
    public static RssFeedConnector Create(HttpMessageHandler handler, string feedUrl, string tenant, int maxItems = 100, DateTimeOffset? since = null,
        int maxResponseBytes = DefaultMaxResponseBytes, bool allowPrivateNetworks = false)
    {
        WebNetwork.Prepare(handler, allowPrivateNetworks);
        return new RssFeedConnector(new HttpClient(handler, disposeHandler: true), feedUrl, tenant, maxItems, since, maxResponseBytes);
    }

    // Tests inject a stub client. Outside this assembly a client cannot be passed, for the same reasons as the crawler.
    internal RssFeedConnector(HttpClient client, string feedUrl, string tenant, int maxItems = 100, DateTimeOffset? since = null,
        int maxResponseBytes = DefaultMaxResponseBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResponseBytes, 1);
        _maxResponseBytes = maxResponseBytes;
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

        using var response = await WebContentReader.GetFollowingRedirectsAsync(_client, new Uri(_feedUrl), cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await WebContentReader.ReadBoundedAsync(response.Content, _maxResponseBytes, cancellationToken)
                   ?? throw new InvalidDataException($"The feed is larger than the {_maxResponseBytes}-byte limit.");
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new MemoryStream(body), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root!;

        var entries = root.Name.LocalName.Equals("rss", StringComparison.OrdinalIgnoreCase)
            ? ParseRss(root)
            : ParseAtom(root);

        var count = 0;
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, displayName, modified, version, payload) in entries)
        {
            if (count >= _maxItems) break;
            if (!seenIds.Add(id)) continue;
            if (_since.HasValue && (modified == UnknownDate || modified < _since.Value)) continue;

            cancellationToken.ThrowIfCancellationRequested();

            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            _itemCache[id] = bytes;
            count++;

            Dictionary<string, string>? metadata = null;
            if (version is not null)
            {
                metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [SourceKeys.Version] = version
                };
            }

            yield return new SourceItem(
                Id: id,
                DisplayName: displayName,
                Extension: "rss-item",
                SizeBytes: bytes.Length,
                Modified: modified,
                TenantContext: _tenant,
                Metadata: metadata);
        }
    }

    public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
        _itemCache.TryGetValue(item.Id, out var bytes)
            ? Task.FromResult(bytes)
            : Task.FromResult(Array.Empty<byte>());

    private static IEnumerable<(string id, string displayName, DateTimeOffset modified, string? version, Dictionary<string, string> payload)> ParseRss(XElement rss)
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
            var pubDate = item.Element("pubDate")?.Value?.Trim();
            var guid = item.Element("guid")?.Value?.Trim();
            var content = (item.Element(RssContentNs + "encoded")?.Value ?? string.Empty).Trim();
            var date = ParseDate(pubDate);
            var modified = date ?? UnknownDate;
            var categories = string.Join("; ", item.Elements("category")
                .Select(c => c.Value?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)));

            // Every field that reaches the payload, so a change to any of them is a new version.
            var versionFields = string.Join('\u001f', guid ?? string.Empty, link, title, description, content, pubDate ?? string.Empty,
                author, categories);
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionFields)))
                .ToLowerInvariant()[..32];

            yield return (link, title, modified, version, new Dictionary<string, string>
            {
                ["link"] = link,
                ["title"] = title,
                ["description"] = description,
                ["author"] = author,
                ["categories"] = categories,
                ["published"] = date?.ToString("O") ?? string.Empty,
            });
        }
    }

    private static IEnumerable<(string id, string displayName, DateTimeOffset modified, string? version, Dictionary<string, string> payload)> ParseAtom(XElement feed)
    {
        foreach (var entry in feed.Elements(AtomNs + "entry"))
        {
            // The content link is rel="alternate", which is also what a link without rel means (RFC 4287 4.2.7.2).
            var link = (entry.Elements(AtomNs + "link")
                .FirstOrDefault(l => (l.Attribute("rel")?.Value ?? "alternate") == "alternate")
                ?.Attribute("href")?.Value ?? string.Empty).Trim();
            var atomId = entry.Element(AtomNs + "id")?.Value?.Trim();
            // The Atom id is the stable identity; the link can change. Without an id, the link stands in.
            var id = string.IsNullOrWhiteSpace(atomId) ? link : atomId;
            if (string.IsNullOrWhiteSpace(id)) continue;

            var title = (entry.Element(AtomNs + "title")?.Value ?? (link.Length > 0 ? link : id)).Trim();
            var summary = (entry.Element(AtomNs + "summary")?.Value
                ?? entry.Element(AtomNs + "content")?.Value
                ?? string.Empty).Trim();
            var author = (entry.Element(AtomNs + "author")?.Element(AtomNs + "name")?.Value
                ?? string.Empty).Trim();
            var updated = entry.Element(AtomNs + "updated")?.Value?.Trim();
            var published = entry.Element(AtomNs + "published")?.Value?.Trim();
            var publishedDate = ParseDate(published);
            var modified = ParseDate(updated) ?? publishedDate ?? UnknownDate;
            var categories = string.Join("; ", entry.Elements(AtomNs + "category")
                .Select(c => c.Attribute("term")?.Value?.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)));

            var payload = new Dictionary<string, string>
            {
                ["link"] = link,
                ["title"] = title,
                ["summary"] = summary,
                ["author"] = author,
                ["categories"] = categories,
                ["published"] = publishedDate?.ToString("O") ?? string.Empty,
                ["updated"] = modified == UnknownDate ? string.Empty : modified.ToString("O"),
            };
            // A hash of the whole payload: a change to any field the extractor sees is a new version.
            var version = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                id + "\u001f" + string.Join('\u001f', payload.Select(p => p.Key + "=" + p.Value)))))[..32];

            yield return (id, title, modified, version, payload);
        }
    }

    private static readonly string[] Rfc2822Formats =
    [
        "dd MMM yyyy HH:mm:ss zzz",
        "d MMM yyyy HH:mm:ss zzz",
    ];

    private static DateTimeOffset? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
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
        return null;
    }
}
