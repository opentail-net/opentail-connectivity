using OpenTail.Connectivity.Memory;


namespace OpenTail.Connectivity.Tests.Memory;

public sealed class SentenceWindowChunkerTests
{
    private static MemoryNormalizedEnvelope Envelope(string content, string summary = "summary") =>
        new(NormalizedId: "env-1",
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "test",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "ext-1",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            Content: content,
            Summary: summary);

    // ── Empty / whitespace ─────────────────────────────────────────────────

    [Fact]
    public async Task EmptyContent_Returns_NoChunks()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope(string.Empty), "rec-1");
        Assert.Empty(chunks);
    }

    [Fact]
    public async Task WhitespaceOnlyContent_Returns_NoChunks()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("   \n  \t  "), "rec-1");
        Assert.Empty(chunks);
    }

    // ── Single sentence ───────────────────────────────────────────────────

    [Fact]
    public async Task SingleSentence_Returns_OneChunk()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Hello world."), "rec-1");
        Assert.Single(chunks);
    }

    [Fact]
    public async Task SingleSentence_Window_IsSentenceItself()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Hello world."), "rec-1");
        Assert.Equal("Hello world.", chunks[0].Content);
        Assert.Equal("Hello world.", chunks[0].WindowText);
    }

    // ── Multi-sentence windowing ──────────────────────────────────────────

    [Fact]
    public async Task TwoSentences_FirstChunk_WindowIncludesBoth()
    {
        var chunker = new SentenceWindowChunker(windowSize: 1);
        var chunks = await chunker.ChunkAsync(Envelope("First sentence. Second sentence."), "rec-1");
        Assert.Equal(2, chunks.Count);
        // First sentence window (index 0): clips at 0, extends to min(1, 1) = 1 → includes both
        Assert.Contains("Second sentence", chunks[0].WindowText);
    }

    [Fact]
    public async Task ThreeSentences_MiddleChunk_WindowIncludesAllThree()
    {
        var chunker = new SentenceWindowChunker(windowSize: 2);
        var chunks = await chunker.ChunkAsync(
            Envelope("Alpha. Beta. Gamma."), "rec-1");
        Assert.Equal(3, chunks.Count);
        // Middle chunk (index 1, windowSize 2): start=max(0,1-2)=0, end=min(2,1+2)=2 → all 3
        Assert.Contains("Alpha", chunks[1].WindowText);
        Assert.Contains("Beta", chunks[1].WindowText);
        Assert.Contains("Gamma", chunks[1].WindowText);
    }

    [Fact]
    public async Task WindowSize_Zero_UsesSentenceAsOwnWindow()
    {
        var chunker = new SentenceWindowChunker(windowSize: 0);
        var chunks = await chunker.ChunkAsync(Envelope("One. Two. Three."), "rec-1");
        Assert.Equal(3, chunks.Count);
        // Each chunk's window should only contain that sentence
        Assert.Equal(chunks[0].Content, chunks[0].WindowText);
        Assert.Equal(chunks[1].Content, chunks[1].WindowText);
        Assert.Equal(chunks[2].Content, chunks[2].WindowText);
    }

    // ── Paragraph isolation ───────────────────────────────────────────────

    [Fact]
    public async Task Window_DoesNotCrossParagraphBoundary()
    {
        // A window that would extend past the paragraph end must be clipped.
        var chunker = new SentenceWindowChunker(windowSize: 5);
        const string content = "Para1 sent1. Para1 sent2.\n\nPara2 sent1.";
        var chunks = await chunker.ChunkAsync(Envelope(content), "rec-1");

        // Last chunk in para1 (Para1 sent2): window should not contain "Para2"
        var lastPara1 = chunks.First(c => c.Content.Contains("Para1 sent2"));
        Assert.DoesNotContain("Para2", lastPara1.WindowText);

        // First chunk in para2 (Para2 sent1): window should not contain "Para1"
        var firstPara2 = chunks.First(c => c.Content.Contains("Para2 sent1"));
        Assert.DoesNotContain("Para1", firstPara2.WindowText);
    }

    [Fact]
    public async Task MultiParagraph_ProducesChunksFromEachParagraph()
    {
        var chunker = new SentenceWindowChunker();
        const string content = "First para. Still first.\n\nSecond para.";
        var chunks = await chunker.ChunkAsync(Envelope(content), "rec-1");

        Assert.True(chunks.Count >= 3);
        Assert.Contains(chunks, c => c.Content.Contains("First para"));
        Assert.Contains(chunks, c => c.Content.Contains("Still first"));
        Assert.Contains(chunks, c => c.Content.Contains("Second para"));
    }

    // ── Metadata / identifiers ────────────────────────────────────────────

    [Fact]
    public async Task ChunkIds_AreSequentialAndContainParentRecordId()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Alpha. Beta. Gamma."), "rec-42");

        Assert.Equal("chunk-rec-42-0", chunks[0].Id);
        Assert.Equal("chunk-rec-42-1", chunks[1].Id);
        Assert.Equal("chunk-rec-42-2", chunks[2].Id);
        Assert.All(chunks, c => Assert.Equal("rec-42", c.ParentRecordId));
    }

    [Fact]
    public async Task ChunkOrdinals_AreSequential()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("A. B. C."), "rec-1");
        for (var i = 0; i < chunks.Count; i++)
            Assert.Equal(i, chunks[i].ChunkOrdinal);
    }

    [Fact]
    public async Task Tags_ContainSentenceWindowTag()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Hello world."), "rec-1");
        Assert.All(chunks, c => Assert.Contains("semantic.chunker:sentence-window", c.Tags!));
    }

    [Fact]
    public async Task ChunkKind_IsRawEvidence()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Hello world."), "rec-1");
        Assert.All(chunks, c => Assert.Equal(MemoryChunkKind.RawEvidence, c.Kind));
    }

    [Fact]
    public async Task Summary_PropagatedFromEnvelope()
    {
        var chunker = new SentenceWindowChunker();
        var chunks = await chunker.ChunkAsync(Envelope("Hello world.", summary: "my summary"), "rec-1");
        Assert.All(chunks, c => Assert.Equal("my summary", c.Summary));
    }
}
