namespace OpenTail.Connectivity.Files.Inbox;

public static class InboxEmailDocumentMapper
{
    public static NormalisedDocument Map(InboxEmailRecord record)
    {
        var body = BuildBody(record);
        var hash = ComputeHash(body);
        var docId = ComputeHash(record.MessageId + ":" + record.TenantContext);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["email.subject"] = record.Subject,
            ["email.from"] = record.From,
            ["email.to"] = record.To,
            ["email.date"] = record.Date.ToString("o"),
            ["email.folder"] = record.Folder,
        };

        if (!string.IsNullOrWhiteSpace(record.Cc))
            metadata["email.cc"] = record.Cc;

        if (!string.IsNullOrWhiteSpace(record.MessageId))
            metadata["email.messageId"] = record.MessageId;

        if (record.AttachmentNames.Count > 0)
            metadata["email.attachments"] = string.Join(", ", record.AttachmentNames);

        foreach (var (key, value) in record.Headers)
        {
            var safeKey = "email.header." + key.ToLowerInvariant().Replace(' ', '-');
            metadata[safeKey] = value;
        }

        return new NormalisedDocument(
            Id: docId,
            Title: string.IsNullOrWhiteSpace(record.Subject) ? "(no subject)" : record.Subject,
            Body: body,
            ContentHash: hash,
            SourceRef: record.MessageId,
            SourceType: "inbox-email",
            TenantContext: record.TenantContext,
            SourceModified: record.Date,
            IngestedAt: DateTimeOffset.UtcNow,
            Metadata: metadata);
    }

    private static string BuildBody(InboxEmailRecord record)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Subject))
            parts.Add($"Subject: {record.Subject}");
        parts.Add($"From: {record.From}");
        parts.Add($"Date: {record.Date:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(record.Body))
            parts.Add(record.Body.Trim());
        return string.Join("\n", parts);
    }

    private static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
