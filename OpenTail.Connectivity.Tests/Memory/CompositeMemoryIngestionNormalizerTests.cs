using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class CompositeMemoryIngestionNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_DelegatesToRegisteredNormalizerForSourceType()
    {
        var composite = new CompositeMemoryIngestionNormalizer(new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>
        {
            [MemoryIngestionSourceType.UserNote] = new UserNoteMemoryIngestionNormalizer()
        });

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-note-dispatch-1",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            RawPayload: "hello");

        var normalized = await composite.NormalizeAsync(capture);

        Assert.Equal(MemoryIngestionSourceType.UserNote, normalized.SourceType);
        Assert.Equal("note-1", normalized.ExternalId);
    }

    [Fact]
    public void Constructor_ExposesRegisteredSourceTypes()
    {
        var composite = new CompositeMemoryIngestionNormalizer(new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>
        {
            [MemoryIngestionSourceType.UserNote] = new UserNoteMemoryIngestionNormalizer(),
            [MemoryIngestionSourceType.ToolExecution] = new ToolExecutionMemoryIngestionNormalizer(),
        });

        Assert.Contains(MemoryIngestionSourceType.UserNote, composite.RegisteredSourceTypes);
        Assert.Contains(MemoryIngestionSourceType.ToolExecution, composite.RegisteredSourceTypes);
    }

    [Fact]
    public async Task NormalizeAsync_WhenUnknownSourceType_ThrowsClearError()
    {
        var composite = new CompositeMemoryIngestionNormalizer(new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>
        {
            [MemoryIngestionSourceType.UserNote] = new UserNoteMemoryIngestionNormalizer()
        });

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-unknown-1",
            SourceType: MemoryIngestionSourceType.Unknown,
            SourceSystem: "",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "x",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            RawPayload: "payload");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => composite.NormalizeAsync(capture));
        Assert.Equal("Cannot normalize capture with Unknown source type.", ex.Message);
    }

    [Fact]
    public async Task NormalizeAsync_WhenSourceTypeNotRegistered_Throws()
    {
        var composite = new CompositeMemoryIngestionNormalizer(new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>
        {
            [MemoryIngestionSourceType.UserNote] = new UserNoteMemoryIngestionNormalizer()
        });

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-dispatch-1",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Issue,
            ExternalId: "issue-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-23T10:00:00Z"),
            RawPayload: "issue body");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => composite.NormalizeAsync(capture));
        Assert.Equal("No ingestion normalizer is registered for source type 'GitHub'.", ex.Message);
    }

    [Fact]
    public void Constructor_WhenEmptyRegistration_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CompositeMemoryIngestionNormalizer(
            new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>()));

        Assert.Equal("At least one normalizer must be registered. (Parameter 'normalizers')", ex.Message);
    }

    [Fact]
    public void Constructor_WhenUnknownRegistered_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => new CompositeMemoryIngestionNormalizer(
            new Dictionary<MemoryIngestionSourceType, IMemoryIngestionNormalizer>
            {
                [MemoryIngestionSourceType.Unknown] = new UserNoteMemoryIngestionNormalizer()
            }));

        Assert.Equal("Unknown source type cannot be registered. (Parameter 'normalizers')", ex.Message);
    }
}
