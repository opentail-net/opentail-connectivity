namespace OpenTail.Connectivity.Tests;

public sealed class FolderPatternTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FolderPatternTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<List<SourceItem>> Discover(FolderConnectorOptions? opts, params string[] files)
    {
        foreach (var f in files)
        {
            var fullPath = Path.Combine(_dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, "content");
        }
        var connector = new FolderSourceConnector(_dir, "t", connectorOptions: opts);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);
        return items;
    }

    [Fact]
    public async Task NoPatterns_AllFilesDiscovered()
    {
        var items = await Discover(null, "a.txt", "b.md", "c.cs");
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task IncludePattern_OnlyMdFiles()
    {
        var opts = new FolderConnectorOptions(IncludePatterns: ["**/*.md"]);
        var items = await Discover(opts, "readme.md", "notes.txt", "code.cs");
        Assert.Single(items);
        Assert.Equal("readme.md", items[0].DisplayName);
    }

    [Fact]
    public async Task ExcludePattern_SkipsMatchingFolder()
    {
        var opts = new FolderConnectorOptions(ExcludePatterns: ["node_modules/**"]);
        var items = await Discover(opts,
            "src/index.ts",
            "node_modules/lib.js",
            "node_modules/deep/util.js",
            "README.md");
        // node_modules files should be skipped
        Assert.All(items, i => Assert.DoesNotContain("node_modules", i.Id));
        Assert.Equal(2, items.Count); // src/index.ts + README.md
    }

    [Fact]
    public async Task BothIncludeAndExclude_Applied()
    {
        var opts = new FolderConnectorOptions(
            IncludePatterns: ["**/*.cs"],
            ExcludePatterns: ["**/*.generated.cs"]);
        var items = await Discover(opts, "Foo.cs", "Bar.generated.cs", "Baz.cs", "notes.txt");
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.EndsWith(".cs", i.DisplayName));
        Assert.DoesNotContain(items, i => i.DisplayName.Contains("generated"));
    }

    [Fact]
    public async Task PatternMatchIsCaseInsensitive()
    {
        var opts = new FolderConnectorOptions(IncludePatterns: ["**/*.MD"]);
        var items = await Discover(opts, "README.md", "doc.txt");
        Assert.Single(items);
    }
}
