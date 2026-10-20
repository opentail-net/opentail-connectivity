using OpenTail.Connectivity.Social.YouTube;

namespace OpenTail.Connectivity.Social.YouTube;

public sealed class YouTubeTextExtractor : ITextExtractor
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        YouTubeItem? item;
        try { item = JsonSerializer.Deserialize<YouTubeItem>(content, _json); }
        catch (JsonException ex) { return new TextExtractionResult(false, string.Empty, ex.Message); }

        if (item is null || string.IsNullOrWhiteSpace(item.Body))
            return new TextExtractionResult(false, string.Empty, "Null or empty YouTube item.");

        var platform = item.Mode == YouTubeMode.VideoComments ? "youtube_comments" : "youtube";
        var author = item.AuthorName ?? item.ChannelTitle;
        var fullText = item.Title + "\n" + item.Body;

        var meta = MarketingMetadata.Build(
            platform: platform,
            postId: item.ItemId,
            author: author,
            authorId: null,
            url: item.Url,
            publishedAt: item.PublishedAt,
            likes: item.LikeCount,
            shares: 0,
            comments: item.CommentCount,
            hashtags: MarketingMetadata.ExtractHashtags(fullText),
            mentions: MarketingMetadata.ExtractMentions(fullText),
            language: item.Language,
            mediaType: item.Mode == YouTubeMode.VideoComments ? "text" : "video",
            bodyText: fullText);

        if (item.ViewCount > 0) meta["social.views"] = item.ViewCount.ToString();
        if (item.VideoId is not null) meta["social.video_id"] = item.VideoId;

        var body = item.Mode == YouTubeMode.VideoComments
            ? item.Body
            : string.IsNullOrWhiteSpace(item.Body) ? item.Title : $"{item.Title}\n\n{item.Body}";

        return new TextExtractionResult(true, body, Metadata: meta);
    }
}
