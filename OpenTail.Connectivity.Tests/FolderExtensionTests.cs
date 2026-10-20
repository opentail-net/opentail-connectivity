namespace OpenTail.Connectivity.Tests;

public sealed class FolderExtensionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FolderExtensionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<List<SourceItem>> Discover(params string[] fileNames)
    {
        foreach (var name in fileNames)
            File.WriteAllText(Path.Combine(_dir, name), "content");
        var connector = new FolderSourceConnector(_dir, "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);
        return items;
    }

    [Fact]
    public async Task Discovers_PptxFile()
    {
        var items = await Discover("slide.pptx");
        Assert.Single(items);
        Assert.Equal("pptx", items[0].Extension);
    }

    [Fact]
    public async Task Discovers_PythonFile()
    {
        var items = await Discover("script.py");
        Assert.Single(items);
        Assert.Equal("py", items[0].Extension);
    }

    [Fact]
    public async Task Discovers_YamlFile()
    {
        var items = await Discover("config.yaml", "config.yml");
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task SkipsUnknownExtension()
    {
        var items = await Discover("binary.xyz", "archive.7z", "compiled.dll");
        Assert.Empty(items);
    }
}
