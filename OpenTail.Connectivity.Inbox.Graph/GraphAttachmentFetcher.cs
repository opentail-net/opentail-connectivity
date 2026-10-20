namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphAttachmentFetcher : IAttachmentFetcher
{
    private readonly GraphSettings _settings;
    private GraphServiceClient? _client;

    public GraphAttachmentFetcher(GraphSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public async Task<AttachmentContent?> FetchAsync(
        string messageId, string attachmentId, CancellationToken ct = default)
    {
        var attachment = await GetClient()
            .Users[_settings.UserEmail]
            .Messages[messageId]
            .Attachments[attachmentId]
            .GetAsync(cancellationToken: ct);

        return attachment is FileAttachment file
            ? new AttachmentContent(
                Name: file.Name ?? string.Empty,
                ContentType: file.ContentType ?? "application/octet-stream",
                Bytes: file.ContentBytes ?? [])
            : null;
    }

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(
            _settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }
}
