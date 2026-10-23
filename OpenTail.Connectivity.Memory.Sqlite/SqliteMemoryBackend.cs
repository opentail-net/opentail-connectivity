using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace OpenTail.Connectivity.Memory.Sqlite;

/// <summary>
/// A memory backend in one SQLite file. Keyword search through FTS5 (bm25). Every <see cref="MemoryGovernance"/> rule is applied
/// in SQL before ranking, so top-k is taken from what the caller may see; <see cref="MemoryGovernance.Deny"/> then runs on each
/// hit as an exact final check. The record itself is stored as JSON and is the truth; the other columns and the FTS index are
/// derived from it and can be rebuilt.
/// <para>With an embedding generator, each chunk also gets a vector (design: docs/design/sqlite-vectors.md). Embeddings are generated
/// before the write transaction, so a failing or slow generator never changes a stored record. A vector is kept only while its
/// chunk's text is the text it was made from: every upsert, with a generator or without, removes vectors whose chunk changed or went.</para>
/// </summary>
public sealed partial class SqliteMemoryBackend : IMemoryBackend
{
    // 2 added chunk_vectors and vector_state; version 1 files are migrated in place, keeping records, chunks and the FTS index.
    private const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _connectionString;
    private readonly TimeProvider _time;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;
    private readonly SqliteEmbeddingOptions? _embeddingOptions;

    /// <param name="path">The database file. It is created when missing; its folder must exist (a backend never creates
    /// where it stores: a wrong path must fail, not start an empty store).</param>
    public SqliteMemoryBackend(string path, TimeProvider? time = null)
        : this(path, embeddings: null, embeddingOptions: null, time)
    {
    }

