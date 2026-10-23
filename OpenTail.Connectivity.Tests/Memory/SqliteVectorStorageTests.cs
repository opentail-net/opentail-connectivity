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

    // ---- V2: vector search

    private static MemorySearch Vector(string query, int k = 5, MemoryFilter? filter = null) => new(query, MemorySearchKinds.Vector, k, filter ?? new MemoryFilter());

    [Fact]
    public async Task Vector_search_ranks_the_closest_chunk_first_and_reports_vector_as_the_match()
    {
        var backend = WithVectors();
        await backend.UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "invoice payment overdue reminder")]);
        await backend.UpsertAsync(Record("r2", "b"), [Chunk("r2", "r2-a", "holiday rota for the summer")]);

        var hits = await backend.SearchAsync(Vector("overdue invoice payment"));

        Assert.Equal("r1-a", hits[0].ChunkId);
        Assert.Equal(MemorySearchKinds.Vector, hits[0].Kind);
        Assert.True(hits[0].Score > (hits.Count > 1 ? hits[1].Score : -1));
        Assert.Equal(MemorySearchKinds.Keyword | MemorySearchKinds.Vector | MemorySearchKinds.Hybrid, backend.Capabilities.Search);
        Assert.Equal(Model.ModelId, backend.Capabilities.EmbeddingModelId);
    }

    [Fact]
    public async Task Forbidden_chunks_never_enter_the_ranking_even_when_they_would_score_higher()
    {
        var backend = WithVectors();
        // The restricted record matches the query exactly; the allowed one only partly. Top-1 must still be the allowed one.
        await backend.UpsertAsync(Record("secret", "s", acl: new MemorySourceAcl(Captured: true, Visibility: "restricted", Principals: ["group:hr"])),
            [Chunk("secret", "secret-a", "salary review for the board")]);
        await backend.UpsertAsync(Record("open", "o"), [Chunk("open", "open-a", "salary bands are published yearly")]);

        var hits = await backend.SearchAsync(Vector("salary review for the board", k: 1));

        Assert.Equal("open", Assert.Single(hits).Record.Id);
        var allowed = await backend.SearchAsync(Vector("salary review for the board", k: 1, filter: new MemoryFilter(Principals: ["group:hr"])));
        Assert.Equal("secret", Assert.Single(allowed).Record.Id);
    }

    [Fact]
    public async Task A_corrupt_stored_vector_is_skipped_and_the_search_still_returns_the_rest()
    {
        var backend = WithVectors();
        await backend.UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "alpha beta gamma")]);
        await backend.UpsertAsync(Record("r2", "b"), [Chunk("r2", "r2-a", "alpha beta delta")]);
        Sql($"UPDATE chunk_vectors SET vector = zeroblob({Dims * 4}) WHERE chunk_id = 'r1-a'"); // right length, but all zero: decodes, scores 0
        Sql("UPDATE chunk_vectors SET dimensions = 1, vector = x'0000C07F' WHERE chunk_id = 'r1-a'"); // NaN and the wrong size: corrupt

        var hits = await backend.SearchAsync(Vector("alpha beta"));

        Assert.Equal(["r2-a"], hits.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Vectors_of_another_model_are_never_compared_with_the_query()
    {
        await WithVectors().UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "alpha words here")]);
        var other = new SqliteMemoryBackend(_path, new HashedBagOfWordsEmbeddings(Dims), new SqliteEmbeddingOptions("test:other-model", Dims));

        Assert.Empty(await other.SearchAsync(Vector("alpha words here")));
        Assert.Single(await WithVectors().SearchAsync(Vector("alpha words here")));
    }

    [Fact]
    public async Task A_keyword_only_backend_refuses_vector_search_rather_than_answering_with_keywords()
    {
        await KeywordOnly().UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "alpha")]);
        await Assert.ThrowsAsync<NotSupportedException>(() => KeywordOnly().SearchAsync(Vector("alpha")));
        Assert.Equal(MemorySearchKinds.Keyword, KeywordOnly().Capabilities.Search);
    }

    [Fact]
    public async Task Top_k_keeps_only_the_best_k_of_many()
    {
        var backend = WithVectors();
        for (var i = 0; i < 40; i++)
            await backend.UpsertAsync(Record($"r{i}", "x"), [Chunk($"r{i}", $"r{i}-a", i == 17 ? "needle haystack special" : $"filler words number {i}")]);

        var hits = await backend.SearchAsync(Vector("needle haystack special", k: 3));

        Assert.Equal(3, hits.Count);
        Assert.Equal("r17-a", hits[0].ChunkId);
        Assert.True(hits.Zip(hits.Skip(1)).All(p => p.First.Score >= p.Second.Score));
    }

    // ---- V3: hybrid and modes

    [Fact]
    public async Task Hybrid_fuses_keyword_and_vector_rankings_by_rank()
    {
        var backend = WithVectors();
        await backend.UpsertAsync(Record("both", "x"), [Chunk("both", "both-a", "quarterly revenue report figures")]);
        await backend.UpsertAsync(Record("other", "y"), [Chunk("other", "other-a", "staff canteen menu")]);

        var hybrid = await backend.SearchAsync(new MemorySearch("quarterly revenue", MemorySearchKinds.Hybrid, 5, new MemoryFilter()));
        var keyword = await backend.SearchAsync(new MemorySearch("quarterly revenue", MemorySearchKinds.Keyword, 5, new MemoryFilter()));
        var vector = await backend.SearchAsync(Vector("quarterly revenue"));

        Assert.Equal("both-a", hybrid[0].ChunkId);
        Assert.Equal(ReciprocalRankFusion.Fuse([keyword, vector], 5).Select(h => h.ChunkId), hybrid.Select(h => h.ChunkId));
    }

    [Fact]
    public async Task Semantic_search_does_not_depend_on_the_keyword_parser()
    {
        // A generator that embeds any text, and a query the keyword parser cannot use (one-letter words).
        var backend = new SqliteMemoryBackend(_path, new LetterEmbeddings(), Model);
        await backend.UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "x")]);

        Assert.Empty(await backend.SearchAsync(new MemorySearch("x", MemorySearchKinds.Keyword, 5, new MemoryFilter())));
        Assert.Single(await backend.SearchAsync(Vector("x")));
        Assert.Single(await backend.SearchAsync(new MemorySearch("x", MemorySearchKinds.Hybrid, 5, new MemoryFilter())));
    }

    [Fact]
    public async Task The_retriever_honours_an_explicit_mode_and_keyword_mode_embeds_nothing()
    {
        var counting = new CountingEmbeddings(new HashedBagOfWordsEmbeddings(Dims));
        var backend = new SqliteMemoryBackend(_path, counting, Model);
        await backend.UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "supplier onboarding checklist")]);
        var retriever = new MemoryRetriever(backend);
        var before = counting.Calls;

        var keyword = await retriever.RetrieveAsync(new MemoryRetrievalRequest("supplier onboarding", RetrievalMode: MemoryRetrievalMode.Keyword));
        Assert.Equal(before, counting.Calls); // keyword mode never embeds

        var semantic = await retriever.RetrieveAsync(new MemoryRetrievalRequest("supplier onboarding", RetrievalMode: MemoryRetrievalMode.Semantic));
        Assert.NotEmpty(semantic.Hits);

        Assert.NotEmpty((await retriever.RetrieveAsync(new MemoryRetrievalRequest("supplier onboarding"))).Hits); // Auto: hybrid
    }

    [Fact]
    public async Task An_explicit_mode_the_backend_cannot_do_fails_clearly()
    {
        await KeywordOnly().UpsertAsync(Record("r1", "a"), [Chunk("r1", "r1-a", "alpha words")]);
        var retriever = new MemoryRetriever(KeywordOnly());

        await Assert.ThrowsAsync<NotSupportedException>(() => retriever.RetrieveAsync(new MemoryRetrievalRequest("alpha", RetrievalMode: MemoryRetrievalMode.Semantic)));
        await Assert.ThrowsAsync<NotSupportedException>(() => retriever.RetrieveAsync(new MemoryRetrievalRequest("alpha", RetrievalMode: MemoryRetrievalMode.Hybrid)));
        Assert.NotEmpty((await retriever.RetrieveAsync(new MemoryRetrievalRequest("alpha words", RetrievalMode: MemoryRetrievalMode.Keyword))).Hits);
    }

    private sealed class LetterEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v =>
            {
                var vector = new float[Dims];
                foreach (var c in v) vector[c % Dims] += 1f;
                vector[0] += 0.001f; // never all zero
                return new Embedding<float>(vector);
            })));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CountingEmbeddings(IEmbeddingGenerator<string, Embedding<float>> inner) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public int Calls;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return inner.GenerateAsync(values, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static MemoryRecord Record(string id, string content, string? document = null, string? collection = null, MemorySourceAcl? acl = null) => new(
        id,
        MemoryRecordKind.SourceDocument,
        new MemorySource(MemorySourceType.UserNote, "test", DocumentId: document, Acl: acl ?? MemorySourceAcl.OrganisationWide),
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
