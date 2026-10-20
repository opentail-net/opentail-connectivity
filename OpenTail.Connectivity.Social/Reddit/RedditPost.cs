using System.Text.Json.Serialization;

namespace OpenTail.Connectivity.Social.Reddit;

internal sealed class RedditListingResponse
{
    [JsonPropertyName("data")] public RedditListingData Data { get; set; } = new();
}

internal sealed class RedditListingData
{
    [JsonPropertyName("after")] public string? After { get; set; }
    [JsonPropertyName("children")] public List<RedditChild> Children { get; set; } = [];
}

internal sealed class RedditChild
{
    [JsonPropertyName("data")] public RedditPost Data { get; set; } = new();
}

public sealed class RedditPost
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("selftext")] public string Selftext { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("subreddit")] public string Subreddit { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("permalink")] public string Permalink { get; set; } = "";
    [JsonPropertyName("created_utc")] public double CreatedUtc { get; set; }
    [JsonPropertyName("score")] public long Score { get; set; }
    [JsonPropertyName("ups")] public long Ups { get; set; }
    [JsonPropertyName("num_comments")] public long NumComments { get; set; }
    [JsonPropertyName("is_video")] public bool IsVideo { get; set; }
    [JsonPropertyName("post_hint")] public string? PostHint { get; set; }
    [JsonPropertyName("stickied")] public bool Stickied { get; set; }

    public DateTimeOffset CreatedAt =>
        DateTimeOffset.FromUnixTimeSeconds((long)CreatedUtc);

    public string MediaType =>
        IsVideo ? "video" : PostHint == "image" ? "image" : PostHint == "link" ? "link" : "text";
}
