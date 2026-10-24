namespace OpenTail.Connectivity.Inbox.Gmail;

/// <summary>
/// Fetches one attachment by the Gmail API message id and the part's <c>partId</c>. The message is read again to find the part,
/// so the short-lived <c>body.attachmentId</c> is always current; parts that carry their data inline need no second call.
/// </summary>
public sealed class GmailAttachmentFetcher : IAttachmentFetcher
{
    private readonly GmailCredentialProvider _credentialProvider;

    public GmailAttachmentFetcher(GmailSettings settings, GmailCredentialProvider? credentialProvider = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _credentialProvider = credentialProvider ?? new GmailCredentialProvider(settings);
    }

    internal Func<GmailService>? ServiceForTests { get; init; }

    public async Task<AttachmentFetchResult> FetchAsync(AttachmentRef reference, AttachmentFetchLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(limits);

        using var service = ServiceForTests?.Invoke() ?? new GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = await _credentialProvider.GetCredentialAsync(ct),
            ApplicationName = "OpenTail-Connectivity"
        });

        Message message;
        try
        {
            var get = service.Users.Messages.Get("me", reference.ProviderMessageId);
            get.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            message = await get.ExecuteAsync(ct);
        }
        catch (Google.GoogleApiException ex) when (ex.HttpStatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            // Gmail answers 400 for an id that is not a message id at all.
            return AttachmentFetchResult.Missing("The message is no longer in the mailbox.");
        }

        if (FindPart(message?.Payload, reference.AttachmentId) is not { } part || string.IsNullOrWhiteSpace(part.Filename))
            return AttachmentFetchResult.Missing("The message has no such attachment.");
        if (part.Body?.Size is int declared && declared > limits.MaxBytes)
            return AttachmentFetchResult.TooLarge(limits.MaxBytes);

        var data = part.Body?.Data;
        if (data is null && part.Body?.AttachmentId is { } attachmentId)
        {
            try
            {
                data = (await service.Users.Messages.Attachments.Get("me", reference.ProviderMessageId, attachmentId).ExecuteAsync(ct))?.Data;
            }
            catch (Google.GoogleApiException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return AttachmentFetchResult.Missing("The attachment is no longer in the mailbox.");
            }
        }

        byte[] bytes;
        if (data is null)
        {
            if (part.Body?.Size is null or 0) bytes = [];
            else return AttachmentFetchResult.Failed("Gmail returned no content for the attachment.");
        }
        else
        {
            // Base64 is 4 characters for 3 bytes; refuse before decoding what cannot fit.
            if ((long)data.Length / 4 * 3 > limits.MaxBytes + 2)
                return AttachmentFetchResult.TooLarge(limits.MaxBytes);
            if (DecodeBase64Url(data) is not { } decoded)
                return AttachmentFetchResult.Failed("Gmail returned content that is not valid base64.");
            if (decoded.Length > limits.MaxBytes)
                return AttachmentFetchResult.TooLarge(limits.MaxBytes);
            bytes = decoded;
        }

        return AttachmentFetchResult.Fetched(new AttachmentContent(
            Name: part.Filename,
            ContentType: string.IsNullOrWhiteSpace(part.MimeType) ? "application/octet-stream" : part.MimeType,
            Bytes: bytes));
    }

    internal static MessagePart? FindPart(MessagePart? part, string partId)
    {
        if (part is null) return null;
        if (part.PartId == partId) return part;
        if (part.Parts is null) return null;
        foreach (var child in part.Parts)
        {
            if (FindPart(child, partId) is { } found) return found;
        }
        return null;
    }

    internal static byte[]? DecodeBase64Url(string data)
    {
        var base64 = data.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        try { return Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
    }
}
