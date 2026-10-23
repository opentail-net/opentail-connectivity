namespace OpenTail.Connectivity.Inbox.Imap;

public sealed class ImapThreadFetcher : IEmailThreadFetcher
{
    private static readonly System.Text.RegularExpressions.Regex StripHtmlRegex =
        new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly ImapSettings _settings;
    private readonly string _tenantContext;

    public ImapThreadFetcher(ImapSettings settings, string tenantContext)
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
        using var client = new ImapClient();
        IMailFolder? folder = null;
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, _settings.UseSsl, ct);
            await client.AuthenticateAsync(_settings.Username, _settings.Password, ct);
            folder = client.Inbox!;
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);

            // threadId for IMAP is the root message-id (from References[0] or InReplyTo)
            var query = SearchQuery.Or(
                SearchQuery.Or(
                    SearchQuery.HeaderContains("References", threadId),
                    SearchQuery.HeaderContains("In-Reply-To", threadId)),
                SearchQuery.HeaderContains("Message-Id", threadId));

            var uids = await folder.SearchAsync(query, ct);
            if (uids.Count == 0) yield break;

            var count = 0;
            foreach (var uid in uids)
            {
                if (count >= maxMessages) break;
                ct.ThrowIfCancellationRequested();
                var msg = await folder.GetMessageAsync(uid, ct);
                yield return MapToRecord(msg, ImapMessageLookup.StableId(_settings, folder.FullName, folder.UidValidity, uid.Id));
                count++;
            }
        }
        finally
        {
            if (folder is { IsOpen: true })
            {
                try { await folder.CloseAsync(false, CancellationToken.None); } catch { }
            }
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); } catch { }
            }
        }
    }

    private InboxEmailRecord MapToRecord(MimeMessage msg, string stableId)
    {
        var body = msg.TextBody ?? StripHtml(msg.HtmlBody) ?? string.Empty;
        var parts = msg.Attachments.OfType<MimePart>().ToList();
        var attachments = parts.Select(a => a.FileName!).Where(n => n is not null).ToList();
        var attachmentMeta = parts
            .Where(a => !string.IsNullOrWhiteSpace(a.FileName))
            .Select(a => new AttachmentMetadata(
                Name: a.FileName!,
                AttachmentId: a.ContentId ?? a.FileName,
                ContentType: a.ContentType is not null ? a.ContentType.MimeType : "application/octet-stream",
                SizeBytes: a.Content?.Stream?.Length ?? 0))
            .ToList();

        return new InboxEmailRecord(
            MessageId: msg.MessageId ?? stableId,
            Subject: msg.Subject?.Trim() ?? string.Empty,
            From: msg.From.ToString(),
            To: msg.To.ToString(),
            Cc: msg.Cc.ToString(),
            Date: msg.Date,
            Body: body,
            BodyHtml: msg.HtmlBody,
            AttachmentNames: attachments,
            Folder: "INBOX",
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
