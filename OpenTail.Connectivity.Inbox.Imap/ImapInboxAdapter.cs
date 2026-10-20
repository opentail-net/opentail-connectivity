namespace OpenTail.Connectivity.Inbox.Imap;

public sealed class ImapInboxAdapter : IInboxAdapter, IInboxSyncCommit
{
    private static readonly TimeSpan DefaultInitialLookback = TimeSpan.FromDays(30);
    private static readonly System.Text.RegularExpressions.Regex StripHtmlRegex =
        new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ImapSettings _settings;
    private readonly string _tenantContext;
    private readonly ImapSyncStateStore? _syncStore;
    private readonly InboxSyncCommit _commit;
    private ImapSyncState? _pending;

    public ImapInboxAdapter(ImapSettings settings, string tenantContext, ImapSyncStateStore? syncStore = null,
        InboxSyncCommit commit = InboxSyncCommit.AfterEnumeration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantContext);
        _settings = settings;
        _tenantContext = tenantContext;
        _syncStore = syncStore;
        _commit = commit;
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
        using var client = new ImapClient();
        IMailFolder? folder = null;
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, _settings.UseSsl, cancellationToken);
            await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);

            folder = options.Folder == "INBOX"
                ? client.Inbox!
                : await client.GetFolderAsync(options.Folder, cancellationToken);

            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var savedState = _syncStore is not null
                ? await _syncStore.TryLoadAsync(options.Folder, cancellationToken)
                : null;

            var query = BuildQuery(options, savedState, folder);
            var uids = await folder.SearchAsync(query, cancellationToken);

            var incremental = savedState is not null && savedState.UidValidity == folder.UidValidity && savedState.HighestSeenUid > 0;
            var toFetch = SelectUids(uids, options.MaxMessages, incremental);

            var fetchRequest = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure);
            var summaries = await folder.FetchAsync(toFetch, fetchRequest, cancellationToken);

            uint highestSeenUid = savedState?.HighestSeenUid ?? 0;

            foreach (var summary in summaries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var full = await folder.GetMessageAsync(summary.UniqueId, cancellationToken);
                yield return MapToRecord(full, options.Folder);

                if (summary.UniqueId.Id > highestSeenUid)
                    highestSeenUid = summary.UniqueId.Id;
            }

            if (_syncStore is not null && highestSeenUid > 0)
            {
                _pending = new ImapSyncState(
                    Folder: options.Folder,
                    UidValidity: folder.UidValidity,
                    HighestSeenUid: highestSeenUid,
                    UpdatedAt: DateTimeOffset.UtcNow);
                if (_commit == InboxSyncCommit.AfterEnumeration) await CommitSyncStateAsync(cancellationToken);
            }
        }
        finally
        {
            if (folder is { IsOpen: true })
            {
                try { await folder.CloseAsync(false, CancellationToken.None); }
                catch { }
            }

            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Which of the matching UIDs (ascending) to fetch. A first run takes the newest <paramref name="max"/>. An incremental run
    /// takes the oldest new ones, so the saved highest UID moves forward through a backlog run by run instead of skipping it.
    /// </summary>
    internal static List<UniqueId> SelectUids(IList<UniqueId> uids, int max, bool incremental)
    {
        var take = Math.Clamp(max, 0, uids.Count);
        var start = incremental ? 0 : uids.Count - take;
        var selected = new List<UniqueId>(take);
        for (var i = start; i < start + take; i++) selected.Add(uids[i]);
        return selected;
    }

    private static SearchQuery BuildQuery(InboxFetchOptions options, ImapSyncState? savedState, IMailFolder folder)
    {
        if (savedState is not null && savedState.UidValidity == folder.UidValidity && savedState.HighestSeenUid > 0)
            return SearchQuery.Uids(new UniqueIdRange(new UniqueId(savedState.HighestSeenUid + 1), UniqueId.MaxValue));

        if (options.Since.HasValue)
            return SearchQuery.DeliveredAfter(options.Since.Value.UtcDateTime);

        return SearchQuery.DeliveredAfter(DateTime.UtcNow.Subtract(DefaultInitialLookback));
    }

    private InboxEmailRecord MapToRecord(MimeMessage msg, string folder)
    {
        var body = msg.TextBody ?? StripHtml(msg.HtmlBody) ?? string.Empty;
        var parts = msg.Attachments.OfType<MimePart>().ToList();

        var attachments = parts
            .Select(a => a.FileName!)
            .Where(n => n is not null)
            .ToList();

        var attachmentMeta = parts
            .Where(a => !string.IsNullOrWhiteSpace(a.FileName))
            .Select(a => new AttachmentMetadata(
                Name: a.FileName!,
                AttachmentId: a.ContentId ?? a.FileName,
                ContentType: a.ContentType?.MimeType ?? "application/octet-stream",
                SizeBytes: a.Content?.Stream?.Length ?? 0))
            .ToList();

        return new InboxEmailRecord(
            MessageId: msg.MessageId ?? Guid.NewGuid().ToString(),
            Subject: msg.Subject?.Trim() ?? string.Empty,
            From: msg.From.ToString(),
            To: msg.To.ToString(),
            Cc: msg.Cc.ToString(),
            Date: msg.Date,
            Body: body,
            BodyHtml: msg.HtmlBody,
            AttachmentNames: attachments,
            Folder: folder,
            TenantContext: _tenantContext,
            Headers: ExtractHeaders(msg),
            Attachments: attachmentMeta,
            ThreadId: msg.References?.Count > 0 ? msg.References[0] : msg.InReplyTo);
    }

    private static Dictionary<string, string> ExtractHeaders(MimeMessage msg)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (msg.InReplyTo is not null)
            headers["In-Reply-To"] = msg.InReplyTo;
        foreach (var header in msg.Headers)
        {
            if (!headers.ContainsKey(header.Field) &&
                header.Field.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                headers[header.Field] = header.Value;
        }
        return headers;
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return StripHtmlRegex.Replace(html, " ").Trim();
    }
}
