using OpenTail.Connectivity.Social.Reddit;

namespace OpenTail.Connectivity.Social.Reddit;

public sealed class RedditTextExtractor : ITextExtractor
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        RedditPost? post;
        try
        {
            post = JsonSerializer.Deserialize<RedditPost>(content, _json);
        }
        catch (JsonException ex)
        {
            return new TextExtractionResult(false, string.Empty, ex.Message);
        }

        if (post is null)
            return new TextExtractionResult(false, string.Empty, "Null post payload.");

        var body = string.IsNullOrWhiteSpace(post.Selftext)
            ? post.Title
            : $"{post.Title}\n\n{post.Selftext}";

        // Strip [deleted] / [removed] bodies
        if (post.Selftext == "[deleted]" || post.Selftext == "[removed]")
            return new TextExtractionResult(false, string.Empty, "Post deleted or removed.");

        var fullText = $"{post.Title}\n{post.Selftext}";
        var meta = MarketingMetadata.Build(
            platform: "reddit",
            postId: post.Id,
            author: "u/" + post.Author,
            authorId: null,
            url: string.IsNullOrWhiteSpace(post.Permalink)
                ? null
                : "https://reddit.com" + post.Permalink,
            publishedAt: post.CreatedAt,
            likes: post.Score,
            shares: 0,
            comments: post.NumComments,
            hashtags: MarketingMetadata.ExtractHashtags(fullText),
            mentions: MarketingMetadata.ExtractMentions(fullText),
            language: "en",
            mediaType: post.MediaType,
            bodyText: fullText);

        meta["social.subreddit"] = post.Subreddit;

        return new TextExtractionResult(true, body, Metadata: meta);
    }
}