    /// <param name="embeddings">The caller's embedding generator; with it, chunks get vectors. Null for keyword only.</param>
    /// <param name="embeddingOptions">The model behind <paramref name="embeddings"/>; required with it.</param>
    public SqliteMemoryBackend(string path, IEmbeddingGenerator<string, Embedding<float>>? embeddings, SqliteEmbeddingOptions? embeddingOptions,
        TimeProvider? time = null)
    {
        if ((embeddings is null) != (embeddingOptions is null))
            throw new ArgumentException("An embedding generator and its SqliteEmbeddingOptions go together.", nameof(embeddingOptions));
        embeddingOptions?.Validate();
        _embeddings = embeddings;
        _embeddingOptions = embeddingOptions;
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null && !Directory.Exists(folder))
            throw new DirectoryNotFoundException($"The folder for the memory database does not exist: {folder}");

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        _time = time ?? TimeProvider.System;
        EnsureSchema();
    }

    public MemoryBackendCapabilities Capabilities => _capabilities ??= new(
        Search: _embeddings is null ? MemorySearchKinds.Keyword : MemorySearchKinds.Keyword | MemorySearchKinds.Vector,
        FiltersBeforeRanking: true,
        TemporalValidity: true,
        DeleteBySource: true,
        EmbeddingModelId: _embeddingOptions?.ModelId);

    private MemoryBackendCapabilities? _capabilities;

    public async Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Any(c => !string.Equals(c.ParentRecordId, record.Id, StringComparison.Ordinal)))
            throw new ArgumentException("Every chunk must belong to the record.", nameof(chunks));

        IReadOnlyList<(string Id, MemoryChunkKind Kind, string Text)> rows = chunks.Count > 0
            ? [.. chunks.Select(c => (c.Id, c.Kind, c.Content))]
            : [(record.Id, MemoryChunkKind.Unknown, record.Content)];
        var hashes = rows.Select(r => SqliteVectors.ContentHash(r.Text)).ToArray();
        // Outside the transaction: the generator may be slow or fail, and either must leave the stored record as it was.
        var vectors = await EmbedAsync(rows.Select(r => r.Text).ToList(), cancellationToken).ConfigureAwait(false);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await DeleteRowsAsync(connection, tx, "id = $id", p => p.AddWithValue("$id", record.Id), cancellationToken, keepVectors: true).ConfigureAwait(false);

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

        await ReplaceVectorsAsync(connection, tx, record.Id, rows, hashes, vectors, cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Generates and checks one vector per text, or returns null without a generator. Throws on any bad vector.</summary>
    private async Task<byte[][]?> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (_embeddings is null || _embeddingOptions is null)
            return null;
        var generated = await _embeddings.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        if (generated.Count != texts.Count)
            throw new InvalidDataException($"The embedding generator returned {generated.Count} embeddings for {texts.Count} texts.");
        return [.. generated.Select(e => SqliteVectors.Encode(SqliteVectors.Normalize(e.Vector.Span, _embeddingOptions.Dimensions)))];
    }

    /// <summary>
    /// Inside the upsert's transaction: vectors of chunks that went, or whose text changed, are deleted for every model; then, with a
    /// generator, this model's vectors are written. Without one, vectors whose chunk text is unchanged stay.
    /// </summary>
    private async Task ReplaceVectorsAsync(SqliteConnection connection, SqliteTransaction tx, string recordId,
        IReadOnlyList<(string Id, MemoryChunkKind Kind, string Text)> rows, string[] hashes, byte[][]? vectors, CancellationToken ct)
    {
        await using (var stale = connection.CreateCommand())
        {
            stale.Transaction = tx;
            var keep = new List<string>();
            for (var i = 0; i < rows.Count; i++)
            {
                stale.Parameters.AddWithValue($"$c{i}", rows[i].Id);
                stale.Parameters.AddWithValue($"$h{i}", hashes[i]);
                keep.Add($"(chunk_id = $c{i} AND content_hash = $h{i})");
            }

            stale.Parameters.AddWithValue("$rid", recordId);
            stale.CommandText = $"DELETE FROM chunk_vectors WHERE record_id = $rid AND NOT ({string.Join(" OR ", keep)})";
            await stale.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (vectors is null || _embeddingOptions is null)
            return;
        for (var i = 0; i < rows.Count; i++)
        {
            var (chunkId, hash, vector) = (rows[i].Id, hashes[i], vectors[i]);
            await ExecAsync(connection, tx, """
                INSERT OR REPLACE INTO chunk_vectors (chunk_id, record_id, model_id, dimensions, content_hash, vector)
                VALUES ($chunk, $rid, $model, $dims, $hash, $vector)
                """, p =>
            {
                p.AddWithValue("$chunk", chunkId);
                p.AddWithValue("$rid", recordId);
                p.AddWithValue("$model", _embeddingOptions.ModelId);
                p.AddWithValue("$dims", _embeddingOptions.Dimensions);
                p.AddWithValue("$hash", hash);
                p.AddWithValue("$vector", vector);
            }, ct).ConfigureAwait(false);
        }
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
        if (search.Kind == MemorySearchKinds.Vector)
            return await VectorSearchAsync(search, cancellationToken).ConfigureAwait(false);
        return await KeywordSearchAsync(search, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<BackendHit>> KeywordSearchAsync(MemorySearch search, CancellationToken cancellationToken)
    {
        var match = FtsQuery(search.Query);
        if (match is null || search.TopK <= 0)
            return [];

        var now = _time.GetUtcNow();
        var f = search.Filter;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = Governance(command, f, now);

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

    /// <summary>
    /// Exact similarity over the vectors of the configured model, for chunks of records that pass the governance predicate only.
    /// Rows are read one at a time and the best k kept in a heap; a corrupt stored vector is skipped (and counted by the maintenance
    /// status), never scored. The query is embedded and checked like a stored vector, whatever the keyword parser would make of it.
    /// </summary>
    private async Task<IReadOnlyList<BackendHit>> VectorSearchAsync(MemorySearch search, CancellationToken cancellationToken)
    {
        if (_embeddings is null || _embeddingOptions is null)
            throw new NotSupportedException("This SQLite memory backend has no embedding generator: keyword search only.");
        if (search.TopK <= 0 || string.IsNullOrWhiteSpace(search.Query))
            return [];

        var embedded = await _embeddings.GenerateAsync([search.Query], cancellationToken: cancellationToken).ConfigureAwait(false);
        if (embedded.Count != 1)
            throw new InvalidDataException($"The embedding generator returned {embedded.Count} embeddings for one query.");
        var query = SqliteVectors.Normalize(embedded[0].Vector.Span, _embeddingOptions.Dimensions);

        var now = _time.GetUtcNow();
        var f = search.Filter;
        var best = new PriorityQueue<string, float>(); // lowest score first, so the weakest of the best k is dropped
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            var where = Governance(command, f, now);
            command.CommandText = $"""
                SELECT cv.chunk_id, cv.dimensions, cv.vector
                FROM chunk_vectors cv
                JOIN chunks c ON c.id = cv.chunk_id AND c.record_id = cv.record_id
                JOIN records r ON r.id = c.record_id
                WHERE cv.model_id = $model AND {where}
                """;
            command.Parameters.AddWithValue("$model", _embeddingOptions.ModelId);
            var stored = new float[_embeddingOptions.Dimensions];
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetInt32(1) != _embeddingOptions.Dimensions
                    || !SqliteVectors.TryDecode((byte[])reader[2], _embeddingOptions.Dimensions, stored))
                    continue; // corrupt or from another size: never scored
                var score = Math.Clamp(System.Numerics.Tensors.TensorPrimitives.Dot<float>(query, stored), -1f, 1f);
                var chunkId = reader.GetString(0);
                if (best.Count < search.TopK)
                {
                    best.Enqueue(chunkId, score);
                    scores[chunkId] = score;
                }
                else if (best.TryPeek(out _, out var weakest) && score > weakest)
                {
                    scores.Remove(best.Dequeue());
                    best.Enqueue(chunkId, score);
                    scores[chunkId] = score;
                }
            }
        }

        if (scores.Count == 0)
            return [];
        var hits = new List<BackendHit>(scores.Count);
        await using (var fetch = connection.CreateCommand())
        {
            fetch.CommandText = $"""
                SELECT c.id, c.kind, c.text, r.record_json FROM chunks c JOIN records r ON r.id = c.record_id
                WHERE c.id IN ({Bind(fetch, "hit", scores.Keys)})
                """;
            await using var reader = await fetch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var record = JsonSerializer.Deserialize<MemoryRecord>(reader.GetString(3), Json)!;
                if (MemoryGovernance.Deny(record, f, now) is not null)
                    continue; // the SQL implements every rule; this exact check catches drift
                var kind = Enum.TryParse<MemoryChunkKind>(reader.GetString(1), out var k) ? k : MemoryChunkKind.Unknown;
                hits.Add(new BackendHit(record, reader.GetString(0), kind, reader.GetString(2), scores[reader.GetString(0)], MemorySearchKinds.Vector));
            }
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.ChunkId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every <see cref="MemoryGovernance"/> rule as one SQL predicate over <c>r</c> (records), binding its parameters on
    /// <paramref name="command"/>. Keyword and vector search both use it, so they cannot drift apart; a forbidden chunk never
    /// reaches a ranking.
    /// </summary>
    private static string Governance(SqliteCommand command, MemoryFilter f, DateTimeOffset now)
    {
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

        return where.ToString();
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
            if (existing == 1)
            {
                // In place, in one transaction: records, chunks and the FTS index are untouched; vectors start empty.
                using var tx = connection.BeginTransaction();
                command.Transaction = tx;
                command.CommandText = VectorTables + "UPDATE schema_info SET version = 2;";
                command.ExecuteNonQuery();
                tx.Commit();
                command.Transaction = null;
            }
            else if (existing != SchemaVersion)
            {
                throw new InvalidOperationException($"The memory database has schema {existing}; this version reads {SchemaVersion}. Rebuild it from the records.");
            }
        }
        else
        {
            command.CommandText = VectorTables + $"INSERT INTO schema_info (version) VALUES ({SchemaVersion});";
            command.ExecuteNonQuery();
        }
    }

    private const string VectorTables = """
        CREATE TABLE IF NOT EXISTS chunk_vectors (
            chunk_id TEXT NOT NULL,
            record_id TEXT NOT NULL,
            model_id TEXT NOT NULL,
            dimensions INTEGER NOT NULL CHECK (dimensions > 0),
            content_hash TEXT NOT NULL,
            vector BLOB NOT NULL,
            PRIMARY KEY (chunk_id, model_id),
            CHECK (length(vector) = dimensions * 4));
        CREATE INDEX IF NOT EXISTS ix_chunk_vectors_model_record ON chunk_vectors (model_id, record_id);
        CREATE INDEX IF NOT EXISTS ix_chunk_vectors_record ON chunk_vectors (record_id);
        CREATE TABLE IF NOT EXISTS vector_state (model_id TEXT PRIMARY KEY, complete INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        """;

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    /// <param name="keepVectors">For an upsert: its vectors are reconciled afterwards against the new chunks, not dropped here.</param>
    private static async Task<int> DeleteRowsAsync(SqliteConnection connection, SqliteTransaction tx, string where, Action<SqliteParameterCollection> bind,
        CancellationToken ct, bool keepVectors = false)
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
            foreach (var table in keepVectors ? ["chunks", "record_principals", "record_claims"] : new[] { "chunks", "record_principals", "record_claims", "chunk_vectors" })
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
