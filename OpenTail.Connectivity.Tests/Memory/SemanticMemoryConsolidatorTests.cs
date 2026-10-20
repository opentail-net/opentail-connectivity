using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class SemanticMemoryConsolidatorTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static MemoryRecord Record(string id, string? scope = null, bool tainted = false, int ageDays = 0)
        => new(
            id,
            MemoryRecordKind.DecisionLog,
            new MemorySource(MemorySourceType.UserNote, "test", Acl: MemorySourceAcl.OrganisationWide),
            new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Durable, PermissionScope: scope, Tainted: tainted),
            "Release notes go out every Friday after the review.",
            Created.AddDays(-ageDays),
            Created);

    private static async Task<IReadOnlyList<MemoryMergeSuggestion>> SuggestAsync(params MemoryRecord[] records)
    {
        var backend = new InMemoryMemoryBackend();
        foreach (var r in records)
            await backend.UpsertAsync(r, []);
        return await new SemanticMemoryConsolidator(backend).FindDuplicatesAsync(records);
    }

    [Fact]
    public async Task SuggestsDuplicatesWithinOneAudience_OlderIsCanonical()
    {
        var suggestion = Assert.Single(await SuggestAsync(Record("old", ageDays: 5), Record("new")));

        Assert.Equal("old", suggestion.CanonicalId);
        Assert.Equal("new", suggestion.DuplicateId);
    }

    [Fact]
    public async Task NeverPairsAcrossPermissionScopes()
        => Assert.Empty(await SuggestAsync(Record("board", scope: "board"), Record("staff", scope: "staff")));

    [Fact]
    public async Task NeverPairsTaintedWithUntainted()
        => Assert.Empty(await SuggestAsync(Record("email", tainted: true), Record("note")));
}
