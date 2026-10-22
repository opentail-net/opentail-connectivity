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

    private async Task<List<string>> Run(FolderConnectorOptions? options = null, bool save = true)
    {
        var connector = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile), options);
        var names = new List<string>();
        await foreach (var item in connector.DiscoverAsync()) names.Add(item.DisplayName);
        if (save) await connector.SaveStateAsync();
        LastDeleted = connector.DeletedSinceLastScan.Select(Path.GetFileName).ToList()!;
        return names;
    }

    private List<string> LastDeleted { get; set; } = [];

    [Fact]
    public async Task A_size_change_with_the_old_timestamp_is_detected()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "content a");
        var stamp = File.GetLastWriteTimeUtc(path);
        Assert.Single(await Run());

        File.WriteAllText(path, "content a, now longer");
        File.SetLastWriteTimeUtc(path, stamp);

        Assert.Equal(["a.txt"], await Run());
        Assert.Empty(await Run());
    }

    [Fact]
    public async Task A_file_older_than_the_cutoff_is_found_once_the_cutoff_is_removed()
    {
        var path = Path.Combine(_dir, "old.txt");
        File.WriteAllText(path, "old content");
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var cutoff = new FolderConnectorOptions { ModifiedSince = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) };

        Assert.Empty(await Run(cutoff));
        Assert.Equal(["old.txt"], await Run());
    }

    [Fact]
    public async Task Raising_the_cutoff_past_a_known_file_reports_it_out_of_scope()
    {
        var path = Path.Combine(_dir, "a.txt");
        File.WriteAllText(path, "content a");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Single(await Run());

        await Run(new FolderConnectorOptions { ModifiedSince = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.Equal(["a.txt"], LastDeleted);
    }

    [Fact]
    public async Task A_changed_filter_offers_unchanged_files_again()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "content a");
        File.WriteAllText(Path.Combine(_dir, "b.md"), "content b");
        Assert.Equal(["a.txt"], await Run(new FolderConnectorOptions { ExcludePatterns = ["*.md"] }));

        Assert.Equal(["a.txt", "b.md"], (await Run()).Order());
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

    [Fact]
    public async Task CompleteScan_ReportsDeletedPathsOnce()
    {
        var firstPath = Path.Combine(_dir, "first.txt");
        var deletedPath = Path.Combine(_dir, "deleted.txt");
        var thirdPath = Path.Combine(_dir, "third.txt");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(deletedPath, "deleted");
        File.WriteAllText(thirdPath, "third");
        var store = new FolderSyncStateStore(_stateFile);
        var firstScan = new FolderSourceConnector(_dir, "tenant", store);

        await firstScan.DiscoverAsync().ToListAsync();
        await firstScan.SaveStateAsync();
        File.Delete(deletedPath);

        var secondScan = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        await secondScan.DiscoverAsync().ToListAsync();

        Assert.Equal([deletedPath], ReadDeletedSinceLastScan(secondScan));
        await secondScan.SaveStateAsync();

        var thirdScan = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        await thirdScan.DiscoverAsync().ToListAsync();

        Assert.Empty(ReadDeletedSinceLastScan(thirdScan));
    }

    [Fact]
    public async Task CancelledScan_DoesNotReportDeletions()
    {
        var changedPath = Path.Combine(_dir, "a-changed.txt");
        var keptPath = Path.Combine(_dir, "z-kept.txt");
        var deletedPath = Path.Combine(_dir, "m-deleted.txt");
        File.WriteAllText(changedPath, "old");
        File.WriteAllText(keptPath, "old");
        File.WriteAllText(deletedPath, "deleted");
        var store = new FolderSyncStateStore(_stateFile);
        var firstScan = new FolderSourceConnector(_dir, "tenant", store);

        await firstScan.DiscoverAsync().ToListAsync();
        await firstScan.SaveStateAsync();
        File.Delete(deletedPath);
        File.WriteAllText(changedPath, "changed");
        File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddMinutes(1));

        var cancelledScan = new FolderSourceConnector(_dir, "tenant", new FolderSyncStateStore(_stateFile));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in cancelledScan.DiscoverAsync(cts.Token))
                cts.Cancel();
        });

        Assert.Empty(ReadDeletedSinceLastScan(cancelledScan));
    }

    private static IReadOnlyList<string> ReadDeletedSinceLastScan(FolderSourceConnector connector)
    {
        var property = connector.GetType().GetProperty("DeletedSinceLastScan");
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(property!.GetValue(connector));
    }
}
