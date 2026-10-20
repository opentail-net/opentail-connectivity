namespace OpenTail.Connectivity.Tests;

public sealed class PipelineMetadataTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PipelineMetadataTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    private async Task<NormalisedDocument> RunAndGetDoc(PipelineOptions? options = null)
    {
        File.WriteAllText(Path.Combine(_dir, "doc.txt"),
            "This is a document with enough content to pass the short content validator check.");

        var outPath = Path.Combine(_outDir, "docs.jsonl");
        var connector = new FolderSourceConnector(_dir, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        try
        {
            var pipeline = new ConnectivityPipeline(connector, extractor, [exporter], options);
            await pipeline.RunAsync();
        }
        finally { await exporter.DisposeAsync(); }

        var line = File.ReadLines(outPath).First(l => !string.IsNullOrWhiteSpace(l));
        return System.Text.Json.JsonSerializer.Deserialize<NormalisedDocument>(line,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }

    [Fact]
    public async Task ExtraMetadata_AppearsWithRunPrefix()
    {
        var opts = new PipelineOptions(ExtraMetadata: new Dictionary<string, string>
        {
            ["env"] = "production",
            ["team"] = "engineering"
        });

        var doc = await RunAndGetDoc(opts);

        Assert.Equal("production", doc.Metadata["run.env"]);
        Assert.Equal("engineering", doc.Metadata["run.team"]);
    }

    [Fact]
    public async Task NoExtraMetadata_NoRunKeys()
    {
        var doc = await RunAndGetDoc();

        Assert.DoesNotContain(doc.Metadata.Keys, k => k.StartsWith("run.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtraMetadata_DoesNotCollideWithExtractionKeys()
    {
        // Extraction adds "extraction.*" keys; run adds "run.*" — they must not overlap
        var opts = new PipelineOptions(ExtraMetadata: new Dictionary<string, string>
        {
            ["source.extension"] = "should-not-overwrite" // matches a built-in key
        });

        var doc = await RunAndGetDoc(opts);

        // The run key is "run.source.extension", not "source.extension"
        Assert.True(doc.Metadata.ContainsKey("run.source.extension"));
        // Original source.extension key still has the correct value
        Assert.Equal("txt", doc.Metadata["source.extension"]);
    }
}
