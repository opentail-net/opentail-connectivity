namespace OpenTail.Connectivity.Tests;

public sealed class PipelineConfigParityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PipelineConfigParityTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    private async Task<IngestionReport> RunWithOptions(
        FolderConnectorOptions? connectorOpts,
        PipelineOptions? pipelineOpts,
        int fileCount,
        string? contentPrefix = null)
    {
        for (var i = 0; i < fileCount; i++)
            File.WriteAllText(Path.Combine(_dir, $"doc{i}.txt"),
                $"{contentPrefix ?? ""}Document {i} with sufficient text to pass content validation checks reliably.");

        var outPath = Path.Combine(_outDir, $"docs-{Guid.NewGuid():N}.jsonl");
        var connector = new FolderSourceConnector(_dir, "t", connectorOptions: connectorOpts);
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        try
        {
            return await new ConnectivityPipeline(connector, extractor, [exporter], pipelineOpts).RunAsync();
        }
        finally { await exporter.DisposeAsync(); }
    }

    [Fact]
    public async Task Pipeline_WithDedup_DeduplicatesIdenticalContent()
    {
        // FF.3: Dedup flag in PipelineOptions removes duplicate documents
        for (var i = 0; i < 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"dup{i}.txt"),
                "Identical content that should only be ingested once in this pipeline run.");

        var outPath = Path.Combine(_outDir, "dedup-docs.jsonl");
        var connector = new FolderSourceConnector(_dir, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        IngestionReport report;
        try
        {
            var opts = new PipelineOptions(DeduplicateByContentHash: true);
            report = await new ConnectivityPipeline(connector, extractor, [exporter], opts).RunAsync();
        }
        finally { await exporter.DisposeAsync(); }

        Assert.Equal(1, report.ItemsProcessed);
    }

    [Fact]
    public async Task Pipeline_WithMaxItems_StopsEarly()
    {
        // FF.3: MaxItemsProcessed limits pipeline run
        var report = await RunWithOptions(
            connectorOpts: null,
            pipelineOpts: new PipelineOptions(MaxItemsProcessed: 2),
            fileCount: 5);

        Assert.Equal(2, report.ItemsProcessed);
    }

    [Fact]
    public async Task Pipeline_WithIncludePattern_FiltersFiles()
    {
        // FF.2: IncludePatterns wired from PipelineSourceConfig through ConnectivitySourceBuilder
        File.WriteAllText(Path.Combine(_dir, "keep.md"),
            "This markdown file should be included in the ingestion run.");
        File.WriteAllText(Path.Combine(_dir, "skip.txt"),
            "This text file should be excluded from the ingestion run.");

        var report = await RunWithOptions(
            connectorOpts: new FolderConnectorOptions(IncludePatterns: ["**/*.md"]),
            pipelineOpts: null,
            fileCount: 0);

        Assert.Equal(1, report.ItemsProcessed);
    }
}
