namespace OpenTail.Connectivity.Files.Contracts;

public interface IInboxAdapter
{
    IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(InboxFetchOptions options, CancellationToken cancellationToken = default);
}
