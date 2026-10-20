namespace OpenTail.Connectivity.Tests.Csv;

public sealed class CsvSourceConnectorTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTempCsv(string content)
    {
        var path = Path.GetTempFileName() + ".csv";
        File.WriteAllText(path, content, Encoding.UTF8);
        _tempFiles.Add(path);
        return path;
    }

    private static CsvConnectorConfig BasicConfig(string[]? bodyColumns = null) =>
        new CsvConnectorConfig(
            IdColumn: "Id",
            BodyColumns: bodyColumns ?? ["Body"],
            TitleColumn: "Title",
            MetadataColumns: ["Category"]);

    [Fact]
    public void Constructor_ThrowsForNullConfig()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new CsvSourceConnector(null!, "file.csv", "tenant"));
    }

    [Fact]
    public void Constructor_ThrowsForNonExistentFile()
    {
        Assert.Throws<FileNotFoundException>(() =>
            new CsvSourceConnector(BasicConfig(), "nonexistent.csv", "tenant"));
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsCorrectCount()
    {
        var csv = "Id,Title,Body,Category\n1,Item One,Content one,A\n2,Item Two,Content two,B\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Equal("1", items[0].Id);
        Assert.Equal("Item One", items[0].DisplayName);
        Assert.Equal("csv-row", items[0].Extension);
    }

    [Fact]
    public async Task FetchAsync_ReturnsBodyConcatenation()
    {
        var csv = "Id,Title,Body,Category\n1,Item One,Hello world,A\n";
        var path = WriteTempCsv(csv);
        var config = BasicConfig(["Body"]);
        var connector = new CsvSourceConnector(config, path, "tenant");

        SourceItem item = default!;
        await foreach (var i in connector.DiscoverAsync()) item = i;

        var bytes = await connector.FetchAsync(item);
        var json = Encoding.UTF8.GetString(bytes);
        var row = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;

        var extractor = new CsvRowTextExtractor(config);
        var result = extractor.Extract("row.csv-row", bytes);

        Assert.True(result.Success);
        Assert.Contains("Hello world", result.Text);
    }

    [Fact]
    public async Task FetchAsync_PopulatesMetadata()
    {
        var csv = "Id,Title,Body,Category\n42,Foo,Bar text,Electronics\n";
        var path = WriteTempCsv(csv);
        var config = BasicConfig();
        var connector = new CsvSourceConnector(config, path, "tenant");

        SourceItem item = default!;
        await foreach (var i in connector.DiscoverAsync()) item = i;

        var bytes = await connector.FetchAsync(item);
        var extractor = new CsvRowTextExtractor(config);
        var result = extractor.Extract("row.csv-row", bytes);

        Assert.True(result.Success);
        Assert.NotNull(result.Metadata);
        Assert.Equal("Electronics", result.Metadata["row.Category"]);
    }

    [Fact]
    public async Task DiscoverAsync_HandlesQuotedFieldWithEmbeddedComma()
    {
        var csv = "Id,Title,Body,Category\n1,\"Item, One\",Content,A\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Single(items);
        Assert.Equal("Item, One", items[0].DisplayName);
    }

    [Fact]
    public async Task DiscoverAsync_ThrowsWhenIdColumnMissing()
    {
        var csv = "Name,Body\nFoo,Bar\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in connector.DiscoverAsync()) { }
        });

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("IdColumn 'Id'", ex.Message);
    }

    [Fact]
    public async Task DiscoverAsync_ThrowsWhenHeadersDifferOnlyByCase()
    {
        var csv = "Id,id,Title,Body,Category\n1,dup,Item,Body,A\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in connector.DiscoverAsync()) { }
        });

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("duplicates", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_ThrowsWhenConfiguredBodyColumnMissing()
    {
        var csv = "Id,Title,Category\n1,Item,A\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(["Body"]), path, "tenant");

        var ex = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in connector.DiscoverAsync()) { }
        });

        Assert.IsType<InvalidOperationException>(ex);
        Assert.Contains("BodyColumns 'Body'", ex.Message);
    }

    [Fact]
    public async Task DiscoverAsync_EmptyFileYieldsNoItems()
    {
        var csv = "Id,Title,Body,Category\n";
        var path = WriteTempCsv(csv);
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync())
            items.Add(item);

        Assert.Empty(items);
    }

    [Fact]
    public async Task Repeated_ids_keep_every_row_and_missing_ids_come_from_the_row_position()
    {
        var path = WriteTempCsv("Id,Title,Body,Category\n7,First,one,A\n7,Second,two,A\n,Third,three,B\n7,Fourth,four,A\n");
        var connector = new CsvSourceConnector(BasicConfig(), path, "tenant");

        var items = new List<SourceItem>();
        await foreach (var item in connector.DiscoverAsync()) items.Add(item);

        Assert.Equal(["7", "7#2", "row:3", "7#3"], items.Select(i => i.Id));
        var bodies = new List<string>();
        foreach (var item in items) bodies.Add(Encoding.UTF8.GetString(await connector.FetchAsync(item)));
        Assert.Contains("one", bodies[0]);
        Assert.Contains("two", bodies[1]);
        Assert.Contains("three", bodies[2]);
        Assert.Contains("four", bodies[3]);
    }

    [Fact]
    public async Task Modified_dates_are_read_without_the_culture_and_as_utc()
    {
        var path = WriteTempCsv("Id,Title,Body,Category,Updated\n1,T,b,A,2026-03-04 05:06:07\n");
        var connector = new CsvSourceConnector(BasicConfig() with { ModifiedColumn = "Updated" }, path, "tenant");
        await foreach (var item in connector.DiscoverAsync())
            Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero), item.Modified);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }
}
