using Microsoft.Graph.Users.Item.Messages.Item.Move;

namespace OpenTail.Connectivity.Inbox.Graph;

public sealed class GraphEmailSender : IEmailSender, IMailboxManager
{
    private readonly GraphSettings _settings;
    private GraphServiceClient? _client;

    public GraphEmailSender(GraphSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public async Task<string> SendAsync(OutboundEmail email, CancellationToken ct = default)
    {
        var client = GetClient();

        var message = new Message
        {
            Subject = email.Subject,
            Body = new ItemBody
            {
                ContentType = email.BodyFormat.Equals("html", StringComparison.OrdinalIgnoreCase)
                    ? BodyType.Html : BodyType.Text,
                Content = email.Body
            },
            ToRecipients = email.To.Select(BuildRecipient).ToList(),
            CcRecipients = email.Cc?.Select(BuildRecipient).ToList() ?? []
        };

        if (!string.IsNullOrWhiteSpace(email.InReplyToMessageId))
        {
            message.InternetMessageHeaders =
            [
                new InternetMessageHeader { Name = "In-Reply-To", Value = email.InReplyToMessageId },
                new InternetMessageHeader { Name = "References", Value = email.InReplyToMessageId }
            ];
        }

        if (email.Attachments is { Count: > 0 })
        {
            message.Attachments = email.Attachments
                .Select(a => (Attachment)new FileAttachment
                {
                    Name = a.Name,
                    ContentType = a.ContentType,
                    ContentBytes = a.Bytes
                })
                .ToList();
        }

        // Create as draft first to obtain the message ID, then send.
        var draft = await client
            .Users[_settings.UserEmail]
            .Messages
            .PostAsync(message, cancellationToken: ct);

        await client
            .Users[_settings.UserEmail]
            .Messages[draft!.Id]
            .Send
            .PostAsync(cancellationToken: ct);

        return draft.Id!;
    }

    public async Task DeleteAsync(string messageId, CancellationToken ct = default)
        => await GetClient()
            .Users[_settings.UserEmail]
            .Messages[messageId]
            .DeleteAsync(cancellationToken: ct);

    public async Task MarkReadAsync(string messageId, bool isRead, CancellationToken ct = default)
        => await GetClient()
            .Users[_settings.UserEmail]
            .Messages[messageId]
            .PatchAsync(new Message { IsRead = isRead }, cancellationToken: ct);

    public async Task MoveAsync(string messageId, string destinationFolder, CancellationToken ct = default)
        => await GetClient()
            .Users[_settings.UserEmail]
            .Messages[messageId]
            .Move
            .PostAsync(new MovePostRequestBody { DestinationId = destinationFolder }, cancellationToken: ct);

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(
            _settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }

    private static Recipient BuildRecipient(string address)
    {
        var lt = address.LastIndexOf('<');
        var gt = address.LastIndexOf('>');
        return lt >= 0 && gt > lt
            ? new Recipient
            {
                EmailAddress = new EmailAddress
                {
                    Name = address[..lt].Trim(),
                    Address = address[(lt + 1)..gt].Trim()
                }
            }
            : new Recipient { EmailAddress = new EmailAddress { Address = address.Trim() } };
    }
}
