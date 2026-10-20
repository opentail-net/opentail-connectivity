namespace OpenTail.Connectivity.Tests;

public sealed class FolderNoisyDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FolderNoisyDirectoryTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void WriteFile(string relativePath, string content = "Sample document content that is long enough to pass validation checks.")
    {
        // The cases are written with '\'; on Linux that is an ordinary file-name character, so map it to the real separator.
        var full = Path.Combine(_root, relativePath.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private async Task<List<SourceItem>> DiscoverAsync(FolderConnectorOptions? opts = null)
    {
        var connector = new FolderSourceConnector(_root, "t", connectorOptions: opts);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task BinObj_SkippedByDefault()
    {
        // HH.2: bin/ and obj/ are in the noisy-directory list
        WriteFile(@"src\main.cs");
        WriteFile(@"bin\main.cs");
        WriteFile(@"obj\temp.cs");

        var items = await DiscoverAsync();

        Assert.All(items, i => Assert.DoesNotContain("bin", i.Id));
        Assert.All(items, i => Assert.DoesNotContain("obj", i.Id));
        Assert.Single(items);
    }

    [Fact]
    public async Task BinObj_IncludedWhenOptIn()
    {
        // HH.1: IncludeNoisyDirectories = true includes bin/ and obj/
        WriteFile(@"src\main.cs");
        WriteFile(@"bin\main.cs");
        WriteFile(@"obj\temp.cs");

        var items = await DiscoverAsync(new FolderConnectorOptions(IncludeNoisyDirectories: true));

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task HiddenDirectory_SkippedByDefault()
    {
        // HH.3: directories starting with '.' are skipped (e.g. .git, .vs)
        WriteFile(@"docs\readme.md");
        WriteFile(@".hidden\secret.md");
        WriteFile(@".git\config.txt");

        var items = await DiscoverAsync();

        var single = Assert.Single(items);
        Assert.Equal("readme.md", single.DisplayName);
    }
}
