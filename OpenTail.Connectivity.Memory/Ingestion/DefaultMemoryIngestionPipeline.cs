// Moved from opentail's OpenTail.Core.Memory (DefaultMemoryIngestionPipeline.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public interface IMemoryIngestionExtractor
{
    Task<MemoryNormalizedEnvelope> ExtractAsync(
        MemoryNormalizedEnvelope envelope,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionChunker
{
    Task<IReadOnlyList<MemoryChunk>> ChunkAsync(
        MemoryNormalizedEnvelope envelope,
        string parentRecordId,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionEmbedder
{
    Task<IReadOnlyList<MemoryChunk>> EmbedAsync(
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionStore
{
    Task<string> StoreAsync(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        MemoryIngestionDecision decision,
        CancellationToken cancellationToken = default);
}

public interface IMemoryIngestionLinker
{
    Task LinkAsync(
        string recordId,
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        CancellationToken cancellationToken = default);
}

public sealed class NoOpMemoryIngestionExtractor : IMemoryIngestionExtractor
{
    public Task<MemoryNormalizedEnvelope> ExtractAsync(MemoryNormalizedEnvelope envelope, CancellationToken cancellationToken = default)
        => Task.FromResult(envelope);
}

public sealed class DefaultMemoryIngestionChunker : IMemoryIngestionChunker
{
    private static readonly Regex ParagraphSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);
    private static readonly Regex ChatTurnSplitRegex = new(@"\r?\n(?=(?:Sender:|Author:|Actor:|Coordinator:|Invoker:|Reporter:|Message:|Question:|Answer:))", RegexOptions.Compiled);
    private const int MaxChunkLength = 480;
    private sealed record ChunkSegment(string Content, MemoryChunkKind? KindHint = null, IReadOnlyList<string>? SegmentTags = null);

    public Task<IReadOnlyList<MemoryChunk>> ChunkAsync(MemoryNormalizedEnvelope envelope, string parentRecordId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);

        var content = envelope.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return Task.FromResult<IReadOnlyList<MemoryChunk>>([]);

        var chunks = new List<MemoryChunk>();
        var ordinal = 0;

        foreach (var segment in BuildSegments(envelope, content))
        {
            foreach (var chunkContent in ChunkParagraphBySentence(segment.Content))
            {
                var chunkKind = ResolveChunkKind(envelope, chunkContent, segment.KindHint);
                var tags = BuildChunkTags(envelope, chunkKind, segment.SegmentTags);

                chunks.Add(new MemoryChunk(
                    Id: $"chunk-{parentRecordId}-{ordinal}",
                    ParentRecordId: parentRecordId,
                    ChunkOrdinal: ordinal,
                    Kind: chunkKind,
                    Content: chunkContent,
                    Summary: envelope.Summary,
                    Tags: tags));

                ordinal++;
            }
        }

        if (ShouldAddThreadSummaryChunk(envelope, chunks))
        {
            chunks.Add(new MemoryChunk(
                Id: $"chunk-{parentRecordId}-{ordinal}",
                ParentRecordId: parentRecordId,
                ChunkOrdinal: ordinal,
                Kind: MemoryChunkKind.Summary,
                Content: BuildThreadSummaryContent(envelope, chunks),
                Summary: envelope.Summary,
                Tags: BuildChunkTags(envelope, MemoryChunkKind.Summary, ["semantic.source:thread-summary"])));
        }

        return Task.FromResult<IReadOnlyList<MemoryChunk>>(chunks);
    }

    private static IEnumerable<ChunkSegment> BuildSegments(MemoryNormalizedEnvelope envelope, string content)
    {
        if (IsChatLikeSource(envelope.SourceType) && !string.IsNullOrWhiteSpace(envelope.ThreadRootExternalId))
        {
            var turns = ChatTurnSplitRegex
                .Split(content)
                .Select(static t => t.Trim())
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (turns.Count > 1)
            {
                foreach (var turn in turns)
                    yield return new ChunkSegment(turn, SegmentTags: ["semantic.source:chat-thread"]);

                foreach (var attachmentSegment in BuildAttachmentTextSegments(envelope))
                    yield return attachmentSegment;

                yield break;
            }
        }

        foreach (var paragraph in SplitParagraphs(content))
            yield return new ChunkSegment(paragraph);

        foreach (var attachmentSegment in BuildAttachmentTextSegments(envelope))
            yield return attachmentSegment;
    }

    private static IEnumerable<ChunkSegment> BuildAttachmentTextSegments(MemoryNormalizedEnvelope envelope)
    {
        if (envelope.Policy is { AllowDirectQuotation: false })
            yield break;

        if (envelope.Metadata is null)
            yield break;

        foreach (var entry in envelope.Metadata
                     .Where(static kvp => kvp.Key.StartsWith("attachment.text.", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(static kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            var attachmentText = entry.Value?.Trim();
            if (string.IsNullOrWhiteSpace(attachmentText))
                continue;

            yield return new ChunkSegment(
                Content: $"AttachmentText: {attachmentText}",
                KindHint: MemoryChunkKind.RawEvidence,
                SegmentTags: ["semantic.source:attachment", $"attachment.key:{entry.Key}"]);
        }
    }

    private static IEnumerable<string> SplitParagraphs(string content)
        => ParagraphSplitRegex
            .Split(content)
            .Select(static p => p.Trim())
            .Where(static p => !string.IsNullOrWhiteSpace(p));

    private static IEnumerable<string> ChunkParagraphBySentence(string paragraph)
    {
        var sentences = SentenceSplitRegex
            .Split(paragraph)
            .Select(static s => s.Trim())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (sentences.Count == 0)
        {
            foreach (var hardChunk in HardChunk(paragraph))
                yield return hardChunk;

            yield break;
        }

        var current = new StringBuilder();

        foreach (var sentence in sentences)
        {
            if (sentence.Length > MaxChunkLength)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                foreach (var hardChunk in HardChunk(sentence))
                    yield return hardChunk;

                continue;
            }

            var additionalLength = current.Length == 0 ? sentence.Length : sentence.Length + 1;
            if (current.Length > 0 && current.Length + additionalLength > MaxChunkLength)
            {
                yield return current.ToString();
                current.Clear();
            }

            if (current.Length > 0)
                current.Append(' ');

            current.Append(sentence);
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    private static IEnumerable<string> HardChunk(string text)
    {
        var normalized = text.Trim();
        for (var i = 0; i < normalized.Length; i += MaxChunkLength)
        {
            var length = Math.Min(MaxChunkLength, normalized.Length - i);
            yield return normalized.Substring(i, length).Trim();
        }
    }

    private static MemoryChunkKind ResolveChunkKind(MemoryNormalizedEnvelope envelope, string chunkContent, MemoryChunkKind? kindHint)
    {
        if (kindHint is not null && kindHint != MemoryChunkKind.Unknown)
            return kindHint.Value;

        if (IsGitHubReviewLike(envelope) && LooksLikeCodeDiff(chunkContent))
            return MemoryChunkKind.CodeContext;

        if (ContainsAny(chunkContent, "decision", "approved", "rejected", "resolved", "verdict"))
            return MemoryChunkKind.Decision;

        if (ContainsAny(chunkContent, "action item", "todo", "next step", "follow-up"))
            return MemoryChunkKind.ActionItem;

        if (chunkContent.Contains('?', StringComparison.Ordinal))
            return MemoryChunkKind.Question;

        if (ContainsAny(chunkContent, "answer", "response", "resolution"))
            return MemoryChunkKind.Answer;

        if (ContainsAny(chunkContent, "policy", "must", "required", "compliance"))
            return MemoryChunkKind.PolicyStatement;

        if (ContainsAny(chunkContent, "incident", "outage", "failure", "error"))
            return MemoryChunkKind.IncidentFact;

        return MemoryChunkKind.RawEvidence;
    }

    private static IReadOnlyList<string>? BuildChunkTags(MemoryNormalizedEnvelope envelope, MemoryChunkKind chunkKind, IReadOnlyList<string>? segmentTags)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (envelope.Metadata is not null)
        {
            foreach (var key in envelope.Metadata.Keys)
                tags.Add(key);
        }

        tags.Add($"semantic.kind:{chunkKind}");

        if (IsChatLikeSource(envelope.SourceType))
            tags.Add("semantic.source:chat-like");

        if (IsGitHubReviewLike(envelope))
            tags.Add("semantic.source:github-review");

        if (segmentTags is not null)
        {
            foreach (var tag in segmentTags.Where(static t => !string.IsNullOrWhiteSpace(t)))
                tags.Add(tag);
        }

        return tags.Count == 0 ? null : [.. tags];
    }

    private static bool ShouldAddThreadSummaryChunk(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks)
        => IsChatLikeSource(envelope.SourceType)
           && !string.IsNullOrWhiteSpace(envelope.ThreadRootExternalId)
           && chunks.Count >= 4;

    private static string BuildThreadSummaryContent(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks)
    {
        if (!string.IsNullOrWhiteSpace(envelope.Summary))
            return $"ThreadSummary: {envelope.Summary}";

        var preview = string.Join(" ", chunks
            .Where(static c => c.Kind != MemoryChunkKind.Summary)
            .Take(3)
            .Select(static c => c.Content)
            .Where(static c => !string.IsNullOrWhiteSpace(c)));

        if (preview.Length > MaxChunkLength)
            preview = preview[..MaxChunkLength].TrimEnd() + "…";

        return string.IsNullOrWhiteSpace(preview)
            ? "ThreadSummary: Conversation thread summary unavailable."
            : $"ThreadSummary: {preview}";
    }

    private static bool IsChatLikeSource(MemoryIngestionSourceType sourceType)
        => sourceType is MemoryIngestionSourceType.Slack or MemoryIngestionSourceType.CrewHandoff;

    private static bool IsGitHubReviewLike(MemoryNormalizedEnvelope envelope)
        => envelope.SourceType == MemoryIngestionSourceType.GitHub
           && envelope.EventKind is MemoryIngestionEventKind.Review or MemoryIngestionEventKind.Comment or MemoryIngestionEventKind.PullRequest;

    private static bool LooksLikeCodeDiff(string content)
        => content.Contains("```diff", StringComparison.OrdinalIgnoreCase)
           || content.Contains("@@", StringComparison.Ordinal)
           || content.Contains("\n+", StringComparison.Ordinal)
           || content.Contains("\n-", StringComparison.Ordinal);

    private static bool ContainsAny(string content, params string[] tokens)
        => tokens.Any(token => content.Contains(token, StringComparison.OrdinalIgnoreCase));
}

public sealed class NoOpMemoryIngestionEmbedder : IMemoryIngestionEmbedder
{
    public Task<IReadOnlyList<MemoryChunk>> EmbedAsync(IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
        => Task.FromResult(chunks);
}

public sealed class BatchingMemoryIngestionEmbedder : IMemoryIngestionEmbedder
{
    private readonly IMemoryIngestionEmbedder _inner;
    private readonly int _batchSize;

    public BatchingMemoryIngestionEmbedder(IMemoryIngestionEmbedder inner, int batchSize = 32)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _batchSize = Math.Max(1, batchSize);
    }

    public async Task<IReadOnlyList<MemoryChunk>> EmbedAsync(IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
    {
        if (chunks.Count <= _batchSize)
            return await _inner.EmbedAsync(chunks, cancellationToken);

        var embedded = new List<MemoryChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i += _batchSize)
        {
            var take = Math.Min(_batchSize, chunks.Count - i);
            var batch = chunks.Skip(i).Take(take).ToList();
            var embeddedBatch = await _inner.EmbedAsync(batch, cancellationToken);
            embedded.AddRange(embeddedBatch);
        }

        return embedded;
    }
}

public sealed class InMemoryIngestionStore : IMemoryIngestionStore
{
    private readonly IMemoryIngestionRegistry _registry;

    public InMemoryIngestionStore(IMemoryIngestionRegistry registry)
    {
        _registry = registry;
    }

    public Task<string> StoreAsync(
        MemoryNormalizedEnvelope envelope,
        IReadOnlyList<MemoryChunk> chunks,
        MemoryIngestionDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var recordId = decision.ExistingRecordId ?? $"record-{envelope.NormalizedId}";
        if (!string.IsNullOrWhiteSpace(decision.DedupeKey))
            _registry.Upsert(decision.DedupeKey!, envelope, recordId);

        return Task.FromResult(recordId);
    }
}

public sealed class NoOpMemoryIngestionLinker : IMemoryIngestionLinker
{
    public Task LinkAsync(string recordId, MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class DefaultMemoryIngestionPipeline : IMemoryIngestionPipeline
{
    private readonly IMemoryIngestionNormalizer _normalizer;
    private readonly IMemoryIngestionPolicy _policy;
    private readonly IMemoryIngestionRegistry _registry;
    private readonly IMemoryIngestionExtractor _extractor;
    private readonly IMemoryIngestionChunker _chunker;
    private readonly IMemoryIngestionEmbedder _embedder;
    private readonly IMemoryIngestionStore _store;
    private readonly IMemoryIngestionLinker _linker;

    public DefaultMemoryIngestionPipeline(
        IMemoryIngestionNormalizer normalizer,
        IMemoryIngestionPolicy policy,
        IMemoryIngestionRegistry registry,
        IMemoryIngestionExtractor? extractor = null,
        IMemoryIngestionChunker? chunker = null,
        IMemoryIngestionEmbedder? embedder = null,
        IMemoryIngestionStore? store = null,
        IMemoryIngestionLinker? linker = null)
    {
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _extractor = extractor ?? new NoOpMemoryIngestionExtractor();
        _chunker = chunker ?? new DefaultMemoryIngestionChunker();
        _embedder = embedder ?? new NoOpMemoryIngestionEmbedder();
        _store = store ?? new InMemoryIngestionStore(registry);
        _linker = linker ?? new NoOpMemoryIngestionLinker();
    }

    public async Task<MemoryIngestionResult> IngestAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        var audit = new List<MemoryIngestionAuditEvent>();

        MemoryIngestionAuditEvent Event(MemoryIngestionStage stage, bool success, string? message = null)
            => new(
                AuditId: Guid.NewGuid().ToString("N"),
                EnvelopeId: capture.EnvelopeId,
                Stage: stage,
                TimestampUtc: DateTimeOffset.UtcNow,
                Success: success,
                Message: message);

        var currentStage = MemoryIngestionStage.Capture;

        try
        {
            if (string.IsNullOrWhiteSpace(capture.RawPayload))
            {
                audit.Add(Event(MemoryIngestionStage.Capture, success: false, "malformed payload: raw payload is empty"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: malformed payload"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: capture.DedupeKey,
                    AuditTrail: audit,
                    Error: "malformed payload");
            }

            audit.Add(Event(MemoryIngestionStage.Capture, success: true, "capture envelope accepted"));

            currentStage = MemoryIngestionStage.Normalize;
            var normalized = await _normalizer.NormalizeAsync(capture, cancellationToken);

            if (string.IsNullOrWhiteSpace(normalized.Content))
            {
                audit.Add(Event(MemoryIngestionStage.Normalize, success: false, "malformed payload: normalized content is empty"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: malformed normalized content"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: capture.DedupeKey,
                    AuditTrail: audit,
                    Error: "malformed normalized content");
            }

            audit.Add(Event(MemoryIngestionStage.Normalize, success: true, "normalized envelope"));

            var decision = _policy.Evaluate(
                normalized,
                _registry.SnapshotByDedupeKey(),
                _registry.SnapshotDedupeToRecordId());

            if (decision.Kind == MemoryIngestionDecisionKind.ScrubAndAccept)
            {
                normalized = normalized with
                {
                    Content = decision.SanitizedContent ?? normalized.Content,
                    Summary = decision.SanitizedSummary ?? normalized.Summary,
                };
            }

            var isDuplicate = decision.Kind == MemoryIngestionDecisionKind.Duplicate;
            var isReplay = decision.Kind == MemoryIngestionDecisionKind.ReplayUpdate;

            if (decision.Kind == MemoryIngestionDecisionKind.Duplicate)
            {
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, decision.Reason ?? "duplicate ignored"));
                return new MemoryIngestionResult(
                    Success: true,
                    RecordId: decision.ExistingRecordId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: true,
                    IsReplay: false,
                    AuditTrail: audit);
            }

            if (decision.Kind == MemoryIngestionDecisionKind.Reject)
            {
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, decision.Reason ?? "rejected by policy"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: false,
                    AuditTrail: audit,
                    Error: decision.Reason ?? "rejected");
            }

            currentStage = MemoryIngestionStage.Extract;
            var extracted = await _extractor.ExtractAsync(normalized, cancellationToken);
            audit.Add(Event(MemoryIngestionStage.Extract, success: true, "extracted normalized content"));

            var parentRecordId = decision.ExistingRecordId ?? $"record-{extracted.NormalizedId}";

            currentStage = MemoryIngestionStage.Chunk;
            var chunks = await _chunker.ChunkAsync(extracted, parentRecordId, cancellationToken);
            if (chunks.Count == 0)
            {
                audit.Add(Event(MemoryIngestionStage.Chunk, success: false, "chunker returned no chunks"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, "ingestion completed with no chunks"));
                return new MemoryIngestionResult(
                    Success: false,
                    Error: "chunker returned no chunks",
                    RecordId: decision.ExistingRecordId,
                    ChunkGroupId: extracted.NormalizedId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: isReplay,
                    AuditTrail: audit);
            }

            audit.Add(Event(MemoryIngestionStage.Chunk, success: true, $"chunked into {chunks.Count} chunk(s)"));

            currentStage = MemoryIngestionStage.Embed;
            var embedded = await _embedder.EmbedAsync(chunks, cancellationToken);
            if (embedded.Count != chunks.Count)
            {
                audit.Add(Event(MemoryIngestionStage.Embed, success: false, "embedder changed chunk count"));
                audit.Add(Event(MemoryIngestionStage.Audit, success: false, "ingestion rejected: embedder output mismatch"));
                return new MemoryIngestionResult(
                    Success: false,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: false,
                    IsReplay: isReplay,
                    AuditTrail: audit,
                    Error: "embedder changed chunk count");
            }

            audit.Add(Event(MemoryIngestionStage.Embed, success: true, "embedding stage completed"));

            currentStage = MemoryIngestionStage.Store;
            var recordId = await _store.StoreAsync(extracted, embedded, decision, cancellationToken);
            audit.Add(Event(MemoryIngestionStage.Store, success: true, "store stage completed"));

            try
            {
                currentStage = MemoryIngestionStage.Link;
                await _linker.LinkAsync(recordId, extracted, embedded, cancellationToken);
                audit.Add(Event(MemoryIngestionStage.Link, success: true, "link stage completed"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception linkEx)
            {
                audit.Add(Event(MemoryIngestionStage.Link, success: false, linkEx.Message));
                audit.Add(Event(MemoryIngestionStage.Audit, success: true, "ingestion completed with partial recovery"));

                return new MemoryIngestionResult(
                    Success: true,
                    RecordId: recordId,
                    ChunkGroupId: extracted.NormalizedId,
                    DedupeKey: decision.DedupeKey,
                    IsDuplicate: isDuplicate,
                    IsReplay: isReplay,
                    AuditTrail: audit,
                    Error: $"link stage failed: {linkEx.Message}");
            }

            audit.Add(Event(MemoryIngestionStage.Audit, success: true, decision.Reason ?? "ingestion completed"));

            return new MemoryIngestionResult(
                Success: true,
                RecordId: recordId,
                ChunkGroupId: extracted.NormalizedId,
                DedupeKey: decision.DedupeKey,
                IsDuplicate: isDuplicate,
                IsReplay: isReplay,
                AuditTrail: audit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            audit.Add(Event(currentStage, success: false, ex.Message));
            audit.Add(Event(MemoryIngestionStage.Audit, success: false, ex.Message));
            return new MemoryIngestionResult(
                Success: false,
                DedupeKey: capture.DedupeKey,
                AuditTrail: audit,
                Error: ex.Message);
        }
    }
}
