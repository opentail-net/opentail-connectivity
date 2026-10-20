namespace OpenTail.Connectivity.Tests;

public sealed class RssFeedConnectorTests
{
    private static HttpClient MakeClient(string feedXml) =>
        new HttpClient(new FeedHttpHandler(feedXml));

    private static readonly string Rss2Feed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <title>Test Feed</title>
            <item>
              <title>Item One</title>
              <link>https://example.com/1</link>
              <description>Description of item one</description>
              <author>alice@example.com</author>
              <category>Tech</category>
              <pubDate>Mon, 01 Jan 2026 10:00:00 +0000</pubDate>
            </item>
            <item>
              <title>Item Two</title>
              <link>https://example.com/2</link>
              <description>Description of item two</description>
              <pubDate>Tue, 02 Jan 2026 10:00:00 +0000</pubDate>
            </item>
            <item>
              <title>Item Three</title>
              <link>https://example.com/3</link>
              <description>Third item</description>
              <pubDate>Wed, 03 Jan 2026 10:00:00 +0000</pubDate>
            </item>
          </channel>
        </rss>
        """;

    private static readonly string AtomFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>Atom Test Feed</title>
          <entry>
            <title>Atom Entry One</title>
            <link href="https://atom.example.com/1"/>
            <summary>Summary of atom entry one</summary>
            <author><name>Bob</name></author>
            <published>2026-02-01T12:00:00Z</published>
          </entry>
          <entry>
            <title>Atom Entry Two</title>
            <link href="https://atom.example.com/2"/>
            <summary>Summary of atom entry two</summary>
            <published>2026-02-02T12:00:00Z</published>
          </entry>
        </feed>
        """;

    [Fact]
    public async Task Rss2_DiscoverAsync_ReturnsAllItems()
    {
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task Atom_DiscoverAsync_ReturnsAllEntries()
    {
        var connector = new RssFeedConnector(MakeClient(AtomFeed), "http://feed.test/atom", "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Rss2_TitleExtractedCorrectly()
    {
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal("Item One", items[0].DisplayName);
    }

    [Fact]
    public async Task Rss2_DescriptionUsedAsBody()
    {
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        var bytes = await connector.FetchAsync(items[0]);
        var extractor = new RssFeedTextExtractor();
        var result = extractor.Extract("rss-item", bytes);

        Assert.True(result.Success);
        Assert.Contains("Description of item one", result.Text);
    }

    [Fact]
    public async Task Rss2_PubDateParsedAsModified()
    {
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        var expected = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, items[0].Modified);
    }

    [Fact]
    public async Task MaxItems_LimitsItems()
    {
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant", maxItems: 2);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task Since_FilterExcludesOldItems()
    {
        var cutoff = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var connector = new RssFeedConnector(MakeClient(Rss2Feed), "http://feed.test/rss", "tenant", since: cutoff);
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        // Only items on/after 2026-01-02 should be included
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.Modified >= cutoff));
    }

    private sealed class FeedHttpHandler : HttpMessageHandler
    {
        private readonly string _xml;
        public FeedHttpHandler(string xml) => _xml = xml;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_xml, Encoding.UTF8, "application/rss+xml")
            });
    }
}
