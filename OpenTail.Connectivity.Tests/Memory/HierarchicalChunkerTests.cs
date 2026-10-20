using OpenTail.Connectivity.Memory;

using System.Threading.Tasks;
using Xunit;

namespace OpenTail.Connectivity.Tests.Memory;

public sealed class HierarchicalChunkerTests
{
    [Fact]
    public async Task ChunkAsync_CreatesParentAndChildChunks()
    {
        var chunker = new HierarchicalChunker();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: "test-item",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "TestSystem",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "ext-123",
            CapturedAtUtc: System.DateTimeOffset.UtcNow,
            ObservedAtUtc: System.DateTimeOffset.UtcNow,
            Content: "This is sentence one. This is sentence two.\n\nThis is paragraph two.",
            Summary: "A summary"
        );
        
        var chunks = await chunker.ChunkAsync(envelope, "record-123");

        // Should have 2 parent chunks (paragraphs) and 3 child chunks (sentences)
        Assert.Equal(5, chunks.Count);

        // Paragraph 1
        var p1 = chunks[0];
        Assert.Equal("chunk-record-123-p0", p1.Id);
        Assert.Contains("semantic.hierarchy:parent", p1.Tags!);
        Assert.Equal("This is sentence one. This is sentence two.", p1.Content);

        // Sentences for P1
        var s1 = chunks[1];
        Assert.Equal("This is sentence one.", s1.Content);
        Assert.Contains("semantic.hierarchy:child", s1.Tags!);
        Assert.Contains($"hierarchy.parent_id:{p1.Id}", s1.Tags!);

        var s2 = chunks[2];
        Assert.Equal("This is sentence two.", s2.Content);
        Assert.Contains("semantic.hierarchy:child", s2.Tags!);

        // Paragraph 2
        var p2 = chunks[3];
        Assert.Equal("This is paragraph two.", p2.Content);
        Assert.Contains("semantic.hierarchy:parent", p2.Tags!);

        // Sentence for P2
        var s3 = chunks[4];
        Assert.Equal("This is paragraph two.", s3.Content);
        Assert.Contains("semantic.hierarchy:child", s3.Tags!);
    }
}
