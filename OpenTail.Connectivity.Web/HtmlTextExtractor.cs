namespace OpenTail.Connectivity.Web;

public sealed class HtmlTextExtractor : ITextExtractor
{
    // Blocks to strip entirely (tag + content)
    private static readonly Regex ScriptBlock = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleBlock = new(@"<style[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NavBlock = new(@"<nav[\s\S]*?</nav>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FooterBlock = new(@"<footer[\s\S]*?</footer>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeaderBlock = new(@"<header[\s\S]*?</header>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AsideBlock = new(@"<aside[\s\S]*?</aside>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FormBlock = new(@"<form[\s\S]*?</form>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ButtonBlock = new(@"<button[\s\S]*?</button>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Title extraction
    private static readonly Regex TitleTag = new(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Meta tag content extraction — matches both attribute orderings
    private static readonly Regex MetaDescription = new(
        @"<meta\s[^>]*name=[""']description[""'][^>]*content=[""']([^""']*)[""']|<meta\s[^>]*content=[""']([^""']*)[""'][^>]*name=[""']description[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MetaKeywords = new(
        @"<meta\s[^>]*name=[""']keywords[""'][^>]*content=[""']([^""']*)[""']|<meta\s[^>]*content=[""']([^""']*)[""'][^>]*name=[""']keywords[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OgTitle = new(
        @"<meta\s[^>]*property=[""']og:title[""'][^>]*content=[""']([^""']*)[""']|<meta\s[^>]*content=[""']([^""']*)[""'][^>]*property=[""']og:title[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Strip remaining tags, collapse whitespace
    private static readonly Regex AnyTag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex MultipleWhitespace = new(@"[ \t]{2,}", RegexOptions.Compiled);
    private static readonly Regex MultipleNewlines = new(@"\n{3,}", RegexOptions.Compiled);

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        var html = System.Text.Encoding.UTF8.GetString(content);
        return ExtractFromHtml(html);
    }

    public TextExtractionResult ExtractFromHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return new TextExtractionResult(false, string.Empty, "Empty HTML");

        var title = ExtractTitle(html);
        var description = ExtractMetaContent(MetaDescription, html);
        var keywords = ExtractMetaContent(MetaKeywords, html);
        var ogTitle = ExtractMetaContent(OgTitle, html);

        var body = StripHtml(html);

        // Prepend description to body for richer semantic content
        if (!string.IsNullOrWhiteSpace(description))
            body = description.Trim() + "\n\n" + body;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Title: prefer <title> tag, fall back to og:title
        var effectiveTitle = !string.IsNullOrWhiteSpace(title) ? title : ogTitle;
        if (!string.IsNullOrWhiteSpace(effectiveTitle))
            metadata["page.title"] = effectiveTitle;
        if (!string.IsNullOrWhiteSpace(description))
            metadata["page.description"] = description.Trim();
        if (!string.IsNullOrWhiteSpace(keywords))
            metadata["page.keywords"] = keywords.Trim();
        if (!string.IsNullOrWhiteSpace(ogTitle) && string.IsNullOrWhiteSpace(title))
            metadata["page.og_title"] = ogTitle.Trim();

        return new TextExtractionResult(true, body, null, metadata);
    }

    public static string? ExtractTitle(string html)
    {
        var m = TitleTag.Match(html);
        if (!m.Success) return null;
        return StripTags(m.Groups[1].Value).Trim();
    }

    private static string? ExtractMetaContent(Regex pattern, string html)
    {
        var m = pattern.Match(html);
        if (!m.Success) return null;
        // One of the two alternation groups will have the captured content
        var val = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return string.IsNullOrWhiteSpace(val) ? null : val.Trim();
    }

    private static string StripHtml(string html)
    {
        html = ScriptBlock.Replace(html, " ");
        html = StyleBlock.Replace(html, " ");
        html = NavBlock.Replace(html, " ");
        html = FooterBlock.Replace(html, " ");
        html = HeaderBlock.Replace(html, " ");
        html = AsideBlock.Replace(html, " ");
        html = FormBlock.Replace(html, " ");
        html = ButtonBlock.Replace(html, " ");
        html = StripTags(html);
        html = DecodeEntities(html);
        html = MultipleWhitespace.Replace(html, " ");
        html = MultipleNewlines.Replace(html, "\n\n");
        return html.Trim();
    }

    private static string StripTags(string html) => AnyTag.Replace(html, " ");

    private static string DecodeEntities(string html) => System.Net.WebUtility.HtmlDecode(html);
}
