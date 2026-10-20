using System.Text.Json.Serialization;

namespace OpenTail.Connectivity.Social.Meta;

public enum MetaPlatform { Facebook, Instagram }

public sealed class MetaPost
{
    public string Id { get; set; } = "";
    public MetaPlatform Platform { get; set; }
    public string? Message { get; set; }
    public string? Caption { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Likes { get; set; }
    public long Shares { get; set; }
    public long Comments { get; set; }
    public string? MediaType { get; set; }
    public string? Permalink { get; set; }
    public string? PageName { get; set; }

    public string Body => Platform == MetaPlatform.Instagram ? (Caption ?? string.Empty) : (Message ?? string.Empty);
}

// ── Raw Facebook models ─────────────────────────────────────────────────────

internal sealed class FbPagePostsResponse
{
    [JsonPropertyName("data")] public List<FbPost>? Data { get; set; }
    [JsonPropertyName("paging")] public FbPaging? Paging { get; set; }
}

internal sealed class FbPost
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("created_time")] public DateTimeOffset CreatedTime { get; set; }
    [JsonPropertyName("likes")] public FbSummaryWrapper? Likes { get; set; }
    [JsonPropertyName("shares")] public FbShares? Shares { get; set; }
    [JsonPropertyName("comments")] public FbSummaryWrapper? Comments { get; set; }
}

internal sealed class FbSummaryWrapper
{
    [JsonPropertyName("summary")] public FbSummary? Summary { get; set; }
}

internal sealed class FbSummary
{
    [JsonPropertyName("total_count")] public long TotalCount { get; set; }
}

internal sealed class FbShares
{
    [JsonPropertyName("count")] public long Count { get; set; }
}

internal sealed class FbPaging
{
    [JsonPropertyName("cursors")] public FbCursors? Cursors { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
}

internal sealed class FbCursors
{
    [JsonPropertyName("after")] public string? After { get; set; }
}

// ── Raw Instagram models ────────────────────────────────────────────────────

internal sealed class IgMediaResponse
{
    [JsonPropertyName("data")] public List<IgMedia>? Data { get; set; }
    [JsonPropertyName("paging")] public IgPaging? Paging { get; set; }
}

internal sealed class IgMedia
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("caption")] public string? Caption { get; set; }
    [JsonPropertyName("timestamp")] public DateTimeOffset Timestamp { get; set; }
    [JsonPropertyName("like_count")] public long LikeCount { get; set; }
    [JsonPropertyName("comments_count")] public long CommentsCount { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
    [JsonPropertyName("permalink")] public string? Permalink { get; set; }
}

internal sealed class IgPaging
{
    [JsonPropertyName("cursors")] public IgCursors? Cursors { get; set; }
    [JsonPropertyName("next")] public string? Next { get; set; }
}

internal sealed class IgCursors
{
    [JsonPropertyName("after")] public string? After { get; set; }
}
