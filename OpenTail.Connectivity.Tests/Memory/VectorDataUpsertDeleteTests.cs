using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Microsoft.Extensions.VectorData;
using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.VectorData;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class VectorDataUpsertDeleteTests
{
    [Fact]
    public async Task Failed_embedding_keeps_existing_record_and_search_result()
    {
        var embeddings = new FailOnSecondEmbeddingGenerator();
        var (backend, _) = Create(embeddings);
        var original = Record("record-1", "original searchable marker");
        await backend.UpsertAsync(original, [Chunk("record-1", "a", "original searchable marker")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.UpsertAsync(
            Record("record-1", "replacement searchable marker"), [Chunk("record-1", "a", "replacement searchable marker")]));

        Assert.Equal(original, await backend.GetAsync("record-1"));
        var hits = await backend.SearchAsync(new MemorySearch("original searchable marker", MemorySearchKinds.Vector, 5, new MemoryFilter()));
        Assert.Contains(hits, hit => hit.Record.Id == "record-1");
    }

    [Fact]
    public async Task Upsert_replaces_matching_chunks_and_removes_stale_chunks()
    {
        var (backend, collection) = Create(new HashedBagOfWordsEmbeddings());
        var original = Record("record-1", "old complete record");
        await backend.UpsertAsync(original,
        [
            Chunk("record-1", "a", "old alpha"),
            Chunk("record-1", "b", "old beta"),
            Chunk("record-1", "c", "old gamma"),
        ]);

        await backend.UpsertAsync(Record("record-1", "new complete record"),
        [
            Chunk("record-1", "a", "new alpha"),
            Chunk("record-1", "d", "new delta"),
        ]);

        var rows = await collection.GetAsync(row => row.RecordId == "record-1", 20).ToListAsync();
        Assert.Equal(["a", "d"], rows.Select(row => row.ChunkId).Order(StringComparer.Ordinal));
        Assert.Equal("new alpha", Assert.Single(rows, row => row.ChunkId == "a").Text);
    }

    [Fact]
    public async Task Stale_chunk_cleanup_pages_past_the_get_limit()
    {
        var (wide, collection) = Create(new HashedBagOfWordsEmbeddings());
        await wide.UpsertAsync(Record("record-1", "old"), [.. Enumerable.Range(0, 8).Select(i => Chunk("record-1", $"old-{i}", $"old {i}"))]);
        var narrow = new VectorDataMemoryBackend(collection, new HashedBagOfWordsEmbeddings(), HashedBagOfWordsEmbeddings.ModelId, getLimit: 3);

        await narrow.UpsertAsync(Record("record-1", "new"), [Chunk("record-1", "a", "new alpha"), Chunk("record-1", "b", "new beta")]);

        var rows = await collection.GetAsync(row => row.RecordId == "record-1", 20).ToListAsync();
        Assert.Equal(["a", "b"], rows.Select(row => row.ChunkId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Upsert_refuses_a_page_of_chunks_before_writing()
    {
        var (backend, collection) = Create(new HashedBagOfWordsEmbeddings(), getLimit: 3);

        await Assert.ThrowsAsync<ArgumentException>(() => backend.UpsertAsync(Record("record-1", "big"),
            [.. Enumerable.Range(0, 3).Select(i => Chunk("record-1", $"c{i}", $"chunk {i}"))]));

        Assert.False(await collection.CollectionExistsAsync() && await collection.GetAsync(_ => true, 5).AnyAsync());
    }

    [Fact]
    public async Task Bulk_delete_pages_until_every_record_is_removed()
    {
        var (backend, collection) = Create(new HashedBagOfWordsEmbeddings(), getLimit: 3);
        foreach (var i in Enumerable.Range(0, 10))
        {
            var id = $"record-{i}";
            await backend.UpsertAsync(Record(id, $"record content {i}"), [Chunk(id, "a", $"record content {i}")]);
        }

        var deleted = await backend.DeleteAsync(MemoryDeletion.ByCollection("vector-upsert-tests"));
        var remaining = await collection.GetAsync(_ => true, 20).ToListAsync();

        Assert.Equal(10, deleted);
        Assert.Empty(remaining);
    }

    private static (VectorDataMemoryBackend Backend, VectorStoreCollection<string, MemoryVectorRow> Collection) Create(
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        int getLimit = 10_000)
    {
        var store = new InMemoryVectorStore();
        var collection = store.GetCollection<string, MemoryVectorRow>(
            $"memory-{Guid.NewGuid():N}", MemoryVectorRow.Definition(4096));
        return (new VectorDataMemoryBackend(collection, embeddings, HashedBagOfWordsEmbeddings.ModelId, getLimit), collection);
    }

    private static MemoryRecord Record(string id, string content) => new(
        id,
        MemoryRecordKind.SourceDocument,
        new MemorySource(MemorySourceType.UserNote, "test", Acl: MemorySourceAcl.OrganisationWide),
        new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
        content,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Collection: "vector-upsert-tests");

    private static MemoryChunk Chunk(string recordId, string id, string content) =>
        new(id, recordId, 0, MemoryChunkKind.RawEvidence, content);

    private sealed class FailOnSecondEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HashedBagOfWordsEmbeddings _inner = new();
        private int _calls;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 2)
                throw new InvalidOperationException("embedding failed");
            return _inner.GenerateAsync(values, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => _inner.GetService(serviceType, serviceKey);
        public void Dispose() => _inner.Dispose();
    }
}
