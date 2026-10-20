namespace OpenTail.Connectivity.Tests;

public sealed class PipelineDedupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PipelineDedupTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    private async Task<(IngestionReport report, int lineCount)> RunPipeline(
        string folder, string outPath, PipelineOptions? options = null)
    {
        var connector = new FolderSourceConnector(folder, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        IngestionReport report;
        try
        {
            var pipeline = new ConnectivityPipeline(connector, extractor, [exporter], options);
            report = await pipeline.RunAsync();
        }
        finally
        {
            await exporter.DisposeAsync();
        }
        var lineCount = File.ReadLines(outPath).Count(l => !string.IsNullOrWhiteSpace(l));
        return (report, lineCount);
    }

    [Fact]
    public async Task DedupOff_BothIdenticalDocumentsExported()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "Identical content for testing deduplication.");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "Identical content for testing deduplication.");
        var outPath = Path.Combine(_outDir, "docs.jsonl");

        var (report, lineCount) = await RunPipeline(_dir, outPath);

        Assert.Equal(2, report.ItemsProcessed);
        Assert.Equal(0, report.ItemsSkipped);
        Assert.Equal(2, lineCount);
    }

    [Fact]
    public async Task DedupOn_SecondIdenticalDocumentSkipped()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "Identical content for testing deduplication behavior.");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "Identical content for testing deduplication behavior.");
        var outPath = Path.Combine(_outDir, "docs.jsonl");

        var (report, lineCount) = await RunPipeline(_dir, outPath,
            new PipelineOptions(DeduplicateByContentHash: true));

        Assert.Equal(1, report.ItemsProcessed);
        Assert.Equal(1, report.ItemsSkipped);
        Assert.Equal(1, lineCount);
    }

    [Fact]
    public async Task DedupOn_UniqueDocumentsAllExported()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "First unique document content alpha.");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "Second unique document content beta.");
        File.WriteAllText(Path.Combine(_dir, "c.txt"), "Third unique document content gamma.");
        var outPath = Path.Combine(_outDir, "docs.jsonl");

        var (report, lineCount) = await RunPipeline(_dir, outPath,
            new PipelineOptions(DeduplicateByContentHash: true));

        Assert.Equal(3, report.ItemsProcessed);
        Assert.Equal(0, report.ItemsSkipped);
        Assert.Equal(3, lineCount);
    }
}
