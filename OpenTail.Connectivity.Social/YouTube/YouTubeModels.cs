using System.Text.Json.Serialization;

namespace OpenTail.Connectivity.Social.YouTube;

public enum YouTubeMode { Search, Channel, VideoComments }

public sealed class YouTubeItem
{
    public string ItemId { get; set; } = "";
    public YouTubeMode Mode { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? AuthorName { get; set; }
    public string? ChannelTitle { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public long LikeCount { get; set; }
    public long CommentCount { get; set; }
    public long ViewCount { get; set; }
    /// <summary>The counts could not be read (the statistics call failed or left this video out): they are unknown, not zero.</summary>
    public bool StatsUnavailable { get; set; }
    public string? VideoId { get; set; }
    public string? Url { get; set; }
    public string? Language { get; set; }
}

// ── Raw API models ──────────────────────────────────────────────────────────

internal sealed class YtSearchResponse
{
    [JsonPropertyName("items")] public List<YtSearchItem>? Items { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

internal sealed class YtSearchItem
{
    [JsonPropertyName("id")] public YtSearchItemId? Id { get; set; }
    [JsonPropertyName("snippet")] public YtSnippet? Snippet { get; set; }
}

internal sealed class YtSearchItemId
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("videoId")] public string? VideoId { get; set; }
}

internal sealed class YtSnippet
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("channelTitle")] public string? ChannelTitle { get; set; }
    [JsonPropertyName("publishedAt")] public DateTimeOffset PublishedAt { get; set; }
    [JsonPropertyName("defaultAudioLanguage")] public string? Language { get; set; }
}

internal sealed class YtVideoListResponse
{
    [JsonPropertyName("items")] public List<YtVideoItem>? Items { get; set; }
}

internal sealed class YtVideoItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("snippet")] public YtSnippet? Snippet { get; set; }
    [JsonPropertyName("statistics")] public YtStatistics? Statistics { get; set; }
}

internal sealed class YtStatistics
{
    [JsonPropertyName("viewCount")] public string? ViewCount { get; set; }
    [JsonPropertyName("likeCount")] public string? LikeCount { get; set; }
    [JsonPropertyName("commentCount")] public string? CommentCount { get; set; }
}

internal sealed class YtCommentThreadResponse
{
    [JsonPropertyName("items")] public List<YtCommentThread>? Items { get; set; }
    [JsonPropertyName("nextPageToken")] public string? NextPageToken { get; set; }
}

internal sealed class YtCommentThread
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("snippet")] public YtCommentThreadSnippet? Snippet { get; set; }
}

internal sealed class YtCommentThreadSnippet
{
    [JsonPropertyName("topLevelComment")] public YtComment? TopLevelComment { get; set; }
}

internal sealed class YtComment
{
    [JsonPropertyName("snippet")] public YtCommentSnippet? Snippet { get; set; }
}

internal sealed class YtCommentSnippet
{
    [JsonPropertyName("textDisplay")] public string TextDisplay { get; set; } = "";
    [JsonPropertyName("authorDisplayName")] public string? AuthorDisplayName { get; set; }
    [JsonPropertyName("likeCount")] public long LikeCount { get; set; }
    [JsonPropertyName("publishedAt")] public DateTimeOffset PublishedAt { get; set; }
}
