using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class ToolExecutionMemoryIngestionNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_MapsToolExecutionCaptureIntoNormalizedEnvelope()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-1",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-123",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: "exitCode=0; output=ok",
            Title: "Build run",
            Status: "succeeded",
            Actor: "agent:implementer",
            ProjectScope: "OpenTail.Manager",
            RepositoryScope: "opentail-net/OpenTail",
            Labels: ["tool-call-processor"],
            DedupeKey: null,
            SourceUrl: "tool://run/tool-123",
            Permalink: "https://logs.example/tool-123",
            Metadata: new Dictionary<string, string>
            {
                ["durationMs"] = "120",
                ["tool.name"] = "functions.run_build"
            });

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("tool-execution:tool-runner:tool-123", normalized.NormalizedId);
        Assert.Equal(MemoryIngestionEventKind.ToolEvent, normalized.EventKind);
        Assert.Equal("tool-execution:tool-runner:tool-123", normalized.DedupeKey);
        Assert.Contains("ToolExecutionId: tool-123", normalized.Content);
        Assert.Contains("Invoker: agent:implementer", normalized.Content);
        Assert.Contains("Project: OpenTail.Manager", normalized.Content);
        Assert.Contains("Repository: opentail-net/OpenTail", normalized.Content);
        Assert.Contains("Title: Build run", normalized.Content);
        Assert.Contains("Status: succeeded", normalized.Content);
        Assert.Contains("ToolName: functions.run_build", normalized.Content);
        Assert.Contains("exitCode=0; output=ok", normalized.Content);
        Assert.Equal("ToolExecution", normalized.Metadata!["provenance.sourceType"]);
        Assert.Equal("tool-runner", normalized.Metadata["provenance.sourceSystem"]);
        Assert.Equal("tool-123", normalized.Metadata["provenance.externalId"]);
        Assert.Equal("agent:implementer", normalized.Metadata["provenance.invoker"]);
        Assert.Equal("OpenTail.Manager", normalized.Metadata["provenance.projectScope"]);
        Assert.Equal("tool://run/tool-123", normalized.Metadata["provenance.sourceUrl"]);
        Assert.Equal("https://logs.example/tool-123", normalized.Metadata["provenance.permalink"]);
        Assert.Equal("raw-preserved-policy-downstream", normalized.Metadata["normalization.payloadHandling"]);
        Assert.Equal("120", normalized.Metadata["durationMs"]);
    }

    [Fact]
    public async Task NormalizeAsync_WhenEventUnknown_DefaultsToToolEvent()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-2",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "tool-124",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: "ok");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal(MemoryIngestionEventKind.ToolEvent, normalized.EventKind);
        Assert.Equal("tool-execution", normalized.Metadata!["provenance.sourceSystem"]);
    }

    [Fact]
    public async Task NormalizeAsync_WhenMetadataToolNameMissing_FallsBackToFirstLabel()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-3",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-125",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: "payload",
            Labels: ["fallback-tool"]);

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Contains("ToolName: fallback-tool", normalized.Content);
    }

    [Fact]
    public async Task NormalizeAsync_PrefersThreadIdForThreadRoot()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-4",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-126",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: "payload",
            ConversationId: "conversation-1",
            ThreadId: "thread-1");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("thread-1", normalized.ThreadRootExternalId);
    }

    [Fact]
    public async Task NormalizeAsync_LongContent_SummaryUsesEllipsis()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-tool-5",
            SourceType: MemoryIngestionSourceType.ToolExecution,
            SourceSystem: "tool-runner",
            EventKind: MemoryIngestionEventKind.ToolEvent,
            ExternalId: "tool-127",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: new string('x', 300));

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.NotNull(normalized.Summary);
        Assert.EndsWith("…", normalized.Summary);
    }

    [Fact]
    public async Task NormalizeAsync_WhenNotToolExecution_Throws()
    {
        var normalizer = new ToolExecutionMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-diag-1",
            SourceType: MemoryIngestionSourceType.Diagnostic,
            SourceSystem: "doctor",
            EventKind: MemoryIngestionEventKind.DiagnosticEvent,
            ExternalId: "diag-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-20T10:00:00Z"),
            RawPayload: "diag output");

        await Assert.ThrowsAsync<InvalidOperationException>(() => normalizer.NormalizeAsync(capture));
    }
}
