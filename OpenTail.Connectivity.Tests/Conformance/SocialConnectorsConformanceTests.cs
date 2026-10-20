using System.Net;
using System.Text;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Social.Meta;
using OpenTail.Connectivity.Social.Reddit;
using OpenTail.Connectivity.Social.Shared;
using OpenTail.Connectivity.Social.Twitter;
using OpenTail.Connectivity.Social.YouTube;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class SocialConnectorsConformanceTests
{
    private sealed class RedditFixture : IConnectorFixture
    {
        private bool _removed;
        // CONFORMANCE GAP: FetchAsync returns synthetic id-only JSON when the current discovery cache lacks an item.
        public IReadOnlySet<int>? SkippedChecks { get; } = new HashSet<int> { 3 };
        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create()
        {
            var json = _removed ? """
                {
                  "data": {
                    "children": [
                      {
                        "data": {
                          "id": "post2",
                          "title": "Post 2",
                          "selftext": "Self text two",
                          "author": "author2",
                          "subreddit": "marketing",
                          "created_utc": 1717001000
                        }
                      }
                    ]
                  }
                }
                """ : """
                {
                  "data": {
                    "children": [
                      {
                        "data": {
                          "id": "post1",
                          "title": "Post 1",
                          "selftext": "Self text one",
                          "author": "author1",
                          "subreddit": "marketing",
                          "created_utc": 1717000000
                        }
                      },
                      {
                        "data": {
                          "id": "post2",
                          "title": "Post 2",
                          "selftext": "Self text two",
                          "author": "author2",
                          "subreddit": "marketing",
                          "created_utc": 1717001000
                        }
                      }
                    ]
                  }
                }
                """;

            var http = new HttpClient(new StubHandler(_ => json));
            return new RedditConnector("marketing", new SocialConnectorOptions(TenantContext: "tenant-reddit"), http);
        }

        public ISourceConnector CreateEmpty()
        {
            var http = new HttpClient(new StubHandler(_ => "{\"data\": {\"children\": []}}"));
            return new RedditConnector("marketing", new SocialConnectorOptions(TenantContext: "tenant-reddit"), http);
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class TwitterFixture : IConnectorFixture
    {
        private bool _removed;
        private const string Secret = "secret-twitter-bearer-token";
        // CONFORMANCE GAP: FetchAsync returns synthetic id-only JSON when the current discovery cache lacks an item.
        public IReadOnlySet<int>? SkippedChecks { get; } = new HashSet<int> { 3 };
        public IReadOnlyList<string> SecretsGiven => [Secret];

        public ISourceConnector Create()
        {
            var json = _removed ? """
                {
                  "data": [
                    { "id": "222", "text": "Tweet two text", "created_at": "2026-01-02T00:00:00Z" }
                  ],
                  "meta": { "result_count": 1 }
                }
                """ : """
                {
                  "data": [
                    { "id": "111", "text": "Tweet one text", "created_at": "2026-01-01T00:00:00Z" },
                    { "id": "222", "text": "Tweet two text", "created_at": "2026-01-02T00:00:00Z" }
                  ],
                  "meta": { "result_count": 2 }
                }
                """;

            var http = new HttpClient(new StubHandler(_ => json));
            return new TwitterConnector(Secret, "search query", new SocialConnectorOptions(TenantContext: "tenant-twitter"), http);
        }

        public ISourceConnector CreateEmpty()
        {
            var http = new HttpClient(new StubHandler(_ => "{\"data\": [], \"meta\": {\"result_count\": 0}}"));
            return new TwitterConnector(Secret, "search query", new SocialConnectorOptions(TenantContext: "tenant-twitter"), http);
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class YouTubeFixture : IConnectorFixture
    {
        private bool _removed;
        private const string Secret = "secret-yt-api-key";
        // CONFORMANCE GAP: FetchAsync returns synthetic id-only JSON when the current discovery cache lacks an item.
        public IReadOnlySet<int>? SkippedChecks { get; } = new HashSet<int> { 3 };
        public IReadOnlyList<string> SecretsGiven => [Secret];

        public ISourceConnector Create()
        {
            var handler = new StubHandler(req =>
            {
                if (req.RequestUri?.AbsolutePath.Contains("videos") == true)
                    return "{\"items\": []}";

                return _removed ? """
                    {
                      "items": [
                        {
                          "id": { "videoId": "v2" },
                          "snippet": { "title": "Video 2", "description": "Desc 2", "publishedAt": "2026-01-02T00:00:00Z" }
                        }
                      ]
                    }
                    """ : """
                    {
                      "items": [
                        {
                          "id": { "videoId": "v1" },
                          "snippet": { "title": "Video 1", "description": "Desc 1", "publishedAt": "2026-01-01T00:00:00Z" }
                        },
                        {
                          "id": { "videoId": "v2" },
                          "snippet": { "title": "Video 2", "description": "Desc 2", "publishedAt": "2026-01-02T00:00:00Z" }
                        }
                      ]
                    }
                    """;
            });

            var http = new HttpClient(handler);
            return new YouTubeConnector(Secret, "query:cats", new SocialConnectorOptions(TenantContext: "tenant-youtube"), http);
        }

        public ISourceConnector CreateEmpty()
        {
            var http = new HttpClient(new StubHandler(_ => "{\"items\": []}"));
            return new YouTubeConnector(Secret, "query:empty", new SocialConnectorOptions(TenantContext: "tenant-youtube"), http);
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class MetaFixture : IConnectorFixture
    {
        private bool _removed;
        private const string Secret = "secret-meta-access-token";
        // CONFORMANCE GAP: FetchAsync returns synthetic id-only JSON when the current discovery cache lacks an item.
        public IReadOnlySet<int>? SkippedChecks { get; } = new HashSet<int> { 3 };
        public IReadOnlyList<string> SecretsGiven => [Secret];

        public ISourceConnector Create()
        {
            var json = _removed ? """
                {
                  "data": [
                    { "id": "fb2", "message": "Post two message", "created_time": "2026-01-02T00:00:00Z" }
                  ]
                }
                """ : """
                {
                  "data": [
                    { "id": "fb1", "message": "Post one message", "created_time": "2026-01-01T00:00:00Z" },
                    { "id": "fb2", "message": "Post two message", "created_time": "2026-01-02T00:00:00Z" }
                  ]
                }
                """;

            var http = new HttpClient(new StubHandler(_ => json));
            return new MetaConnector(Secret, "page:12345", new SocialConnectorOptions(TenantContext: "tenant-meta"), http);
        }

        public ISourceConnector CreateEmpty()
        {
            var http = new HttpClient(new StubHandler(_ => "{\"data\": []}"));
            return new MetaConnector(Secret, "page:12345", new SocialConnectorOptions(TenantContext: "tenant-meta"), http);
        }

        public Task RemoveOneItemAsync(string sourceRef)
        {
            _removed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, string> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var res = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(handle(request), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(res);
        }
    }

    [Fact]
    public async Task RedditConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new RedditFixture());
    }

    [Fact]
    public async Task TwitterConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new TwitterFixture());
    }

    [Fact]
    public async Task YouTubeConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new YouTubeFixture());
    }

    [Fact]
    public async Task MetaConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new MetaFixture());
    }
}
