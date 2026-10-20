namespace OpenTail.Connectivity.Tests;

public sealed class SourceMetadataTests
{
    private sealed class One(SourceItem item) : ISourceConnector
    {
        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return item;
        }

        public Task<byte[]> FetchAsync(SourceItem i, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes(new string('x', 80)));
    }

    private sealed class Plain : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Collect : IConnectivityExporter
    {
        public NormalisedDocument? Doc { get; private set; }
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) { Doc = document; return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<NormalisedDocument> Run(IReadOnlyDictionary<string, string>? metadata)
    {
        var collect = new Collect();
        await new ConnectivityPipeline(new One(new SourceItem("r1", "a.txt", "txt", 3, DateTimeOffset.UnixEpoch, "t", metadata)), new Plain(), [collect]).RunAsync();
        return collect.Doc!;
    }

    [Fact]
    public async Task Item_metadata_lands_under_source_and_cannot_override_the_pipeline_keys()
    {
        var doc = await Run(new Dictionary<string, string>
        {
            ["url"] = "https://example.com/a",
            [SourceKeys.Container] = "Finance",
            ["source.sizeBytes"] = "999",
        });
        Assert.Equal("https://example.com/a", doc.Metadata[SourceKeys.Url]);
        Assert.Equal("Finance", doc.Metadata[SourceKeys.Container]);
        Assert.Equal("3", doc.Metadata["source.sizeBytes"]);
    }

    [Fact]
    public async Task Absence_of_captured_permissions_reads_as_not_captured()
    {
        Assert.False(SourceAcl.IsCaptured((await Run(null)).Metadata));
        Assert.False(SourceAcl.IsCaptured((await Run(SourceAcl.NotCaptured())).Metadata));
        Assert.True(SourceAcl.IsCaptured((await Run(SourceAcl.Captured("organisation"))).Metadata));
    }

    [Fact]
    public void Principals_over_the_cap_are_cut_and_the_item_becomes_restricted()
    {
        var meta = SourceAcl.Captured("organisation", Enumerable.Range(0, 5).Select(i => "user" + i), maxPrincipals: 3);
        Assert.Equal("restricted", meta[SourceKeys.AclVisibility]);
        Assert.Equal("true", meta[SourceKeys.AclTruncated]);
        Assert.Equal(["user0", "user1", "user2"], JsonSerializer.Deserialize<string[]>(meta[SourceKeys.AclPrincipals])!);

        var fits = SourceAcl.Captured("private", ["a", "a", "b"]);
        Assert.Equal("private", fits[SourceKeys.AclVisibility]);
        Assert.False(fits.ContainsKey(SourceKeys.AclTruncated));
        Assert.Equal(["a", "b"], JsonSerializer.Deserialize<string[]>(fits[SourceKeys.AclPrincipals])!);
    }
}
