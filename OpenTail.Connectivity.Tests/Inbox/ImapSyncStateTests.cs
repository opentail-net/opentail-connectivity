namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class ImapSyncStateTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTrips()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"imap-sync-{Guid.NewGuid()}.json");
        try
        {
            var store = new ImapSyncStateStore(filePath);
            var state = new ImapSyncState("INBOX", UidValidity: 12345, HighestSeenUid: 999, DateTimeOffset.UtcNow);
            await store.SaveAsync(state);

            var loaded = await store.TryLoadAsync("INBOX");
            Assert.NotNull(loaded);
            Assert.Equal("INBOX", loaded.Folder);
            Assert.Equal(12345u, loaded.UidValidity);
            Assert.Equal(999u, loaded.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task TryLoad_ReturnsNull_WhenFileAbsent()
    {
        var store = new ImapSyncStateStore(Path.Combine(Path.GetTempPath(), $"no-such-file-{Guid.NewGuid()}.json"));
        var result = await store.TryLoadAsync("INBOX");
        Assert.Null(result);
    }

    [Fact]
    public async Task TryLoad_ReturnsNull_ForUnknownFolder()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"imap-sync-{Guid.NewGuid()}.json");
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 10, DateTimeOffset.UtcNow));

            var result = await store.TryLoadAsync("Sent");
            Assert.Null(result);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Save_MultipleFolders_IndependentlyRetrievable()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"imap-sync-{Guid.NewGuid()}.json");
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 100, DateTimeOffset.UtcNow));
            await store.SaveAsync(new ImapSyncState("Sent", 2, 50, DateTimeOffset.UtcNow));

            var inbox = await store.TryLoadAsync("INBOX");
            var sent = await store.TryLoadAsync("Sent");

            Assert.Equal(100u, inbox?.HighestSeenUid);
            Assert.Equal(50u, sent?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }

    [Fact]
    public async Task Save_OverwritesPreviousState()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"imap-sync-{Guid.NewGuid()}.json");
        try
        {
            var store = new ImapSyncStateStore(filePath);
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 100, DateTimeOffset.UtcNow));
            await store.SaveAsync(new ImapSyncState("INBOX", 1, 200, DateTimeOffset.UtcNow));

            var loaded = await store.TryLoadAsync("INBOX");
            Assert.Equal(200u, loaded?.HighestSeenUid);
        }
        finally { File.Delete(filePath); }
    }
}
