using System.Text.Json;
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Export;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Pipeline;

namespace OpenTail.Connectivity.Tests;

public sealed class JsonlDeletionExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-del-exp-" + Guid.NewGuid().ToString("N"));

    public JsonlDeletionExporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Two_deletions_give_two_parseable_lines_in_order()
    {
        var path = Path.Combine(_dir, "deletions.jsonl");
        var d1 = new DocumentDeletion("id1", "ref1", "file", "tenant1", DeletionReason.Deleted, DateTimeOffset.UtcNow);
        var d2 = new DocumentDeletion("id2", "ref2", "file", "tenant1", DeletionReason.OutOfScope, DateTimeOffset.UtcNow.AddMinutes(1));

        await using (var exporter = new JsonlDeletionExporter(path))
        {
            await exporter.DeleteAsync(d1);
            await exporter.DeleteAsync(d2);
            await exporter.FlushAsync();
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Equal(2, lines.Length);

        using var doc1 = JsonDocument.Parse(lines[0]);
        Assert.Equal("id1", doc1.RootElement.GetProperty("id").GetString());
        Assert.Equal("ref1", doc1.RootElement.GetProperty("sourceRef").GetString());
        Assert.Equal("file", doc1.RootElement.GetProperty("sourceType").GetString());
        Assert.Equal("tenant1", doc1.RootElement.GetProperty("tenantContext").GetString());
        Assert.Equal("deleted", doc1.RootElement.GetProperty("reason").GetString());
        Assert.True(doc1.RootElement.TryGetProperty("detectedAt", out var detectedAt1));
        Assert.False(string.IsNullOrWhiteSpace(detectedAt1.GetString()));

        using var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal("id2", doc2.RootElement.GetProperty("id").GetString());
        Assert.Equal("ref2", doc2.RootElement.GetProperty("sourceRef").GetString());
        Assert.Equal("outofscope", doc2.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task FlushAsync_called_twice_is_safe()
    {
        var path = Path.Combine(_dir, "flush.jsonl");
        var d = new DocumentDeletion("id1", "ref1", "file", "tenant1", DeletionReason.Deleted, DateTimeOffset.UtcNow);

        await using (var exporter = new JsonlDeletionExporter(path))
        {
            await exporter.DeleteAsync(d);
            await exporter.FlushAsync();
            await exporter.FlushAsync();
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
    }

    [Fact]
    public async Task Pipeline_run_with_doc_exporter_and_deletion_exporter_writes_to_separate_files()
    {
        var docFile = Path.Combine(_dir, "docs.jsonl");
        var delFile = Path.Combine(_dir, "dels.jsonl");
        var stateFile = Path.Combine(_dir, "state.json");

        var docExporter = new JsonlDocumentExporter(docFile);
        var delExporter = new JsonlDeletionExporter(delFile);

        var connector = new ScriptedIncremental(["U:a", "D:b", "C:1"]);
        var pipeline = new ConnectivityPipeline(
            connector,
            new TextExtractorStub(),
            [docExporter, delExporter],
            new PipelineOptions(SyncStore: new FileSyncCursorStore(stateFile)));

        var report = await pipeline.RunAsync();

        await docExporter.DisposeAsync();
        await delExporter.DisposeAsync();

        Assert.Equal(1, report.ItemsProcessed);
        Assert.Equal(1, report.ItemsDeleted);

        var docLines = await File.ReadAllLinesAsync(docFile);
        Assert.Single(docLines);
        using var parsedDoc = JsonDocument.Parse(docLines[0]);
        Assert.Equal("a", parsedDoc.RootElement.GetProperty("SourceRef").GetString());

        var delLines = await File.ReadAllLinesAsync(delFile);
        Assert.Single(delLines);
        using var parsedDel = JsonDocument.Parse(delLines[0]);
        Assert.Equal("b", parsedDel.RootElement.GetProperty("sourceRef").GetString());
        Assert.Equal("deleted", parsedDel.RootElement.GetProperty("reason").GetString());
    }

    private sealed class ScriptedIncremental(string[] script) : IIncrementalSourceConnector
    {
        public string SyncScope => "test:jsonl:scope";
        public bool ReportsDeletions => true;

        public async IAsyncEnumerable<SourceChange> GetChangesAsync(string? cursor, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var step in script)
            {
                await Task.Yield();
                yield return step switch
                {
                    _ when step.StartsWith("C:") => new SourceChange.Checkpoint(step[2..]),
                    _ when step.StartsWith("D:") => new SourceChange.Delete(step[2..], DeletionReason.Deleted),
                    _ => new SourceChange.Upsert(new SourceItem(step[2..], step[2..] + ".txt", "txt", 10, DateTimeOffset.UnixEpoch, "tenant")),
                };
            }
        }

        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var c in GetChangesAsync(null, ct))
                if (c is SourceChange.Upsert u) yield return u.Item;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken ct = default) =>
            Task.FromResult(System.Text.Encoding.UTF8.GetBytes("Content for " + item.Id + new string('.', 50)));
    }

    private sealed class TextExtractorStub : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) =>
            new(true, System.Text.Encoding.UTF8.GetString(content));
    }
}
