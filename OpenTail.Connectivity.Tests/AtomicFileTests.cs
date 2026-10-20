using OpenTail.Connectivity.Files;
using OpenTail.Connectivity.Inbox.Gmail;
using OpenTail.Connectivity.Inbox.Graph;

namespace OpenTail.Connectivity.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-atomic-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Creates_the_folder_replaces_the_file_and_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir, "nested", "state.json");
        await AtomicFile.WriteAllTextAsync(path, "first");
        await AtomicFile.WriteAllTextAsync(path, "second");
        Assert.Equal("second", await File.ReadAllTextAsync(path));
        Assert.Equal(["state.json"], Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Parallel_writers_leave_one_whole_file()
    {
        var path = Path.Combine(_dir, "state.json");
        var texts = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 10_000)).ToList();
        await Task.WhenAll(texts.Select(t => Task.Run(async () =>
        {
            // On Windows a move can fail while another writer's move holds the target; the caller retries in real use.
            for (var attempt = 0; ; attempt++)
            {
                try { await AtomicFile.WriteAllTextAsync(path, t); return; }
                catch (Exception ex) when (attempt < 20 && ex is IOException or UnauthorizedAccessException) { await Task.Delay(10); }
            }
        })));
        Assert.Contains(await File.ReadAllTextAsync(path), texts);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task The_inbox_state_stores_write_atomically()
    {
        var imap = Path.Combine(_dir, "imap.json");
        await new ImapSyncStateStore(imap).SaveAsync(new ImapSyncState("INBOX", 1, 2, DateTimeOffset.UtcNow));
        var gmail = Path.Combine(_dir, "gmail.json");
        await new GmailSyncStateStore(gmail).SaveAsync(new GmailSyncState("a@example.com", "42", DateTimeOffset.UtcNow));
        var graph = Path.Combine(_dir, "graph.json");
        await new GraphSyncStateStore(graph).SaveAsync(new GraphSyncState("a@example.com", "https://graph/delta", DateTimeOffset.UtcNow));

        Assert.Equal(["gmail.json", "graph.json", "imap.json"], Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
        Assert.Equal(2u, (await new ImapSyncStateStore(imap).TryLoadAsync("INBOX"))!.HighestSeenUid);
    }
}
