using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace OpenTail.Connectivity.Memory.Sqlite;

/// <summary>
/// A memory backend in one SQLite file. Keyword search through FTS5 (bm25). Every <see cref="MemoryGovernance"/> rule is applied
/// in SQL before ranking, so top-k is taken from what the caller may see; <see cref="MemoryGovernance.Deny"/> then runs on each
/// hit as an exact final check. The record itself is stored as JSON and is the truth; the other columns and the FTS index are
/// derived from it and can be rebuilt.
/// </summary>
public sealed partial class SqliteMemoryBackend : IMemoryBackend
{
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    /// <param name="path">The database file. It is created when missing; its folder must exist (a backend never creates
    /// where it stores: a wrong path must fail, not start an empty store).</param>
    public SqliteMemoryBackend(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null && !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder for the memory database does not exist: {folder}");

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _time = time ?? TimeProvider.System;
        EnsureSchema();
    }

    public MemoryBackendCapabilities Capabilities { get; } = new(
        Search: MemorySearchKinds.Keyword,
        FiltersBeforeRanking: true,
        TemporalValidity: true,
        DeleteBySource: true);

    public async Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(c => !string.Equals(c.ParentRecordId, record.Id, StringComparison.Ordinal)))
            throw new ArgumentException("Every chunk must belong to the record.", nameof(chunks));

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DeleteRowsAsync(connection, tx, "id = $id", p => p.AddWithValue("$id", record.Id), cancellationToken).ConfigureAwait(false);

        var policy = record.Policy;
        var source = record.Source;
        await ExecAsync(connection, tx, """
            INSERT INTO records (id, document_id, collection, workspace, project, repository, permission_scope, allow_cross_workspace,
                trust, sensitivity_rank, tainted, derived, superseded, deleted_at, expires_at, observed_at,
                acl_present, acl_captured, acl_open, record_json)
            VALUES ($id, $doc, $collection, $workspace, $project, $repository, $scope, $cross,
                $trust, $sensitivity, $tainted, $derived, $superseded, $deleted, $expires, $observed,
                $aclPresent, $aclCaptured, $aclOpen, $json)
            """, p =>
        {
            p.AddWithValue("$id", record.Id);
            p.AddWithValue("$doc", (object?)source.DocumentId ?? DBNull.Value);
            p.AddWithValue("$collection", Norm(record.Collection));
            p.AddWithValue("$workspace", Norm(source.Workspace));
            p.AddWithValue("$project", Norm(source.ProjectScope));
            p.AddWithValue("$repository", Norm(source.RepositoryScope));
            p.AddWithValue("$scope", Norm(policy.PermissionScope));
            p.AddWithValue("$cross", policy.AllowCrossWorkspaceRetrieval ? 1 : 0);
            p.AddWithValue("$trust", (int)policy.TrustLevel);
            p.AddWithValue("$sensitivity", MemoryGovernance.SensitivityRank(policy.Sensitivity));
            p.AddWithValue("$tainted", policy.Tainted ? 1 : 0);
            p.AddWithValue("$derived", record.IsDerived ? 1 : 0);
            p.AddWithValue("$superseded", record.IsSuperseded ? 1 : 0);
            p.AddWithValue("$deleted", Ticks(record.DeletedAtUtc));
            p.AddWithValue("$expires", Ticks(policy.ExpiresAtUtc));
            p.AddWithValue("$observed", record.ObservedAtUtc.UtcTicks);
            p.AddWithValue("$aclPresent", source.Acl is null ? 0 : 1);
            p.AddWithValue("$aclCaptured", source.Acl?.Captured == true ? 1 : 0);
            p.AddWithValue("$aclOpen", IsOpen(source.Acl) ? 1 : 0);
            p.AddWithValue("$json", JsonSerializer.Serialize(record, Json));
        }, cancellationToken).ConfigureAwait(false);

        foreach (var principal in (source.Acl?.Principals ?? []).Distinct(StringComparer.Ordinal))
            await ExecAsync(connection, tx, "INSERT INTO record_principals (record_id, principal) VALUES ($id, $p)",
                p => { p.AddWithValue("$id", record.Id); p.AddWithValue("$p", principal); }, cancellationToken).ConfigureAwait(false);

        foreach (var claim in (policy.RequiredClaims ?? []).Distinct(StringComparer.Ordinal))
            await ExecAsync(connection, tx, "INSERT INTO record_claims (record_id, claim) VALUES ($id, $c)",
                p => { p.AddWithValue("$id", record.Id); p.AddWithValue("$c", claim); }, cancellationToken).ConfigureAwait(false);

        IEnumerable<(string Id, MemoryChunkKind Kind, string Text)> rows = chunks.Count > 0
            ? chunks.Select(c => (c.Id, c.Kind, c.Content))
            : [(record.Id, MemoryChunkKind.Unknown, record.Content)];
        foreach (var (id, kind, text) in rows)
        {
            await ExecAsync(connection, tx, "INSERT INTO chunks (id, record_id, kind, text) VALUES ($id, $rid, $kind, $text)", p =>
            {
                p.AddWithValue("$id", id);
                p.AddWithValue("$rid", record.Id);
                p.AddWithValue("$kind", kind.ToString());
                p.AddWithValue("$text", text);
            }, cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT record_json FROM records WHERE id = $id";
        command.Parameters.AddWithValue("$id", recordId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<MemoryRecord>(json, Json)
            : null;
    }

    public async Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deletion);
        var (where, bind) = deletion switch
        {
            { RecordId: { } id } => ("id = $v", id),
            { DocumentId: { } doc } => ("document_id = $v", doc),
            { Collection: { } col } => ("collection = $v", NormText(col)),
            _ => throw new ArgumentException("A deletion must name a record, a document or a collection.", nameof(deletion)),
        };

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var removed = await DeleteRowsAsync(connection, tx, where, p => p.AddWithValue("$v", bind), cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    public async Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var match = FtsQuery(search.Query);
        if (match is null || search.TopK <= 0)
            return [];

        var now = _time.GetUtcNow();
        var f = search.Filter;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder("""
            r.deleted_at IS NULL
            AND (r.expires_at IS NULL OR r.expires_at > $now)
            """);
        command.Parameters.AddWithValue("$now", now.UtcTicks);

        if (!f.IncludeSuperseded) where.Append(" AND r.superseded = 0");
        if (!f.IncludeDerived) where.Append(" AND r.derived = 0");
        if (!f.IncludeTainted) where.Append(" AND r.tainted = 0");

        if (f.Collections is { Count: > 0 })
            where.Append(" AND r.collection IN (").Append(Bind(command, "col", f.Collections.Select(NormText))).Append(')');

        // Every claim the record requires must be held by the caller.
        where.Append(" AND NOT EXISTS (SELECT 1 FROM record_claims rc WHERE rc.record_id = r.id");
        if (f.Claims is { Count: > 0 })
            where.Append(" AND rc.claim NOT IN (").Append(Bind(command, "claim", f.Claims)).Append(')');
        where.Append(')');

        where.Append(" AND (r.permission_scope IS NULL");
        if (f.Scopes is { Count: > 0 })
            where.Append(" OR r.permission_scope IN (").Append(Bind(command, "scope", f.Scopes.Select(NormText))).Append(')');
        where.Append(')');

        // Absence is not permission: a missing ACL, or one that was not captured, restricts.
        where.Append(" AND ((r.acl_captured = 1 AND (r.acl_open = 1");
        if (f.Principals is { Count: > 0 })
            where.Append(" OR EXISTS (SELECT 1 FROM record_principals rp WHERE rp.record_id = r.id AND rp.principal IN (")
                .Append(Bind(command, "principal", f.Principals)).Append("))");
        where.Append(")))");

        if (!string.IsNullOrWhiteSpace(f.Workspace))
        {
            where.Append(" AND (r.workspace = $workspace OR r.allow_cross_workspace = 1)");
            command.Parameters.AddWithValue("$workspace", Norm(f.Workspace));
        }

        if (!string.IsNullOrWhiteSpace(f.ProjectScope))
        {
            where.Append(" AND r.project = $project");
            command.Parameters.AddWithValue("$project", Norm(f.ProjectScope));
        }

        if (!string.IsNullOrWhiteSpace(f.RepositoryScope))
        {
            where.Append(" AND r.repository = $repository");
            command.Parameters.AddWithValue("$repository", Norm(f.RepositoryScope));
        }

        if (f.MinTrustLevel is { } min && min > MemoryTrustLevel.Low)
        {
            where.Append(" AND r.trust >= $trust");
            command.Parameters.AddWithValue("$trust", (int)min);
        }

        if (f.MaxSensitivity is { } max)
        {
            where.Append(" AND r.sensitivity_rank <= $sensitivity");
            command.Parameters.AddWithValue("$sensitivity", MemoryGovernance.SensitivityRank(max));
        }

        if (f.FromUtc is { } from)
        {
            where.Append(" AND r.observed_at >= $from");
            command.Parameters.AddWithValue("$from", from.UtcTicks);
        }

        if (f.ToUtc is { } to)
        {
            where.Append(" AND r.observed_at <= $to");
            command.Parameters.AddWithValue("$to", to.UtcTicks);
        }

        command.CommandText = $"""
            SELECT c.id, c.kind, c.text, r.record_json, bm25(chunks_fts) AS rank
            FROM chunks_fts
            JOIN chunks c ON c.rowid = chunks_fts.rowid
            JOIN records r ON r.id = c.record_id
            WHERE chunks_fts MATCH $match AND {where}
            ORDER BY rank, c.id
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$limit", search.TopK);

        var hits = new List<BackendHit>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = JsonSerializer.Deserialize<MemoryRecord>(reader.GetString(3), Json)!;

            // The SQL above implements every rule; this exact check catches any drift between the two.
            if (MemoryGovernance.Deny(record, f, now) is not null)
                continue;

            // bm25 is lower for better matches (negative in FTS5); the score is its negation, so higher is better.
            var kind = Enum.TryParse<MemoryChunkKind>(reader.GetString(1), out var k) ? k : MemoryChunkKind.Unknown;
            hits.Add(new BackendHit(record, reader.GetString(0), kind, reader.GetString(2), -reader.GetDouble(4), MemorySearchKinds.Keyword));
        }

        return hits;
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS records (
                id TEXT PRIMARY KEY,
                document_id TEXT,
                collection TEXT,
                workspace TEXT,
                project TEXT,
                repository TEXT,
                permission_scope TEXT,
                allow_cross_workspace INTEGER NOT NULL,
                trust INTEGER NOT NULL,
                sensitivity_rank INTEGER NOT NULL,
                tainted INTEGER NOT NULL,
                derived INTEGER NOT NULL,
                superseded INTEGER NOT NULL,
                deleted_at INTEGER,
                expires_at INTEGER,
                observed_at INTEGER NOT NULL,
                acl_present INTEGER NOT NULL,
                acl_captured INTEGER NOT NULL,
                acl_open INTEGER NOT NULL,
                record_json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_records_document ON records (document_id);
            CREATE INDEX IF NOT EXISTS ix_records_collection ON records (collection);
            CREATE TABLE IF NOT EXISTS record_principals (record_id TEXT NOT NULL, principal TEXT NOT NULL, PRIMARY KEY (record_id, principal));
            CREATE TABLE IF NOT EXISTS record_claims (record_id TEXT NOT NULL, claim TEXT NOT NULL, PRIMARY KEY (record_id, claim));
            CREATE TABLE IF NOT EXISTS chunks (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, record_id TEXT NOT NULL, kind TEXT NOT NULL, text TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_chunks_record ON chunks (record_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5 (text, content = 'chunks', content_rowid = 'rowid');
            CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts (rowid, text) VALUES (new.rowid, new.text);
            END;
            CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
                INSERT INTO chunks_fts (chunks_fts, rowid, text) VALUES ('delete', old.rowid, old.text);
            END;
            """;
        command.ExecuteNonQuery();

        command.CommandText = "SELECT version FROM schema_info LIMIT 1";
        if (command.ExecuteScalar() is long existing)
        {
            if (existing != SchemaVersion)
                throw new InvalidOperationException($"The memory database has schema {existing}; this version reads {SchemaVersion}. Rebuild it from the records.");
        }
        else
        {
            command.CommandText = $"INSERT INTO schema_info (version) VALUES ({SchemaVersion})";
            command.ExecuteNonQuery();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<int> DeleteRowsAsync(SqliteConnection connection, SqliteTransaction tx, string where, Action<SqliteParameterCollection> bind, CancellationToken ct)
    {
        var ids = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = $"SELECT id FROM records WHERE {where}";
            bind(select.Parameters);
            await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                ids.Add(reader.GetString(0));
        }

        foreach (var id in ids)
        {
            foreach (var table in new[] { "chunks", "record_principals", "record_claims" })
                await ExecAsync(connection, tx, $"DELETE FROM {table} WHERE record_id = $id", p => p.AddWithValue("$id", id), ct).ConfigureAwait(false);
            await ExecAsync(connection, tx, "DELETE FROM records WHERE id = $id", p => p.AddWithValue("$id", id), ct).ConfigureAwait(false);
        }

        return ids.Count;
    }

    private static async Task ExecAsync(SqliteConnection connection, SqliteTransaction tx, string sql, Action<SqliteParameterCollection> bind, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        bind(command.Parameters);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static string Bind(SqliteCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = $"${prefix}{names.Count}";
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    // Each term quoted, so FTS5 operators in user input are never interpreted.
    private static string? FtsQuery(string query)
    {
        var terms = TermPattern().Matches(query.ToLowerInvariant()).Select(static m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        return terms.Count == 0 ? null : string.Join(" OR ", terms.Select(static t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    [GeneratedRegex(@"[\p{L}\p{N}]{2,}")]
    private static partial Regex TermPattern();

    private static string NormText(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static object Norm(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : NormText(value);

    private static object Ticks(DateTimeOffset? value) => value is { } v ? v.UtcTicks : DBNull.Value;

    private static bool IsOpen(MemorySourceAcl? acl)
        => acl is { Captured: true } && (string.Equals(acl.Visibility, "public", StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(acl.Visibility, "organisation", StringComparison.OrdinalIgnoreCase));
}
