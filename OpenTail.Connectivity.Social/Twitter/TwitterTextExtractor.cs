using OpenTail.Connectivity.Social.Twitter;

namespace OpenTail.Connectivity.Social.Twitter;

public sealed class TwitterTextExtractor : ITextExtractor
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public TextExtractionResult Extract(string fileName, byte[] content)
    {
        TwitterTweet? tweet;
        try { tweet = JsonSerializer.Deserialize<TwitterTweet>(content, _json); }
        catch (JsonException ex) { return new TextExtractionResult(false, string.Empty, ex.Message); }

        if (tweet is null || string.IsNullOrWhiteSpace(tweet.Text))
            return new TextExtractionResult(false, string.Empty, "Null or empty tweet.");

        var hashtags = tweet.Entities?.Hashtags?.Count > 0
            ? string.Join(",", tweet.Entities.Hashtags.Select(h => "#" + h.Tag))
            : MarketingMetadata.ExtractHashtags(tweet.Text);

        var mentions = tweet.Entities?.Mentions?.Count > 0
            ? string.Join(",", tweet.Entities.Mentions.Select(m => "@" + m.Username))
            : MarketingMetadata.ExtractMentions(tweet.Text);

        var metrics = tweet.Metrics ?? new TwitterMetrics();
        var author = tweet.AuthorUsername is not null ? "@" + tweet.AuthorUsername : tweet.AuthorId;

        var meta = MarketingMetadata.Build(
            platform: "twitter",
            postId: tweet.Id,
            author: author,
            authorId: tweet.AuthorId,
            url: tweet.AuthorUsername is not null
                ? $"https://x.com/{tweet.AuthorUsername}/status/{tweet.Id}"
                : null,
            publishedAt: tweet.CreatedAt,
            likes: metrics.LikeCount,
            shares: metrics.RetweetCount + metrics.QuoteCount,
            comments: metrics.ReplyCount,
            hashtags: hashtags,
            mentions: mentions,
            language: tweet.Lang,
            mediaType: "text",
            bodyText: tweet.Text);

        return new TextExtractionResult(true, tweet.Text, Metadata: meta);
    }
}
