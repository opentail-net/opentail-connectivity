namespace OpenTail.Connectivity.Tests;

public sealed class PipelineMaxItemsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PipelineMaxItemsTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    private async Task<IngestionReport> RunWithMaxItems(int fileCount, int? maxItems)
    {
        for (var i = 0; i < fileCount; i++)
            File.WriteAllText(Path.Combine(_dir, $"doc{i}.txt"),
                $"Document {i} with enough content to pass the short content check validator.");

        var outPath = Path.Combine(_outDir, "docs.jsonl");
        var connector = new FolderSourceConnector(_dir, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        try
        {
            var opts = maxItems.HasValue ? new PipelineOptions(MaxItemsProcessed: maxItems.Value) : null;
            var pipeline = new ConnectivityPipeline(connector, extractor, [exporter], opts);
            return await pipeline.RunAsync();
        }
        finally { await exporter.DisposeAsync(); }
    }

    [Fact]
    public async Task MaxItems_1_StopsAfterFirst()
    {
        var report = await RunWithMaxItems(fileCount: 5, maxItems: 1);
        Assert.Equal(1, report.ItemsProcessed);
    }

    [Fact]
    public async Task MaxItems_ExceedsTotal_AllExported()
    {
        var report = await RunWithMaxItems(fileCount: 3, maxItems: 10);
        Assert.Equal(3, report.ItemsProcessed);
    }

    [Fact]
    public async Task MaxItems_Null_NoLimit()
    {
        var report = await RunWithMaxItems(fileCount: 4, maxItems: null);
        Assert.Equal(4, report.ItemsProcessed);
    }
}
