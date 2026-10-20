namespace OpenTail.Connectivity.Tests.Inbox;

public sealed class ReadOnlyInboxGuardTests
{
    private sealed class SafeAdapter : IInboxAdapter
    {
        public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(
            InboxFetchOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class UnsafeAdapter : IInboxAdapter
    {
        public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(
            InboxFetchOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void DeleteMessage(string messageId) { }
    }

    [Fact]
    public void AssertReadOnly_DoesNotThrow_ForReadOnlyAdapter()
    {
        var ex = Record.Exception(() => ReadOnlyInboxGuard.AssertReadOnly(typeof(SafeAdapter)));
        Assert.Null(ex);
    }

    [Fact]
    public void AssertReadOnly_Throws_WhenWriteMethodPresent()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ReadOnlyInboxGuard.AssertReadOnly(typeof(UnsafeAdapter)));
    }

    [Fact]
    public void IsReadOnly_ReturnsTrue_ForSafeAdapter()
    {
        Assert.True(ReadOnlyInboxGuard.IsReadOnly(typeof(SafeAdapter)));
    }

    [Fact]
    public void IsReadOnly_ReturnsFalse_ForUnsafeAdapter()
    {
        Assert.False(ReadOnlyInboxGuard.IsReadOnly(typeof(UnsafeAdapter)));
    }
}
