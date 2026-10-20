// Moved from opentail's OpenTail.Core.Memory (SentenceWindowChunker.cs); namespace changed. See docs/design/memory.md.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenTail.Connectivity.Memory;

public sealed class SentenceWindowChunker : IMemoryIngestionChunker
{
    private static readonly Regex ParagraphSplitRegex = new(@"\r?\n\s*\r?\n", RegexOptions.Compiled);
    private static readonly Regex SentenceSplitRegex = new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    private readonly int _windowSize;

    /// <summary>
    /// Initializes a new instance of the SentenceWindowChunker.
    /// </summary>
    /// <param name="windowSize">Number of sentences before and after to include in the window.</param>
    public SentenceWindowChunker(int windowSize = 2)
    {
        _windowSize = windowSize;
    }

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
            var sentences = SentenceSplitRegex
                .Split(paragraph)
                .Select(static s => s.Trim())
                .Where(static s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (sentences.Count == 0)
                continue;

            for (var i = 0; i < sentences.Count; i++)
            {
                var sentence = sentences[i];
                var windowStart = Math.Max(0, i - _windowSize);
                var windowEnd = Math.Min(sentences.Count - 1, i + _windowSize);
                
                var windowText = string.Join(" ", sentences.GetRange(windowStart, windowEnd - windowStart + 1));

                chunks.Add(new MemoryChunk(
                    Id: $"chunk-{parentRecordId}-{ordinal}",
                    ParentRecordId: parentRecordId,
                    ChunkOrdinal: ordinal,
                    Kind: MemoryChunkKind.RawEvidence, // Could make this smarter based on content like DefaultChunker
                    Content: sentence,
                    WindowText: windowText,
                    Summary: envelope.Summary,
                    Tags: ["semantic.chunker:sentence-window"]));

                ordinal++;
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
