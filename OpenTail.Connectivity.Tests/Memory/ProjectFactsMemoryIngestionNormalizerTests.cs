using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class ProjectFactsMemoryIngestionNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_MapsProjectFactCaptureIntoNormalizedEnvelope()
    {
        var normalizer = new ProjectFactsMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-fact-1",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "ledger",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "fact-123",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            RawPayload: "Default model preference is Auto (local)",
            Title: "Model selection policy",
            Status: "active",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail",
            SourceUrl: "C:/Git/OpenTail/OpenTail.Core/Workflows/POLICY.md",
            Labels: ["policy", "default-model"],
            DedupeKey: null,
            Metadata: new Dictionary<string, string> { ["stability"] = "durable" });

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("project-fact:ledger:fact-123", normalized.NormalizedId);
        Assert.Equal(MemoryIngestionEventKind.Document, normalized.EventKind);
        Assert.Equal("project-fact:ledger:fact-123", normalized.DedupeKey);
        Assert.Contains("ProjectFactId: fact-123", normalized.Content);
        Assert.Contains("Project: OpenTail.Core", normalized.Content);
        Assert.Contains("Repository: opentail-net/OpenTail", normalized.Content);
        Assert.Contains("Title: Model selection policy", normalized.Content);
        Assert.Contains("Status: active", normalized.Content);
        Assert.Contains("SourcePath: C:/Git/OpenTail/OpenTail.Core/Workflows/POLICY.md", normalized.Content);
        Assert.Contains("FactTags: policy, default-model", normalized.Content);
        Assert.Contains("Default model preference is Auto (local)", normalized.Content);
        Assert.Equal("LocalDocs", normalized.Metadata!["provenance.sourceType"]);
        Assert.Equal("ledger", normalized.Metadata["provenance.sourceSystem"]);
        Assert.Equal("fact-123", normalized.Metadata["provenance.externalId"]);
        Assert.Equal("OpenTail.Core", normalized.Metadata["provenance.projectScope"]);
        Assert.Equal("opentail-net/OpenTail", normalized.Metadata["provenance.repositoryScope"]);
        Assert.Equal("C:/Git/OpenTail/OpenTail.Core/Workflows/POLICY.md", normalized.Metadata["provenance.sourcePath"]);
        Assert.Equal("durable", normalized.Metadata["stability"]);
    }

    [Fact]
    public async Task NormalizeAsync_WhenEventUnknown_DefaultsToDocument()
    {
        var normalizer = new ProjectFactsMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-fact-2",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "fact-124",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            RawPayload: "fact body");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal(MemoryIngestionEventKind.Document, normalized.EventKind);
        Assert.Equal("local-docs", normalized.Metadata!["provenance.sourceSystem"]);
        Assert.Equal("project-fact:local-docs:fact-124", normalized.DedupeKey);
    }

    [Fact]
    public async Task NormalizeAsync_LongContent_SummaryUsesEllipsis()
    {
        var normalizer = new ProjectFactsMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-fact-3",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "ledger",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "fact-125",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            RawPayload: new string('x', 400));

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.NotNull(normalized.Summary);
        Assert.EndsWith("…", normalized.Summary);
    }

    [Fact]
    public async Task NormalizeAsync_WhenNotLocalDocs_Throws()
    {
        var normalizer = new ProjectFactsMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-1",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-21T10:00:00Z"),
            RawPayload: "tool output");

        await Assert.ThrowsAsync<InvalidOperationException>(() => normalizer.NormalizeAsync(capture));
    }
}
