using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.Sqlite;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>SQLite vectors, step V1: storage, atomic upsert, stale-vector removal, deletion, migration (docs/design/sqlite-vectors.md).</summary>
public sealed class SqliteVectorStorageTests : IDisposable
{
    private const int Dims = 64;
    private static readonly SqliteEmbeddingOptions Model = new("test:hashed-64", Dims);
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ot-vectors-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_path + suffix);
    }

    private SqliteMemoryBackend WithVectors(IEmbeddingGenerator<string, Embedding<float>>? generator = null)
        => new(_path, generator ?? new HashedBagOfWordsEmbeddings(Dims), Model);

    private SqliteMemoryBackend KeywordOnly() => new(_path);

    [Fact]
    public async Task Each_chunk_gets_one_normalised_little_endian_vector_with_the_hash_of_its_text()
    {
        await WithVectors().UpsertAsync(Record("r1", "whole"), [Chunk("r1", "r1-a", "alpha words"), Chunk("r1", "r1-b", "beta words")]);

        var rows = Vectors();
        Assert.Equal(["r1-a", "r1-b"], rows.Select(r => r.Chunk));
        Assert.All(rows, r =>
        {
            Assert.Equal(Model.ModelId, r.Model);
            Assert.Equal(Dims * 4, r.Bytes.Length);
            var v = new float[Dims];
            Assert.True(SqliteVectors.TryDecode(r.Bytes, Dims, v));
            Assert.InRange(MathF.Sqrt(v.Sum(x => x * x)), 0.9999f, 1.0001f);
        });
        Assert.Equal(SqliteVectors.ContentHash("alpha words"), rows[0].Hash);
    }

    [Fact]
    public void The_vector_format_is_little_endian_whatever_the_machine()
    {
        var bytes = SqliteVectors.Encode([1f]);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, bytes);
        var back = new float[1];
        Assert.True(SqliteVectors.TryDecode(bytes, 1, back));
        Assert.Equal(1f, back[0]);
        Assert.False(SqliteVectors.TryDecode(bytes, 2, back)); // wrong length is corrupt
        Assert.False(SqliteVectors.TryDecode(SqliteVectors.Encode([float.NaN]), 1, back)); // non-finite is corrupt
    }

    [Fact]
    public async Task A_failed_embedding_leaves_the_previous_record_and_its_vectors_as_they_were()
    {
        var generator = new FailingGenerator(failOnCall: 2);
        var backend = WithVectors(generator);
        var original = Record("r1", "original marker");
        await backend.UpsertAsync(original, [Chunk("r1", "r1-a", "original marker")]);
        var before = Vectors();

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.UpsertAsync(Record("r1", "replacement"), [Chunk("r1", "r1-a", "replacement")]));

        Assert.Equal(original.Content, (await backend.GetAsync("r1"))!.Content);
        Assert.Equal(before.Select(r => r.Hash), Vectors().Select(r => r.Hash));
        Assert.Single(await backend.SearchAsync(new MemorySearch("original marker", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
    }

    [Theory]
    [InlineData("wrong-size")]
    [InlineData("nan")]
    [InlineData("zero")]
    [InlineData("count")]
    public async Task A_bad_generated_vector_fails_the_upsert_and_changes_nothing(string fault)
    {
        await WithVectors().UpsertAsync(Record("r1", "kept"), [Chunk("r1", "r1-a", "kept")]);

        var bad = WithVectors(new FaultyGenerator(fault));
        await Assert.ThrowsAsync<InvalidDataException>(() => bad.UpsertAsync(Record("r1", "changed"), [Chunk("r1", "r1-a", "changed")]));

        Assert.Equal("kept", (await KeywordOnly().GetAsync("r1"))!.Content);
        Assert.Equal(SqliteVectors.ContentHash("kept"), Assert.Single(Vectors()).Hash);
    }

    [Fact]
    public async Task Replacing_a_record_replaces_its_vectors_and_drops_those_of_removed_chunks()
    {
        var backend = WithVectors();
        await backend.UpsertAsync(Record("r1", "v1"), [Chunk("r1", "r1-a", "old alpha"), Chunk("r1", "r1-b", "old beta"), Chunk("r1", "r1-c", "gamma")]);

        await backend.UpsertAsync(Record("r1", "v2"), [Chunk("r1", "r1-a", "new alpha"), Chunk("r1", "r1-c", "gamma"), Chunk("r1", "r1-d", "delta")]);

        var rows = Vectors();
        Assert.Equal(["r1-a", "r1-c", "r1-d"], rows.Select(r => r.Chunk));
        Assert.Equal(SqliteVectors.ContentHash("new alpha"), rows.Single(r => r.Chunk == "r1-a").Hash);
    }

    [Fact]
    public async Task A_keyword_only_instance_that_edits_a_chunk_leaves_no_stale_vector()
    {
        await WithVectors().UpsertAsync(Record("r1", "v1"), [Chunk("r1", "r1-a", "unchanged text"), Chunk("r1", "r1-b", "old text"), Chunk("r1", "r1-c", "gone")]);

        await KeywordOnly().UpsertAsync(Record("r1", "v2"), [Chunk("r1", "r1-a", "unchanged text"), Chunk("r1", "r1-b", "edited text")]);

        // The unchanged chunk keeps its vector; the edited and the removed lose theirs. Re-embedding fills the gap later.
        var rows = Vectors();
        Assert.Equal(["r1-a"], rows.Select(r => r.Chunk));
        Assert.Equal(SqliteVectors.ContentHash("unchanged text"), rows[0].Hash);
    }

    [Fact]
    public async Task Stale_vectors_of_other_models_go_too_when_their_chunk_changes()
    {
        Insert("r1-a", "r1", "other:model", SqliteVectors.ContentHash("old"));
        await WithVectors().UpsertAsync(Record("r1", "v"), [Chunk("r1", "r1-a", "new")]);

        Assert.Equal([Model.ModelId], Vectors().Select(r => r.Model));
    }

    [Theory]
    [InlineData("record")]
    [InlineData("document")]
    [InlineData("collection")]
    public async Task Deleting_records_deletes_their_vectors(string by)
    {
        var backend = WithVectors();
        await backend.UpsertAsync(Record("r1", "one", document: "doc-1", collection: "c1"), [Chunk("r1", "r1-a", "one")]);
        await backend.UpsertAsync(Record("r2", "two", document: "doc-2", collection: "c2"), [Chunk("r2", "r2-a", "two")]);

        await backend.DeleteAsync(by switch
        {
            "record" => new MemoryDeletion(RecordId: "r1"),
            "document" => new MemoryDeletion(DocumentId: "doc-1"),
            _ => new MemoryDeletion(Collection: "c1"),
        });

        Assert.Equal(["r2-a"], Vectors().Select(r => r.Chunk));
    }

    [Fact]
    public void Embedding_options_must_name_a_model_and_a_size_and_come_with_a_generator()
    {
        Assert.Throws<ArgumentException>(() => new SqliteMemoryBackend(_path, new HashedBagOfWordsEmbeddings(Dims), new SqliteEmbeddingOptions(" ", Dims)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteMemoryBackend(_path, new HashedBagOfWordsEmbeddings(Dims), new SqliteEmbeddingOptions("m", 0)));
        Assert.Throws<ArgumentException>(() => new SqliteMemoryBackend(_path, new HashedBagOfWordsEmbeddings(Dims), null));
        Assert.Throws<ArgumentException>(() => new SqliteMemoryBackend(_path, null, Model));
    }

    [Fact]
    public async Task A_version_1_database_migrates_in_place_keeping_records_and_keyword_search()
    {
        var old = KeywordOnly();
        await old.UpsertAsync(Record("r1", "first"), [Chunk("r1", "r1-a", "searchable marker"), Chunk("r1", "r1-b", "second part")]);
        await old.UpsertAsync(Record("r2", "other"), [Chunk("r2", "r2-a", "unrelated text")]);
        // Make it a version 1 file: no vector tables, version 1.
        Sql("DROP TABLE chunk_vectors; DROP TABLE vector_state; UPDATE schema_info SET version = 1;");
        SqliteConnection.ClearAllPools();

        var migrated = WithVectors();

        Assert.Equal(2L, Scalar("SELECT version FROM schema_info"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM chunk_vectors")); // vectors start empty
        Assert.Single(await migrated.SearchAsync(new MemorySearch("searchable marker", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        // And the migrated store still replaces and deletes chunks properly.
        await migrated.UpsertAsync(Record("r1", "first again"), [Chunk("r1", "r1-a", "replaced words")]);
        Assert.Empty(await migrated.SearchAsync(new MemorySearch("searchable marker", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        Assert.Single(await migrated.SearchAsync(new MemorySearch("replaced words", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        await migrated.DeleteAsync(new MemoryDeletion(RecordId: "r2"));
        Assert.Empty(await migrated.SearchAsync(new MemorySearch("unrelated", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        Assert.Equal(["r1-a"], Vectors().Select(r => r.Chunk));
    }

    private static MemoryRecord Record(string id, string content, string? document = null, string? collection = null) => new(
        id,
        MemoryRecordKind.SourceDocument,
        new MemorySource(MemorySourceType.UserNote, "test", DocumentId: document, Acl: MemorySourceAcl.OrganisationWide),
        new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
        content,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Collection: collection ?? "vector-tests");

    private static MemoryChunk Chunk(string recordId, string id, string content) => new(id, recordId, 0, MemoryChunkKind.RawEvidence, content);

    private List<(string Chunk, string Model, string Hash, byte[] Bytes)> Vectors()
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT chunk_id, model_id, content_hash, vector FROM chunk_vectors ORDER BY chunk_id, model_id";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string, byte[])>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), (byte[])reader[3]));
        return rows;
    }

    private void Insert(string chunk, string record, string model, string hash)
    {
        _ = KeywordOnly(); // make sure the schema exists
        Sql($"INSERT INTO chunk_vectors (chunk_id, record_id, model_id, dimensions, content_hash, vector) VALUES ('{chunk}', '{record}', '{model}', 1, '{hash}', x'0000803F')");
    }

    private void Sql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class FailingGenerator(int failOnCall) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HashedBagOfWordsEmbeddings _inner = new(Dims);
        private int _calls;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _calls) == failOnCall
                ? throw new InvalidOperationException("the embedding service is down")
                : _inner.GenerateAsync(values, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FaultyGenerator(string fault) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var texts = values.ToList();
            var vector = fault switch
            {
                "wrong-size" => new float[Dims + 1],
                "nan" => Enumerable.Repeat(float.NaN, Dims).ToArray(),
                "zero" => new float[Dims],
                _ => Enumerable.Repeat(1f, Dims).ToArray(),
            };
            if (fault == "wrong-size") vector[0] = 1f;
            var count = fault == "count" ? texts.Count + 1 : texts.Count;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(Enumerable.Range(0, count).Select(_ => new Embedding<float>(vector))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
