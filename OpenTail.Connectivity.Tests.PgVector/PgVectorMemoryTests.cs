using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.InMemory;
using Microsoft.SemanticKernel.Connectors.PgVector;
using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.VectorData;
using OpenTail.Connectivity.Tests.Memory;
using Xunit;

namespace OpenTail.Connectivity.Tests.PgVector;

/// <summary>
/// The VectorData backend on a real Postgres with pgvector, with an exact (flat) and an approximate (HNSW) index. Needs
/// OTAIL_MEMORY_PGVECTOR, a connection string to a database where the vector extension exists; skipped otherwise.
/// </summary>
public sealed class PgVectorMemoryTests
{
    // pgvector's HNSW index takes at most 2,000 dimensions.
    private const int Dimensions = 2000;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("OTAIL_MEMORY_PGVECTOR");

    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

    private static readonly Dictionary<string, string?> IndexKinds = new(StringComparer.Ordinal)
    {
        ["flat"] = IndexKind.Flat,
        ["hnsw"] = IndexKind.Hnsw,
    };

    private static IMemoryBackend New(string index, TimeProvider? time = null)
    {
        var embeddings = new HashedBagOfWordsEmbeddings(Dimensions);
        var definition = MemoryVectorRow.Definition(Dimensions, DistanceFunction.CosineDistance, IndexKinds[index]);
        var collection = new PostgresCollection<string, MemoryVectorRow>(
            ConnectionString!, $"memory_{index}_{Guid.NewGuid():N}", new PostgresCollectionOptions { Definition = definition });
        return new VectorDataMemoryBackend(collection, embeddings, HashedBagOfWordsEmbeddings.ModelId, DistanceFunction.CosineDistance, minScore: 0.05, time: time);
    }

    // Only the checks that apply: a skipped test in this project means the server is missing, and the workflow fails on it.
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        var probe = MemoryBackendConformance.Checks(static () => new VectorDataMemoryBackend(
            new InMemoryVectorStore().GetCollection<string, MemoryVectorRow>("probe", MemoryVectorRow.Definition(Dimensions)),
            new HashedBagOfWordsEmbeddings(Dimensions), HashedBagOfWordsEmbeddings.ModelId));
        foreach (var index in IndexKinds.Keys)
            foreach (var check in probe.Where(static c => c.AppliesTo))
                data.Add(index, check.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Conformance(string index, string check)
    {
        Assert.SkipWhen(ConnectionString is null, "OTAIL_MEMORY_PGVECTOR is not set");
        await MemoryBackendConformance.Checks(() => New(index)).Single(c => c.Name == check).RunAsync();
    }

    [Theory]
    [InlineData("flat")]
    [InlineData("hnsw")]
    public async Task QualityGate(string index)
    {
        Assert.SkipWhen(ConnectionString is null, "OTAIL_MEMORY_PGVECTOR is not set");
        var report = await MemoryQualityGate.RunAsync($"pgvector-{index}", New(index, Clock), MemoryQualityCorpus.Records, MemoryQualityCorpus.Scenarios, time: Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Cases.Where(static c => !c.Passed)
            .Select(static c => $"{c.ScenarioId}: recall={c.RecallAtK:0.00} returned=[{string.Join(", ", c.ReturnedIds)}] {string.Join("; ", c.Notes)}")));
    }

    // Runs everywhere: the same embedding size on the in-memory connector, so a failure on the server is the server's, not the
    // test embedding's (hash collisions grow as the size shrinks).
    [Fact]
    public async Task QualityGate_SameEmbeddingSizeInMemory()
    {
        var embeddings = new HashedBagOfWordsEmbeddings(Dimensions);
        var collection = new InMemoryVectorStore().GetCollection<string, MemoryVectorRow>("gate", MemoryVectorRow.Definition(Dimensions));
        var backend = new VectorDataMemoryBackend(collection, embeddings, HashedBagOfWordsEmbeddings.ModelId, minScore: 0.05, time: Clock);

        var report = await MemoryQualityGate.RunAsync("in-memory-2000", backend, MemoryQualityCorpus.Records, MemoryQualityCorpus.Scenarios, time: Clock, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Passed, string.Join(Environment.NewLine, report.Cases.Where(static c => !c.Passed).Select(static c => c.ScenarioId)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
