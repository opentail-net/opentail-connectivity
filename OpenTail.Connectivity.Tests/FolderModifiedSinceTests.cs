namespace OpenTail.Connectivity.Tests;

public sealed class FolderModifiedSinceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public FolderModifiedSinceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ModifiedSince_SkipsOlderFiles()
    {
        // II.2: files modified before the cutoff are excluded
        var oldFile = Path.Combine(_dir, "old.txt");
        var newFile = Path.Combine(_dir, "new.txt");
        File.WriteAllText(oldFile, "Old file content that is sufficiently long for validation purposes.");
        File.WriteAllText(newFile, "New file content that is sufficiently long for validation purposes.");

        // Set old file's last-write time well in the past
        File.SetLastWriteTimeUtc(oldFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newFile, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var cutoff = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var connector = new FolderSourceConnector(_dir, "t",
            connectorOptions: new FolderConnectorOptions(ModifiedSince: cutoff));

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var single = Assert.Single(items);
        Assert.Equal("new.txt", single.DisplayName);
    }

    [Fact]
    public async Task ModifiedSince_Null_IncludesAllFiles()
    {
        // II.2: no cutoff → all files included
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(_dir, $"doc{i}.txt");
            File.WriteAllText(path, $"Document {i} with sufficient content for validation checks.");
            File.SetLastWriteTimeUtc(path, new DateTime(2020 + i, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(3, items.Count);
    }
}
