namespace OpenTail.Connectivity.Inbox.Graph;

public static class GraphMessageMapper
{
    private static readonly System.Text.RegularExpressions.Regex StripHtmlRegex = new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <c>attachments</c> is a relationship of message, not a property, so <c>$select</c> does not return it; it needs <c>$expand</c>.
    /// The nested <c>$select</c> keeps <c>contentBytes</c> out of listings.
    /// </summary>
    internal const string AttachmentsExpand = "attachments($select=id,name,contentType,size,isInline)";

    public static InboxEmailRecord Map(Message msg, string tenantContext, string folder = "INBOX")
    {
        var messageId = msg.InternetMessageId ?? msg.Id ?? Guid.NewGuid().ToString();
        var subject = msg.Subject ?? string.Empty;
        var from = FormatRecipient(msg.From);
        var to = FormatRecipients(msg.ToRecipients);
        var cc = FormatRecipients(msg.CcRecipients);
        var date = msg.ReceivedDateTime ?? DateTimeOffset.UtcNow;
        var body = msg.Body?.ContentType == BodyType.Text
            ? msg.Body.Content ?? string.Empty
            : null;
        var htmlBody = msg.Body?.ContentType == BodyType.Html
            ? msg.Body.Content
            : null;
        var attachments = (msg.Attachments ?? [])
            .Select(a => a.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        var attachmentMeta = (msg.Attachments ?? [])
            .Where(a => a.Name is not null && a.Id is not null)
            .Select(a => new AttachmentMetadata(
                Name: a.Name!,
                AttachmentId: a.Id,
                ContentType: a.ContentType ?? "application/octet-stream",
                SizeBytes: a.Size ?? 0))
            .ToList();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (msg.InternetMessageHeaders is not null)
        {
            foreach (var h in msg.InternetMessageHeaders)
            {
                if (h.Name is not null && h.Value is not null &&
                    !headers.ContainsKey(h.Name) &&
                    (h.Name.StartsWith("X-", StringComparison.OrdinalIgnoreCase) ||
                     h.Name.Equals("In-Reply-To", StringComparison.OrdinalIgnoreCase)))
                    headers[h.Name] = h.Value;
            }
        }

        return new InboxEmailRecord(
            MessageId: messageId,
            Subject: subject.Trim(),
            From: from,
            To: to,
            Cc: cc,
            Date: date,
            Body: body ?? StripHtml(htmlBody) ?? string.Empty,
            BodyHtml: htmlBody,
            AttachmentNames: attachments,
            Folder: folder,
            TenantContext: tenantContext,
            Headers: headers,
            Attachments: attachmentMeta,
            ThreadId: msg.ConversationId,
            ProviderMessageId: msg.Id);
    }

    private static string FormatRecipient(Recipient? r) =>
        r?.EmailAddress is { } ea
            ? string.IsNullOrWhiteSpace(ea.Name) ? ea.Address ?? string.Empty : $"{ea.Name} <{ea.Address}>"
            : string.Empty;

    private static string FormatRecipients(IEnumerable<Recipient>? recipients) =>
        recipients is null ? string.Empty : string.Join(", ", recipients.Select(FormatRecipient));

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return StripHtmlRegex.Replace(html, " ").Trim();
    }
}
