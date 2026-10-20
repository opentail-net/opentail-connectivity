namespace OpenTail.Connectivity.Tests;

public sealed class FolderSyncStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _stateFile;
    private readonly List<string> _cleanupFiles = [];

    public FolderSyncStateTests()
    {
        Directory.CreateDirectory(_dir);
        _stateFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        _cleanupFiles.Add(_stateFile);
    }

    public void Dispose()
    {
        Directory.Delete(_dir, recursive: true);
        foreach (var f in _cleanupFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task NoState_RunsAllFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "content a");
        File.WriteAllText(Path.Combine(_dir, "b.txt"), "content b");

        var connector = new FolderSourceConnector(_dir, "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task SecondRun_SkipsUnchangedFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "content a");

        // First run — save state
        var store = new FolderSyncStateStore(_stateFile);
        var c1 = new FolderSourceConnector(_dir, "tenant", store);
        var run1 = new List<SourceItem>();
        await foreach (var item in c1.DiscoverAsync()) run1.Add(item);
        await c1.SaveStateAsync();

        Assert.Single(run1);

        // Second run — file unchanged, should yield nothing
        var c2 = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        var run2 = new List<SourceItem>();
        await foreach (var item in c2.DiscoverAsync()) run2.Add(item);

        Assert.Empty(run2);
    }

    [Fact]
    public async Task ReusedConnector_ReloadsStateBetweenRuns()
    {
        var firstPath = Path.Combine(_dir, "first.txt");
        var secondPath = Path.Combine(_dir, "second.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");
        var connector = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));

        var firstRun = await connector.DiscoverAsync().ToListAsync();
        await connector.SaveStateAsync();
        var secondRun = await connector.DiscoverAsync().ToListAsync();
        File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddMinutes(1));
        var thirdRun = await connector.DiscoverAsync().ToListAsync();

        Assert.Equal(2, firstRun.Count);
        Assert.Empty(secondRun);
        Assert.Equal(firstPath, Assert.Single(thirdRun).Id);
    }

    [Fact]
    public async Task ChangedFile_IncludedOnSecondRun()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "original");

        // Save state with artificially old timestamp so file looks changed on next run
        var store = new FolderSyncStateStore(_stateFile);
        var oldState = new FolderSyncState(
            FolderPath: _dir,
            FileTimestamps: new Dictionary<string, DateTimeOffset> { [path] = DateTimeOffset.UnixEpoch },
            UpdatedAt: DateTimeOffset.UtcNow);
        await store.SaveAsync(oldState);

        var connector = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
        Assert.Equal(path, items[0].Id);
    }

    [Fact]
    public async Task NewFileAfterState_IsIncluded()
    {
        var existingPath = Path.Combine(_dir, "existing.txt");
        File.WriteAllText(existingPath, "existing");

        // First run — saves state for existing file only
        var store = new FolderSyncStateStore(_stateFile);
        var c1 = new FolderSourceConnector(_dir, "tenant", store);
        await foreach (var _ in c1.DiscoverAsync()) { }
        await c1.SaveStateAsync();

        // Add new file
        var newPath = Path.Combine(_dir, "new.txt");
        File.WriteAllText(newPath, "new content");

        // Second run — should yield only the new file
        var c2 = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        var items = new List<SourceItem>();
        await foreach (var item in c2.DiscoverAsync()) items.Add(item);

        Assert.Single(items);
        Assert.Equal(newPath, items[0].Id);
    }

    [Fact]
    public async Task StateRoundTrip_SerialiseDeserialise()
    {
        var store = new FolderSyncStateStore(_stateFile);
        var original = new FolderSyncState(
            FolderPath: _dir,
            FileTimestamps: new Dictionary<string, DateTimeOffset>
            {
                [@"C:\test\file.txt"] = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
            },
            UpdatedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        await store.SaveAsync(original);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(_dir, loaded.FolderPath);
        Assert.Single(loaded.FileTimestamps);
        Assert.Equal(
            new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero),
            loaded.FileTimestamps[@"C:\test\file.txt"]);
    }

    [Fact]
    public async Task MissingStateFile_StartsFresh()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");

        // State file does not exist — connector should run without error and yield all files
        var connector = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        var items = new List<SourceItem>();
        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var item in connector.DiscoverAsync()) items.Add(item);
        });

        Assert.Null(ex);
        Assert.Single(items);
    }

    [Fact]
    public async Task SaveStateAsync_NoOp_WhenNoStoreConfigured()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "hello");

        var connector = new FolderSourceConnector(_dir, "tenant");
        await foreach (var _ in connector.DiscoverAsync()) { }

        // Should not throw even with no stateStore
        var ex = await Record.ExceptionAsync(() => connector.SaveStateAsync());
        Assert.Null(ex);
    }
}
