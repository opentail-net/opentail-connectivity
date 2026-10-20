using OpenTail.Connectivity.Social.Meta;

namespace OpenTail.Connectivity.Social.Meta;

public sealed class MetaTextExtractor : ITextExtractor
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        MetaPost? post;
        try { post = JsonSerializer.Deserialize<MetaPost>(content, _json); }
        catch (JsonException ex) { return new TextExtractionResult(false, string.Empty, ex.Message); }

        if (post is null)
            return new TextExtractionResult(false, string.Empty, "Null post payload.");

        var body = post.Body;
        if (string.IsNullOrWhiteSpace(body))
            return new TextExtractionResult(false, string.Empty, "Empty post body.");

        var platform = post.Platform == MetaPlatform.Instagram ? "instagram" : "facebook";

        var meta = MarketingMetadata.Build(
            platform: platform,
            postId: post.Id,
            author: post.PageName,
            authorId: null,
            url: post.Permalink,
            publishedAt: post.CreatedAt,
            likes: post.Likes,
            shares: post.Shares,
            comments: post.Comments,
            hashtags: MarketingMetadata.ExtractHashtags(body),
            mentions: MarketingMetadata.ExtractMentions(body),
            language: null,
            mediaType: post.MediaType ?? "text",
            bodyText: body);

        return new TextExtractionResult(true, body, Metadata: meta);
    }
}
