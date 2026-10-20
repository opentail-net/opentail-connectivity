namespace OpenTail.Connectivity.Tests;

public sealed class FolderExtraExtensionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public FolderExtraExtensionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task LogFile_NotDiscoveredByDefault()
    {
        // MM.2: .log is not in SupportedExtensions
        File.WriteAllText(Path.Combine(_dir, "app.log"), "Log content that would be useful to ingest for analysis.");

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Empty(items);
    }

    [Fact]
    public async Task LogFile_DiscoveredWhenAddedToExtraExtensions()
    {
        // MM.2: .log file discovered when added to ExtraExtensions
        File.WriteAllText(Path.Combine(_dir, "app.log"), "Log content that would be useful to ingest for analysis.");
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "Markdown file that is always discovered.");

        var connector = new FolderSourceConnector(_dir, "t",
            connectorOptions: new FolderConnectorOptions(ExtraExtensions: [".log"]));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.DisplayName == "app.log");
        Assert.Contains(items, i => i.DisplayName == "readme.md");
    }

    [Fact]
    public async Task DryRun_EmptyOutput_NoException()
    {
        // KK.3: discovering without --output set does not throw
        File.WriteAllText(Path.Combine(_dir, "doc.txt"), "Content sufficient to pass validation.");

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Single(items);
        // KK: no output directory needed for discovery-only operations
    }
}
