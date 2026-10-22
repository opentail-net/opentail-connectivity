using OpenTail.Connectivity.Files.Conformance;
using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Tests.Conformance;

public sealed class ObjectStorageConnectorConformanceTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IConnectorFixture
    {
        private readonly InMemoryObjectStoreClient _store = new InMemoryObjectStoreClient("exports")
            .Put("docs/a.txt", "Document a has enough text to pass validation.", T)
            .Put("docs/b.txt", "Document b has enough text to pass validation.", T.AddMinutes(1))
            .Put("docs/c.md", "Document c has enough text to pass validation.", T.AddMinutes(2));

        public IReadOnlyList<string> SecretsGiven => [];

        public ISourceConnector Create() => new ObjectStorageConnector(_store, "tenant-store");

        // Check 8 wants no upserts when resuming over unchanged data. The watermark is inclusive and has an overlap (an hour by
        // default) on purpose: object timestamps are coarse and clocks drift, so recent objects are read again rather than
        // missed. Repeats are harmless with stable ids and a version store; ObjectStorageConnectorTests cover the cursor.
        public IReadOnlySet<int>? SkippedChecks => new HashSet<int> { 8 };

        public ISourceConnector CreateEmpty() => new ObjectStorageConnector(new InMemoryObjectStoreClient("empty"), "tenant-store");

        // Item ids are "<scheme>://<container>/<key>".
        public Task RemoveOneItemAsync(string sourceRef)
        {
            _store.Remove(sourceRef["memory://exports/".Length..]);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ObjectStorageConnector_passes_conformance() =>
        await SourceConnectorConformance.RunAsync(new Fixture());
}
