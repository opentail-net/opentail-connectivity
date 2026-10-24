namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphThreadFetcher : IEmailThreadFetcher
{
    private readonly GraphSettings _settings;
    private readonly string _tenantContext;
    private GraphServiceClient? _client;

    public GraphThreadFetcher(GraphSettings settings, string tenantContext)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _tenantContext = tenantContext;
    }

    public async IAsyncEnumerable<InboxEmailRecord> FetchThreadAsync(
        string threadId,
        int maxMessages = 50,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = GetClient();

        var response = await client.Users[_settings.UserEmail]
            .Messages
            .GetAsync(req =>
            {
                req.QueryParameters.Filter = $"conversationId eq '{threadId}'";
                req.QueryParameters.Top = maxMessages;
                req.QueryParameters.Select = ["id", "internetMessageId", "subject", "from",
                    "toRecipients", "ccRecipients", "receivedDateTime", "body",
                    "internetMessageHeaders", "conversationId"];
                req.QueryParameters.Expand = [GraphMessageMapper.AttachmentsExpand];
                req.QueryParameters.Orderby = ["receivedDateTime asc"];
            }, ct);

        if (response?.Value is null) yield break;

        foreach (var msg in response.Value)
        {
            if (msg is null) continue;
            yield return GraphMessageMapper.Map(msg, _tenantContext);
        }
    }

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(_settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }
}
