using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.Sqlite;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>
/// SQLite vectors, step V6: how exact search scales. The measurement runs only with OTAIL_MEMORY_BENCH=1 (it writes up to 300 MB);
/// the advisory check always runs.
/// </summary>
public sealed class SqliteVectorBenchmarkTests(ITestOutputHelper output)
{
    private const int Dims = 1_536;
    private const int ChunksPerRecord = 5;

    [Fact]
    public void The_status_flags_the_advisory_limit_by_count_or_bytes_without_refusing_anything()
    {
        SqliteVectorIndexStatus Status(long count, long bytes) => new("m", true, 1, 1, 0, 0, count, bytes, 0);

        Assert.False(Status(50_000, 50_000L * Dims * 4).OverAdvisoryLimit);
        Assert.True(Status(50_001, 1).OverAdvisoryLimit); // many small vectors: many dot products
        Assert.True(Status(10, SqliteVectorIndexStatus.AdvisoryVectorBytes + 1).OverAdvisoryLimit); // few huge ones
        Assert.Contains("pgvector", Assert.Single(Status(60_000, 1).Advisories));
        Assert.Empty(Status(10, 10).Advisories);
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    [InlineData(50_000)]
    public async Task Measure_search_at(int vectors)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("OTAIL_MEMORY_BENCH") == "1", "set OTAIL_MEMORY_BENCH=1 to measure");
        var path = Path.Combine(Path.GetTempPath(), $"ot-bench-{Guid.NewGuid():N}.db");
        try
        {
            var backend = new SqliteMemoryBackend(path, new RandomEmbeddings(), new SqliteEmbeddingOptions("bench:random-1536", Dims));
            var write = Stopwatch.StartNew();
            for (var r = 0; r < vectors / ChunksPerRecord; r++)
                await backend.UpsertAsync(Record($"r{r}"), [.. Enumerable.Range(0, ChunksPerRecord).Select(c =>
                    new MemoryChunk($"r{r}-{c}", $"r{r}", c, MemoryChunkKind.RawEvidence, $"chunk {c} of record {r} words {r % 97} {c * 13}"))]);
            write.Stop();

            var status = await new SqliteMemoryVectorMaintenance(backend).GetStatusAsync();
            var (vectorMs, vectorAlloc) = await TimeAsync(() => backend.SearchAsync(new MemorySearch("record words", MemorySearchKinds.Vector, 10, new MemoryFilter())));
            var (hybridMs, _) = await TimeAsync(() => backend.SearchAsync(new MemorySearch("record words", MemorySearchKinds.Hybrid, 10, new MemoryFilter())));
            SqliteConnection.ClearAllPools();
            var row = $"| {status.VectorCount:N0} | {vectors / ChunksPerRecord:N0} | {status.VectorBytes / 1_048_576.0:N0} MB | {new FileInfo(path).Length / 1_048_576.0:N0} MB | " +
                             $"{vectorMs:N0} ms | {hybridMs:N0} ms | {vectorAlloc / 1024.0:N0} KB | {write.Elapsed.TotalSeconds:N0} s |";
            output.WriteLine(row);
            // The runner does not echo test output to the console; the rows also go to a file for the design doc.
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "ot-sqlite-vector-bench.md"), row + Environment.NewLine);
            Assert.Equal(vectors, status.VectorCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                File.Delete(path + suffix);
        }
    }

    /// <summary>
    /// Median time and allocated bytes over 7 runs, after one warm-up. Allocations are counted for the whole process
    /// (<see cref="GC.GetTotalAllocatedBytes"/>): a search awaits, so it can resume on another thread, and a per-thread count would
    /// miss or misattribute part of it. Nothing else runs during the measurement, so the process total is the search's.
    /// </summary>
    private static async Task<(double Ms, long Allocated)> TimeAsync(Func<Task> search)
    {
        await search();
        var times = new List<double>();
        var allocations = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var watch = Stopwatch.StartNew();
            await search();
            times.Add(watch.Elapsed.TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        return (times.Order().ElementAt(times.Count / 2), allocations.Order().ElementAt(allocations.Count / 2));
    }

    private static MemoryRecord Record(string id) => new(
        id,
        MemoryRecordKind.SourceDocument,
        new MemorySource(MemorySourceType.UserNote, "bench", Acl: MemorySourceAcl.OrganisationWide),
        new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
        id,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        Collection: "bench");

    private sealed class RandomEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(v =>
            {
                var random = new Random(v.GetHashCode());
                return new Embedding<float>(Enumerable.Range(0, Dims).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray());
            })));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
