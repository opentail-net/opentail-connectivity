namespace OpenTail.Connectivity.Tests;

public sealed class FolderSizeLimitTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FolderSizeLimitTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] MakeContent(int byteCount) => new byte[byteCount];

    [Fact]
    public async Task FileOverLimit_Skipped()
    {
        File.WriteAllBytes(Path.Combine(_dir, "big.txt"), MakeContent(2000));
        File.WriteAllBytes(Path.Combine(_dir, "small.txt"), MakeContent(500));

        var opts = new FolderConnectorOptions(MaxFileSizeBytes: 1000);
        var connector = new FolderSourceConnector(_dir, "t", connectorOptions: opts);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
        Assert.Equal("small.txt", items[0].DisplayName);
    }

    [Fact]
    public async Task FileAtExactLimit_Included()
    {
        File.WriteAllBytes(Path.Combine(_dir, "exact.txt"), MakeContent(1000));

        var opts = new FolderConnectorOptions(MaxFileSizeBytes: 1000);
        var connector = new FolderSourceConnector(_dir, "t", connectorOptions: opts);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
    }

    [Fact]
    public async Task NoLimit_AllSizesIncluded()
    {
        File.WriteAllBytes(Path.Combine(_dir, "a.txt"), MakeContent(100));
        File.WriteAllBytes(Path.Combine(_dir, "b.txt"), MakeContent(100_000));

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(2, items.Count);
    }
}
