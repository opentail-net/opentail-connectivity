namespace OpenTail.Connectivity.Inbox.Gmail;

public static class GmailMessageMapper
{
    private static readonly System.Text.RegularExpressions.Regex StripHtmlRegex = new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static InboxEmailRecord Map(Message msg, string tenantContext, string folder = "INBOX")
    {
        var headers = IndexHeaders(msg.Payload?.Headers);

        var subject = headers.GetValueOrDefault("Subject", string.Empty);
        var from = headers.GetValueOrDefault("From", string.Empty);
        var to = headers.GetValueOrDefault("To", string.Empty);
        var cc = headers.GetValueOrDefault("Cc", string.Empty);
        var messageId = headers.GetValueOrDefault("Message-ID", msg.Id ?? Guid.NewGuid().ToString());
        var dateHeader = headers.GetValueOrDefault("Date", string.Empty);
        var date = ParseDate(dateHeader);

        var (plainBody, htmlBody) = ExtractBody(msg.Payload);
        var attachments = ExtractAttachmentNames(msg.Payload);
        var attachmentMeta = ExtractAttachmentMetadata(msg.Payload);

        var extraHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers.TryGetValue("In-Reply-To", out var inReplyTo))
            extraHeaders["In-Reply-To"] = inReplyTo;
        foreach (var (k, v) in headers)
        {
            if (k.StartsWith("X-", StringComparison.OrdinalIgnoreCase) && !extraHeaders.ContainsKey(k))
                extraHeaders[k] = v;
        }

        return new InboxEmailRecord(
            MessageId: messageId,
            Subject: subject.Trim(),
            From: from,
            To: to,
            Cc: cc,
            Date: date,
            Body: plainBody ?? StripHtml(htmlBody) ?? string.Empty,
            BodyHtml: htmlBody,
            AttachmentNames: attachments,
            Folder: folder,
            TenantContext: tenantContext,
            Headers: extraHeaders,
            Attachments: attachmentMeta,
            ThreadId: msg.ThreadId,
            ProviderMessageId: msg.Id);
    }

    private static Dictionary<string, string> IndexHeaders(IList<MessagePartHeader>? headers)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null) return dict;
        foreach (var h in headers)
        {
            if (h.Name is not null && h.Value is not null && !dict.ContainsKey(h.Name))
                dict[h.Name] = h.Value;
        }
        return dict;
    }

    private static DateTimeOffset ParseDate(string header)
    {
        if (DateTimeOffset.TryParse(header, out var dt))
            return dt;
        return DateTimeOffset.UtcNow;
    }

    private static (string? plain, string? html) ExtractBody(MessagePart? part)
    {
        if (part is null) return (null, null);

        var mimeType = part.MimeType ?? string.Empty;

        if (mimeType.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
            return (DecodeBase64Url(part.Body?.Data), null);

        if (mimeType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            return (null, DecodeBase64Url(part.Body?.Data));

        if (mimeType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) && part.Parts is not null)
        {
            string? plain = null, html = null;
            foreach (var child in part.Parts)
            {
                var (p, h) = ExtractBody(child);
                plain ??= p;
                html ??= h;
            }
            return (plain, html);
        }

        return (null, null);
    }

    private static List<string> ExtractAttachmentNames(MessagePart? part)
    {
        var names = new List<string>();
        CollectAttachments(part, names);
        return names;
    }

    private static void CollectAttachments(MessagePart? part, List<string> names)
    {
        if (part is null) return;
        var filename = part.Filename;
        if (!string.IsNullOrWhiteSpace(filename)) names.Add(filename);
        if (part.Parts is null) return;
        foreach (var child in part.Parts) CollectAttachments(child, names);
    }

    private static List<AttachmentMetadata> ExtractAttachmentMetadata(MessagePart? part)
    {
        var meta = new List<AttachmentMetadata>();
        CollectAttachmentMeta(part, meta);
        return meta;
    }

    /// <summary>
    /// Every part with a file name. The reference is the part's <c>partId</c> ("1", "1.2"), not <c>body.attachmentId</c>: Google does
    /// not document that id as stable across reads, and small parts carry their data inline with no attachment id at all. The
    /// fetcher re-reads the message and finds the part by its partId.
    /// </summary>
    private static void CollectAttachmentMeta(MessagePart? part, List<AttachmentMetadata> meta)
    {
        if (part is null) return;
        var filename = part.Filename;
        if (!string.IsNullOrWhiteSpace(filename) && !string.IsNullOrWhiteSpace(part.PartId))
        {
            meta.Add(new AttachmentMetadata(
                Name: filename,
                AttachmentId: part.PartId,
                ContentType: part.MimeType ?? "application/octet-stream",
                SizeBytes: part.Body?.Size ?? 0));
        }
        if (part.Parts is null) return;
        foreach (var child in part.Parts) CollectAttachmentMeta(child, meta);
    }

    private static string? DecodeBase64Url(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            var base64 = data.Replace('-', '+').Replace('_', '/');
            var padding = (4 - base64.Length % 4) % 4;
            base64 += new string('=', padding);
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException) { return null; }
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return StripHtmlRegex.Replace(html, " ").Trim();
    }
}
