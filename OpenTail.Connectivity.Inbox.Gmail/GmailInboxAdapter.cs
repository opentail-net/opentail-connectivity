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
        using var service = ServiceForTests?.Invoke() ?? new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = await BuildCredentialAsync(cancellationToken),
            ApplicationName = "OpenTail-Connectivity"
        });

        var savedState = _syncStore is not null
            ? await _syncStore.TryLoadAsync(_settings.UserEmail, cancellationToken)
            : null;

        var startedAt = DateTimeOffset.UtcNow;
        var remaining = Math.Max(0, options.MaxMessages);
        bool useHistory = savedState?.HistoryId is not null && ulong.TryParse(savedState.HistoryId, out _);
        var backfill = savedState?.Backfill;
        var coveredUntil = savedState?.CoveredUntil;

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
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Gmail's answer for a history id too old to use: a full sync. Any other failure is thrown, so the next run tries the
                // history again; a transient error must not move the state. The full sync is a backfill from the last point all mail
                // was known read (a day earlier, for dates that do not match arrival), kept across runs until it is done.
                useHistory = false;
                var known = savedState!.CoveredUntil ?? savedState.LastFetchAt;
                var after = (known - BackfillOverlap).ToUnixTimeSeconds();
                if (options.Since is { } since) after = Math.Max(after, since.ToUnixTimeSeconds());
                if (backfill is not null) after = Math.Min(after, backfill.After);
                backfill = new GmailBackfill(after, Before: null);
            }
        }

        string? historyId;
        // Set when MaxMessages stops a history run before the end: the state then records the last history record read in
        // full, so the next run carries on from there instead of jumping to the mailbox's latest history id.
        var stoppedEarly = false;

        if (useHistory && firstHistoryResponse != null)
        {
            var startHistoryId = ulong.Parse(savedState!.HistoryId!);
            string? pageToken = firstHistoryResponse.NextPageToken;
            var historyResponse = firstHistoryResponse;
            string? latestHistoryId = savedState.HistoryId;
            string? resumeHistoryId = savedState.HistoryId;

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

            // The history responses' own id, not the profile's read afterwards: mail arriving in between would be skipped.
            historyId = stoppedEarly ? resumeHistoryId : latestHistoryId;
            if (!stoppedEarly) coveredUntil = startedAt;
        }
        else
        {
            // Read before any listing, so mail that arrives while it runs comes with the next history run, never in between.
            var profile = await service.Users.GetProfile("me").ExecuteAsync(cancellationToken);
            historyId = profile.HistoryId?.ToString() ?? savedState?.HistoryId;
            coveredUntil = startedAt;
            if (backfill is null)
            {
                // A first run: the newest MaxMessages (documented); older mail is not fetched.
                await foreach (var msg in ListAsync(service, BuildQuery(options), options.Folder, remaining, new ListProgress(), cancellationToken))
                {
                    remaining--;
                    yield return GmailMessageMapper.Map(msg, _tenantContext, options.Folder);
                }
            }
        }

        // A backfill goes on after the new mail, with what MaxMessages leaves. Listing is newest first, so what is left is the older end.
        if (backfill is not null && !stoppedEarly && remaining > 0)
        {
            var progress = new ListProgress();
            await foreach (var msg in ListAsync(service, backfill.Query, options.Folder, remaining, progress, cancellationToken))
            {
                remaining--;
                yield return GmailMessageMapper.Map(msg, _tenantContext, options.Folder);
            }
            // A second's overlap at the cut: a message can come twice, never not at all. When that would not move the cut (a run
            // that read only that second), it moves past it, or the same messages would be read for ever; only more than
            // MaxMessages messages in one second can then be missed.
            if (progress.Exhausted || progress.OldestSeconds is not { } oldest) backfill = null;
            else backfill = backfill with { Before = oldest + 1 < (backfill.Before ?? long.MaxValue) ? oldest + 1 : oldest };
        }

        if (_syncStore is not null && historyId is not null)
        {
            _pending = new GmailSyncState(
                UserEmail: _settings.UserEmail,
                HistoryId: historyId,
                LastFetchAt: DateTimeOffset.UtcNow,
                CoveredUntil: coveredUntil,
                Backfill: backfill);
            if (_commit == InboxSyncCommit.AfterEnumeration) await CommitSyncStateAsync(cancellationToken);
        }
    }

    private static readonly TimeSpan BackfillOverlap = TimeSpan.FromDays(1);

    /// <summary>For tests: the Gmail client to use instead of one built from the settings' credentials. Called once per fetch.</summary>
    internal Func<GmailService>? ServiceForTests { get; init; }

    private sealed class ListProgress
    {
        /// <summary>The listing reached its end: no page and no message left.</summary>
        public bool Exhausted { get; set; }
        /// <summary>The oldest internal date read, in Unix seconds.</summary>
        public long? OldestSeconds { get; set; }
    }

    /// <summary>Messages matching <paramref name="query"/>, newest first, at most <paramref name="max"/>.</summary>
    private static async IAsyncEnumerable<Message> ListAsync(GmailService service, string? query, string folder, int max, ListProgress progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? pageToken = null;
        var taken = 0;
        while (taken < max)
        {
            var listRequest = service.Users.Messages.List("me");
            listRequest.Q = query;
            listRequest.MaxResults = Math.Min(max - taken, GmailMaxPageSize);
            listRequest.PageToken = pageToken;
            listRequest.LabelIds = folder == "INBOX" ? new[] { "INBOX" } : null;

            var listResponse = await listRequest.ExecuteAsync(cancellationToken);
            var refs = listResponse.Messages ?? [];
            var lastPage = string.IsNullOrWhiteSpace(listResponse.NextPageToken);
            if (refs.Count == 0) lastPage = true;
            for (var i = 0; i < refs.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (taken >= max) yield break;

                var getRequest = service.Users.Messages.Get("me", refs[i].Id);
                getRequest.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
                var fullMsg = await getRequest.ExecuteAsync(cancellationToken);
                taken++;
                if (fullMsg.InternalDate is { } ms && (progress.OldestSeconds is null || ms / 1000 < progress.OldestSeconds))
                    progress.OldestSeconds = ms / 1000;
                if (lastPage && i == refs.Count - 1) progress.Exhausted = true;
                yield return fullMsg;
            }

            if (lastPage)
            {
                progress.Exhausted = true;
                yield break;
            }
            pageToken = listResponse.NextPageToken;
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
