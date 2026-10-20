using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryEntityRecallTests
{
    [Fact]
    public void Recall_WhenEntityMatchesByAlias_PivotsByEntityNotTextOnly()
    {
        var service = new DefaultMemoryEntityRecallService();

        var entities = new List<MemoryEntity>
        {
            new("entity:person:@alex", "person", "Alex", "@alex", ["alex", "@alex"], ["team:platform"]),
            new("entity:repository:opentail-net/opentail", "repository", "opentail-net/OpenTail", "opentail-net/OpenTail", ["opentail"], ["repo"]),
        };

        var links = new List<MemoryLink>
        {
            new("record-1", MemoryNodeKind.Record, "entity:person:@alex", MemoryNodeKind.Entity, MemoryRelationType.Mentioned, 0.8, "mention"),
            new("record-2", MemoryNodeKind.Record, "entity:person:@alex", MemoryNodeKind.Entity, MemoryRelationType.Reviewed, 0.9, "review"),
            new("record-2", MemoryNodeKind.Record, "entity:repository:opentail-net/opentail", MemoryNodeKind.Entity, MemoryRelationType.DiscussedIn, 0.7, "repo"),
        };

        var response = service.Recall(new MemoryEntityRecallRequest("alex", TopK: 5), entities, links);

        Assert.NotEmpty(response.Hits);
        Assert.Equal("entity:person:@alex", response.Hits[0].Entity.Id);
        Assert.True(response.Hits[0].Score > 0);
        Assert.Contains(response.Hits[0].Reasons, r => r.Contains("alias", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Recall_WhenSlackAndGitHubLinked_ContainsStorylineLinks()
    {
        var service = new DefaultMemoryEntityRecallService();

        var entities = new List<MemoryEntity>
        {
            new("entity:person:@casey", "person", "@casey", "@casey", ["casey"], ["slack", "github"]),
        };

        var links = new List<MemoryLink>
        {
            new("record:slack-thread-1", MemoryNodeKind.Record, "entity:person:@casey", MemoryNodeKind.Entity, MemoryRelationType.Mentioned, 0.8, "slack mention"),
            new("record:github-pr-123", MemoryNodeKind.Record, "entity:person:@casey", MemoryNodeKind.Entity, MemoryRelationType.Reviewed, 0.95, "github review"),
            new("record:decision-1", MemoryNodeKind.Record, "entity:person:@casey", MemoryNodeKind.Entity, MemoryRelationType.Approved, 0.9, "decision approval"),
        };

        var response = service.Recall(new MemoryEntityRecallRequest("@casey", TopK: 5), entities, links);

        var hit = Assert.Single(response.Hits);
        Assert.Equal(3, hit.Links.Count);
        Assert.Contains(hit.Links, l => l.SourceId.Contains("slack", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hit.Links, l => l.SourceId.Contains("github", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(hit.Links, l => l.SourceId.Contains("decision", StringComparison.OrdinalIgnoreCase));
    }
}
