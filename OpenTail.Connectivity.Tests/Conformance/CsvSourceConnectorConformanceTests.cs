using OpenTail.Connectivity.Csv;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class CsvSourceConnectorConformanceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "otail-conf-" + Guid.NewGuid().ToString("N") + ".csv");
    private readonly string _emptyFile = Path.Combine(Path.GetTempPath(), "otail-conf-empty-" + Guid.NewGuid().ToString("N") + ".csv");
    private readonly CsvConnectorConfig _config = new(
        IdColumn: "id",
        TitleColumn: "title",
        BodyColumns: ["body"],
        ModifiedColumn: "modified");

    public CsvSourceConnectorConformanceTests()
    {
        File.WriteAllText(_file, """
            id,title,body,modified
            row1,Title 1,Body text for row one which is long enough,2026-01-01T00:00:00Z
            row2,Title 2,Body text for row two which is long enough,2026-01-02T00:00:00Z
            """);
        File.WriteAllText(_emptyFile, """
            id,title,body,modified
            """);
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
        try { File.Delete(_emptyFile); } catch (IOException) { }
    }

    private sealed class Fixture(CsvConnectorConfig config, string file, string emptyFile) : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];
        public ISourceConnector Create() => new CsvSourceConnector(config, file, "tenant-csv");
        public ISourceConnector CreateEmpty() => new CsvSourceConnector(config, emptyFile, "tenant-csv");
        public Task RemoveOneItemAsync(string sourceRef)
        {
            File.WriteAllText(file, """
                id,title,body,modified
                row2,Title 2,Body text for row two which is long enough,2026-01-02T00:00:00Z
                """);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CsvSourceConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new Fixture(_config, _file, _emptyFile));
    }
}
