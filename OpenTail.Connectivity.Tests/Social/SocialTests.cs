using System.Net;
using OpenTail.Connectivity.Social.Reddit;
using OpenTail.Connectivity.Social.Sentiment;
using OpenTail.Connectivity.Social.Shared;

namespace OpenTail.Connectivity.Tests.Social;

public sealed class SentimentScorerTests
{
    [Fact]
    public void Score_PositiveText_ReturnsPositive()
    {
        var score = SentimentScorer.Score("This product is amazing and wonderful. I love it!");
        Assert.True(score > 0.1, $"Expected positive, got {score}");
    }

    [Fact]
    public void Score_NegativeText_ReturnsNegative()
    {
        var score = SentimentScorer.Score("Terrible service. I hate this awful product. Complete disaster.");
        Assert.True(score < -0.1, $"Expected negative, got {score}");
    }

    [Fact]
    public void Score_NeutralText_ReturnsNeutral()
    {
        var score = SentimentScorer.Score("The package arrived on Tuesday in a brown box.");
        Assert.InRange(score, -0.1, 0.1);
    }

    [Fact]
    public void Score_Negation_FlipsPositiveToNegative()
    {
        var withNegation = SentimentScorer.Score("This is not good and not great.");
        var without = SentimentScorer.Score("This is good and great.");
        Assert.True(withNegation < without, "Negation should reduce sentiment.");
    }

    [Fact]
    public void Label_ThresholdsCorrect()
    {
        Assert.Equal("positive", SentimentScorer.Label(0.5));
        Assert.Equal("negative", SentimentScorer.Label(-0.5));
        Assert.Equal("neutral", SentimentScorer.Label(0.0));
        Assert.Equal("neutral", SentimentScorer.Label(0.05));
    }

    [Fact]
    public void EngagementScore_HighValues_CapsAtOne()
    {
        var score = MarketingMetadata.EngagementScore(10000, 10000, 10000);
        Assert.Equal(1.0, score);
    }
}

public sealed class MarketingMetadataTests
{
    [Fact]
    public void Build_PopulatesRequiredKeys()
    {
        var meta = MarketingMetadata.Build(
            platform: "reddit",
            postId: "abc123",
            author: "u/testuser",
            authorId: null,
            url: "https://reddit.com/r/test/abc123",
            publishedAt: DateTimeOffset.UtcNow,
            likes: 42,
            shares: 5,
            comments: 8,
            hashtags: "#marketing",
            mentions: "@brand",
            language: "en",
            mediaType: "text",
            bodyText: "This is an amazing product launch!");

        Assert.Equal("reddit", meta["social.platform"]);
        Assert.Equal("abc123", meta["social.post_id"]);
        Assert.True(meta.ContainsKey("social.sentiment_score"));
        Assert.True(meta.ContainsKey("social.sentiment_label"));
        Assert.True(meta.ContainsKey("social.engagement_score"));
        Assert.Equal("positive", meta["social.sentiment_label"]);
    }

    [Fact]
    public void ExtractHashtags_ReturnsCommaSeparated()
    {
        var tags = MarketingMetadata.ExtractHashtags("Check out #marketing and #brandstrategy today!");
        Assert.Contains("#marketing", tags);
        Assert.Contains("#brandstrategy", tags);
    }

    [Fact]
    public void ExtractMentions_ReturnsCommaSeparated()
    {
        var mentions = MarketingMetadata.ExtractMentions("Thanks @company and @ceo for the update!");
        Assert.Contains("@company", mentions);
        Assert.Contains("@ceo", mentions);
    }
}

public sealed class RedditConnectorTests
{
    private static HttpClient MockHttp(string subreddit, string? keyword = null)
    {
        var responseJson = $$"""
            {
              "data": {
                "after": null,
                "children": [
                  {
                    "data": {
                      "id": "post1",
                      "name": "t3_post1",
                      "title": "Amazing marketing campaign launched today",
                      "selftext": "We are thrilled to announce our new product launch. Great results so far!",
                      "author": "marketingpro",
                      "subreddit": "{{subreddit}}",
                      "url": "https://reddit.com/r/{{subreddit}}/post1",
                      "permalink": "/r/{{subreddit}}/comments/post1/",
                      "created_utc": 1717000000,
                      "score": 150,
                      "ups": 150,
                      "num_comments": 23,
                      "is_video": false,
                      "stickied": false
                    }
                  },
                  {
                    "data": {
                      "id": "post2",
                      "name": "t3_post2",
                      "title": "Brand strategy update",
                      "selftext": "Some thoughts on brand positioning #branding #strategy",
                      "author": "brandexpert",
                      "subreddit": "{{subreddit}}",
                      "url": "https://reddit.com/r/{{subreddit}}/post2",
                      "permalink": "/r/{{subreddit}}/comments/post2/",
                      "created_utc": 1717001000,
                      "score": 42,
                      "ups": 42,
                      "num_comments": 5,
                      "is_video": false,
                      "stickied": false
                    }
                  }
                ]
              }
            }
            """;

        var handler = new MockHttpMessageHandler(responseJson);
        return new HttpClient(handler);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsPosts()
    {
        using var http = MockHttp("marketing");
        var connector = new RedditConnector("marketing", http: http);
        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Equal(2, items.Count);
        Assert.Equal("t3_post1".Length > 0 ? "reddit:marketing:post1" : "", items[0].Id);
        Assert.Equal("reddit:marketing:post1", items[0].Id);
        Assert.Equal(".reddit-post", items[0].Extension);
        Assert.Equal("", items[0].TenantContext);
    }

    [Theory]
    [InlineData("rust", "rust")]
    [InlineData("racing", "racing")]
    [InlineData("r/marketing", "marketing")]
    [InlineData("/r/marketing", "marketing")]
    [InlineData("/marketing", "marketing")]
    [InlineData("R/rust", "rust")]
    public async Task Constructor_NormalizesSubreddit(string input, string expected)
    {
        string? requestPath = null;
        using var http = new HttpClient(new CallbackHttpMessageHandler(request =>
        {
            requestPath = request.RequestUri!.AbsolutePath;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"children\":[]}}") };
        }));
        var connector = new RedditConnector(input, http: http);

