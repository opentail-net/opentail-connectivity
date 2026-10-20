namespace OpenTail.Connectivity.Tests;

public sealed class SqliteSourceConnectorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
    private readonly SqlConnectorConfig _config;

    public SqliteSourceConnectorTests()
    {
        _config = new SqlConnectorConfig(
            ConnectionString: $"Data Source={_dbPath}",
            TableOrQuery: "articles",
            IdField: "id",
            TitleField: "title",
            BodyFields: ["body", "summary"],
            ModifiedField: "updated_at",
            MetadataFields: ["author"]);

        using var conn = new SqliteConnection(_config.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE articles (
                id TEXT PRIMARY KEY,
                title TEXT,
                body TEXT,
                summary TEXT,
                author TEXT,
                updated_at TEXT
            );
            INSERT INTO articles VALUES ('1','First','Body one','Summary one','Alice','2026-01-01T00:00:00Z');
            INSERT INTO articles VALUES ('2','Second','Body two','Summary two','Bob','2026-02-01T00:00:00Z');
            """;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public async Task DiscoverAsync_returns_all_rows()
    {
        var connector = new SqliteSourceConnector(_config, "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.DisplayName == "First");
        Assert.Contains(items, i => i.DisplayName == "Second");
    }

    [Fact]
    public async Task Query_cannot_modify_database()
    {
        var config = _config with { IsQuery = true, TableOrQuery = "INSERT INTO articles VALUES ('3','Third','Body','Summary','Eve','2026-03-01T00:00:00Z')" };
        var connector = new SqliteSourceConnector(config, "tenant");

        await Assert.ThrowsAsync<SqliteException>(async () => await connector.DiscoverAsync().ToListAsync());

        using var conn = new SqliteConnection(_config.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM articles";
        Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task Read_write_connection_string_is_refused()
    {
        var config = _config with { ConnectionString = $"Data Source={_dbPath};Mode=ReadWrite" };
        var connector = new SqliteSourceConnector(config, "tenant");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await connector.DiscoverAsync().ToListAsync());
    }

    [Fact]
    public async Task DiscoverAsync_skips_null_ids_and_discovered_rows_can_be_fetched()
    {
        using (var conn = new SqliteConnection(_config.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE nullable_articles (id TEXT, title TEXT, body TEXT, updated_at TEXT); INSERT INTO nullable_articles VALUES (NULL, 'No ID', 'Unfetchable body', '2026-03-01T00:00:00Z'); INSERT INTO nullable_articles VALUES ('normal', 'Normal', 'Fetchable body', '2026-03-02T00:00:00Z');";
            cmd.ExecuteNonQuery();
        }
        var config = _config with { TableOrQuery = "nullable_articles", BodyFields = ["body"], MetadataFields = null };
        var connector = new SqliteSourceConnector(config, "tenant");

        var items = await connector.DiscoverAsync().ToListAsync();

        var item = Assert.Single(items);
        Assert.Equal("normal", item.Id);
        var bytes = await connector.FetchAsync(item);
        var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(Encoding.UTF8.GetString(bytes))!;
        Assert.Equal("Fetchable body", row["body"]);
    }

    [Fact]
    public async Task Incremental_discovery_skips_null_ids_without_losing_batch_rows()
    {
        using (var conn = new SqliteConnection(_config.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE nullable_articles (id TEXT, title TEXT, body TEXT, updated_at TEXT); INSERT INTO nullable_articles VALUES (NULL, 'No ID', 'Body', '2026-03-01T00:00:00Z'); INSERT INTO nullable_articles VALUES ('a', 'A', 'Body A', '2026-03-01T00:00:00Z'); INSERT INTO nullable_articles VALUES ('b', 'B', 'Body B', '2026-03-01T00:00:00Z');";
            cmd.ExecuteNonQuery();
        }
        var config = _config with { TableOrQuery = "nullable_articles", BodyFields = ["body"], MetadataFields = null };
        var connector = new SqliteSourceConnector(config, "tenant", incrementalBatchSize: 1);

        var changes = await connector.GetChangesAsync(null).ToListAsync();

        Assert.Equal(["a", "b"], changes.OfType<SourceChange.Upsert>().Select(change => change.Item.Id));
    }

    [Fact]
    public async Task FetchAsync_returns_json_with_configured_fields_only()
    {
        var connector = new SqliteSourceConnector(_config, "tenant");
        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        var bytes = await connector.FetchAsync(items[0]);
        var json = Encoding.UTF8.GetString(bytes);
        var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;

        Assert.True(row.ContainsKey("body"));
        Assert.True(row.ContainsKey("title"));
        Assert.True(row.ContainsKey("author"));
    }

    [Fact]
    public void SqlRowTextExtractor_concatenates_body_fields()
    {
        var extractor = new SqlRowTextExtractor(_config);
        var row = new Dictionary<string, string?> { ["body"] = "Body text", ["summary"] = "Summary text" };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row));
        var result = extractor.Extract("row", bytes);

        Assert.True(result.Success);
        Assert.Contains("Body text", result.Text);
        Assert.Contains("Summary text", result.Text);
    }

    [Fact]
    public void SqlRowTextExtractor_maps_metadata_fields()
    {
        var extractor = new SqlRowTextExtractor(_config);
        var row = new Dictionary<string, string?> { ["body"] = "Body", ["author"] = "Alice" };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(row));
        var result = extractor.Extract("row", bytes);

        Assert.True(result.Metadata!.ContainsKey("row.author"));
        Assert.Equal("Alice", result.Metadata["row.author"]);
    }

    [Fact]
    public void SqlRowTextExtractor_returns_failure_for_invalid_json()
    {
        var extractor = new SqlRowTextExtractor(_config);
        var result = extractor.Extract("row", "not json"u8.ToArray());
        Assert.False(result.Success);
    }
}
