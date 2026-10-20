namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed class GmailThreadFetcher : IEmailThreadFetcher
{
    private readonly string _tenantContext;
    private readonly GmailCredentialProvider _credentialProvider;

    public GmailThreadFetcher(GmailSettings settings, string tenantContext, GmailCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _tenantContext = tenantContext;
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
    }

    public async IAsyncEnumerable<InboxEmailRecord> FetchThreadAsync(
        string threadId,
        int maxMessages = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var credential = await BuildCredentialAsync(ct);
        var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });

        var request = service.Users.Threads.Get("me", threadId);
        request.Format = UsersResource.ThreadsResource.GetRequest.FormatEnum.Full;
        var thread = await request.ExecuteAsync(ct);
        if (thread?.Messages is null) yield break;

        var count = 0;
        foreach (var msg in thread.Messages)
        {
            if (count >= maxMessages) break;
            if (msg is null) continue;
            yield return GmailMessageMapper.Map(msg, _tenantContext);
            count++;
        }
    }

    private Task<ICredential> BuildCredentialAsync(CancellationToken ct)
        => _credentialProvider.GetCredentialAsync(ct);
}
