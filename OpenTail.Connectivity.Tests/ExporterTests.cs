namespace OpenTail.Connectivity.Tests;

public sealed class ExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public ExporterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static NormalisedDocument MakeDoc(string id = "doc1", string body = "Some body text here") =>
        new(Id: id, Title: "Test Doc", Body: body, ContentHash: "abc123",
            SourceRef: "/path/to/file.txt", SourceType: "file", TenantContext: "test",
            SourceModified: DateTimeOffset.UtcNow, IngestedAt: DateTimeOffset.UtcNow,
            Metadata: new Dictionary<string, string> { ["source.extension"] = "txt" });

    [Fact]
    public async Task JsonlDocumentExporter_writes_one_line_per_document()
    {
        var path = Path.Combine(_dir, "docs.jsonl");
        await using (var exporter = new JsonlDocumentExporter(path))
        {
            await exporter.ExportAsync(MakeDoc("d1"));
            await exporter.ExportAsync(MakeDoc("d2"));
            await exporter.ExportAsync(MakeDoc("d3"));
            await exporter.FlushAsync();
        }

        var lines = await File.ReadAllLinesAsync(path);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(3, nonEmpty.Count);
    }

    [Fact]
    public async Task JsonlDocumentExporter_each_line_is_valid_json()
    {
        var path = Path.Combine(_dir, "valid.jsonl");
        await using (var exporter = new JsonlDocumentExporter(path))
        {
            await exporter.ExportAsync(MakeDoc());
            await exporter.FlushAsync();
        }

        var line = (await File.ReadAllLinesAsync(path))[0];
        var doc = JsonSerializer.Deserialize<JsonElement>(line);
        Assert.Equal("doc1", doc.GetProperty("Id").GetString());
        Assert.Equal("Test Doc", doc.GetProperty("Title").GetString());
    }

    [Fact]
    public async Task JsonlDocumentExporter_creates_parent_directory()
    {
        var nested = Path.Combine(_dir, "sub", "docs.jsonl");
        await using (var exporter = new JsonlDocumentExporter(nested))
        {
            await exporter.ExportAsync(MakeDoc());
            await exporter.FlushAsync();
        }
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task JsonReportExporter_writes_valid_indented_json()
    {
        var path = Path.Combine(_dir, "report.json");
        var report = new IngestionReport(
            RunId: "run1", TenantContext: "test",
            StartedAt: DateTimeOffset.UtcNow, CompletedAt: DateTimeOffset.UtcNow,
            ItemsDiscovered: 5, ItemsProcessed: 4, ItemsSkipped: 1, ItemsFailed: 0,
            DocumentsProduced: 4,
            ValidationIssues: [],
            Errors: []);

        await new JsonReportExporter(path).ExportAsync(report);

        var json = await File.ReadAllTextAsync(path);
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("run1", element.GetProperty("RunId").GetString());
        Assert.Equal(5, element.GetProperty("ItemsDiscovered").GetInt32());
        Assert.Contains("\n", json); // indented
    }
}
