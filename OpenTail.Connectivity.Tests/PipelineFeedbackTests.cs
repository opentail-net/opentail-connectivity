namespace OpenTail.Connectivity.Tests;

public sealed class PipelineFeedbackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-fb-" + Guid.NewGuid().ToString("N"));
    private readonly string _state = Path.Combine(Path.GetTempPath(), "otail-fb-" + Guid.NewGuid().ToString("N") + ".json");

    public PipelineFeedbackTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        try { File.Delete(_state); } catch (IOException) { }
    }

    private sealed class FailingFor(string name) : ITextExtractor
    {
        public bool Fail { get; set; } = true;

        public TextExtractionResult Extract(string fileName, byte[] content) =>
            Fail && fileName == name ? new(false, "", "broken") : new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Collect : IConnectivityExporter
    {
        public List<NormalisedDocument> Docs { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) { Docs.Add(document); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private async Task<List<string>> Run(ITextExtractor extractor, PipelineOptions? options = null)
    {
        var connector = new FolderSourceConnector(_dir, "t", new FolderSyncStateStore(_state));
        var collect = new Collect();
        await new ConnectivityPipeline(connector, extractor, [collect], options).RunAsync();
        await connector.SaveStateAsync();
        return [.. collect.Docs.Select(d => d.Title)];
    }

    [Fact]
    public async Task A_file_that_failed_extraction_is_tried_again_next_run()
    {
        File.WriteAllText(Path.Combine(_dir, "good.txt"), "good");
        File.WriteAllText(Path.Combine(_dir, "bad.txt"), "bad");
        var extractor = new FailingFor("bad.txt");

        Assert.Equal(["good"], await Run(extractor));
        extractor.Fail = false;
        Assert.Equal(["bad"], await Run(extractor)); // good.txt is unchanged and skipped; bad.txt comes back
    }

    [Fact]
    public async Task A_file_discovered_when_the_item_cap_stopped_the_run_is_offered_next_run()
    {
        foreach (var n in new[] { "a", "b", "c" }) File.WriteAllText(Path.Combine(_dir, n + ".txt"), n);
        var extractor = new FailingFor("none");

        Assert.Equal(["a"], await Run(extractor, new PipelineOptions { MaxItemsProcessed = 1 }));
        Assert.Equal(["b"], await Run(extractor, new PipelineOptions { MaxItemsProcessed = 1 }));
        Assert.Equal(["c"], await Run(extractor, new PipelineOptions { MaxItemsProcessed = 1 }));
    }

    [Theory]
    [InlineData("report.pdf", "pdf", "report")]
    [InlineData("Release 2.1 notes", "", "Release 2.1 notes")]
    [InlineData("Re: v1.2 plan", "eml", "Re: v1.2 plan")]
    [InlineData("archive.tar.gz", "gz", "archive.tar")]
    [InlineData(".env", "env", ".env")]
    public void Titles_lose_only_their_own_extension(string displayName, string extension, string title)
    {
        var item = new SourceItem("id", displayName, extension, 1, DateTimeOffset.UnixEpoch, "t");
        Assert.Equal(title, ConnectivityPipeline.Title(item));
    }
}
