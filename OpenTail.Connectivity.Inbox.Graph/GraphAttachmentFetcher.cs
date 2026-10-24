using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Serialization;
using OpenTail.Connectivity.Files.Inbox;

namespace OpenTail.Connectivity.Inbox.Graph;

/// <summary>
/// Fetches one attachment by the Graph message id and attachment id. It reads the attachment's type and size first, without
/// <c>contentBytes</c>, then streams <c>$value</c> with a byte limit. Graph returns an attached message, contact or event from
/// <c>$value</c> as MIME, so item attachments are handed on as <c>.eml</c>; a link to a cloud file has no bytes (405) and is
/// <see cref="AttachmentFetchStatus.NotAFile"/>.
/// </summary>
public sealed class GraphAttachmentFetcher : IAttachmentFetcher
{
    private static readonly Dictionary<string, ParsableFactory<IParsable>> ErrorMapping = new()
    {
        ["XXX"] = ODataError.CreateFromDiscriminatorValue,
    };

    private readonly GraphSettings _settings;
    private GraphServiceClient? _client;

    public GraphAttachmentFetcher(GraphSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    internal GraphAttachmentFetcher(GraphSettings settings, GraphServiceClient client) : this(settings) => _client = client;

    public async Task<AttachmentFetchResult> FetchAsync(AttachmentRef reference, AttachmentFetchLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(limits);
        var builder = GetClient().Users[_settings.UserEmail].Messages[reference.ProviderMessageId].Attachments[reference.AttachmentId];

        Attachment? attachment;
        try
        {
            attachment = await builder.GetAsync(r => r.QueryParameters.Select = ["id", "name", "contentType", "size"], ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 404 or 400)
        {
            return AttachmentFetchResult.Missing("The message or the attachment is no longer in the mailbox.");
        }

        switch (attachment)
        {
            case null:
                return AttachmentFetchResult.Missing("The message or the attachment is no longer in the mailbox.");
            case ReferenceAttachment:
                return AttachmentFetchResult.NotAFile("The attachment is a link to a cloud file, not a file.");
        }
        if (attachment.Size is int declared && declared > limits.MaxBytes)
            return AttachmentFetchResult.TooLarge(limits.MaxBytes);

        byte[]? bytes;
        try
        {
            await using var stream = await GetClient().RequestAdapter.SendPrimitiveAsync<Stream>(ValueRequest(builder), ErrorMapping, ct);
            if (stream is null)
                return AttachmentFetchResult.Failed("Microsoft Graph returned no content for the attachment.");
            bytes = await AttachmentDownload.ReadLimitedAsync(stream, limits.MaxBytes, ct);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 404)
        {
            return AttachmentFetchResult.Missing("The message or the attachment is no longer in the mailbox.");
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 405)
        {
            return AttachmentFetchResult.NotAFile("The attachment has no file content.");
        }
        if (bytes is null)
            return AttachmentFetchResult.TooLarge(limits.MaxBytes);

        var name = attachment.Name ?? reference.AttachmentId;
        return attachment is ItemAttachment
            ? AttachmentFetchResult.Fetched(new AttachmentContent(
                name.EndsWith(".eml", StringComparison.OrdinalIgnoreCase) ? name : name + ".eml", "message/rfc822", bytes))
            : AttachmentFetchResult.Fetched(new AttachmentContent(name, attachment.ContentType ?? "application/octet-stream", bytes));
    }

    /// <summary><c>GET …/attachments/{id}/$value</c>: the SDK has no builder for it, so the attachment's URL is extended.</summary>
    private static RequestInformation ValueRequest(Microsoft.Graph.Users.Item.Messages.Item.Attachments.Item.AttachmentItemRequestBuilder builder)
    {
        var uri = builder.ToGetRequestInformation().URI;
        var path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new RequestInformation { HttpMethod = Method.GET, URI = new Uri(path + "/$value") };
    }

    private GraphServiceClient GetClient()
    {
        if (_client is not null) return _client;
        var credential = new ClientSecretCredential(
            _settings.TenantId, _settings.ClientId, _settings.ClientSecret);
        _client = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
        return _client;
    }
}
