namespace OpenTail.Connectivity.Tests;

public sealed class StableIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "otail-ids-" + Guid.NewGuid().ToString("N"));

    public StableIdTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Collect : IConnectivityExporter
    {
        public List<NormalisedDocument> Docs { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) { Docs.Add(document); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Items(params SourceItem[] items) : ISourceConnector
    {
        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var i in items) { await Task.Yield(); yield return i; }
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes("body of " + item.DisplayName + new string('.', 60)));
    }

    private static SourceItem Item(string id, string tenant = "acme", string name = "a.txt") => new(id, name, "txt", 1, DateTimeOffset.UnixEpoch, tenant);

    private static async Task<(List<NormalisedDocument> Docs, IngestionReport Report)> Run(ISourceConnector source)
    {
        var collect = new Collect();
        var report = await new ConnectivityPipeline(source, new PlainTextExtractor(), [collect]).RunAsync();
        return (collect.Docs, report);
    }

    private sealed class PlainTextExtractor : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    [Fact]
    public async Task The_same_item_gets_the_same_id_on_every_run()
    {
        var first = (await Run(new Items(Item("x/1")))).Docs.Single();
        var second = (await Run(new Items(Item("x/1")))).Docs.Single();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(32, first.Id.Length);
        Assert.Equal(ConnectivityPipeline.DocumentId("file", "acme", "x/1"), first.Id);
    }

    [Fact]
    public async Task Tenant_and_source_type_keep_ids_apart()
    {
        var a = (await Run(new Items(Item("x/1", "acme")))).Docs.Single().Id;
        var b = (await Run(new Items(Item("x/1", "globex")))).Docs.Single().Id;
        Assert.NotEqual(a, b);
        Assert.NotEqual(ConnectivityPipeline.DocumentId("file", "acme", "x"), ConnectivityPipeline.DocumentId("web", "acme", "x"));
        // The separator keeps "a" + "b\nc" and "a\nb" + "c" apart.
        Assert.NotEqual(ConnectivityPipeline.DocumentId("a", "b\nc", "d"), ConnectivityPipeline.DocumentId("a\nb", "c", "d"));
    }

    [Fact]
    public async Task A_repeated_source_ref_in_one_run_is_reported()
    {
        var (docs, report) = await Run(new Items(Item("dup", name: "one.txt"), Item("dup", name: "two.txt")));
        Assert.Equal(2, docs.Count);
        Assert.Contains(report.ValidationIssues, i => i.Code == "DUPLICATE_SOURCE_REF");
    }

    [Fact]
    public async Task Folder_ids_do_not_depend_on_how_the_root_is_spelled()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "x");
        var plain = new FolderSourceConnector(_dir, "t");
        var spelled = new FolderSourceConnector(_dir + Path.DirectorySeparatorChar + "." + Path.DirectorySeparatorChar, "t");
        var a = new List<string>();
        var b = new List<string>();
        await foreach (var i in plain.DiscoverAsync()) a.Add(i.Id);
        await foreach (var i in spelled.DiscoverAsync()) b.Add(i.Id);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task An_mbox_message_without_a_message_id_gets_the_same_id_every_run()
    {
        var mbox = Path.Combine(_dir, "box.mbox");
        File.WriteAllText(mbox, "From a@example.com Thu Jan  1 00:00:00 2026\nFrom: a@example.com\nSubject: hi\n\nbody\n");
        var ids = new List<string>();
        for (var run = 0; run < 2; run++)
            await foreach (var i in new MboxConnector(mbox, "t").DiscoverAsync()) ids.Add(i.Id);
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
        Assert.StartsWith("sha256:", ids[0]);
    }
}
