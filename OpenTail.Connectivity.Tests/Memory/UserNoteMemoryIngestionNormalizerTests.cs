using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class UserNoteMemoryIngestionNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_MapsUserNoteCaptureIntoNormalizedEnvelope()
    {
        var normalizer = new UserNoteMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-note-1",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-123",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            RawPayload: "Approved: move memory retrieval to scoped reranking.",
            Title: "Memory retrieval decision",
            Status: "approved",
            Actor: "operator:casey",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail",
            ConversationId: "crew-planning",
            ThreadId: "decision-thread-1",
            SourceUrl: "opentail://notes/note-123",
            Permalink: "https://opentail.local/notes/note-123",
            Labels: ["decision", "memory"],
            LinkedExternalIds: ["pr-456"],
            DedupeKey: null,
            Metadata: new Dictionary<string, string>
            {
                ["note.kind"] = "decision",
                ["lane"] = "memory"
            });

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("user-note:operator-console:note-123", normalized.NormalizedId);
        Assert.Equal(MemoryIngestionEventKind.Document, normalized.EventKind);
        Assert.Equal("user-note:operator-console:note-123", normalized.DedupeKey);
        Assert.Equal("decision-thread-1", normalized.ThreadRootExternalId);
        Assert.Contains("UserNoteId: note-123", normalized.Content);
        Assert.Contains("Title: Memory retrieval decision", normalized.Content);
        Assert.Contains("Status: approved", normalized.Content);
        Assert.Contains("Author: operator:casey", normalized.Content);
        Assert.Contains("Project: OpenTail.Core", normalized.Content);
        Assert.Contains("Repository: opentail-net/OpenTail", normalized.Content);
        Assert.Contains("Labels: decision, memory", normalized.Content);
        Assert.Contains("NoteKind: decision", normalized.Content);
        Assert.Contains("Approved: move memory retrieval to scoped reranking.", normalized.Content);
        Assert.Equal("UserNote", normalized.Metadata!["provenance.sourceType"]);
        Assert.Equal("operator-console", normalized.Metadata["provenance.sourceSystem"]);
        Assert.Equal("note-123", normalized.Metadata["provenance.externalId"]);
        Assert.Equal("operator:casey", normalized.Metadata["provenance.actor"]);
        Assert.Equal("OpenTail.Core", normalized.Metadata["provenance.projectScope"]);
        Assert.Equal("opentail-net/OpenTail", normalized.Metadata["provenance.repositoryScope"]);
        Assert.Equal("opentail://notes/note-123", normalized.Metadata["provenance.sourceUrl"]);
        Assert.Equal("https://opentail.local/notes/note-123", normalized.Metadata["provenance.permalink"]);
        Assert.Equal("memory", normalized.Metadata["lane"]);
    }

    [Fact]
    public async Task NormalizeAsync_WhenEventUnknown_DefaultsToDocument()
    {
        var normalizer = new UserNoteMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-note-2",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "note-124",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            RawPayload: "note body");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal(MemoryIngestionEventKind.Document, normalized.EventKind);
        Assert.Equal("user-note", normalized.Metadata!["provenance.sourceSystem"]);
        Assert.Equal("user-note:user-note:note-124", normalized.DedupeKey);
    }

    [Fact]
    public async Task NormalizeAsync_LongContent_SummaryUsesEllipsis()
    {
        var normalizer = new UserNoteMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-note-3",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-125",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            RawPayload: new string('x', 500));

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.NotNull(normalized.Summary);
        Assert.EndsWith("…", normalized.Summary);
    }

    [Fact]
    public async Task NormalizeAsync_WhenNotUserNote_Throws()
    {
        var normalizer = new UserNoteMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-note-4",
            SourceType: MemoryIngestionSourceType.WorkflowRun,
            SourceSystem: "workflow",
            EventKind: MemoryIngestionEventKind.WorkflowEvent,
            ExternalId: "run-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-22T10:00:00Z"),
            RawPayload: "payload");

        await Assert.ThrowsAsync<InvalidOperationException>(() => normalizer.NormalizeAsync(capture));
    }
}
