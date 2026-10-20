using OpenTail.Connectivity.Memory;
using OpenTail.Connectivity.Memory.Sqlite;
using OpenTail.Connectivity.Memory.VectorData;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class MemoryQualityGateTests
{
    // The corpus is dated 2026; a fixed clock keeps expiry and windows stable.
    private static readonly TimeProvider Clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));

    private static Task<MemoryQualityReport> Gate(string name, IMemoryBackend backend)
        => MemoryQualityGate.RunAsync(name, backend, MemoryQualityCorpus.Records, MemoryQualityCorpus.Scenarios, time: Clock);

    public static TheoryData<string> Backends() => ["in-memory", "sqlite", "files", "vectordata-inmemory"];

    private static IMemoryBackend New(string name) => name switch
    {
        "in-memory" => new InMemoryMemoryBackend(Clock),
        "files" => new FilesMemoryBackend(Directory.CreateTempSubdirectory("ot-quality-files-").FullName, Clock),
        "vectordata-inmemory" => VectorDataInMemory.New(Clock),
        _ => new SqliteMemoryBackend(Path.Combine(Path.GetTempPath(), $"ot-quality-{Guid.NewGuid():N}.db"), Clock),
    };

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task Backend_PassesTheDefaultGate(string backend)
    {
        var report = await Gate(backend, New(backend));

        var failures = report.Cases.Where(static c => !c.Passed)
            .Select(static c => $"{c.ScenarioId}: recall={c.RecallAtK:0.00} mrr={c.Mrr:0.00} returned=[{string.Join(", ", c.ReturnedIds)}] {string.Join("; ", c.Notes)}");
        Assert.True(report.Passed, string.Join(Environment.NewLine, failures));
        Assert.Equal(Enum.GetValues<MemoryAbility>().Length, report.RecallByAbility.Count);
        Assert.All(report.Cases, static c => Assert.Equal(0, c.Leaks + c.UntrustedAsFact + c.SupersededAsCurrent));
    }

    [Fact]
    public async Task TheTwoBackendsAgree()
    {
        var memory = await Gate("in-memory", New("in-memory"));
        var sqlite = await Gate("sqlite", New("sqlite"));

        var files = await Gate("files", New("files"));

        Assert.Empty(MemoryQualityGate.Compare(memory, sqlite));
        Assert.Empty(MemoryQualityGate.Compare(memory, files));
    }

    [Fact]
    public async Task ALeakingBackendFailsThePermissionScenarios()
    {
        var report = await Gate("leaky", new IgnoresAccess(New("in-memory")));

        Assert.False(report.Passed);
        var staff = report.Cases.Single(static c => c.ScenarioId == "permission-acquisition-staff");
        Assert.False(staff.Passed);
        Assert.Equal(0, staff.Leaks);
        Assert.DoesNotContain("board-acquisition", staff.ReturnedIds);
    }

    [Fact]
    public async Task CompareListsNewlyFailingFirst()
    {
        var good = await Gate("in-memory", New("in-memory"));
        var bad = good with
        {
            Cases = [.. good.Cases.Select(static c => c.ScenarioId == "extraction-owner" ? c with { Passed = false, RecallAtK = 0 } : c)],
        };

        var change = Assert.Single(MemoryQualityGate.Compare(good, bad));
        Assert.Equal("extraction-owner", change.ScenarioId);
        Assert.False(change.NowPasses);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // Claims to filter before ranking but ignores who is asking. The retriever's final check still withholds what it should,
    // so leaks stay at zero, but recall changes and the permission scenarios show it.
    private sealed class IgnoresAccess(IMemoryBackend inner) : IMemoryBackend
    {
        public MemoryBackendCapabilities Capabilities => inner.Capabilities;

        public Task UpsertAsync(MemoryRecord record, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default) => inner.UpsertAsync(record, chunks, cancellationToken);

        public Task<MemoryRecord?> GetAsync(string recordId, CancellationToken cancellationToken = default) => inner.GetAsync(recordId, cancellationToken);

        public Task<int> DeleteAsync(MemoryDeletion deletion, CancellationToken cancellationToken = default) => inner.DeleteAsync(deletion, cancellationToken);

        public Task<IReadOnlyList<BackendHit>> SearchAsync(MemorySearch search, CancellationToken cancellationToken = default)
            => inner.SearchAsync(search with { Filter = new MemoryFilter(Scopes: ["board"], Principals: ["group:hr"]), TopK = 1 }, cancellationToken);
    }
}
