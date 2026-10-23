using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace OpenTail.Connectivity.Memory.Sqlite;

/// <summary>Where a SQLite memory store's vectors stand for its configured model.</summary>
/// <param name="IsComplete">Every chunk has a current, readable vector of the configured model.</param>
/// <param name="IndexedChunks">Chunks with a current, readable vector of the configured model.</param>
/// <param name="MissingOrStaleVectors">Chunks with no vector of the configured model, or one made from older text.</param>
/// <param name="CorruptVectors">Stored vectors of the configured model that cannot be read; never scored, redone by re-embedding.</param>
/// <param name="VectorCount">Vector rows of every model.</param>
/// <param name="VectorBytes">Their raw size.</param>
/// <param name="OtherModelVectors">Vector rows of other models, removed once the configured model covers every chunk.</param>
public sealed record SqliteVectorIndexStatus(
    string? ConfiguredModelId,
    bool IsComplete,
    long TotalChunks,
    long IndexedChunks,
    long MissingOrStaleVectors,
    long CorruptVectors,
    long VectorCount,
    long VectorBytes,
    long OtherModelVectors)
{
    /// <summary>Past this many vectors exact search gets slow; the owner's advisory ceiling (2026-10-23). Not enforced.</summary>
    public const long AdvisoryVectorCount = 50_000;

    /// <summary>The same ceiling in bytes, at 1,536 float32 dimensions, for models of other sizes.</summary>
    public const long AdvisoryVectorBytes = AdvisoryVectorCount * 1_536 * 4;

    /// <summary>Past the advisory ceiling in count or bytes: searches still run, but consider Postgres with pgvector.</summary>
    public bool OverAdvisoryLimit => VectorCount > AdvisoryVectorCount || VectorBytes > AdvisoryVectorBytes;

    /// <summary>What needs a person's attention, in words; empty when nothing does.</summary>
    public IReadOnlyList<string> Advisories =>
    [
        .. OverAdvisoryLimit
            ? [$"{VectorCount:N0} vectors ({VectorBytes / 1_048_576.0:N0} MB) is past the advisory {AdvisoryVectorCount:N0}; exact search slows down. For more, use Postgres with pgvector."]
            : Array.Empty<string>(),
        .. MissingOrStaleVectors > 0 ? [$"{MissingOrStaleVectors:N0} chunks have no current vector for {ConfiguredModelId}: semantic results are partial until re-embedding finishes."] : Array.Empty<string>(),
        .. CorruptVectors > 0 ? [$"{CorruptVectors:N0} stored vectors cannot be read; re-embedding redoes them."] : Array.Empty<string>(),
    ];
}

/// <param name="Embedded">Vectors written in this run.</param>
/// <param name="Discarded">Results thrown away because the chunk changed or went while it was being embedded.</param>
/// <param name="Remaining">Chunks still without a current vector (0 when the run finished).</param>
public sealed record SqliteReembedResult(int Embedded, int Discarded, long Remaining);

/// <summary>
/// Fills and repairs a SQLite memory store's vectors for the backend's configured model: chunks with no vector, one made from older
/// text, or a corrupt one (docs/design/sqlite-vectors.md §6). Resumable: a restarted run skips everything already current.
/// </summary>
/// <remarks>
/// Each batch is embedded outside any transaction (the generator may be slow), then written in one transaction that first re-reads
/// each chunk's text: a vector is stored only if the chunk still has the text that was embedded, so a chunk edited or deleted
/// meanwhile never gets a stale vector. When every chunk is covered, vectors of other models are deleted.
/// </remarks>
public sealed class SqliteMemoryVectorMaintenance
{
    private readonly SqliteMemoryBackend _backend;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly SqliteEmbeddingOptions _model;

    public SqliteMemoryVectorMaintenance(SqliteMemoryBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _embeddings = backend.Embeddings ?? throw new ArgumentException("The backend has no embedding generator to re-embed with.", nameof(backend));
        _model = backend.EmbeddingOptions!;
    }

