namespace OpenTail.Connectivity.Tests;

public sealed class PipelineFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public PipelineFormatTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    [Fact]
    public async Task Pipeline_MarkdownFormat_ProducesMarkdownOutput()
    {
        // WW: pipeline with markdown format writes markdown directory
        File.WriteAllText(Path.Combine(_dir, "doc.txt"),
            "This is sufficient content for the markdown export pipeline format test.");

        var markdownDir = Path.Combine(_outDir, "01-files-markdown");
        var connector = new FolderSourceConnector(_dir, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new MarkdownDocumentExporter(markdownDir);

        try
        {
            await new ConnectivityPipeline(connector, extractor, [exporter]).RunAsync();
        }
        finally { await exporter.DisposeAsync(); }

        Assert.True(Directory.Exists(markdownDir));
        var mdFiles = Directory.GetFiles(markdownDir, "*.md", SearchOption.TopDirectoryOnly);
        Assert.NotEmpty(mdFiles);
    }
}
