using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.Sqlite;
using OpenTail.Connectivity.Memory.VectorData;

namespace OpenTail.Connectivity.Tests.Memory;

/// <summary>Every backend in this repository runs the full conformance suite.</summary>
public sealed class MemoryBackendConformanceTests
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var backend in Factories.Keys)
            foreach (var check in MemoryBackendConformance.Checks(Factories[backend]))
                data.Add(backend, check.Name);
        return data;
    }

    private static readonly Dictionary<string, Func<IMemoryBackend>> Factories = new(StringComparer.Ordinal)
    {
        ["in-memory"] = static () => new InMemoryMemoryBackend(),
        ["sqlite"] = static () => new SqliteMemoryBackend(Path.Combine(Path.GetTempPath(), $"ot-memory-{Guid.NewGuid():N}.db")),
        ["files"] = static () => new FilesMemoryBackend(Directory.CreateTempSubdirectory("ot-memory-files-").FullName),
        ["vectordata-inmemory"] = static () => VectorDataInMemory.New(),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Backend_PassesCheck(string backend, string check)
    {
        var c = MemoryBackendConformance.Checks(Factories[backend]).Single(x => x.Name == check);
        Assert.SkipUnless(c.AppliesTo, $"{backend} does not declare the capability this check needs");
        await c.RunAsync();
    }

    [Theory]
    [InlineData("filters before ranking")]
    [InlineData("words cannot raise trust")]
    public async Task Suite_CatchesABackendThatFiltersAfterRanking(string check)
    {
        var c = MemoryBackendConformance.Checks(static () => new FiltersAfterTopK(new InMemoryMemoryBackend())).Single(x => x.Name == check);

        await Assert.ThrowsAsync<MemoryConformanceException>(c.RunAsync);
    }

    // Claims to filter before ranking but takes top-k first and filters afterwards, and checks trust by looking for words.
    private sealed class FiltersAfterTopK(IMemoryBackend inner) : IMemoryBackend
    {
        public MemoryBackendCapabilities Capabilities => inner.Capabilities;

        public Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default) => inner.UpsertAsync(record, chunks, cancellationToken);

        public Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default) => inner.GetAsync(recordId, cancellationToken);

        public Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default) => inner.DeleteAsync(deletion, cancellationToken);

        public async Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
        {
            var unfiltered = await inner.SearchAsync(search with { Filter = new MemoryFilter() }, cancellationToken);
            return [.. unfiltered.Where(h => Allowed(h, search.Filter))];
        }

        private static bool Allowed(BackendHit hit, MemoryFilter filter)
        {
            if (filter.Scopes is { Count: > 0 } scopes && hit.Record.Policy.PermissionScope is { } scope && !scopes.Contains(scope))
                return false;
            if (filter.MinTrustLevel is { } min && min > MemoryTrustLevel.Low)
                return hit.Record.Policy.TrustLevel >= min || hit.Text.Contains("verified", StringComparison.OrdinalIgnoreCase);
            return true;
        }
    }
}
