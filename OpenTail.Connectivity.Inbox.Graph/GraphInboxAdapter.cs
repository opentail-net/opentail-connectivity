namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphInboxAdapter : IInboxAdapter, IInboxSyncCommit
{
    private readonly GraphSettings _settings;
    private readonly string _tenantContext;
    private readonly GraphSyncStateStore? _syncStore;
    private readonly InboxSyncCommit _commit;
    private GraphSyncState? _pending;
    private GraphServiceClient? _client;

    public GraphInboxAdapter(GraphSettings settings, string tenantContext, GraphSyncStateStore? syncStore = null,
        InboxSyncCommit commit = InboxSyncCommit.AfterEnumeration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _settings = settings;
        _tenantContext = tenantContext;
        _syncStore = syncStore;
        _commit = commit;
    }

    /// <summary>For tests: a client built over a stub HTTP handler.</summary>
    internal GraphInboxAdapter(GraphSettings settings, string tenantContext, GraphSyncStateStore? syncStore, InboxSyncCommit commit, GraphServiceClient client)
        : this(settings, tenantContext, syncStore, commit)
    {
        _client = client;
    }

    public bool HasPendingSyncState => _pending is not null;

    public async Task CommitSyncStateAsync(CancellationToken cancellationToken = default)
    {
        if (_pending is not { } state || _syncStore is null) return;
        await _syncStore.SaveAsync(state, cancellationToken);
        _pending = null;
    }

    /// <summary>
    /// Messages of one folder, through Graph's delta query. State is kept per mailbox and folder. With saved state the run
    /// continues from the saved link: a delta link (all changes since), or a next link when <see cref="InboxFetchOptions.MaxMessages"/>
    /// stopped the last run, so a backlog is read run by run instead of skipped. Pages are asked for at most MaxMessages long.
    /// </summary>
    public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(
        InboxFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        var folderPath = options.Folder == "INBOX" ? "inbox" : options.Folder;
        var pageSize = Math.Clamp(options.MaxMessages, 1, 1000);

        var savedState = _syncStore is null ? null
            : await _syncStore.TryLoadAsync(_settings.UserEmail, folderPath, cancellationToken)
              // State saved before it was kept per folder belonged to the inbox.
              ?? (folderPath == "inbox" ? await _syncStore.TryLoadAsync(_settings.UserEmail, cancellationToken) : null);

        string? pageLink = savedState?.DeltaLink; // the link that fetched the current page; null for the first initial page
        MessageCollectionResponse? page = pageLink is null
            ? await FirstPageAsync(client, folderPath, options, pageSize, cancellationToken)
            : await SendRawDeltaPageAsync(client, pageLink, pageSize, cancellationToken);

        var emitted = 0;
        string? resumeLink = null;
        while (page is not null)
        {
            var messages = (page.Value ?? []).Where(m => m is not null && m.AdditionalData?.ContainsKey("@removed") != true).ToList();
            var room = options.MaxMessages - emitted;
            foreach (var msg in messages.Take(Math.Max(0, room)))
            {
                emitted++;
                yield return GraphMessageMapper.Map(msg, _tenantContext, options.Folder);
            }

            // The SDK maps @odata.nextLink to a property; @odata.deltaLink stays in AdditionalData.
            var deltaLink = GetODataLink(page.AdditionalData, "@odata.deltaLink");
            var nextLink = page.OdataNextLink ?? GetODataLink(page.AdditionalData, "@odata.nextLink");
            if (messages.Count > room)
            {
                // Stopped inside a page: read this page again next time (ids are stable, so repeats overwrite themselves).
                resumeLink = pageLink;
                break;
            }
            if (!string.IsNullOrWhiteSpace(deltaLink)) { resumeLink = deltaLink; break; }
            if (string.IsNullOrWhiteSpace(nextLink)) break;
            if (emitted >= options.MaxMessages) { resumeLink = nextLink; break; }

            pageLink = nextLink;
            page = await SendRawDeltaPageAsync(client, nextLink, pageSize, cancellationToken);
        }

        if (_syncStore is not null && resumeLink is not null)
        {
            _pending = new GraphSyncState(_settings.UserEmail, resumeLink, DateTimeOffset.UtcNow, folderPath);
            if (_commit == InboxSyncCommit.AfterEnumeration) await CommitSyncStateAsync(cancellationToken);
        }
    }

    private Task<MessageCollectionResponse?> FirstPageAsync(
        GraphServiceClient client, string folderPath, InboxFetchOptions options, int pageSize, CancellationToken ct)
    {
        // Sent as a raw request so the first page and the later ones are read the same way (MessageCollectionResponse).
        var requestInfo = client.Users[_settings.UserEmail]
            .MailFolders[folderPath]
            .Messages
            .Delta
            .ToGetRequestInformation(req =>
            {
                req.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");
                req.QueryParameters.Select = ["id", "internetMessageId", "subject", "from", "toRecipients",
                    "ccRecipients", "receivedDateTime", "body", "attachments", "internetMessageHeaders"];
                // The messages delta query accepts a receivedDateTime filter; the delta link then keeps it.
                if (options.Since is { } since)
                    req.QueryParameters.Filter = $"receivedDateTime ge {since.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            });
        return client.RequestAdapter.SendAsync(requestInfo, MessageCollectionResponse.CreateFromDiscriminatorValue, cancellationToken: ct);
    }

    private GraphServiceClient GetClient()
    {
        if (_client is not null)
            return _client;

        var credential = new ClientSecretCredential(_settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }

    private static Task<MessageCollectionResponse?> SendRawDeltaPageAsync(
        GraphServiceClient client,
        string pageUrl,
        int pageSize,
        CancellationToken ct)
    {
        // Graph delta and next links are opaque, fully-qualified URLs returned by Graph.
        // Reusing them as the request URL is the documented way to continue delta paging.
        var requestInfo = new RequestInformation
        {
            HttpMethod = Method.GET,
            UrlTemplate = pageUrl
        };
        requestInfo.Headers.Add("Prefer", $"odata.maxpagesize={pageSize}");

        return client.RequestAdapter.SendAsync(
            requestInfo,
            MessageCollectionResponse.CreateFromDiscriminatorValue,
            cancellationToken: ct);
    }

    private static string? GetODataLink(IDictionary<string, object>? additionalData, string key)
        => additionalData is not null && additionalData.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
}
