using System.Text.Json.Serialization;

namespace OpenTail.Connectivity.Social.Twitter;

public sealed class TwitterTweet
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("author_id")] public string? AuthorId { get; set; }
    [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("public_metrics")] public TwitterMetrics? Metrics { get; set; }
    [JsonPropertyName("entities")] public TwitterEntities? Entities { get; set; }
    // Populated after expansion join
    public string? AuthorUsername { get; set; }
    public string? AuthorName { get; set; }
}

public sealed class TwitterMetrics
{
    [JsonPropertyName("retweet_count")] public long RetweetCount { get; set; }
    [JsonPropertyName("reply_count")] public long ReplyCount { get; set; }
    [JsonPropertyName("like_count")] public long LikeCount { get; set; }
    [JsonPropertyName("quote_count")] public long QuoteCount { get; set; }
}

public sealed class TwitterEntities
{
    [JsonPropertyName("hashtags")] public List<TwitterHashtag>? Hashtags { get; set; }
    [JsonPropertyName("mentions")] public List<TwitterMention>? Mentions { get; set; }
}

public sealed class TwitterHashtag
{
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";
}

public sealed class TwitterMention
{
    [JsonPropertyName("username")] public string Username { get; set; } = "";
}

internal sealed class TwitterSearchResponse
{
    [JsonPropertyName("data")] public List<TwitterTweet>? Data { get; set; }
    [JsonPropertyName("includes")] public TwitterIncludes? Includes { get; set; }
    [JsonPropertyName("meta")] public TwitterMeta? Meta { get; set; }
}

internal sealed class TwitterIncludes
{
    [JsonPropertyName("users")] public List<TwitterUser>? Users { get; set; }
}

internal sealed class TwitterUser
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("username")] public string Username { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal sealed class TwitterMeta
{
    [JsonPropertyName("next_token")] public string? NextToken { get; set; }
    [JsonPropertyName("result_count")] public int ResultCount { get; set; }
}
