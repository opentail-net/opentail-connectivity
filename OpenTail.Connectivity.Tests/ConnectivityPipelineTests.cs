using System.Runtime.CompilerServices;

namespace OpenTail.Connectivity.Tests;

public sealed class ConnectivityPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _out = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + "_out");

    public ConnectivityPipelineTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_out);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        Directory.Delete(_out, recursive: true);
    }

    [Fact]
    public async Task Pipeline_produces_report_with_correct_counts()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), new string('x', 100));
        File.WriteAllText(Path.Combine(_dir, "b.txt"), new string('y', 100));

        var source = new FolderSourceConnector(_dir, "test-tenant");
        var extractor = new StubTextExtractor("Extracted content that is long enough to pass validation checks.");

        var docsPath = Path.Combine(_out, "docs.jsonl");
        await using var exporter = new JsonlDocumentExporter(docsPath);
        var pipeline = new ConnectivityPipeline(source, extractor, [exporter]);

        var report = await pipeline.RunAsync();

        Assert.Equal(2, report.ItemsDiscovered);
        Assert.Equal(2, report.ItemsProcessed);
        Assert.Equal(0, report.ItemsFailed);
        Assert.Equal(2, report.DocumentsProduced);
        Assert.Equal("test-tenant", report.TenantContext);
    }

    [Fact]
    public async Task Pipeline_skips_empty_extraction()
    {
        File.WriteAllText(Path.Combine(_dir, "empty.txt"), "content");
        var source = new FolderSourceConnector(_dir, "t");
        var extractor = new StubTextExtractor("");  // empty extraction

        await using var exporter = new JsonlDocumentExporter(Path.Combine(_out, "d.jsonl"));
        var pipeline = new ConnectivityPipeline(source, extractor, [exporter]);
        var report = await pipeline.RunAsync();

        Assert.Equal(1, report.ItemsSkipped);
        Assert.Equal(0, report.ItemsProcessed);
    }

    [Fact]
    public async Task Pipeline_handles_extractor_failure_gracefully()
    {
        File.WriteAllText(Path.Combine(_dir, "file.txt"), "content");
        var source = new FolderSourceConnector(_dir, "t");
        var extractor = new StubTextExtractor(null); // signals failure

        await using var exporter = new JsonlDocumentExporter(Path.Combine(_out, "d.jsonl"));
        var pipeline = new ConnectivityPipeline(source, extractor, [exporter]);
        var report = await pipeline.RunAsync();

        Assert.Equal(1, report.ItemsFailed);
        Assert.Equal(0, report.ItemsProcessed);
        Assert.Single(report.Errors);
    }

    [Fact]
    public async Task Pipeline_Deduplication_UsesBoundedRollingHashWindow()
    {
        var repeated = new string('a', 80);
        var different = new string('b', 80);
        var source = new InMemorySource(
            ("a1", repeated),
            ("b1", different),
            ("a2", repeated));
        var docsPath = Path.Combine(_out, "bounded-dedup.jsonl");
        await using var exporter = new JsonlDocumentExporter(docsPath);
        var pipeline = new ConnectivityPipeline(
            source,
            new BytesTextExtractor(),
            [exporter],
            new PipelineOptions(DeduplicateByContentHash: true, MaxDedupHashes: 1));

        var report = await pipeline.RunAsync();

        Assert.Equal(3, report.ItemsDiscovered);
        Assert.Equal(3, report.ItemsProcessed);
    }

    [Fact]
    public async Task Dedup_is_per_tenant()
    {
        const string body = "The same body text, long enough for the document validator to accept it.";
        var source = new InMemorySource(("a", body, "alpha"), ("b", body, "bravo"), ("c", body, "alpha"));
        var exporter = new RecordingExporter();

        var report = await new ConnectivityPipeline(source, new BytesTextExtractor(), [exporter],
            new PipelineOptions(DeduplicateByContentHash: true)).RunAsync();

        Assert.Equal(["alpha", "bravo"], exporter.Tenants);
        Assert.Equal(1, report.ItemsSkipped);
    }

    [Fact]
    public async Task A_failed_export_does_not_hide_a_later_copy_of_the_same_body()
    {
        const string body = "The same body text, long enough for the document validator to accept it.";
        var source = new InMemorySource(("a", body, "tenant"), ("b", body, "tenant"), ("c", body, "tenant"));
        var first = new RecordingExporter();
        var flaky = new RecordingExporter { FailOnce = true };

        var report = await new ConnectivityPipeline(source, new BytesTextExtractor(), [first, flaky],
            new PipelineOptions(DeduplicateByContentHash: true)).RunAsync();

        Assert.Equal(1, report.ItemsFailed);
        Assert.Single(flaky.Tenants); // "b" reached the exporter that failed on "a"
        Assert.Equal(1, report.ItemsSkipped); // "c" is a duplicate of the exported "b"
    }

    private sealed class RecordingExporter : IConnectivityExporter
    {
        public bool FailOnce { get; set; }
        public List<string> Tenants { get; } = [];

        public Task ExportAsync(NormalisedDocument doc, CancellationToken cancellationToken = default)
        {
            if (FailOnce)
            {
                FailOnce = false;
                throw new IOException("disk full");
            }
            Tenants.Add(doc.TenantContext);
            return Task.CompletedTask;
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Null text = extraction failure; empty string = empty content; otherwise success.
    private sealed class StubTextExtractor : ITextExtractor
    {
        private readonly string? _text;
        public StubTextExtractor(string? text) => _text = text;

        public TextExtractionResult Extract(string fileName, byte[] content) =>
            _text is null
                ? new TextExtractionResult(false, string.Empty, "Stub failure")
                : new TextExtractionResult(true, _text);
    }

    private sealed class BytesTextExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class InMemorySource(params (string Id, string Body, string Tenant)[] items) : ISourceConnector
    {
        public InMemorySource(params (string Id, string Body)[] items) : this(items.Select(i => (i.Id, i.Body, "tenant")).ToArray())
        {
        }

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SourceItem(
                    item.Id,
                    item.Id,
                    "txt",
                    item.Body.Length,
                    DateTimeOffset.UtcNow,
                    item.Tenant);
            }

            await Task.CompletedTask;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            var body = items.First(i => i.Id == item.Id).Body;
            return Task.FromResult(Encoding.UTF8.GetBytes(body));
        }
    }
}
