using System.Text.Json;

namespace OpenTail.Connectivity.Web;

public sealed class RssFeedTextExtractor : ITextExtractor
{
    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            var json = Encoding.UTF8.GetString(content);
            var item = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (item is null)
                return new TextExtractionResult(false, string.Empty, "Failed to deserialise feed item payload.");

            var body = item.GetValueOrDefault("description")
                ?? item.GetValueOrDefault("summary")
                ?? string.Empty;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.TryGetValue("author", out var author) && !string.IsNullOrWhiteSpace(author))
                metadata["feed.author"] = author;
            if (item.TryGetValue("link", out var link) && !string.IsNullOrWhiteSpace(link))
                metadata["feed.link"] = link;
            if (item.TryGetValue("published", out var pub) && !string.IsNullOrWhiteSpace(pub))
                metadata["feed.published"] = pub;
            if (item.TryGetValue("categories", out var cats) && !string.IsNullOrWhiteSpace(cats))
                metadata["feed.categories"] = cats;

            return new TextExtractionResult(true, body, null, metadata);
        }
        catch (Exception ex)
        {
            return new TextExtractionResult(false, string.Empty, ex.Message);
        }
    }
}
