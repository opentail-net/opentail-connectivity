using System.IO.Compression;
using OpenTail.Connectivity.Storage;

namespace OpenTail.Connectivity.Tests.Storage;

public sealed class ObjectStoreReaderTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private static InMemoryObjectStoreClient Store() => new InMemoryObjectStoreClient("acme-exports")
        .Put("finance/monthly/2026-09.csv", "month,total\n2026-09,1200\n", T)
        .Put("finance/monthly/2026-08.csv", "month,total\n2026-08,900\n", T.AddDays(-30))
        .Put("finance/monthly/", [])                               // folder placeholder
        .Put("finance-private/salaries.csv", "secret", T)
        .Put("crm/exports/leads.json", """[{"name":"Ada"}]""", T);

    private static ObjectStoreReader Reader(InMemoryObjectStoreClient store) =>
        new(store, new ObjectStoreReaderOptions { Prefixes = ["finance/monthly/"] });

    [Fact]
    public async Task Lists_the_only_prefix_by_default_and_leaves_out_folder_placeholders()
    {
        var result = await Reader(Store()).ListAsync();
        Assert.Equal(["finance/monthly/2026-08.csv", "finance/monthly/2026-09.csv"], result.Objects.Select(o => o.Key));
        Assert.Null(result.NextResumeToken);
    }

    [Fact]
    public async Task Pages_with_the_resume_token()
    {
        var store = Store();
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["finance/"], MaxListItems = 1 });
        var first = await reader.ListAsync();
        Assert.Single(first.Objects);
        Assert.NotNull(first.NextResumeToken);
        var second = await reader.ListAsync(resumeToken: first.NextResumeToken);
        Assert.NotEqual(first.Objects[0].Key, second.Objects.Single().Key);
    }

    [Fact]
    public async Task With_several_prefixes_the_caller_must_name_one()
    {
        var reader = new ObjectStoreReader(Store(), new ObjectStoreReaderOptions { Prefixes = ["finance/monthly/", "crm/exports/"] });
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => reader.ListAsync());
        Assert.Single((await reader.ListAsync("crm/exports/")).Objects);
    }

    [Theory]
    [InlineData("finance-private/salaries.csv")]          // the allowed prefix is "finance/", not "finance"
    [InlineData("finance/../finance-private/salaries.csv")]
    [InlineData("/finance/monthly/2026-09.csv")]
    [InlineData("Finance/monthly/2026-09.csv")]           // compared exactly
    [InlineData("crm/exports/leads.json")]
    public async Task Refuses_keys_outside_the_prefixes_before_any_call(string key)
    {
        var store = Store();
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["finance/monthly/"] });
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => reader.ReadTextAsync(key));
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => reader.ListAsync(key));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task A_key_with_dot_dot_inside_an_allowed_prefix_is_an_ordinary_key()
    {
        // S3 does not resolve "..": this names a different object, which simply does not exist.
        var store = Store();
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["finance/monthly/"] });
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => reader.ReadTextAsync("finance/monthly/../../finance-private/salaries.csv"));
        Assert.Equal(["head:finance/monthly/../../finance-private/salaries.csv"], store.Calls);
    }

    [Fact]
    public async Task Reads_text_whole()
    {
        var text = await Reader(Store()).ReadTextAsync("finance/monthly/2026-09.csv");
        Assert.Equal("month,total\n2026-09,1200\n", text.Text);
        Assert.False(text.Truncated);
        Assert.Null(text.Note);
        Assert.Equal(T, text.LastModified);
    }

    [Fact]
    public async Task Reads_the_start_of_a_large_text_object_and_says_so()
    {
        var store = new InMemoryObjectStoreClient().Put("logs/app.log", string.Concat(Enumerable.Repeat("line é\n", 1000)));
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"], MaxReadBytes = 100 });
        var text = await reader.ReadTextAsync("logs/app.log");
        Assert.True(text.Truncated);
        Assert.StartsWith("line é\nline é\n", text.Text);
        Assert.DoesNotContain('�', text.Text); // a split "é" is dropped, not shown as a replacement character
        Assert.Contains("first 100 bytes", text.Note);
    }

    [Fact]
    public async Task Decompresses_gz_and_names_the_inner_file()
    {
        var store = new InMemoryObjectStoreClient().Put("logs/app.log.gz", Gzip("hello from a gzipped log\n"));
        var text = await new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"] }).ReadTextAsync("logs/app.log.gz");
        Assert.Equal("hello from a gzipped log\n", text.Text);
        Assert.False(text.Truncated);
    }

    [Fact]
    public async Task Decompresses_content_encoding_gzip()
    {
        var store = new InMemoryObjectStoreClient().Put("logs/app.log", Gzip("encoded"), contentEncoding: "gzip");
        var text = await new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"] }).ReadTextAsync("logs/app.log");
        Assert.Equal("encoded", text.Text);
    }

    [Fact]
    public async Task A_small_gz_cannot_expand_without_bound()
    {
        var bomb = Gzip(new string('a', 20_000_000));
        Assert.True(bomb.Length < 100_000);
        var store = new InMemoryObjectStoreClient().Put("logs/bomb.txt.gz", bomb);
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"], MaxReadBytes = 200_000, MaxDecompressedBytes = 1000 });
        var text = await reader.ReadTextAsync("logs/bomb.txt.gz");
        Assert.True(text.Truncated);
        Assert.Equal(1000, text.Text.Length);
        Assert.Contains("cut at 1000 bytes", text.Note);
    }

    [Fact]
    public async Task A_gz_cut_by_the_read_limit_keeps_what_decoded()
    {
        var random = new Random(7);
        var words = string.Join(' ', Enumerable.Range(0, 50_000).Select(_ => random.Next(100_000).ToString()));
        var store = new InMemoryObjectStoreClient().Put("logs/big.txt.gz", Gzip(words));
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"], MaxReadBytes = 2000 });
        var text = await reader.ReadTextAsync("logs/big.txt.gz");
        Assert.True(text.Truncated);
        Assert.NotEmpty(text.Text);
        Assert.StartsWith(text.Text, words, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_gzip_is_refused()
    {
        var store = new InMemoryObjectStoreClient().Put("logs/bad.gz", "not gzip at all");
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() =>
            new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["logs/"] }).ReadTextAsync("logs/bad.gz"));
    }

    [Theory]
    [InlineData("data/x.zip")]
    [InlineData("data/x.tar.gz")]
    [InlineData("data/x.parquet")]
    public async Task Archives_and_columnar_data_are_refused_before_any_call(string key)
    {
        var store = new InMemoryObjectStoreClient().Put(key, "x");
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() =>
            new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["data/"] }).ReadTextAsync(key));
        Assert.Empty(store.Calls);
    }

    [Fact]
    public async Task Objects_that_need_a_restore_are_never_read()
    {
        var store = new InMemoryObjectStoreClient().Put("old/a.txt", Encoding.UTF8.GetBytes("x"), storageClass: "DEEP_ARCHIVE", needsRestore: true);
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["old/"], ReadColdStorage = true });
        var ex = await Assert.ThrowsAsync<ObjectStoreRefusedException>(() => reader.ReadTextAsync("old/a.txt"));
        Assert.Contains("restore", ex.Message);
        Assert.DoesNotContain("read:old/a.txt", store.Calls);
    }

    [Fact]
    public async Task Cold_objects_are_read_only_when_allowed()
    {
        var store = new InMemoryObjectStoreClient().Put("old/a.txt", Encoding.UTF8.GetBytes("cold"), storageClass: "GLACIER_IR", costsExtraToRead: true);
        await Assert.ThrowsAsync<ObjectStoreRefusedException>(() =>
            new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["old/"] }).ReadTextAsync("old/a.txt"));
        var text = await new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["old/"], ReadColdStorage = true }).ReadTextAsync("old/a.txt");
        Assert.Equal("cold", text.Text);
    }

    [Fact]
    public async Task Binary_formats_go_through_the_extractor_and_only_whole()
    {
        var extractor = new RecordingExtractor();
        var store = new InMemoryObjectStoreClient()
            .Put("docs/small.pdf", new byte[50])
            .Put("docs/large.pdf", new byte[5000]);
        var reader = new ObjectStoreReader(store, new ObjectStoreReaderOptions { Prefixes = ["docs/"], MaxReadBytes = 1000, Extractor = extractor });

        var small = await reader.ReadTextAsync("docs/small.pdf");
        Assert.Equal("extracted small.pdf (50 bytes)", small.Text);

        var large = await reader.ReadTextAsync("docs/large.pdf");
        Assert.Equal("", large.Text);
        Assert.True(large.Truncated);
        Assert.Contains("only be read whole", large.Note);
        Assert.Equal(["small.pdf"], extractor.Names);
    }

    [Fact]
    public async Task A_missing_object_throws_not_found()
    {
        await Assert.ThrowsAsync<ObjectNotFoundException>(() => Reader(Store()).ReadTextAsync("finance/monthly/1999-01.csv"));
    }

    [Fact]
    public void Needs_at_least_one_prefix()
    {
        Assert.Throws<ArgumentException>(() => new ObjectStoreReader(Store(), new ObjectStoreReaderOptions()));
    }

    internal static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }

    private sealed class RecordingExtractor : ITextExtractor
    {
        public List<string> Names { get; } = [];

        public TextExtractionResult Extract(string fileName, byte[] content)
        {
            Names.Add(fileName);
            return new TextExtractionResult(true, $"extracted {fileName} ({content.Length} bytes)");
        }
    }
}
