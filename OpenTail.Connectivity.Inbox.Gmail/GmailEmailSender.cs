using MimeKit;
using Google.Apis.Gmail.v1.Data;

namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed class GmailEmailSender : IEmailSender
{
    private readonly GmailCredentialProvider _credentialProvider;

    public GmailEmailSender(GmailSettings settings, GmailCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
    }

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var credential = await BuildCredentialAsync(ct);
        var service = new GmailService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });

        var mime = BuildMimeMessage(email);
        var raw = EncodeMessage(mime);

        var gmailMessage = new Message { Raw = raw };
        if (!string.IsNullOrWhiteSpace(email.ThreadId))
            gmailMessage.ThreadId = email.ThreadId;

        var sent = await service.Users.Messages.Send(gmailMessage, "me").ExecuteAsync(ct);
        return sent.Id ?? string.Empty;
    }

    public async Task DeleteAsync(string messageId, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        await svc.Users.Messages.Trash(messageId, "me").ExecuteAsync(ct);
    }

    public async Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        var req = new ModifyMessageRequest
        {
            AddLabelIds = isRead ? [] : ["UNREAD"],
            RemoveLabelIds = isRead ? ["UNREAD"] : []
        };
        await svc.Users.Messages.Modify(req, "me", messageId).ExecuteAsync(ct);
    }

    public async Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default)
    {
        var svc = await BuildServiceAsync(ct);
        // Gmail uses labels not folders; destinationFolder is treated as a label to add.
        var req = new ModifyMessageRequest
        {
            AddLabelIds = [destinationFolder.ToUpperInvariant()],
            RemoveLabelIds = ["INBOX"]
        };
        await svc.Users.Messages.Modify(req, "me", messageId).ExecuteAsync(ct);
    }

    private static MimeMessage BuildMimeMessage(OutboundEmail email)
    {
        var msg = new MimeMessage();
        msg.From.Add(MailboxAddress.Parse("me"));
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

    private static string EncodeMessage(MimeMessage msg)
    {
        using var stream = new MemoryStream();
        msg.WriteTo(stream);
        var bytes = stream.ToArray();
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private async Task<GmailService> BuildServiceAsync(CancellationToken ct)
    {
        var credential = await BuildCredentialAsync(ct);
        return new GmailService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });
    }

    private Task<ICredential> BuildCredentialAsync(CancellationToken ct)
        => _credentialProvider.GetCredentialAsync(ct);
}
