// Moved from opentail's OpenTail.Core.Memory (HierarchicalChunker.cs); namespace changed. See docs/design/memory.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenTail.Connectivity.Memory;

public sealed class HierarchicalChunker : IMemoryIngestionChunker
{
    private static readonly Regex ParagraphSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    public Task<IReadOnlyList<MemoryChunk>> ChunkAsync(
        MemoryNormalizedEnvelope envelope,
        string parentRecordId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentRecordId);

        var content = envelope.Content?.Trim() ?? string.Empty;
        if (content.Length == 0)
            return Task.FromResult<IReadOnlyList<MemoryChunk>>([]);

        var chunks = new List<MemoryChunk>();
        var ordinal = 0;

        foreach (var paragraph in SplitParagraphs(content))
        {
            // The parent chunk (Level 1)
            var parentChunkId = $"chunk-{parentRecordId}-p{ordinal}";
            chunks.Add(new MemoryChunk(
                Id: parentChunkId,
                ParentRecordId: parentRecordId,
                ChunkOrdinal: ordinal++,
                Kind: MemoryChunkKind.RawEvidence,
                Content: paragraph,
                Summary: envelope.Summary,
                Tags: ["semantic.hierarchy:parent"]));

            var sentences = SentenceSplitRegex
                .Split(paragraph)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            // The child chunks (Level 2)
            foreach (var sentence in sentences)
            {
                chunks.Add(new MemoryChunk(
                    Id: $"chunk-{parentRecordId}-p{ordinal-1}-s{ordinal}",
                    ParentRecordId: parentRecordId,
                    ChunkOrdinal: ordinal++,
                    Kind: MemoryChunkKind.RawEvidence,
                    Content: sentence,
                    Summary: envelope.Summary,
                    Tags: ["semantic.hierarchy:child", $"hierarchy.parent_id:{parentChunkId}"]));
            }
        }

        return Task.FromResult<IReadOnlyList<MemoryChunk>>(chunks);
    }

    private static IEnumerable<string> SplitParagraphs(string content)
        => ParagraphSplitRegex
            .Split(content)
            .Select(static p => p.Trim())
            .Where(static p => !string.IsNullOrWhiteSpace(p));
}
