namespace OpenTail.Connectivity.Tests;

public sealed class MarkdownDocumentExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public MarkdownDocumentExporterTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static NormalisedDocument MakeDoc(
        string title = "My Test Doc",
        string body = "Some body text.",
        Dictionary<string, string>? metadata = null) =>
        new NormalisedDocument(
            Id: "abcdef1234567890",
            Title: title,
            Body: body,
            ContentHash: "hash123",
            SourceRef: "/path/to/source.txt",
            SourceType: "file",
            TenantContext: "acme",
            SourceModified: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IngestedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            Metadata: metadata ?? new Dictionary<string, string>());

    private async Task<string> ExportAndRead(NormalisedDocument doc)
    {
        var exporter = new MarkdownDocumentExporter(_dir);
        await exporter.ExportAsync(doc);
        var files = Directory.GetFiles(_dir, "*.md");
        Assert.Single(files);
        return await File.ReadAllTextAsync(files[0]);
    }

    [Fact]
    public async Task FrontmatterContainsAllRequiredFields()
    {
        var content = await ExportAndRead(MakeDoc());

        Assert.Contains("id: abcdef1234567890", content);
        Assert.Contains("source_ref: /path/to/source.txt", content);
        Assert.Contains("source_type: file", content);
        Assert.Contains("tenant: acme", content);
        Assert.Contains("content_hash: hash123", content);
        Assert.Contains("source_modified:", content);
        Assert.Contains("ingested_at:", content);
    }

    [Fact]
    public async Task H1TitlePresentInFile()
    {
        var content = await ExportAndRead(MakeDoc(title: "My Great Title"));

        Assert.Contains("# My Great Title", content);
    }

    [Fact]
    public async Task BodyPresentInFile()
    {
        var content = await ExportAndRead(MakeDoc(body: "Hello from the body!"));

        Assert.Contains("Hello from the body!", content);
    }

    [Fact]
    public async Task MetadataKeysAppearInFrontmatter()
    {
        var meta = new Dictionary<string, string> { ["category"] = "tech", ["author"] = "alice" };
        var content = await ExportAndRead(MakeDoc(metadata: meta));

        Assert.Contains("category: tech", content);
        Assert.Contains("author: alice", content);
    }

    [Fact]
    public async Task MetadataNewlineCannotInjectFields()
    {
        var meta = new Dictionary<string, string> { ["note"] = "line1\ntenant: evil" };
        var content = await ExportAndRead(MakeDoc(metadata: meta));

        Assert.Contains("note: \"line1\\ntenant: evil\"", content);
        Assert.DoesNotContain("\ntenant: evil", content);
    }

    [Theory]
    [InlineData("a: b", "\"a: b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("# not a comment", "\"# not a comment\"")]
    [InlineData("true", "\"true\"")]
    [InlineData("123", "\"123\"")]
    [InlineData("", "\"\"")]
    [InlineData("café", "\"café\"")]
    [InlineData("back\\slash", "\"back\\\\slash\"")]
    [InlineData("plain-value_1.0", "plain-value_1.0")]
    public void YamlScalarQuotesAnythingAmbiguous(string input, string expected) =>
        Assert.Equal(expected, MarkdownDocumentExporter.YamlScalar(input));

    [Fact]
    public async Task MetadataKeysAreEncodedAndCannotShadowFixedFields()
    {
        var meta = new Dictionary<string, string> { ["id"] = "spoof", ["bad: key"] = "v" };
        var content = await ExportAndRead(MakeDoc(metadata: meta));

        Assert.Contains("meta_id: spoof", content);
        Assert.Contains("\"bad: key\": v", content);
        Assert.Single(content.Split('\n'), l => l.StartsWith("id:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TitleNewlinesDoNotBreakHeading()
    {
        var content = await ExportAndRead(MakeDoc(title: "One\nTwo"));

        Assert.Contains("# One Two", content);
    }

    [Fact]
    public async Task SafeFilenameStripsPathUnsafeChars()
    {
        var doc = MakeDoc(title: "Title:With/Unsafe*Chars?");
        var exporter = new MarkdownDocumentExporter(_dir);
        await exporter.ExportAsync(doc);

        var files = Directory.GetFiles(_dir, "*.md");
        Assert.Single(files);
        var name = Path.GetFileName(files[0]);
        Assert.DoesNotContain(":", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("*", name);
        Assert.DoesNotContain("?", name);
    }

    [Fact]
    public async Task MultipleDocsCreateMultipleFiles()
    {
        var exporter = new MarkdownDocumentExporter(_dir);

        var doc1 = MakeDoc(title: "Doc One") with { Id = "id0000001" };
        var doc2 = MakeDoc(title: "Doc Two") with { Id = "id0000002" };

        await exporter.ExportAsync(doc1);
        await exporter.ExportAsync(doc2);

        var files = Directory.GetFiles(_dir, "*.md");
        Assert.Equal(2, files.Length);
    }
}
