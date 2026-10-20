namespace OpenTail.Connectivity.Email;

public sealed class EmlTextExtractor : ITextExtractor
{
    private static readonly System.Text.RegularExpressions.Regex StripHtmlRegex = new("<[^>]+>", System.Text.RegularExpressions.RegexOptions.Compiled);

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        try
        {
            using var stream = new MemoryStream(content);
            var msg = MimeMessage.Load(stream);

            var body = msg.TextBody ?? StripHtmlTags(msg.HtmlBody) ?? string.Empty;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["email.subject"] = msg.Subject ?? string.Empty,
                ["email.from"] = msg.From.ToString(),
                ["email.to"] = msg.To.ToString(),
                ["email.date"] = msg.Date.ToString("o"),
            };

            if (msg.MessageId is not null)
                metadata["email.messageId"] = msg.MessageId;

            if (msg.InReplyTo is not null)
                metadata["email.inReplyTo"] = msg.InReplyTo;

            var attachmentNames = msg.Attachments
                .OfType<MimePart>()
                .Select(a => a.FileName)
                .Where(n => n is not null)
                .ToList();
            if (attachmentNames.Count > 0)
                metadata["email.attachments"] = string.Join(", ", attachmentNames);

            var fullText = BuildFullText(msg.Subject, msg.From.ToString(), msg.Date, body);
            return new TextExtractionResult(true, fullText, null, metadata);
        }
        catch (Exception ex)
        {
            return new TextExtractionResult(false, string.Empty, ex.Message);
        }
    }

    private static string BuildFullText(string? subject, string from, DateTimeOffset date, string body)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(subject)) parts.Add($"Subject: {subject}");
        parts.Add($"From: {from}");
        parts.Add($"Date: {date:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(body)) parts.Add(body.Trim());
        return string.Join("\n", parts);
    }

    private static string? StripHtmlTags(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        return StripHtmlRegex.Replace(html, " ").Trim();
    }
}
