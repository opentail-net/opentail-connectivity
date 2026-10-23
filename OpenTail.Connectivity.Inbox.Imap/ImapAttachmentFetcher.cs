namespace OpenTail.Connectivity.Inbox.Imap;

public sealed class ImapAttachmentFetcher : IAttachmentFetcher
{
    private readonly ImapSettings _settings;

    public ImapAttachmentFetcher(ImapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public async Task<AttachmentContent?> FetchAsync(
        string messageId, string attachmentId, CancellationToken ct = default)
    {
        // For IMAP, attachmentId is the part specifier (e.g. "2", "2.1").
        // We fetch the full message and extract the part by specifier or filename.
        using var client = new ImapClient();
        await client.ConnectAsync(_settings.Host, _settings.Port, _settings.UseSsl, ct);
        await client.AuthenticateAsync(_settings.Username, _settings.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);

        if (await ImapMessageLookup.FindExactAsync(inbox, messageId, _settings, ct) is not { } uid)
        {
            await client.DisconnectAsync(true, ct);
            return null;
        }

        var full = await inbox.GetMessageAsync(uid, ct);
        await client.DisconnectAsync(true, ct);

        // Find the attachment part matching the specifier (filename or part number)
        foreach (var part in full.Attachments.OfType<MimePart>())
        {
            if (part.FileName == attachmentId ||
                part.ContentId == attachmentId ||
                part.ContentDisposition?.FileName == attachmentId)
            {
                using var stream = new MemoryStream();
                if (part.Content is not null)
                    await part.Content.DecodeToAsync(stream, ct);
                return new AttachmentContent(
                    Name: part.FileName ?? attachmentId,
                    ContentType: part.ContentType is not null
                        ? part.ContentType.MimeType : "application/octet-stream",
                    Bytes: stream.ToArray());
            }
        }

        return null;
    }
}
