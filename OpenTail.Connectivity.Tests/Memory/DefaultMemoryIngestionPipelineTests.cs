using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class DefaultMemoryIngestionPipelineTests
{
    private sealed class EmptyChunker : IMemoryIngestionChunker
    {
        public Task<IReadOnlyList<MemoryChunk>> ChunkAsync(MemoryNormalizedEnvelope envelope, string parentRecordId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryChunk>>([]);
    }

    private sealed class DroppingEmbedder : IMemoryIngestionEmbedder
    {
        public Task<IReadOnlyList<MemoryChunk>> EmbedAsync(IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryChunk>>([]);
    }

    private sealed class ThrowingChunker : IMemoryIngestionChunker
    {
        public Task<IReadOnlyList<MemoryChunk>> ChunkAsync(MemoryNormalizedEnvelope envelope, string parentRecordId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated chunk failure");
    }

    private sealed class PassThroughNormalizer : IMemoryIngestionNormalizer
    {
        public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryNormalizedEnvelope(
                NormalizedId: $"norm-{capture.EnvelopeId}",
                SourceType: capture.SourceType,
                SourceSystem: capture.SourceSystem,
                EventKind: capture.EventKind,
                ExternalId: capture.ExternalId,
                CapturedAtUtc: capture.CapturedAtUtc,
                ObservedAtUtc: capture.ObservedAtUtc,
                Content: capture.RawPayload,
                Title: capture.Title,
                Status: capture.Status,
                Summary: null,
                Workspace: capture.Workspace,
                ProjectScope: capture.ProjectScope,
                RepositoryScope: capture.RepositoryScope,
                ConversationId: capture.ConversationId,
                ThreadId: capture.ThreadId,
                ThreadRootExternalId: capture.ThreadRootExternalId,
                Actor: capture.Actor,
                SourceUrl: capture.SourceUrl,
                ContentHash: capture.ContentHash,
                DedupeKey: capture.DedupeKey,
                Policy: null,
                Metadata: capture.Metadata));
    }

    private sealed class TransformingNormalizer : IMemoryIngestionNormalizer
    {
        public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryNormalizedEnvelope(
                NormalizedId: $"norm-{capture.EnvelopeId}",
                SourceType: capture.SourceType,
                SourceSystem: capture.SourceSystem,
                EventKind: capture.EventKind,
                ExternalId: capture.ExternalId,
                CapturedAtUtc: capture.CapturedAtUtc,
                ObservedAtUtc: capture.ObservedAtUtc,
                Content: "normalized-content",
                Title: capture.Title,
                Status: capture.Status,
                Summary: "normalized-summary",
                Workspace: capture.Workspace,
                ProjectScope: capture.ProjectScope,
                RepositoryScope: capture.RepositoryScope,
                ConversationId: capture.ConversationId,
                ThreadId: capture.ThreadId,
                ThreadRootExternalId: capture.ThreadRootExternalId,
                Actor: capture.Actor,
                SourceUrl: capture.SourceUrl,
                ContentHash: capture.ContentHash,
                DedupeKey: capture.DedupeKey,
                Policy: null,
                Metadata: capture.Metadata));
    }

    private sealed class EmptyContentNormalizer : IMemoryIngestionNormalizer
    {
        public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryNormalizedEnvelope(
                NormalizedId: $"norm-{capture.EnvelopeId}",
                SourceType: capture.SourceType,
                SourceSystem: capture.SourceSystem,
                EventKind: capture.EventKind,
                ExternalId: capture.ExternalId,
                CapturedAtUtc: capture.CapturedAtUtc,
                ObservedAtUtc: capture.ObservedAtUtc,
                Content: string.Empty,
                Title: capture.Title,
                Status: capture.Status,
                DedupeKey: capture.DedupeKey));
    }

    private sealed class ThrowingLinker : IMemoryIngestionLinker
    {
        public Task LinkAsync(string recordId, MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated link failure");
    }

    private sealed class RecordingStore : IMemoryIngestionStore
    {
        public MemoryNormalizedEnvelope? LastEnvelope { get; private set; }
        public IReadOnlyList<MemoryChunk>? LastChunks { get; private set; }
        public string? LastDecisionRecordId { get; private set; }

        public Task<string> StoreAsync(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks, MemoryIngestionDecision decision, CancellationToken cancellationToken = default)
        {
            LastEnvelope = envelope;
            LastChunks = chunks;
            LastDecisionRecordId = decision.ExistingRecordId;
            return Task.FromResult($"record-{envelope.NormalizedId}");
        }
    }

    private sealed class CancelingNormalizer : IMemoryIngestionNormalizer
    {
        public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
            => throw new OperationCanceledException(cancellationToken);
    }

    [Fact]
    public async Task IngestAsync_NewEnvelope_SucceedsWithOrderedAuditStages()
    {
        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "hello world",
            DedupeKey: "slack:msg-1",
            ContentHash: "hash-1");

        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry());

        var result = await pipeline.IngestAsync(capture);

        Assert.True(result.Success);
        Assert.NotNull(result.RecordId);
        Assert.Equal("slack:msg-1", result.DedupeKey);
        Assert.NotNull(result.AuditTrail);

        var stages = result.AuditTrail!.Select(a => a.Stage).ToList();
        Assert.Equal(
        [
            MemoryIngestionStage.Capture,
            MemoryIngestionStage.Normalize,
            MemoryIngestionStage.Extract,
            MemoryIngestionStage.Chunk,
            MemoryIngestionStage.Embed,
            MemoryIngestionStage.Store,
            MemoryIngestionStage.Link,
            MemoryIngestionStage.Audit,
        ],
        stages);
    }

    [Fact]
    public async Task IngestAsync_DuplicateEnvelope_ReportsDuplicateWithoutFailure()
    {
        var registry = new InMemoryIngestionRegistry();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: registry);

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-dup",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-dup",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "same payload",
            DedupeKey: "slack:msg-dup",
            ContentHash: "hash-dup");

        var first = await pipeline.IngestAsync(capture);
        var second = await pipeline.IngestAsync(capture);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.True(second.IsDuplicate);
        Assert.False(second.IsReplay);
        Assert.NotNull(second.RecordId);

        var stages = second.AuditTrail!.Select(a => a.Stage).ToList();
        Assert.Equal(
        [
            MemoryIngestionStage.Capture,
            MemoryIngestionStage.Normalize,
            MemoryIngestionStage.Audit,
        ],
        stages);
    }

    [Fact]
    public async Task IngestAsync_RepeatImport_IsIdempotentWithStableRecordIdentity()
    {
        var registry = new InMemoryIngestionRegistry();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: registry);

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-idempotent-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-idempotent",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "same import payload",
            DedupeKey: "slack:msg-idempotent",
            ContentHash: "hash-idempotent");

        var first = await pipeline.IngestAsync(capture);
        var second = await pipeline.IngestAsync(capture);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotNull(first.RecordId);
        Assert.Equal(first.RecordId, second.RecordId);
        Assert.True(second.IsDuplicate);
        Assert.False(second.IsReplay);
        Assert.Equal(first.DedupeKey, second.DedupeKey);

        var stages = second.AuditTrail!.Select(a => a.Stage).ToList();
        Assert.Equal(
        [
            MemoryIngestionStage.Capture,
            MemoryIngestionStage.Normalize,
            MemoryIngestionStage.Audit,
        ],
        stages);
    }

    [Fact]
    public async Task IngestAsync_OlderReplay_IsRejectedWithAuditError()
    {
        var registry = new InMemoryIngestionRegistry();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: registry);

        var newer = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-new",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-r",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "newer",
            DedupeKey: "slack:msg-r",
            ContentHash: "hash-new");

        var olderReplay = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-old",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-r",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T10:00:00Z"),
            RawPayload: "older",
            DedupeKey: "slack:msg-r",
            ContentHash: "hash-old");

        var first = await pipeline.IngestAsync(newer);
        var second = await pipeline.IngestAsync(olderReplay);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Contains("Out-of-order replay", second.Error);
        Assert.NotNull(second.AuditTrail);
        Assert.Equal(MemoryIngestionStage.Audit, second.AuditTrail![^1].Stage);
        Assert.False(second.AuditTrail![^1].Success);
    }

    [Fact]
    public async Task IngestAsync_EditedReplayUpdate_PreservesRecordIdentityWithoutHistoryCorruption()
    {
        var registry = new InMemoryIngestionRegistry();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: registry);

        var original = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-replay-original",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-edit-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "original content",
            DedupeKey: "slack:msg-edit-1",
            ContentHash: "hash-original");

        var editedReplay = original with
        {
            EnvelopeId = "env-replay-edited",
            CapturedAtUtc = DateTimeOffset.Parse("2026-05-14T11:03:00Z"),
            ObservedAtUtc = DateTimeOffset.Parse("2026-05-14T11:05:00Z"),
            RawPayload = "edited content",
            ContentHash = "hash-edited",
            EditedAtUtc = DateTimeOffset.Parse("2026-05-14T11:04:00Z")
        };

        var first = await pipeline.IngestAsync(original);
        var second = await pipeline.IngestAsync(editedReplay);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.False(second.IsDuplicate);
        Assert.True(second.IsReplay);
        Assert.NotNull(first.RecordId);
        Assert.Equal(first.RecordId, second.RecordId);
        Assert.Equal("slack:msg-edit-1", second.DedupeKey);

        var stages = second.AuditTrail!.Select(a => a.Stage).ToList();
        Assert.Equal(
        [
            MemoryIngestionStage.Capture,
            MemoryIngestionStage.Normalize,
            MemoryIngestionStage.Extract,
            MemoryIngestionStage.Chunk,
            MemoryIngestionStage.Embed,
            MemoryIngestionStage.Store,
            MemoryIngestionStage.Link,
            MemoryIngestionStage.Audit,
        ],
        stages);
    }

    [Fact]
    public async Task IngestAsync_EmptyRawPayload_IsRejectedAsMalformed()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-malformed-raw",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-malformed-raw",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "",
            DedupeKey: "slack:msg-malformed-raw");

        var result = await pipeline.IngestAsync(capture);

        Assert.False(result.Success);
        Assert.Contains("malformed payload", result.Error);
        Assert.NotNull(result.AuditTrail);
        Assert.Equal(MemoryIngestionStage.Capture, result.AuditTrail![0].Stage);
        Assert.False(result.AuditTrail[0].Success);
    }

    [Fact]
    public async Task IngestAsync_EmptyNormalizedContent_IsRejectedAsMalformed()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new EmptyContentNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-malformed-normalized",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-malformed-normalized",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-malformed-normalized");

        var result = await pipeline.IngestAsync(capture);

        Assert.False(result.Success);
        Assert.Contains("malformed normalized content", result.Error);
        Assert.NotNull(result.AuditTrail);
        Assert.Equal(MemoryIngestionStage.Normalize, result.AuditTrail![^2].Stage);
        Assert.False(result.AuditTrail[^2].Success);
    }

    [Fact]
    public async Task IngestAsync_LinkFailure_ReturnsRecoveredSuccessWithAuditFailureMarker()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            linker: new ThrowingLinker());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-link-fail",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-link-fail",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-link-fail",
            ContentHash: "hash-link-fail");

        var result = await pipeline.IngestAsync(capture);

        Assert.True(result.Success);
        Assert.NotNull(result.RecordId);
        Assert.Contains("link stage failed", result.Error);
        Assert.NotNull(result.AuditTrail);
        Assert.Equal(MemoryIngestionStage.Link, result.AuditTrail![^2].Stage);
        Assert.False(result.AuditTrail[^2].Success);
        Assert.Equal(MemoryIngestionStage.Audit, result.AuditTrail[^1].Stage);
        Assert.True(result.AuditTrail[^1].Success);
    }

    [Fact]
    public async Task IngestAsync_UsesNormalizedContentInsteadOfRawPayload()
    {
        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-separation",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-separation",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "RAW_PAYLOAD_SHOULD_NOT_BE_PERSISTED_DIRECTLY",
            DedupeKey: "slack:msg-separation",
            ContentHash: "hash-separation");

        var store = new RecordingStore();

        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new TransformingNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            store: store);

        var result = await pipeline.IngestAsync(capture);

        Assert.True(result.Success);
        Assert.NotNull(store.LastEnvelope);
        Assert.Equal("normalized-content", store.LastEnvelope!.Content);
        Assert.NotEqual(capture.RawPayload, store.LastEnvelope.Content);
    }

    [Fact]
    public async Task IngestAsync_EmptyChunkOutput_IsRejected()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            chunker: new EmptyChunker());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-empty-chunks",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-empty-chunks",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-empty-chunks");

        var result = await pipeline.IngestAsync(capture);

        Assert.False(result.Success);
        Assert.Contains("no chunks", result.Error);
    }

    [Fact]
    public async Task IngestAsync_EmbedderCountMismatch_IsRejected()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            embedder: new DroppingEmbedder());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-embed-mismatch",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-embed-mismatch",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-embed-mismatch");

        var result = await pipeline.IngestAsync(capture);

        Assert.False(result.Success);
        Assert.Contains("embedder changed chunk count", result.Error);
    }

    [Fact]
    public async Task IngestAsync_ChunkException_RecordsStageFailure()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            chunker: new ThrowingChunker());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-chunk-fail",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-chunk-fail",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-chunk-fail");

        var result = await pipeline.IngestAsync(capture);

        Assert.False(result.Success);
        Assert.NotNull(result.AuditTrail);
        Assert.Equal(MemoryIngestionStage.Chunk, result.AuditTrail![^2].Stage);
        Assert.False(result.AuditTrail[^2].Success);
    }

    [Fact]
    public async Task IngestAsync_OperationCanceled_PropagatesCancellation()
    {
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new CancelingNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry());

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-cancel",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-cancel",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => pipeline.IngestAsync(capture, cts.Token));
    }

    [Fact]
    public async Task IngestAsync_DefaultChunker_UsesStoreRecordIdAsParentRecordId()
    {
        var store = new RecordingStore();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            store: store);

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-parent",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-parent",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "payload",
            DedupeKey: "slack:msg-parent",
            ContentHash: "hash-parent");

        var result = await pipeline.IngestAsync(capture);

        Assert.True(result.Success);
        Assert.NotNull(store.LastChunks);
        Assert.Single(store.LastChunks!);
        Assert.Equal($"record-norm-{capture.EnvelopeId}", store.LastChunks![0].ParentRecordId);
    }

    [Fact]
    public void Ctor_NullRequiredDependencies_Throws()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var registry = new InMemoryIngestionRegistry();
        var normalizer = new PassThroughNormalizer();

        Assert.Throws<ArgumentNullException>(() => new DefaultMemoryIngestionPipeline(null!, policy, registry));
        Assert.Throws<ArgumentNullException>(() => new DefaultMemoryIngestionPipeline(normalizer, null!, registry));
        Assert.Throws<ArgumentNullException>(() => new DefaultMemoryIngestionPipeline(normalizer, policy, null!));
    }

    [Fact]
    public async Task DefaultChunker_WhenMultipleParagraphs_CreatesSeparateSemanticChunks()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-semantic-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "First paragraph sentence one. First paragraph sentence two.\n\nSecond paragraph sentence one. Second paragraph sentence two.");

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-1");

        Assert.Equal(2, chunks.Count);
        Assert.Contains("First paragraph sentence one.", chunks[0].Content);
        Assert.DoesNotContain("Second paragraph", chunks[0].Content);
        Assert.Contains("Second paragraph sentence one.", chunks[1].Content);
        Assert.Equal(0, chunks[0].ChunkOrdinal);
        Assert.Equal(1, chunks[1].ChunkOrdinal);
    }

    [Fact]
    public async Task DefaultChunker_WhenSentenceExceedsLimit_UsesBoundedHardChunks()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var longSentence = new string('x', 1200);
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-2",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-semantic-2",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: longSentence);

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-2");

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.InRange(c.Content.Length, 1, 480));
        Assert.Equal(0, chunks[0].ChunkOrdinal);
        Assert.Equal(chunks.Count - 1, chunks[^1].ChunkOrdinal);
    }

    [Fact]
    public async Task DefaultChunker_WhenChatThreadLikeContent_CreatesThreadSegmentsAndSummaryChunk()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-chat",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-semantic-chat",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Sender: U1\nMessage: First turn.\nSender: U2\nMessage: Second turn?\nSender: U1\nMessage: Third turn.\nSender: U3\nMessage: Fourth turn.",
            ThreadRootExternalId: "thread-1");

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-chat");

        Assert.True(chunks.Count >= 5);
        Assert.Contains(chunks, c => c.Kind == MemoryChunkKind.Summary);
        Assert.All(chunks, c => Assert.Contains(c.Tags!, t => t.StartsWith("semantic.kind:", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task DefaultChunker_WhenChatThreadUsesActorPrefix_SplitsTurns()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-chat-actor",
            SourceType: MemoryIngestionSourceType.CrewHandoff,
            SourceSystem: "opentail-runtime",
            EventKind: MemoryIngestionEventKind.WorkflowEvent,
            ExternalId: "handoff-semantic-chat",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Actor: planner\nMessage: first\nActor: implementer\nMessage: second\nActor: reviewer\nMessage: third\nActor: operator\nMessage: fourth",
            ThreadRootExternalId: "handoff-thread-1");

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-chat-actor");

        Assert.True(chunks.Count >= 5);
        Assert.Contains(chunks, c => c.Kind == MemoryChunkKind.Summary);
    }

    [Fact]
    public async Task DefaultChunker_WhenGitHubDiffLikeContent_MarksCodeContextChunk()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-github",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Review,
            ExternalId: "review-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "```diff\n@@ -1,3 +1,3 @@\n- old line\n+ new line\n```",
            Metadata: new Dictionary<string, string> { ["review.id"] = "1" });

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-github");

        Assert.NotEmpty(chunks);
        Assert.Equal(MemoryChunkKind.CodeContext, chunks[0].Kind);
        Assert.Contains("semantic.source:github-review", chunks[0].Tags!);
    }

    [Fact]
    public async Task DefaultChunker_WhenAttachmentTextPresentAndPolicyAllows_AddsAttachmentChunk()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Internal,
            TrustLevel: MemoryTrustLevel.Medium,
            RetentionClass: MemoryRetentionClass.Durable,
            AllowDirectQuotation: true,
            AllowSummarization: true);

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-attach",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-semantic-attach",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Message body",
            Policy: policy,
            Metadata: new Dictionary<string, string>
            {
                ["attachment.text.1"] = "Attachment extracted text"
            });

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-attach");

        Assert.Contains(chunks, c => c.Content.Contains("AttachmentText:", StringComparison.Ordinal));
        Assert.Contains(chunks, c => c.Content.Contains("AttachmentText:", StringComparison.Ordinal) && c.Kind == MemoryChunkKind.RawEvidence);
        Assert.Contains(chunks.SelectMany(c => c.Tags ?? []).Distinct(StringComparer.OrdinalIgnoreCase), t => t.StartsWith("attachment.key:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DefaultChunker_WhenUserNoteHasThreadRoot_DoesNotForceChatTurnSplits()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-usernote",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-semantic-usernote",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Author: operator\nMessage: single-note narrative with no turn structure",
            ThreadRootExternalId: "note-thread-1");

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-usernote");

        Assert.Single(chunks);
        Assert.DoesNotContain(chunks, c => c.Kind == MemoryChunkKind.Summary);
    }

    [Fact]
    public async Task DefaultChunker_WhenAttachmentTextPresentAndPolicyBlocksQuotation_SkipsAttachmentChunk()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Internal,
            TrustLevel: MemoryTrustLevel.Medium,
            RetentionClass: MemoryRetentionClass.Durable,
            AllowDirectQuotation: false,
            AllowSummarization: true);

        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-attach-block",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-semantic-attach-block",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Message body",
            Policy: policy,
            Metadata: new Dictionary<string, string>
            {
                ["attachment.text.1"] = "Should not be chunked"
            });

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-attach-block");

        Assert.DoesNotContain(chunks, c => c.Content.Contains("AttachmentText:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DefaultChunker_WhenDecisionLikeContent_ClassifiesDecisionChunk()
    {
        var chunker = new DefaultMemoryIngestionChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-semantic-kind",
            SourceType: MemoryIngestionSourceType.UserNote,
            SourceSystem: "operator-console",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "note-semantic-kind",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-24T10:00:00Z"),
            Content: "Decision: approved rollout after verification.");

        var chunks = await chunker.ChunkAsync(envelope, parentRecordId: "record-semantic-kind");

        Assert.NotEmpty(chunks);
        Assert.Equal(MemoryChunkKind.Decision, chunks[0].Kind);
        Assert.Contains("semantic.kind:Decision", chunks[0].Tags!, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IngestAsync_ScrubAndAccept_ReplacesSensitiveContentBeforeStore()
    {
        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-scrub-1",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-scrub-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T11:00:00Z"),
            RawPayload: "Decision approved; token=abc123",
            DedupeKey: "github:pr-scrub-1",
            ContentHash: "hash-scrub-1");

        var store = new RecordingStore();
        var pipeline = new DefaultMemoryIngestionPipeline(
            normalizer: new PassThroughNormalizer(),
            policy: new DefaultMemoryIngestionPolicy(),
            registry: new InMemoryIngestionRegistry(),
            store: store);

        var result = await pipeline.IngestAsync(capture);

        Assert.True(result.Success);
        Assert.NotNull(store.LastEnvelope);
        Assert.Contains("[REDACTED]", store.LastEnvelope!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", store.LastEnvelope.Content, StringComparison.OrdinalIgnoreCase);
    }
}
