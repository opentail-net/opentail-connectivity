namespace OpenTail.Connectivity.Tests;

public sealed class HtmlMetaExtractionTests
{
    private readonly HtmlTextExtractor _extractor = new();

    [Fact]
    public void MetaDescription_ExtractedAndPrependedToBody()
    {
        const string html = """
            <html>
            <head>
              <meta name="description" content="The best product overview page">
              <title>Products</title>
            </head>
            <body><p>Product details here.</p></body>
            </html>
            """;

        var result = _extractor.ExtractFromHtml(html);

        Assert.True(result.Success);
        Assert.StartsWith("The best product overview page", result.Text);
        Assert.Equal("The best product overview page", result.Metadata?["page.description"]);
    }

    [Fact]
    public void MetaKeywords_StoredInMetadata()
    {
        const string html = """
            <html><head>
              <meta name="keywords" content="openapi, rest, json">
            </head><body>API docs.</body></html>
            """;

        var result = _extractor.ExtractFromHtml(html);

        Assert.True(result.Success);
        Assert.Equal("openapi, rest, json", result.Metadata?["page.keywords"]);
    }

    [Fact]
    public void OgTitle_UsedWhenNoTitleTag()
    {
        const string html = """
            <html><head>
              <meta property="og:title" content="My Open Graph Title">
            </head><body>Content here.</body></html>
            """;

        var result = _extractor.ExtractFromHtml(html);

        Assert.True(result.Success);
        Assert.Equal("My Open Graph Title", result.Metadata?["page.title"]);
    }

    [Fact]
    public void TitleTagWins_OverOgTitle()
    {
        const string html = """
            <html><head>
              <title>Real Title</title>
              <meta property="og:title" content="OG Title">
            </head><body>Content.</body></html>
            """;

        var result = _extractor.ExtractFromHtml(html);

        Assert.True(result.Success);
        Assert.Equal("Real Title", result.Metadata?["page.title"]);
    }

    [Fact]
    public void MissingMetaTags_NoException()
    {
        const string html = "<html><body><p>Plain page, no meta tags.</p></body></html>";

        var result = _extractor.ExtractFromHtml(html);

        Assert.True(result.Success);
        Assert.False(result.Metadata?.ContainsKey("page.description") ?? false);
        Assert.False(result.Metadata?.ContainsKey("page.keywords") ?? false);
    }
}
