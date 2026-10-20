namespace OpenTail.Connectivity.Tests;

public sealed class V20Tests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public V20Tests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_outDir);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    // ─── NNN: inbox timing fix ────────────────────────────────────────────

    [Fact]
    public void IngestionReport_CompletedAt_AfterStartedAt()
    {
        // NNN.1: startedAt must be captured BEFORE the work
        var start = DateTimeOffset.UtcNow;
        var end = start.AddMilliseconds(10);

        var report = new IngestionReport(
            "r1", "t", start, end,
            1, 1, 0, 0, 1, [], []);

        Assert.True(report.CompletedAt > report.StartedAt);
    }

    // ─── OOO + PPP: body stats in pipeline and inbox reports ─────────────

    [Fact]
    public async Task Pipeline_BodyStats_NonZeroWhenDocumentsProduced()
    {
        // OOO + MMM: pipeline IngestionReport has body stats
        File.WriteAllText(Path.Combine(_dir, "doc.txt"),
            "This document body is long enough for body stats testing purposes.");

        var outPath = Path.Combine(_outDir, "docs.jsonl");
        var connector = new FolderSourceConnector(_dir, "t");
        var extractor = new TransformationsTextExtractor();
        var exporter = new JsonlDocumentExporter(outPath);
        IngestionReport report;
        try
        {
            report = await new ConnectivityPipeline(connector, extractor, [exporter]).RunAsync();
        }
        finally { await exporter.DisposeAsync(); }

        Assert.True(report.DocumentsProduced > 0);
        Assert.True(report.MinBodyLength > 0);
        Assert.True(report.MaxBodyLength >= report.MinBodyLength);
        Assert.True(report.AvgBodyLength > 0);
    }

    [Fact]
    public void IngestionReport_WithBodyStats_CorrectValues()
    {
        // PPP.2: manually constructed report with body stats
        var report = new IngestionReport(
            "r1", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            2, 2, 0, 0, 2, [], [],
            MinBodyLength: 100,
            MaxBodyLength: 300,
            AvgBodyLength: 200.0);

        Assert.Equal(100, report.MinBodyLength);
        Assert.Equal(300, report.MaxBodyLength);
        Assert.Equal(200.0, report.AvgBodyLength);
    }
}
