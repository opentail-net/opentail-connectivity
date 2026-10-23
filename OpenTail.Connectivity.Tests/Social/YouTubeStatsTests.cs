using System.Net;
using OpenTail.Connectivity.Social.Shared;
using OpenTail.Connectivity.Social.YouTube;

namespace OpenTail.Connectivity.Tests.Social;

/// <summary>Counts the statistics call could not read are unknown, not zero.</summary>
public sealed class YouTubeStatsTests
{
    private const string Search = """
        { "items": [ { "id": { "videoId": "v1" }, "snippet": { "title": "Video 1", "description": "Desc 1", "publishedAt": "2026-01-01T00:00:00Z" } } ] }
        """;

    private sealed class Handler(HttpStatusCode statsStatus, string statsBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(request.RequestUri!.AbsolutePath.Contains("videos")
                ? new HttpResponseMessage(statsStatus) { Content = new StringContent(statsBody) }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Search) });
    }

    private static async Task<IReadOnlyDictionary<string, string>> MetadataAsync(HttpStatusCode statsStatus, string statsBody)
    {
        using var connector = new YouTubeConnector("key", "query:cats", new SocialConnectorOptions(TenantContext: "t"),
            new HttpClient(new Handler(statsStatus, statsBody)));
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync(TestContext.Current.CancellationToken)) items.Add(item);
        var bytes = await connector.FetchAsync(Assert.Single(items), TestContext.Current.CancellationToken);
        var result = new YouTubeTextExtractor().Extract("v1.json", bytes);
        Assert.True(result.Success);
        return result.Metadata!;
    }

    [Fact]
    public async Task A_failed_statistics_call_leaves_the_counts_out_instead_of_writing_zeros()
    {
        var meta = await MetadataAsync(HttpStatusCode.Forbidden, "quota");

        Assert.Equal("unavailable", meta["social.stats"]);
        Assert.False(meta.ContainsKey("social.likes"));
        Assert.False(meta.ContainsKey("social.comments"));
        Assert.False(meta.ContainsKey("social.engagement_score"));
    }

    [Fact]
    public async Task Read_statistics_are_written_as_before()
    {
        var meta = await MetadataAsync(HttpStatusCode.OK,
            """{ "items": [ { "id": "v1", "statistics": { "viewCount": "10", "likeCount": "3", "commentCount": "0" } } ] }""");

        Assert.Equal("3", meta["social.likes"]);
        Assert.Equal("0", meta["social.comments"]); // a real zero stays
        Assert.Equal("10", meta["social.views"]);
        Assert.False(meta.ContainsKey("social.stats"));
    }
}
