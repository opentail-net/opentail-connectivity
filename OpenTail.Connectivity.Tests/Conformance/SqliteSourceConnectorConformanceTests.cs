using Microsoft.Data.Sqlite;
using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Sql;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class SqliteSourceConnectorConformanceTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), "otail-conf-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _emptyDb = Path.Combine(Path.GetTempPath(), "otail-conf-empty-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly SqlConnectorConfig _config;
    private readonly SqlConnectorConfig _emptyConfig;

    public SqliteSourceConnectorConformanceTests()
    {
        _config = new SqlConnectorConfig($"Data Source={_db}", "tickets", "id", "subject", ["body"], ModifiedField: "updated");
        _emptyConfig = new SqlConnectorConfig($"Data Source={_emptyDb}", "tickets", "id", "subject", ["body"], ModifiedField: "updated");

        using (var c = new SqliteConnection($"Data Source={_db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE tickets (id TEXT PRIMARY KEY, subject TEXT, body TEXT, updated TEXT);
                INSERT INTO tickets VALUES ('t1', 'One', 'Body one is long enough to validate ok', '2026-01-01T00:00:00Z');
                INSERT INTO tickets VALUES ('t2', 'Two', 'Body two is long enough to validate ok', '2026-01-02T00:00:00Z');
                INSERT INTO tickets VALUES ('t3', 'Three', 'Body three is long enough to validate ok', '2026-01-03T00:00:00Z');
                INSERT INTO tickets VALUES ('t4', 'Four', 'Body four is long enough to validate ok', '2026-01-04T00:00:00Z');
                """;
            cmd.ExecuteNonQuery();
        }

        using (var c = new SqliteConnection($"Data Source={_emptyDb}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE tickets (id TEXT PRIMARY KEY, subject TEXT, body TEXT, updated TEXT);
                """;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_db); } catch (IOException) { }
        try { File.Delete(_emptyDb); } catch (IOException) { }
    }

    private sealed class Fixture(SqlConnectorConfig config, SqlConnectorConfig emptyConfig, string db) : IConnectorFixture
    {
        public IReadOnlyList<string> SecretsGiven => [];
        public ISourceConnector Create() => new SqliteSourceConnector(config, "tenant-sql", incrementalBatchSize: 2);
        public ISourceConnector CreateEmpty() => new SqliteSourceConnector(emptyConfig, "tenant-sql", incrementalBatchSize: 2);
        public Task RemoveOneItemAsync(string sourceRef)
        {
            using var c = new SqliteConnection($"Data Source={db}");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM tickets WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", sourceRef);
            cmd.ExecuteNonQuery();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SqliteSourceConnector_passes_conformance()
    {
        await SourceConnectorConformance.RunAsync(new Fixture(_config, _emptyConfig, _db));
    }
}