        await connector.DiscoverAsync().ToListAsync();

        Assert.Equal($"/r/{expected}/new.json", requestPath);
    }

    [Fact]
    public void Constructor_RejectsEmptyNormalizedSubreddit()
    {
        using var http = MockHttp("marketing");
        Assert.Throws<ArgumentException>(() => new RedditConnector("r/", http: http));
    }

    [Fact]
    public async Task DiscoverAsync_SetsTheTenantFromOptions()
    {
        using var http = MockHttp("marketing");
        var connector = new RedditConnector("marketing", new SocialConnectorOptions(TenantContext: "acme"), http);
        var items = await connector.DiscoverAsync().ToListAsync();
        Assert.All(items, i => Assert.Equal("acme", i.TenantContext));
    }

    [Fact]
    public async Task FetchAsync_ReturnsJsonWithPost()
    {
        using var http = MockHttp("marketing");
        var connector = new RedditConnector("marketing", http: http);
        var items = await connector.DiscoverAsync().ToListAsync();
        var bytes = await connector.FetchAsync(items[0]);

        Assert.True(bytes.Length > 0);
        using var doc = JsonDocument.Parse(bytes);
        Assert.Equal("post1", doc.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public void RedditTextExtractor_ProducesSentimentMetadata()
    {
        var post = new RedditPost
        {
            Id = "post1",
            Title = "Amazing new product launch — great results!",
            Selftext = "We are so thrilled and excited. Fantastic campaign!",
            Author = "user1",
            Subreddit = "marketing",
            Permalink = "/r/marketing/comments/post1/",
            Score = 200,
            NumComments = 15,
            CreatedUtc = 1717000000
        };

        var extractor = new RedditTextExtractor();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(post);
        var result = extractor.Extract("post1.reddit-post", bytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Metadata);
        Assert.Equal("reddit", result.Metadata!["social.platform"]);
        Assert.True(result.Metadata.ContainsKey("social.sentiment_score"));
        Assert.Equal("positive", result.Metadata["social.sentiment_label"]);
        Assert.Equal("marketing", result.Metadata["social.subreddit"]);
    }

    [Fact]
    public async Task DiscoverAsync_SkipsStickiedPosts()
    {
        var json = """
            {
              "data": {
                "after": null,
                "children": [
                  {"data": {"id":"s1","name":"t3_s1","title":"Rules","selftext":"","author":"mod","subreddit":"marketing","url":"","permalink":"","created_utc":1717000000,"score":0,"ups":0,"num_comments":0,"is_video":false,"stickied":true}},
                  {"data": {"id":"p1","name":"t3_p1","title":"Real post","selftext":"content","author":"user","subreddit":"marketing","url":"","permalink":"","created_utc":1717000000,"score":10,"ups":10,"num_comments":2,"is_video":false,"stickied":false}}
                ]
              }
            }
            """;
        var handler = new MockHttpMessageHandler(json);
        using var http = new HttpClient(handler);
        var connector = new RedditConnector("marketing", http: http);
        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Single(items);
        Assert.Equal("reddit:marketing:p1", items[0].Id);
    }

    [Fact]
    public async Task DiscoverAsync_RespectsSinceFilter()
    {
        using var http = MockHttp("marketing");
        var since = DateTimeOffset.FromUnixTimeSeconds(1717005000); // after both posts
        var opts = new SocialConnectorOptions(Since: since);
        var connector = new RedditConnector("marketing", options: opts, http: http);
        var items = await connector.DiscoverAsync().ToListAsync();

        Assert.Empty(items);
    }
}

file sealed class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly string _responseBody;
    public MockHttpMessageHandler(string responseBody) => _responseBody = responseBody;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
        });
}

file sealed class CallbackHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(callback(request));
}
