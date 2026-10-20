namespace OpenTail.Connectivity.Tests;

public sealed class HtmlTextExtractorTests
{
    private readonly HtmlTextExtractor _extractor = new();

    [Fact]
    public void Extracts_title_from_title_tag()
    {
        var html = "<html><head><title>My Page</title></head><body>Hello</body></html>";
        var result = _extractor.ExtractFromHtml(html);
        Assert.True(result.Success);
        Assert.Equal("My Page", result.Metadata!["page.title"]);
    }

    [Fact]
    public void Strips_script_blocks()
    {
        var html = "<body><script>alert('xss')</script>Clean text</body>";
        var result = _extractor.ExtractFromHtml(html);
        Assert.DoesNotContain("alert", result.Text);
        Assert.Contains("Clean text", result.Text);
    }

    [Fact]
    public void Strips_style_blocks()
    {
        var html = "<body><style>body{color:red}</style>Visible</body>";
        var result = _extractor.ExtractFromHtml(html);
        Assert.DoesNotContain("color", result.Text);
        Assert.Contains("Visible", result.Text);
    }

    [Fact]
    public void Strips_nav_and_footer()
    {
        var html = "<nav>Nav link</nav><main>Main content</main><footer>Footer</footer>";
        var result = _extractor.ExtractFromHtml(html);
        Assert.DoesNotContain("Nav link", result.Text);
        Assert.DoesNotContain("Footer", result.Text);
        Assert.Contains("Main content", result.Text);
    }

    [Fact]
    public void Decodes_html_entities()
    {
        var html = "<body>Tom &amp; Jerry &lt;great&gt;</body>";
        var result = _extractor.ExtractFromHtml(html);
        Assert.Contains("Tom & Jerry", result.Text);
        Assert.Contains("<great>", result.Text);
    }

    [Fact]
    public void Empty_html_returns_failure()
    {
        var result = _extractor.ExtractFromHtml("");
        Assert.False(result.Success);
    }

    [Fact]
    public void Extract_via_bytes_works()
    {
        var html = "<html><body>Byte content</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var result = _extractor.Extract("page.html", bytes);
        Assert.True(result.Success);
        Assert.Contains("Byte content", result.Text);
    }
}
