namespace OpenTail.Connectivity.Tests;

public sealed class AppendModeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public AppendModeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static NormalisedDocument MakeDoc(string id, string body) =>
        new(id, $"Title {id}", body, id, $"ref:{id}", "file", "t",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new Dictionary<string, string>());

    [Fact]
    public async Task AppendMode_AddsToExistingFile()
    {
        // NN.1: append: true adds documents to existing content
        var path = Path.Combine(_dir, "docs.jsonl");

        await using (var e1 = new JsonlDocumentExporter(path, append: false))
            await e1.ExportAsync(MakeDoc("1", "First document body."));

        await using (var e2 = new JsonlDocumentExporter(path, append: true))
            await e2.ExportAsync(MakeDoc("2", "Second document body."));

        var lines = await File.ReadAllLinesAsync(path);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Equal(2, nonEmpty.Length);
    }

    [Fact]
    public async Task DefaultMode_OverwritesExistingFile()
    {
        // NN.1: append: false (default) overwrites existing content
        var path = Path.Combine(_dir, "docs.jsonl");

        await using (var e1 = new JsonlDocumentExporter(path, append: false))
            await e1.ExportAsync(MakeDoc("1", "First document body."));

        await using (var e2 = new JsonlDocumentExporter(path, append: false))
            await e2.ExportAsync(MakeDoc("2", "Second document body."));

        var lines = await File.ReadAllLinesAsync(path);
        var nonEmpty = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Single(nonEmpty);
    }
}
