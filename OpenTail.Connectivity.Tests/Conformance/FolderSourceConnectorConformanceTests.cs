using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Connectors;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class FolderSourceConnectorConformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-conf-folder-" + Guid.NewGuid().ToString("N"));
    private readonly string _emptyDir = Path.Combine(Path.GetTempPath(), "otail-conf-folder-empty-" + Guid.NewGuid().ToString("N"));

    public FolderSourceConnectorConformanceTests()
    {
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(_emptyDir);
        File.WriteAllText(Path.Combine(_dir, "file1.txt"), "Content of file one which is long enough to be real.");
        File.WriteAllText(Path.Combine(_dir, "file2.txt"), "Content of file two which is long enough to be real.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_emptyDir, recursive: true); } catch (IOException) { }
    }

    private sealed class Fixture(string dir, string emptyDir) : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];
        public ISourceConnector Create() => new FolderSourceConnector(dir, "tenant-a");
        public ISourceConnector CreateEmpty() => new FolderSourceConnector(emptyDir, "tenant-a");
        public Task RemoveOneItemAsync(string sourceRef)
        {
            if (File.Exists(sourceRef)) File.Delete(sourceRef);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FolderSourceConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new Fixture(_dir, _emptyDir));
    }
}
