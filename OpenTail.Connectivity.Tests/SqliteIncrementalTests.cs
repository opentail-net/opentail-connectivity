namespace OpenTail.Connectivity.Tests;

/// <summary>The first real connector on the sync core.</summary>
public sealed class SqliteIncrementalTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "otail-inc-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _state = Path.Combine(Path.GetTempPath(), "otail-inc-" + Guid.NewGuid().ToString("N") + ".json");

    public SqliteIncrementalTests() => Exec("""
        CREATE TABLE tickets (id TEXT PRIMARY KEY, subject TEXT, body TEXT, updated TEXT);
        INSERT INTO tickets VALUES ('t1','One',  'body one is long enough to pass validation easily ok','2026-01-01T00:00:00Z');
        INSERT INTO tickets VALUES ('t2','Two',  'body two is long enough to pass validation easily ok','2026-01-02T00:00:00Z');
        INSERT INTO tickets VALUES ('t3','Three','body three is long enough to pass validation easily','2026-01-02T00:00:00Z');
        INSERT INTO tickets VALUES ('t4','Four', 'body four is long enough to pass validation easily ok','2026-01-03T00:00:00Z');
        INSERT INTO tickets VALUES ('t5','Five', 'body five is long enough to pass validation easily ok',NULL);
        """);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
        try { File.Delete(_state); } catch (IOException) { }
    }

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={_db}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class Collect : IConnectivityExporter
    {
        public List<string> Refs { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) { Refs.Add(document.SourceRef); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private SqlConnectorConfig Config => new($"Data Source={_db}", "tickets", "id", "subject", ["body"], ModifiedField: "updated");

    private async Task<(List<string> Refs, IngestionReport Report)> Run(int batch = 2, int? cap = null)
    {
        var collect = new Collect();
        var connector = new SqliteSourceConnector(Config, "acme", batch);
        var report = await new ConnectivityPipeline(connector, new SqlRowTextExtractor(Config), [collect],
            new PipelineOptions(MaxItemsProcessed: cap, SyncStore: new FileSyncCursorStore(_state), Sync: new SyncOptions(CheckpointEveryItems: 1)), "sql").RunAsync();
        return (collect.Refs, report);
    }

    [Fact]
    public async Task A_full_run_reads_in_batches_and_the_next_run_reads_only_what_changed()
    {
        var (first, report) = await Run();
        Assert.Equal(["t1", "t2", "t3", "t4"], first); // t5 has no modified value
        Assert.True(report.FullRun);
        Assert.True(report.SyncCommitted);

        Exec("""
            UPDATE tickets SET updated = '2026-02-01T00:00:00Z' WHERE id = 't2';
            INSERT INTO tickets VALUES ('t6','Six','body six is long enough to pass validation easily ok','2026-02-02T00:00:00Z');
            """);
        var (second, again) = await Run();
        Assert.False(again.FullRun);
        // The run resumes strictly after (t4's modified value, t4): nothing already read comes round again.
        Assert.Equal(["t2", "t6"], second);
    }

    [Fact]
    public async Task A_capped_run_inside_a_group_of_equal_modified_values_carries_on_next_time()
    {
        // The old cursor held only the modified value, so each capped run re-read t1 and never got further.
        Exec("UPDATE tickets SET updated = '2026-01-05T00:00:00Z' WHERE id IN ('t1','t2','t3','t4')");
        var seen = new List<string>();
        for (var run = 0; run < 4; run++)
            seen.AddRange((await Run(batch: 1, cap: 1)).Refs);

        Assert.Equal(["t1", "t2", "t3", "t4"], seen);
        Assert.Empty((await Run(batch: 1, cap: 1)).Refs);
    }

    [Fact]
    public async Task A_cursor_without_an_id_resumes_at_its_modified_value_and_is_rewritten()
    {
        var store = new FileSyncCursorStore(_state);
        var scope = new SqliteSourceConnector(Config, "acme", 2).SyncScope;
        await store.SaveAsync(scope, """{"Type":"text","Text":"2026-01-02T00:00:00Z"}""", 0);

        var (refs, _) = await Run();

        Assert.Equal(["t2", "t3", "t4"], refs);
        Assert.Contains("\"IdText\":\"t4\"", (await new FileSyncCursorStore(_state).LoadAsync(scope))!.Cursor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_sharing_a_modified_value_across_a_batch_boundary_are_all_read()
    {
        Exec("UPDATE tickets SET updated = '2026-01-05T00:00:00Z' WHERE id IN ('t1','t2','t3','t4')");
        var (refs, _) = await Run(batch: 3);
        Assert.Equal(["t1", "t2", "t3", "t4"], refs);
    }

    [Fact]
    public async Task Full_incremental_run_skips_empty_string_ids()
    {
        Exec("INSERT INTO tickets VALUES ('','Empty id','body is long enough to pass validation easily','2026-01-01T12:00:00Z')");

        var (refs, report) = await Run();

        Assert.Equal(["t1", "t2", "t3", "t4"], refs);
        Assert.Equal(4, report.ItemsDiscovered);
        Assert.Contains("t1", refs);
    }

    [Fact]
    public async Task Incremental_integer_primary_key_ids_are_read()
    {
        Exec("CREATE TABLE numbered (id INTEGER PRIMARY KEY, subject TEXT, body TEXT, updated INTEGER); INSERT INTO numbered VALUES (1,'One','numbered event one is long enough to pass validation',100); INSERT INTO numbered VALUES (2,'Two','numbered event two is long enough to pass validation',200); INSERT INTO numbered VALUES (3,'Three','numbered event three is long enough to pass validation',300);");
        var config = new SqlConnectorConfig($"Data Source={_db}", "numbered", "id", "subject", ["body"], ModifiedField: "updated");
        var collect = new Collect();

        await new ConnectivityPipeline(new SqliteSourceConnector(config, "acme", 1), new SqlRowTextExtractor(config), [collect],
            new PipelineOptions(SyncStore: new FileSyncCursorStore(_state), Sync: new SyncOptions(CheckpointEveryItems: 1))).RunAsync();

        Assert.Equal(["1", "2", "3"], collect.Refs);
    }

    [Fact]
    public async Task Unix_second_columns_page_as_numbers()
    {
        Exec("""
            CREATE TABLE events (id TEXT PRIMARY KEY, subject TEXT, body TEXT, ts INTEGER);
            INSERT INTO events VALUES ('e1','A','event one is long enough to pass validation easily',900);
            INSERT INTO events VALUES ('e2','B','event two is long enough to pass validation easily',1000);
            INSERT INTO events VALUES ('e3','C','event three is long enough to pass validation easily',10000);
            """);
        var config = new SqlConnectorConfig($"Data Source={_db}", "events", "id", "subject", ["body"], ModifiedField: "ts");
        var collect = new Collect();
        await new ConnectivityPipeline(new SqliteSourceConnector(config, "acme", 1), new SqlRowTextExtractor(config), [collect],
            new PipelineOptions(SyncStore: new FileSyncCursorStore(_state), Sync: new SyncOptions(CheckpointEveryItems: 1))).RunAsync();
        Assert.Equal(["e1", "e2", "e3"], collect.Refs); // as text, "10000" would sort before "900"
    }

    [Fact]
    public async Task Incremental_runs_need_a_modified_field()
    {
        var noModified = new SqlConnectorConfig($"Data Source={_db}", "tickets", "id", "subject", ["body"]);
        var connector = new SqliteSourceConnector(noModified, "acme");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in connector.GetChangesAsync(null)) { }
        });
        Assert.StartsWith("sqlite:acme:", connector.SyncScope);
        Assert.Contains("/tickets#", connector.SyncScope);
    }

    [Fact]
    public void Scopes_differ_by_tenant_and_row_fields_and_stay_stable()
    {
        var baseConfig = new SqlConnectorConfig($"Data Source={_db}", "tickets", "id", "subject", ["body"], ModifiedField: "updated");
        string Scope(SqlConnectorConfig config, string tenant = "acme") => new SqliteSourceConnector(config, tenant).SyncScope;

        Assert.Equal(Scope(baseConfig), Scope(baseConfig));
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig, "other"));
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig with { IdField = "ticket_no" }));
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig with { ModifiedField = "created" }));
        // Fields that make the document: a change must not keep the old cursor, or rows not modified since keep stale documents.
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig with { TitleField = "title" }));
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig with { BodyFields = ["body", "notes"] }));
        Assert.NotEqual(Scope(baseConfig), Scope(baseConfig with { MetadataFields = ["status"] }));
        Assert.DoesNotContain("Password", Scope(baseConfig with { ConnectionString = $"Data Source={_db};Password=secret" }));
    }

    [Fact]
    public async Task Full_run_with_collect_source_refs_lists_all_seen_ids()
    {
        var db = Path.Combine(Path.GetTempPath(), "otail-refs-" + Guid.NewGuid().ToString("N") + ".db");
        var state = Path.Combine(Path.GetTempPath(), "otail-refs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var c = new SqliteConnection($"Data Source={db}"))
            {
                c.Open();
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE items (id TEXT PRIMARY KEY, subject TEXT, body TEXT, updated TEXT);
                    INSERT INTO items VALUES ('r1', 'Row 1', 'Content for row one long enough to validate ok', '2026-01-01T00:00:00Z');
                    INSERT INTO items VALUES ('r2', 'Row 2', 'Content for row two long enough to validate ok', '2026-01-02T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            var config = new SqlConnectorConfig($"Data Source={db}", "items", "id", "subject", ["body"], ModifiedField: "updated");
            var collect = new Collect();
            var connector = new SqliteSourceConnector(config, "acme", 2);
            var report = await new ConnectivityPipeline(connector, new SqlRowTextExtractor(config), [collect],
                new PipelineOptions(SyncStore: new FileSyncCursorStore(state), Sync: new SyncOptions(CheckpointEveryItems: 1, CollectSourceRefs: true)), "sql").RunAsync();

            Assert.Equal(["r1", "r2"], report.SeenSourceRefs);
            Assert.False(report.SourceRefsTruncated);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch (IOException) { }
            try { File.Delete(state); } catch (IOException) { }
        }
    }
}
