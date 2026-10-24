using OpenTail.Connectivity.Files.Inbox;

namespace OpenTail.Connectivity.Inbox.Imap;

/// <summary>
/// Fetches one MIME part. The message reference is the adapter's stable id (account, folder, UIDVALIDITY, UID), so the fetch
/// opens that folder and a renumbered folder gives <see cref="AttachmentFetchStatus.Missing"/>, never another message. The
/// attachment reference is the part specifier from BODYSTRUCTURE; only that part is downloaded, not the whole message.
/// </summary>
public sealed class ImapAttachmentFetcher : IAttachmentFetcher
{
    private readonly ImapSettings _settings;

    public ImapAttachmentFetcher(ImapSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public async Task<AttachmentFetchResult> FetchAsync(AttachmentRef reference, AttachmentFetchLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(limits);
        if (!ImapMessageLookup.TryParseStableId(reference.ProviderMessageId, _settings, out var folderName, out var uidValidity, out var uidValue))
            return AttachmentFetchResult.Missing("The message reference does not belong to this mailbox.");

        using var client = new ImapClient();
        IMailFolder? folder = null;
        try
        {
            await client.ConnectAsync(_settings.Host, _settings.Port, _settings.UseSsl, ct);
            await client.AuthenticateAsync(_settings.Username, _settings.Password, ct);

            try
            {
                folder = await client.GetFolderAsync(folderName, ct);
            }
            catch (FolderNotFoundException)
            {
                return AttachmentFetchResult.Missing("The folder no longer exists.");
            }
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            if (folder.UidValidity != uidValidity)
                return AttachmentFetchResult.Missing("The folder was renumbered since the message was listed.");

            var uid = new UniqueId(uidValidity, uidValue);
            var summaries = await folder.FetchAsync([uid], MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct);
            if (summaries.Count == 0 || summaries[0].Body is not { } body)
                return AttachmentFetchResult.Missing("The message is no longer in the folder.");

            if (FindPart(body, reference.AttachmentId) is not { } part)
                return AttachmentFetchResult.Missing("The message has no such attachment.");
            if (ImapInboxAdapter.DecodedSize(part) > limits.MaxBytes)
                return AttachmentFetchResult.TooLarge(limits.MaxBytes);

            var entity = await folder.GetBodyPartAsync(uid, part, ct);
            using var buffer = new AttachmentDownload.LimitedStream(limits.MaxBytes);
            try
            {
                switch (entity)
                {
                    case MimePart mime when mime.Content is not null:
                        await mime.Content.DecodeToAsync(buffer, ct);
                        break;
                    case MessagePart { Message: { } message }:
                        await message.WriteToAsync(buffer, ct);
                        break;
                    default:
                        return AttachmentFetchResult.Failed("The attachment has no content.");
                }
            }
            catch (AttachmentDownload.LimitExceededException)
            {
                return AttachmentFetchResult.TooLarge(limits.MaxBytes);
            }

            var contentType = entity is MessagePart ? "message/rfc822" : part.ContentType?.MimeType ?? "application/octet-stream";
            return AttachmentFetchResult.Fetched(new AttachmentContent(
                Name: ImapInboxAdapter.AttachmentName(part) ?? reference.AttachmentId,
                ContentType: contentType,
                Bytes: buffer.ToArray()));
        }
        finally
        {
            if (folder is { IsOpen: true })
            {
                try { await folder.CloseAsync(false, CancellationToken.None); }
                catch { }
            }
            if (client.IsConnected)
            {
                try { await client.DisconnectAsync(true, CancellationToken.None); }
                catch { }
            }
        }
    }

    /// <summary>The part with this specifier: a leaf or a forwarded message, never a multipart container.</summary>
    internal static BodyPartBasic? FindPart(BodyPart body, string specifier)
    {
        switch (body)
        {
            case BodyPartMultipart multipart:
                foreach (var child in multipart.BodyParts)
                    if (FindPart(child, specifier) is { } found) return found;
                return null;
            case BodyPartMessage message:
                if (message.PartSpecifier == specifier) return message;
                return message.Body is { } inner ? FindPart(inner, specifier) : null;
            case BodyPartBasic basic:
                return basic.PartSpecifier == specifier ? basic : null;
            default:
                return null;
        }
    }
}
