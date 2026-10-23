using MailKit.Net.Smtp;
using MimeKit.Text;

namespace OpenTail.Connectivity.Inbox.Imap;

public sealed class ImapEmailSender : IEmailSender, IMailboxManager
{
    private readonly SmtpSettings _smtp;
    private readonly ImapSettings _imap;

    public ImapEmailSender(SmtpSettings smtp, ImapSettings imap)
    {
        ArgumentNullException.ThrowIfNull(smtp);
        ArgumentNullException.ThrowIfNull(imap);
        _smtp = smtp;
        _imap = imap;
    }

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var mime = BuildMimeMessage(email);

        using var client = new SmtpClient();
        await client.ConnectAsync(_smtp.Host, _smtp.Port, _smtp.UseSsl, ct);
        await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(true, CancellationToken.None);

        return mime.MessageId ?? Guid.NewGuid().ToString();
    }

    public async Task DeleteAsync(string messageId, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(_imap.Host, _imap.Port, _imap.UseSsl, ct);
        await client.AuthenticateAsync(_imap.Username, _imap.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        if (await ImapMessageLookup.FindExactAsync(inbox, messageId, ct) is { } uid)
            await ImapMessageLookup.DeleteAsync(client, inbox, uid, ct);
        await client.DisconnectAsync(true, CancellationToken.None);
    }

    public async Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(_imap.Host, _imap.Port, _imap.UseSsl, ct);
        await client.AuthenticateAsync(_imap.Username, _imap.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        if (await ImapMessageLookup.FindExactAsync(inbox, messageId, ct) is { } uid)
        {
            if (isRead)
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            else
                await inbox.RemoveFlagsAsync(uid, MessageFlags.Seen, true, ct);
        }
        await client.DisconnectAsync(true, CancellationToken.None);
    }

    public async Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default)
    {
        using var client = new ImapClient();
        await client.ConnectAsync(_imap.Host, _imap.Port, _imap.UseSsl, ct);
        await client.AuthenticateAsync(_imap.Username, _imap.Password, ct);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite, ct);
        if (await ImapMessageLookup.FindExactAsync(inbox, messageId, ct) is { } uid)
        {
            var dest = await client.GetFolderAsync(destinationFolder, ct);
            await inbox.MoveToAsync(uid, dest, ct);
        }
        await client.DisconnectAsync(true, CancellationToken.None);
    }

    private MimeMessage BuildMimeMessage(OutboundEmail email)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse(_smtp.Username));
        foreach (var to in email.To)
            msg.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in email.Cc ?? [])
            msg.Cc.Add(MailboxAddress.Parse(cc));
        msg.Subject = email.Subject;

        if (!string.IsNullOrWhiteSpace(email.InReplyToMessageId))
        {
            msg.InReplyTo = email.InReplyToMessageId;
            msg.References.Add(email.InReplyToMessageId);
        }

        var builder = new BodyBuilder();
        if (email.BodyFormat.Equals("html", StringComparison.OrdinalIgnoreCase))
            builder.HtmlBody = email.Body;
        else
            builder.TextBody = email.Body;

        foreach (var att in email.Attachments ?? [])
            builder.Attachments.Add(att.Name, att.Bytes, ContentType.Parse(att.ContentType));

        msg.Body = builder.ToMessageBody();
        return msg;
    }
}
