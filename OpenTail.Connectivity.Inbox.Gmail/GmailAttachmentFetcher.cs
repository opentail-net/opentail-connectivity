namespace OpenTail.Connectivity.Inbox.Gmail;

public sealed class GmailAttachmentFetcher : IAttachmentFetcher
{
    private readonly GmailSettings _settings;
    private readonly GmailCredentialProvider _credentialProvider;

    public GmailAttachmentFetcher(GmailSettings settings, GmailCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
    }

    public async Task<AttachmentContent?> FetchAsync(
        string messageId, string attachmentId, CancellationToken ct = default)
    {
        var credential = await BuildCredentialAsync(ct);
        var service = new GmailService(new Google.Apis.Services.BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "OpenTail-Connectivity"
        });

        var result = await service.Users.Messages.Attachments
            .Get("me", messageId, attachmentId)
            .ExecuteAsync(ct);

        if (result?.Data is null)
            return null;

        // Gmail uses URL-safe base64 encoding
        var base64 = result.Data.Replace('-', '+').Replace('_', '/');
        var padding = (4 - base64.Length % 4) % 4;
        base64 += new string('=', padding);

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        // Retrieve the filename by fetching the message's attachment metadata
        var msg = await service.Users.Messages.Get("me", messageId).ExecuteAsync(ct);
        var name = FindAttachmentName(msg?.Payload, attachmentId) ?? attachmentId;

        return new AttachmentContent(
            Name: name,
            ContentType: "application/octet-stream",
            Bytes: bytes);
    }

    private static string? FindAttachmentName(
        Google.Apis.Gmail.v1.Data.MessagePart? part, string attachmentId)
    {
        if (part is null) return null;
        if (part.Body?.AttachmentId == attachmentId && !string.IsNullOrWhiteSpace(part.Filename))
            return part.Filename;
        if (part.Parts is null) return null;
        foreach (var child in part.Parts)
        {
            var name = FindAttachmentName(child, attachmentId);
            if (name is not null) return name;
        }
        return null;
    }

    private Task<Google.Apis.Auth.OAuth2.ICredential> BuildCredentialAsync(CancellationToken ct)
        => _credentialProvider.GetCredentialAsync(ct);
}