    public async Task<SqliteReembedResult> ReembedAsync(int batchSize = 64, bool removeOtherModels = true, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        int embedded = 0, discarded = 0;
        long afterRowId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (batch, last, scanned) = await NextBatchAsync(afterRowId, batchSize, cancellationToken).ConfigureAwait(false);
            if (scanned == 0)
                break;
            afterRowId = last;
            if (batch.Count == 0)
                continue;

            var generated = await _embeddings.GenerateAsync(batch.Select(b => b.Text), cancellationToken: cancellationToken).ConfigureAwait(false);
            if (generated.Count != batch.Count)
                throw new InvalidDataException($"The embedding generator returned {generated.Count} embeddings for {batch.Count} texts.");
            var vectors = generated.Select(g => SqliteVectors.Encode(SqliteVectors.Normalize(g.Vector.Span, _model.Dimensions))).ToArray();

            await using var connection = await _backend.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                // Same transaction as the write: the text cannot change between this check and the vector landing.
                string? current;
                await using (var read = connection.CreateCommand())
                {
                    read.Transaction = tx;
                    read.CommandText = "SELECT text FROM chunks WHERE id = $id AND record_id = $rid";
                    read.Parameters.AddWithValue("$id", batch[i].ChunkId);
                    read.Parameters.AddWithValue("$rid", batch[i].RecordId);
                    current = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
                }

                if (current is null || SqliteVectors.ContentHash(current) != batch[i].Hash)
                {
                    discarded++;
                    continue;
                }

                var (chunk, record, hash, vector) = (batch[i].ChunkId, batch[i].RecordId, batch[i].Hash, vectors[i]);
                await SqliteMemoryBackend.ExecAsync(connection, tx, """
                    INSERT OR REPLACE INTO chunk_vectors (chunk_id, record_id, model_id, dimensions, content_hash, vector)
                    VALUES ($chunk, $rid, $model, $dims, $hash, $vector)
                    """, p =>
                {
                    p.AddWithValue("$chunk", chunk);
                    p.AddWithValue("$rid", record);
                    p.AddWithValue("$model", _model.ModelId);
                    p.AddWithValue("$dims", _model.Dimensions);
                    p.AddWithValue("$hash", hash);
                    p.AddWithValue("$vector", vector);
                }, cancellationToken).ConfigureAwait(false);
                embedded++;
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        await using (var connection = await _backend.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (status.IsComplete && removeOtherModels)
                await SqliteMemoryBackend.ExecAsync(connection, tx, "DELETE FROM chunk_vectors WHERE model_id <> $model",
                    p => p.AddWithValue("$model", _model.ModelId), cancellationToken).ConfigureAwait(false);
            await SqliteMemoryBackend.ExecAsync(connection, tx, """
                INSERT OR REPLACE INTO vector_state (model_id, complete, updated_at) VALUES ($model, $complete, $now)
                """, p =>
            {
                p.AddWithValue("$model", _model.ModelId);
                p.AddWithValue("$complete", status.IsComplete ? 1 : 0);
                p.AddWithValue("$now", DateTimeOffset.UtcNow.UtcTicks);
            }, cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SqliteReembedResult(embedded, discarded, status.MissingOrStaleVectors);
    }

    /// <summary>Counts chunks and vectors against the configured model. Reads every chunk's text to check vectors are current.</summary>
    public async Task<SqliteVectorIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        long total = 0, indexed = 0, corrupt = 0;
        var scratch = new float[_model.Dimensions];
        await using var connection = await _backend.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.text, cv.content_hash, cv.dimensions, cv.vector
                FROM chunks c LEFT JOIN chunk_vectors cv ON cv.chunk_id = c.id AND cv.model_id = $model
                """;
            command.Parameters.AddWithValue("$model", _model.ModelId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                total++;
                if (reader.IsDBNull(1))
                    continue;
                if (reader.GetInt32(2) != _model.Dimensions || !SqliteVectors.TryDecode((byte[])reader[3], _model.Dimensions, scratch))
                    corrupt++;
                else if (reader.GetString(1) == SqliteVectors.ContentHash(reader.GetString(0)))
                    indexed++;
            }
        }

        long count = 0, bytes = 0, others = 0;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(length(vector)), 0), COALESCE(SUM(model_id <> $model), 0) FROM chunk_vectors";
            command.Parameters.AddWithValue("$model", _model.ModelId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                (count, bytes, others) = (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }

        return new SqliteVectorIndexStatus(_model.ModelId, indexed == total, total, indexed, total - indexed - corrupt, corrupt, count, bytes, others);
    }

    /// <summary>The next chunks after <paramref name="afterRowId"/> that need a vector: none, one from older text, or a corrupt one.</summary>
    private async Task<(List<(string ChunkId, string RecordId, string Text, string Hash)> Batch, long LastRowId, int Scanned)> NextBatchAsync(
        long afterRowId, int batchSize, CancellationToken ct)
    {
        var batch = new List<(string, string, string, string)>();
        long last = afterRowId;
        var scanned = 0;
        var scratch = new float[_model.Dimensions];
        await using var connection = await _backend.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.rowid, c.id, c.record_id, c.text, cv.content_hash, cv.dimensions, cv.vector
            FROM chunks c LEFT JOIN chunk_vectors cv ON cv.chunk_id = c.id AND cv.model_id = $model
            WHERE c.rowid > $after ORDER BY c.rowid LIMIT $limit
            """;
        command.Parameters.AddWithValue("$model", _model.ModelId);
        command.Parameters.AddWithValue("$after", afterRowId);
        command.Parameters.AddWithValue("$limit", batchSize);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            scanned++;
            last = reader.GetInt64(0);
            var text = reader.GetString(3);
            var hash = SqliteVectors.ContentHash(text);
            var current = !reader.IsDBNull(4) && reader.GetString(4) == hash && reader.GetInt32(5) == _model.Dimensions
                          && SqliteVectors.TryDecode((byte[])reader[6], _model.Dimensions, scratch);
            if (!current)
                batch.Add((reader.GetString(1), reader.GetString(2), text, hash));
        }

        return (batch, last, scanned);
    }
}
