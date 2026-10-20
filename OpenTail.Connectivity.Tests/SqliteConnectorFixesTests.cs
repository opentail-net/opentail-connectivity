using System.Globalization;

namespace OpenTail.Connectivity.Tests;

public sealed class SqliteConnectorFixesTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "otail-sqlfix-" + Guid.NewGuid().ToString("N") + ".db");

    public SqliteConnectorFixesTests()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE "odd""name" ("id" TEXT PRIMARY KEY, "ti""tle" TEXT, "body" TEXT, "price" REAL, "updated" TEXT, "unix" INTEGER);
            INSERT INTO "odd""name" VALUES ('1', 'One', 'b1', 1.5, 'not a date', 1767225600);
            INSERT INTO "odd""name" VALUES ('2', 'Two', 'b2', 2.25, '2026-03-04 05:06:07', NULL);
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private SqlConnectorConfig Config(string modifiedField) => new(
        ConnectionString: $"Data Source={_dbPath}",
        TableOrQuery: "odd\"name",
        IdField: "id",
        TitleField: "ti\"tle",
        BodyFields: ["body"],
        ModifiedField: modifiedField,
        MetadataFields: ["price"]);

    private static async Task<List<SourceItem>> Discover(SqliteSourceConnector c)
    {
        var items = new List<SourceItem>();
        await foreach (var i in c.DiscoverAsync()) items.Add(i);
        return items;
    }

    [Fact]
    public async Task Table_and_field_names_with_quotes_are_escaped()
    {
        var items = await Discover(new SqliteSourceConnector(Config("updated"), "t"));
        Assert.Equal(["One", "Two"], items.Select(i => i.DisplayName));
    }

    [Fact]
    public async Task An_unreadable_modified_value_is_unknown_not_now_and_others_parse_as_utc()
    {
        var items = await Discover(new SqliteSourceConnector(Config("updated"), "t"));
        Assert.Equal(SqliteSourceConnector.UnknownModified, items[0].Modified);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), items[1].Modified);
    }

    [Fact]
    public async Task Unix_seconds_are_read_as_a_time()
    {
        var items = await Discover(new SqliteSourceConnector(Config("unix"), "t"));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), items[0].Modified);
        Assert.Equal(SqliteSourceConnector.UnknownModified, items[1].Modified); // NULL
    }

    [Fact]
    public void Social_scores_are_formatted_without_the_current_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var meta = OpenTail.Connectivity.Social.Shared.MarketingMetadata.Build(
                "reddit", "p1", null, null, null, DateTimeOffset.UnixEpoch, 500, 0, 0, null, null, null, null, "great");
            Assert.Equal("0.5000", meta["social.engagement_score"]);
            Assert.Contains('.', meta["social.sentiment_score"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public async Task Values_are_formatted_without_the_current_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var connector = new SqliteSourceConnector(Config("updated"), "t");
            var items = await Discover(connector);
            var payload = Encoding.UTF8.GetString(await connector.FetchAsync(items[1]));
            Assert.Contains("\"price\":\"2.25\"", payload);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
