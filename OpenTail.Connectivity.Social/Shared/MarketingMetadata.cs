using System.Globalization;

namespace OpenTail.Connectivity.Social.Shared;

public static class MarketingMetadata
{
    public static double EngagementScore(long likes, long shares, long comments)
    {
        var raw = likes + shares * 2.0 + comments * 3.0;
        return Math.Min(1.0, raw / 1000.0);
    }

    public static Dictionary<string, string> Build(
        string platform,
        string postId,
        string? author,
        string? authorId,
        string? url,
        DateTimeOffset publishedAt,
        long likes,
        long shares,
        long comments,
        string? hashtags,
        string? mentions,
        string? language,
        string? mediaType,
        string bodyText)
    {
        var score = SentimentScorer.Score(bodyText);
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["social.platform"] = platform,
            ["social.post_id"] = postId,
            ["social.published_at"] = publishedAt.ToString("O", CultureInfo.InvariantCulture),
            ["social.likes"] = likes.ToString(CultureInfo.InvariantCulture),
            ["social.shares"] = shares.ToString(CultureInfo.InvariantCulture),
            ["social.comments"] = comments.ToString(CultureInfo.InvariantCulture),
            ["social.engagement_score"] = EngagementScore(likes, shares, comments).ToString("F4", CultureInfo.InvariantCulture),
            ["social.sentiment_score"] = score.ToString("F4", CultureInfo.InvariantCulture),
            ["social.sentiment_label"] = SentimentScorer.Label(score),
        };
        if (!string.IsNullOrWhiteSpace(author)) meta["social.author"] = author;
        if (!string.IsNullOrWhiteSpace(authorId)) meta["social.author_id"] = authorId;
        if (!string.IsNullOrWhiteSpace(url)) meta["social.url"] = url;
        if (!string.IsNullOrWhiteSpace(hashtags)) meta["social.hashtags"] = hashtags;
        if (!string.IsNullOrWhiteSpace(mentions)) meta["social.mentions"] = mentions;
        if (!string.IsNullOrWhiteSpace(language)) meta["social.language"] = language;
        if (!string.IsNullOrWhiteSpace(mediaType)) meta["social.media_type"] = mediaType;
        return meta;
    }

    public static string ExtractHashtags(string text) =>
        string.Join(",", Regex.Matches(text, @"#(\w+)").Select(m => "#" + m.Groups[1].Value));

    public static string ExtractMentions(string text) =>
        string.Join(",", Regex.Matches(text, @"@(\w+)").Select(m => "@" + m.Groups[1].Value));
}
