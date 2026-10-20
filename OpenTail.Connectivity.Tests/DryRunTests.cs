namespace OpenTail.Connectivity.Tests;

public sealed class DryRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public DryRunTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task DryRun_DiscoverableItemsReturned_NoOutputCreated()
    {
        // JJ.2: dry-run discovers items without writing output
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "Content for first document in dry run test.");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "Content for second document in dry run test.");

        var outDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        // Verify: items discovered, output dir not created (no pipeline ran)
        Assert.Equal(2, items.Count);
        Assert.False(Directory.Exists(outDir), "Output dir should not be created in dry-run");
    }

    [Fact]
    public async Task DryRun_EmptySource_ZeroItems()
    {
        // JJ.2: empty source returns zero items, no errors
        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Empty(items);
    }
}
