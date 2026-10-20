using OpenTail.Connectivity.Files.Redaction;

namespace OpenTail.Connectivity.Tests;

public sealed class RedactionTests
{
    private static NormalisedDocument Doc(string body, string title = "t", Dictionary<string, string>? metadata = null) =>
        new("id", title, body, "hash", "ref", "file", "tenant", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, metadata ?? []);

    private static readonly PatternRedactor All = new(new RedactionOptions(EmailAddresses: true, PhoneNumbers: true, PaymentCards: true, Ibans: true));

    [Theory]
    [InlineData("mail ada@example.co.uk now", "mail [REDACTED:email] now")]
    [InlineData("card 4111 1111 1111 1111 ok", "card [REDACTED:card] ok")]
    [InlineData("card 4111-1111-1111-1112 ok", "card 4111-1111-1111-1112 ok")]          // fails Luhn: left alone
    [InlineData("iban GB82 WEST 1234 5698 7654 32.", "iban [REDACTED:iban].")]
    [InlineData("iban DE89370400440532013000", "iban [REDACTED:iban]")]
    [InlineData("iban DE89370400440532013001", "iban DE89370400440532013001")]          // bad checksum
    [InlineData("call +44 20 7946 0958 today", "call [REDACTED:phone] today")]
    [InlineData("call (555) 123-4567 today", "call [REDACTED:phone] today")]
    [InlineData("on 2026-10-17 at 10:30", "on 2026-10-17 at 10:30")]                    // dates and times stay
    [InlineData("order 1234 of 5678", "order 1234 of 5678")]
    public void Patterns(string input, string expected) => Assert.Equal(expected, All.Transform(Doc(input)).Body);

    [Fact]
    public void Title_and_metadata_are_redacted_and_counted()
    {
        var redacted = All.Transform(Doc("ada@example.com and bob@example.com", "From ada@example.com", new() { ["email.from"] = "ada@example.com" }));
        Assert.Equal("From [REDACTED:email]", redacted.Title);
        Assert.Equal("[REDACTED:email]", redacted.Metadata["email.from"]);
        Assert.Equal("4", redacted.Metadata["redaction.count"]);
    }

    [Fact]
    public void Custom_rules_and_nothing_on_by_default()
    {
        Assert.Equal("ticket ref ACME-123", new PatternRedactor(new RedactionOptions()).Transform(Doc("ticket ref ACME-123")).Body);
        var custom = new PatternRedactor(new RedactionOptions(Custom: new Dictionary<string, string> { ["employee"] = @"EMP-\d{5}" }));
        Assert.Equal("by [REDACTED:employee]", custom.Transform(Doc("by EMP-00042")).Body);
        Assert.Throws<ArgumentException>(() => new PatternRedactor(new RedactionOptions(Custom: new Dictionary<string, string> { ["bad name]"] = "x" })));
    }

    private sealed class One : ISourceConnector
    {
        public async IAsyncEnumerable<SourceItem> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new SourceItem("r", "a.txt", "txt", 1, DateTimeOffset.UnixEpoch, "t");
        }

        public Task<byte[]> FetchAsync(SourceItem item, CancellationToken cancellationToken = default) =>
            Task.FromResult(Encoding.UTF8.GetBytes("Contact ada@example.com about the renewal, which is due at the end of the month."));
    }

    private sealed class Plain : ITextExtractor
    {
        public TextExtractionResult Extract(string fileName, byte[] content) => new(true, Encoding.UTF8.GetString(content));
    }

    private sealed class Collect : IConnectivityExporter
    {
        public List<NormalisedDocument> Docs { get; } = [];
        public Task ExportAsync(NormalisedDocument document, CancellationToken cancellationToken = default) { Docs.Add(document); return Task.CompletedTask; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Throws : IDocumentTransformer
    {
        public NormalisedDocument Transform(NormalisedDocument document) => throw new InvalidOperationException("rule broke");
    }

    [Fact]
    public async Task The_pipeline_hashes_the_redacted_body()
    {
        var collect = new Collect();
        await new ConnectivityPipeline(new One(), new Plain(), [collect], new PipelineOptions(Transformers: [All])).RunAsync();
        var doc = Assert.Single(collect.Docs);
        Assert.DoesNotContain("ada@example.com", doc.Body);
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(doc.Body))).ToLowerInvariant();
        Assert.Equal(expected, doc.ContentHash);
    }

    [Fact]
    public async Task A_failing_transformer_keeps_the_item_from_export()
    {
        var collect = new Collect();
        var report = await new ConnectivityPipeline(new One(), new Plain(), [collect], new PipelineOptions(Transformers: [new Throws()])).RunAsync();
        Assert.Empty(collect.Docs);
        Assert.Equal(1, report.ItemsFailed);
        Assert.Contains(report.Errors, e => e.Contains("not exported"));
    }
}
