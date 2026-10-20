using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class SourceConnectorConformanceTests
{
    [Fact]
    public async Task Check3_rejects_content_returned_for_a_removed_item()
    {
        var ex = await Assert.ThrowsAsync<ConnectorConformanceException>(
            () => SourceConnectorConformance.RunAsync(new StaleContentFixture()));

        Assert.Contains("Check 3 failed: FetchAsync returned content for a removed item.", ex.Message);
    }

    private sealed class StaleContentFixture : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create() => new StaleContentConnector(isEmpty: false);

        public ISourceConnector CreateEmpty() => new StaleContentConnector(isEmpty: true);

        public Task RemoveOneItemAsync(string sourceRef) => Task.CompletedTask;
    }

    private sealed class StaleContentConnector(bool isEmpty) : ISourceConnector
    {
        private static readonly SourceItem Item = new(
            "item-1", "Item", "txt", 1, DateTimeOffset.UnixEpoch.AddDays(1), "tenant");

        public async IAsyncEnumerable<SourceItem> DiscoverAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isEmpty) yield return Item;
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1 });
    }
}
