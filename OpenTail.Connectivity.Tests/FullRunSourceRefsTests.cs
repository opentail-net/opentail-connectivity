using OpenTail.Connectivity.Files.Contracts;
using OpenTail.Connectivity.Files.Models;
using OpenTail.Connectivity.Files.Pipeline;

namespace OpenTail.Connectivity.Tests;

public sealed class FullRunSourceRefsTests
{
    private sealed class ItemsConnector(params (string Id, string Content)[] items) : ISourceConnector
    {
        public async IAsyncEnumerable<SourceItem> DiscoverAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var (id, _) in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SourceItem(id, id + ".txt", "txt", 1, DateTimeOffset.UnixEpoch.AddDays(1), "tenant");
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default)
        {
            var content = items.Single(entry => entry.Id == item.Id).Content;
            return Task.FromResult(Encoding.UTF8.GetBytes(content));
        }
    }

    private sealed class Extractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content)
        {
            var text = Encoding.UTF8.GetString(content);
            return text == "bad" ? new(false, string.Empty, "rejected") : new(true, text);
        }
    }

    private sealed class Exporter : IConnectivityExporter
    {
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task FlushAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static ConnectivityPipeline Pipeline(ItemsConnector source, SyncOptions sync)
        => new(source, new Extractor(), [new Exporter()], new PipelineOptions(Sync: sync));

    [Fact]
    public async Task Complete_ordinary_run_collects_every_ref_including_failed_items()
    {
        var report = await Pipeline(new ItemsConnector(("a", "good"), ("bad", "bad")),
            new SyncOptions(CollectSourceRefs: true)).RunAsync();

        Assert.Equal(["a", "bad"], report.SeenSourceRefs);
    }

    [Fact]
    public async Task Ordinary_run_stopped_by_item_cap_leaves_refs_null()
    {
        var source = new ItemsConnector(("a", "good"), ("b", "good"));
        var pipeline = new ConnectivityPipeline(source, new Extractor(), [new Exporter()],
            new PipelineOptions(MaxItemsProcessed: 1, Sync: new SyncOptions(CollectSourceRefs: true)));

        var report = await pipeline.RunAsync();

        Assert.Null(report.SeenSourceRefs);
    }

    [Fact]
    public async Task Ordinary_run_caps_refs_and_reports_truncation_warning()
    {
        var report = await Pipeline(new ItemsConnector(("a", "good"), ("b", "good")),
            new SyncOptions(CollectSourceRefs: true, MaxReportedRefs: 1)).RunAsync();

        Assert.Equal(["a"], report.SeenSourceRefs);
        Assert.True(report.SourceRefsTruncated);
        Assert.Contains(report.ValidationIssues,
            issue => issue.Code == "SOURCE_REFS_TRUNCATED" && issue.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public async Task Ordinary_run_leaves_refs_null_when_collection_is_disabled()
    {
        var report = await Pipeline(new ItemsConnector(("a", "good")), new SyncOptions()).RunAsync();

        Assert.Null(report.SeenSourceRefs);
    }

    private sealed class StopsPartWay(string reason) : ISourceConnector, IReportsIncompleteDiscovery
    {
        private readonly ItemsConnector inner = new(("a", "good"));
        public string? IncompleteReason { get; private set; }

        public async IAsyncEnumerable<SourceItem> DiscoverAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in inner.DiscoverAsync(cancellationToken)) yield return item;
            IncompleteReason = reason;
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) => inner.FetchAsync(item, cancellationToken);
    }

    [Fact]
    public async Task A_discovery_that_stopped_part_way_reports_an_error_and_no_refs()
    {
        var source = new StopsPartWay("message 2 could not be parsed.");
        var report = await new ConnectivityPipeline(source, new Extractor(), [new Exporter()],
            new PipelineOptions(Sync: new SyncOptions(CollectSourceRefs: true))).RunAsync();

        Assert.Equal(1, report.ItemsProcessed); // what was read is kept
        Assert.Null(report.SeenSourceRefs);     // absence proves nothing
        Assert.Contains("message 2 could not be parsed.", report.Errors);
        Assert.Contains(report.ValidationIssues, i => i.Code == "DISCOVERY_INCOMPLETE");
    }
}
