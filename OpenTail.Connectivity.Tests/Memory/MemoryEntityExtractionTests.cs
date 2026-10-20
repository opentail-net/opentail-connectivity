using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryEntityExtractionTests
{
    [Fact]
    public void Extract_WhenSlackAndGitHubSignalsPresent_ProducesCrossSourceEntitiesAndLinks()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "slack-msg-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "@alex discussed PR review in opentail-net/OpenTail and approved decision",
            Actor: "user:casey",
            RepositoryScope: "opentail-net/OpenTail",
            Mentions: ["@alex"],
            Title: "Decision thread",
            Status: "approved");

        var chunks = new List<MemoryChunk>
        {
            new("chunk-1", "record-1", 0, MemoryChunkKind.RawEvidence, "GitHub review noted blocking issue", null, null, null)
        };

        var result = extractor.Extract(envelope, recordId: "record-1", chunks);

        Assert.NotEmpty(result.Entities);
        Assert.NotEmpty(result.Links);
        Assert.Contains(result.Entities, e => e.Kind == "person" && e.Id.Contains("user:casey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Entities, e => e.Kind == "repository" && e.Name == "opentail-net/OpenTail");
        Assert.Contains(result.Entities, e => e.Kind == "decision");
        Assert.Contains(result.Links, l => l.RelationType == MemoryRelationType.Authored);
        Assert.Contains(result.Links, l => l.RelationType == MemoryRelationType.Mentioned);
        Assert.Contains(result.Links, l => l.RelationType == MemoryRelationType.DiscussedIn);
        Assert.Contains(result.Links, l => l.RelationType == MemoryRelationType.Approved);
    }

    [Fact]
    public void Extract_WhenDecisionTextIsRejected_UsesRejectedRelation()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-4",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "Decision rejected after review.",
            Title: "Model switch",
            Actor: "operator:casey");

        var result = extractor.Extract(envelope, "record-4", []);

        Assert.Contains(result.Links, l => l.RelationType == MemoryRelationType.Rejected);
        Assert.DoesNotContain(result.Links, l => l.RelationType == MemoryRelationType.Approved && l.Evidence == "decision");
    }

    [Fact]
    public void Extract_WhenToolNameMetadataPresent_PrefersToolNameSeed()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-5",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-run-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "tool execution output",
            Metadata: new Dictionary<string, string> { ["tool.name"] = "functions.run_build" });

        var result = extractor.Extract(envelope, "record-5", []);

        Assert.Contains(result.Entities, e => e.Kind == "tool" && e.Name == "functions.run_build");
    }

    [Fact]
    public void Extract_WhenAliasVariantsExist_MergesEntityAliases()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-2",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Comment,
            ExternalId: "gh-comment-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "@Alex and @alex both reviewed the same issue",
            Mentions: ["@Alex", "@alex"],
            Actor: "@Alex");

        var result = extractor.Extract(envelope, "record-2", []);

        var person = Assert.Single(result.Entities, e => e.Kind == "person" && e.Id.Contains("alex", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("@", person.Id);
        Assert.Contains(person.Aliases!, a => a.Contains("@alex", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(person.Aliases!, a => string.Equals(a, "alex", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extract_WhenPersonIdentityContainsOnlyPunctuation_UsesUnknownFallbackId()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-6",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "@!!!",
            Mentions: ["@!!!"]);

        var result = extractor.Extract(envelope, "record-6", []);

        var person = Assert.Single(result.Entities, e => e.Kind == "person");
        Assert.Equal("entity:person:unknown", person.Id);
    }

    [Fact]
    public void Extract_WhenNoEntitySignals_ReturnsDiagnostics()
    {
        var extractor = new DefaultMemoryEntityExtractor();

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-entity-3",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "docs",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "doc-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-25T10:00:00Z"),
            Content: "plain informational paragraph with no markers",
            Title: "General note",
            Status: "open");

        var result = extractor.Extract(envelope, "record-3", []);

        Assert.Empty(result.Entities);
        Assert.Empty(result.Links);
        Assert.Contains("entity-extraction:no-entities-detected", result.Diagnostics);
    }
}
