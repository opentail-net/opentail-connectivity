using OpenTail.Connectivity.Files.Connectors;

namespace OpenTail.Connectivity.Tests;

public sealed class FileSyncCursorStoreConcurrencyTests
{
    [Fact]
    public async Task First_save_creates_a_missing_parent_directory()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "state.json");
        try
        {
            var store = new FileSyncCursorStore(path);
            await store.SaveAsync("scope", "cursor", 0);
            Assert.NotNull(await store.LoadAsync("scope"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Separate_instances_preserve_concurrent_scope_updates()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var first = new FileSyncCursorStore(path);
            var second = new FileSyncCursorStore(path);
            var saves = Enumerable.Range(0, 25)
                .SelectMany(i => new[]
                {
                    first.SaveAsync($"first:{i}", i.ToString(), 0),
                    second.SaveAsync($"second:{i}", i.ToString(), 0),
                });
            await Task.WhenAll(saves);

            var read = new FileSyncCursorStore(path);
            foreach (var i in Enumerable.Range(0, 25))
            {
                Assert.NotNull(await read.LoadAsync($"first:{i}"));
                Assert.NotNull(await read.LoadAsync($"second:{i}"));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".lock")) File.Delete(path + ".lock");
        }
    }
}
