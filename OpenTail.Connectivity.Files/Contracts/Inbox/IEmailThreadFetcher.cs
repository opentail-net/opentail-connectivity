namespace OpenTail.Connectivity.Files.Contracts;

public interface IEmailThreadFetcher
{
    IAsyncEnumerable<InboxEmailRecord> FetchThreadAsync(
        string threadId,
        int maxMessages = 50,
        CancellationToken ct = default);
}
