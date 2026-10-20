namespace OpenTail.Connectivity.Tests;

public sealed class FolderSyncStateCleanTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    public FolderSyncStateCleanTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task CleanStaleEntries_RemovesDeletedFilePaths()
    {
        // BBB.1: stale entries (deleted files) are removed
        var file = Path.Combine(_dir, "doc.txt");
        File.WriteAllText(file, "Content.");

        var stateFile = Path.Combine(_dir, "state.json");
        var store = new FolderSyncStateStore(stateFile);

        // Build state with one real + one stale entry
        var staleEntry = Path.Combine(_dir, "deleted.txt");
        var state = new FolderSyncState(
            FolderPath: _dir,
            FileTimestamps: new Dictionary<string, DateTimeOffset>
            {
                [file] = DateTimeOffset.UtcNow,
                [staleEntry] = DateTimeOffset.UtcNow,  // stale — file doesn't exist
            },
            UpdatedAt: DateTimeOffset.UtcNow);
        await store.SaveAsync(state);

        var removed = await store.CleanStaleEntriesAsync();

        Assert.Equal(1, removed);
        var reloaded = store.Load();
        Assert.NotNull(reloaded);
        Assert.True(reloaded.FileTimestamps.ContainsKey(file));
        Assert.False(reloaded.FileTimestamps.ContainsKey(staleEntry));
    }

    [Fact]
    public async Task CleanStaleEntries_NoStaleEntries_ReturnsZero()
    {
        // BBB.1: no stale entries → returns 0 and state is unchanged
        var file = Path.Combine(_dir, "doc.txt");
        File.WriteAllText(file, "Content.");

        var stateFile = Path.Combine(_dir, "state.json");
        var store = new FolderSyncStateStore(stateFile);
        var state = new FolderSyncState(
            FolderPath: _dir,
            FileTimestamps: new Dictionary<string, DateTimeOffset> { [file] = DateTimeOffset.UtcNow },
            UpdatedAt: DateTimeOffset.UtcNow);
        await store.SaveAsync(state);

        var removed = await store.CleanStaleEntriesAsync();

        Assert.Equal(0, removed);
        var reloaded = store.Load();
        Assert.NotNull(reloaded);
        Assert.Single(reloaded.FileTimestamps);
    }
}
