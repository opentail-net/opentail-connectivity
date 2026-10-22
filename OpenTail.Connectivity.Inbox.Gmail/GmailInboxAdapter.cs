namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed class GmailInboxAdapter : IInboxAdapter, IInboxSyncCommit
{
    private const int GmailMaxPageSize = 500;

    private readonly GmailSettings _settings;
    private readonly string _tenantContext;
    private readonly GmailSyncStateStore? _syncStore;
    private readonly GmailCredentialProvider _credentialProvider;
    private readonly InboxSyncCommit _commit;
    private GmailSyncState? _pending;

    public GmailInboxAdapter(
        GmailSettings settings,
        string tenantContext,
        GmailSyncStateStore? syncStore = null,
        GmailCredentialProvider? credentialProvider = null,
        InboxSyncCommit? commit = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _settings = settings;
        _tenantContext = tenantContext;
        _syncStore = syncStore;
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
        _commit = InboxSyncCommits.Require(syncStore is not null, commit);
    }

    public bool HasPendingSyncState => _pending is not null;

    public async Task CommitSyncStateAsync(CancellationToken cancellationToken = default)
    {
        if (_pending is not { } state || _syncStore is null) return;
        await _syncStore.SaveAsync(state, cancellationToken);
        _pending = null;
    }

    public async IAsyncEnumerable<InboxEmailRecord> FetchRecentAsync(
        InboxFetchOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var credential = await BuildCredentialAsync(cancellationToken);
        var service = new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });

        var savedState = _syncStore is not null
            ? await _syncStore.TryLoadAsync(_settings.UserEmail, cancellationToken)
            : null;

        var remaining = Math.Max(0, options.MaxMessages);
        string? latestHistoryId = savedState?.HistoryId;
        bool useHistory = savedState?.HistoryId is not null && ulong.TryParse(savedState.HistoryId, out _);
        
        Google.Apis.Gmail.v1.Data.ListHistoryResponse? firstHistoryResponse = null;
        if (useHistory)
        {
            try
            {
                var historyRequest = service.Users.History.List("me");
                historyRequest.StartHistoryId = ulong.Parse(savedState!.HistoryId!);
                historyRequest.MaxResults = Math.Min(remaining, GmailMaxPageSize);
                firstHistoryResponse = await historyRequest.ExecuteAsync(cancellationToken);
            }
            catch (Google.GoogleApiException)
            {
                // History ID expired or invalid, fallback to full sync
                useHistory = false;
            }
        }

        // Set when MaxMessages stops a history run before the end: the state then records the last history record read in
        // full, so the next run carries on from there instead of jumping to the mailbox's latest history id.
        string? resumeHistoryId = null;
        var stoppedEarly = false;

        if (useHistory && firstHistoryResponse != null)
        {
            var startHistoryId = ulong.Parse(savedState!.HistoryId!);
            string? pageToken = firstHistoryResponse.NextPageToken;
            var historyResponse = firstHistoryResponse;
            resumeHistoryId = savedState.HistoryId;

            while (true)
            {
                latestHistoryId = MaxHistoryId(latestHistoryId, historyResponse.HistoryId?.ToString());

                if (historyResponse.History is not null && historyResponse.History.Count > 0)
                {
                    foreach (var history in historyResponse.History)
                    {
                        if (stoppedEarly) break;
                        foreach (var msgAdded in history.MessagesAdded ?? [])
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (remaining <= 0) { stoppedEarly = true; break; }

                            var getRequest = service.Users.Messages.Get("me", msgAdded.Message.Id);
                            getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                            var fullMsg = await getRequest.ExecuteAsync(cancellationToken);

                            if (options.Folder != "INBOX" || (fullMsg.LabelIds != null && fullMsg.LabelIds.Contains("INBOX")))
                            {
                                remaining--;
                                yield return GmailMessageMapper.Map(fullMsg, _tenantContext, options.Folder);
                            }
                        }
                        if (!stoppedEarly && history.Id is { } done) resumeHistoryId = MaxHistoryId(resumeHistoryId, done.ToString());
                    }
                }

                if (stoppedEarly || string.IsNullOrWhiteSpace(pageToken))
                    break;
                if (remaining <= 0)
                {
                    stoppedEarly = true; // more pages wait
                    break;
                }

                var historyRequest = service.Users.History.List("me");
                historyRequest.StartHistoryId = startHistoryId;
                historyRequest.MaxResults = Math.Min(remaining, GmailMaxPageSize);
                historyRequest.PageToken = pageToken;
                historyResponse = await historyRequest.ExecuteAsync(cancellationToken);
                pageToken = historyResponse.NextPageToken;
            }
        }

        if (!useHistory)
        {
            var query = BuildQuery(options);
            string? pageToken = null;

            while (remaining > 0)
            {
                var listRequest = service.Users.Messages.List("me");
                listRequest.Q = query;
                listRequest.MaxResults = Math.Min(remaining, GmailMaxPageSize);
                listRequest.PageToken = pageToken;
                listRequest.LabelIds = options.Folder == "INBOX" ? new[] { "INBOX" } : null;

                var listResponse = await listRequest.ExecuteAsync(cancellationToken);
                if (listResponse.Messages is null or { Count: 0 })
                    break;

                foreach (var msgRef in listResponse.Messages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (remaining <= 0)
                        break;

                    var getRequest = service.Users.Messages.Get("me", msgRef.Id);
                    getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                    var fullMsg = await getRequest.ExecuteAsync(cancellationToken);

                    latestHistoryId = MaxHistoryId(latestHistoryId, fullMsg.HistoryId?.ToString());
                    remaining--;

                    yield return GmailMessageMapper.Map(fullMsg, _tenantContext, options.Folder);
                }

                pageToken = listResponse.NextPageToken;
                if (string.IsNullOrWhiteSpace(pageToken))
                    break;
            }
        }

        if (stoppedEarly)
        {
            latestHistoryId = resumeHistoryId;
        }
        else
        {
            var profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
            latestHistoryId = profile.HistoryId?.ToString() ?? latestHistoryId;
        }

        if (_syncStore is not null && latestHistoryId is not null)
        {
            _pending = new GmailSyncState(
                UserEmail: _settings.UserEmail,
                HistoryId: latestHistoryId,
                LastFetchAt: DateTimeOffset.UtcNow);
            if (_commit == InboxSyncCommit.AfterEnumeration) await CommitSyncStateAsync(cancellationToken);
        }
    }

    private Task<ICredential> BuildCredentialAsync(CancellationToken ct)
        => _credentialProvider.GetCredentialAsync(ct);

    private static string? BuildQuery(InboxFetchOptions options)
    {
        if (options.Since.HasValue)
            return $"after:{options.Since.Value.ToUnixTimeSeconds()}";
        return null;
    }

    private static string? MaxHistoryId(string? current, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return current;
        if (string.IsNullOrWhiteSpace(current))
            return candidate;

        return ulong.TryParse(candidate, out var candidateValue) &&
               ulong.TryParse(current, out var currentValue) &&
               candidateValue > currentValue
            ? candidate
            : current;
    }
}
