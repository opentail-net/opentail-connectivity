namespace OpenTail.Connectivity.Tests;

public sealed class FolderSourceConnectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public FolderSourceConnectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task DiscoverAsync_yields_supported_files()
    {
        File.WriteAllText(Path.Combine(_dir, "doc.txt"), "hello");
        File.WriteAllText(Path.Combine(_dir, "sheet.csv"), "a,b");
        File.WriteAllText(Path.Combine(_dir, "page.md"), "# Title");

        var connector = new FolderSourceConnector(_dir, "tenant1");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal("tenant1", i.TenantContext));
    }

    [Fact]
    public async Task DiscoverAsync_skips_unsupported_extensions()
    {
        File.WriteAllText(Path.Combine(_dir, "binary.exe"), "MZ");
        File.WriteAllText(Path.Combine(_dir, "archive.zip"), "PK");
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "text");

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("txt", items[0].Extension);
    }

    [Fact]
    public async Task DiscoverAsync_recurses_subdirectories()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "root.txt"), "root");
        File.WriteAllText(Path.Combine(sub.FullName, "nested.txt"), "nested");

        var connector = new FolderSourceConnector(_dir, "t");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task FetchAsync_returns_file_bytes()
    {
        var content = "Hello, OpenTail!";
        var path = Path.Combine(_dir, "sample.txt");
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

        var connector = new FolderSourceConnector(_dir, "t");
        var item = new SourceItem(path, "sample.txt", "txt", content.Length, DateTimeOffset.UtcNow, "t");
        var bytes = await connector.FetchAsync(item);

        Assert.Equal(content, new UTF8Encoding(false).GetString(bytes));
    }

    [Fact]
    public async Task FetchAsync_rejects_paths_outside_root()
    {
        var outsidePath = _dir + "-outside.txt";
        File.WriteAllText(outsidePath, "secret");
        try
        {
            var connector = new FolderSourceConnector(_dir, "t");
            var item = new SourceItem(outsidePath, "outside.txt", "txt", 6, DateTimeOffset.UtcNow, "t");

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(item));
        }
        finally
        {
            File.Delete(outsidePath);
        }
    }

    [Fact]
    public async Task DiscoverAsync_skips_directory_symlinks()
    {
        File.WriteAllText(Path.Combine(_dir, "inside.txt"), "inside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_dir, "loop"), _dir);
        }
        catch (UnauthorizedAccessException)
        {
            // Symlink creation may require elevated privileges on Windows.
            return;
        }
        catch (IOException)
        {
            // Symlink creation may be unavailable in this environment.
            return;
        }

        var connector = new FolderSourceConnector(_dir, "t");
        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Single(items);
        Assert.Equal("inside.txt", items[0].DisplayName);
    }

    [Fact]
    public async Task Files_below_a_linked_folder_cannot_be_fetched()
    {
        var outside = _dir + "-outside-dir";
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var linkedDir = Path.Combine(_dir, "linked");
        try
        {
            try
            {
                Directory.CreateSymbolicLink(linkedDir, outside);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return; // symlink creation may need elevated privileges on Windows
            }

            var connector = new FolderSourceConnector(_dir, "t");
            var item = new SourceItem(Path.Combine(linkedDir, "secret.txt"), "secret.txt", "txt", 6, DateTimeOffset.UtcNow, "t");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(item));
        }
        finally
        {
            if (Directory.Exists(linkedDir)) Directory.Delete(linkedDir);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task File_symlinks_are_skipped_and_cannot_be_fetched()
    {
        var outside = _dir + "-outside";
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        var link = Path.Combine(_dir, "link.txt");
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "inside");
        File.WriteAllText(secret, "secret");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }
        catch (IOException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            var connector = new FolderSourceConnector(_dir, "t");
            var items = await connector.DiscoverAsync().ToListAsync();

            Assert.Equal(["a.txt"], items.Select(item => item.DisplayName));
            var linkedItem = new SourceItem(link, "link.txt", "txt", 6, DateTimeOffset.UtcNow, "t");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connector.FetchAsync(linkedItem));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task DiscoverAsync_UsesConfiguredSupportedExtensions()
    {
        File.WriteAllText(Path.Combine(_dir, "notes.log"), "log");
        File.WriteAllText(Path.Combine(_dir, "doc.txt"), "text");

        var connector = new FolderSourceConnector(
            _dir,
            "t",
            connectorOptions: new FolderConnectorOptions(SupportedExtensions: [".log"]));
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var single = Assert.Single(items);
        Assert.Equal("notes.log", single.DisplayName);
    }

    [Fact]
    public async Task DiscoverAsync_UsesConfiguredNoisyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "generated"));
        File.WriteAllText(Path.Combine(_dir, "generated", "skip.txt"), "skip");
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "keep");

        var connector = new FolderSourceConnector(
            _dir,
            "t",
            connectorOptions: new FolderConnectorOptions(NoisyDirectories: ["generated"]));
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var single = Assert.Single(items);
        Assert.Equal("keep.txt", single.DisplayName);
    }

    [Fact]
    public async Task DiscoverAsync_ThrowsWhenPendingDirectoryLimitExceeded()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a"));
        Directory.CreateDirectory(Path.Combine(_dir, "b"));

        var connector = new FolderSourceConnector(
            _dir,
            "t",
            connectorOptions: new FolderConnectorOptions(MaxPendingDirectories: 1));

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in connector.DiscoverAsync()) { }
        });

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("pending-directory limit", ex.Message);
    }

    [Fact]
    public void Constructor_throws_for_missing_folder()
    {
        Assert.Throws<DirectoryNotFoundException>(
            () => new FolderSourceConnector(@"C:\does-not-exist-opentail-test", "t"));
    }

    [Fact]
    public async Task SourceItem_has_correct_metadata()
    {
        var path = Path.Combine(_dir, "meta.txt");
        File.WriteAllText(path, "content");

        var connector = new FolderSourceConnector(_dir, "acme");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var item0 = items[0];
        Assert.Equal("meta.txt", item0.DisplayName);
        Assert.Equal("txt", item0.Extension);
        Assert.Equal("acme", item0.TenantContext);
        Assert.True(item0.SizeBytes > 0);
    }
}
