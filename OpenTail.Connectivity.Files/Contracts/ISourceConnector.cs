namespace OpenTail.Connectivity.Files.Contracts;

public interface ISourceConnector
{
    IAsyncEnumerable<SourceItem> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default);
}
