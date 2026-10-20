using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryContractValidationTests
{
    [Fact]
    public void ValidateRequest_WhenTopKNonPositive_Throws()
    {
        var request = new MemoryRetrievalRequest("q", TopK: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryContractValidation.Validate(request));
    }

    [Fact]
    public void ValidateRequest_WhenQueryBlank_Throws()
    {
        var request = new MemoryRetrievalRequest(" ", TopK: 1);
        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(request));
    }

    [Fact]
    public void ValidateRequest_WhenFromAfterTo_Throws()
    {
        var request = new MemoryRetrievalRequest(
            Query: "q",
            TopK: 1,
            FromUtc: DateTimeOffset.Parse("2026-05-22T12:00:00Z"),
            ToUtc: DateTimeOffset.Parse("2026-05-22T11:00:00Z"));

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(request));
    }

    [Fact]
    public void ValidateEmbedding_WhenDimensionsNonPositive_Throws()
    {
        var model = new MemoryEmbeddingModel("p", "m", "r", 0, "cosine");
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryContractValidation.Validate(model));
    }

    [Fact]
    public void ValidateEmbedding_WhenProviderBlank_Throws()
    {
        var model = new MemoryEmbeddingModel(" ", "m", "r", 1536, "cosine");
        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(model));
    }

    [Fact]
    public void ValidateEmbedding_WhenModelBlank_Throws()
    {
        var model = new MemoryEmbeddingModel("p", " ", "r", 1536, "cosine");
        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(model));
    }

    [Fact]
    public void ValidateEmbedding_WhenDistanceMetricBlank_Throws()
    {
        var model = new MemoryEmbeddingModel("p", "m", "r", 1536, " ");
        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(model));
    }

    [Fact]
    public void ValidateLink_WhenWeightOutOfRange_Throws()
    {
        var link = new MemoryLink("a", MemoryNodeKind.Record, "b", MemoryNodeKind.Record, MemoryRelationType.RelatedTo, 1.5);
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryContractValidation.Validate(link));
    }

    [Fact]
    public void ValidateFact_WhenConfidenceOutOfRange_Throws()
    {
        var fact = new MemoryFact("f", "r", "statement", 1.2, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryContractValidation.Validate(fact));
    }

    [Fact]
    public void ValidateFact_WhenStatementBlank_Throws()
    {
        var fact = new MemoryFact("f", "r", " ", 0.8, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(fact));
    }

    [Fact]
    public void ValidateFact_WhenValidFromAfterValidTo_Throws()
    {
        var fact = new MemoryFact(
            Id: "f",
            RecordId: "r",
            Statement: "statement",
            Confidence: 0.8,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            ValidFromUtc: DateTimeOffset.Parse("2026-05-22T12:00:00Z"),
            ValidToUtc: DateTimeOffset.Parse("2026-05-22T11:00:00Z"));

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(fact));
    }

    [Fact]
    public void ValidateFact_WhenSupersededWithoutReplacement_Throws()
    {
        var fact = new MemoryFact(
            Id: "f",
            RecordId: "r",
            Statement: "statement",
            Confidence: 0.8,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            IsSuperseded: true,
            SupersededByFactId: null);

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(fact));
    }

    [Fact]
    public void ValidateRecord_WhenExpiringWithoutExpiry_Throws()
    {
        var record = new MemoryRecord(
            Id: "r1",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Expiring),
            Content: "content",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.5);

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(record));
    }

    [Fact]
    public void ValidateRecord_WhenDeletedWithoutReason_Throws()
    {
        var record = new MemoryRecord(
            Id: "r2",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "content",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.5,
            DeletedAtUtc: DateTimeOffset.UtcNow);

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(record));
    }

    [Fact]
    public void ValidateRecord_WhenSupersededMissingFields_Throws()
    {
        var record = new MemoryRecord(
            Id: "r3",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "content",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.5,
            IsSuperseded: true);

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(record));
    }

    [Fact]
    public void ValidateRecord_WhenValid_DoesNotThrow()
    {
        var policy = new MemoryPolicy(
            MemorySensitivity.Internal,
            MemoryTrustLevel.High,
            MemoryRetentionClass.Expiring,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(7));

        var record = new MemoryRecord(
            Id: "r4",
            Kind: MemoryRecordKind.DecisionLog,
            Source: new MemorySource(MemorySourceType.GitHub, "github"),
            Policy: policy,
            Content: "decision",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.8,
            IsSuperseded: true,
            SupersededByRecordId: "r5",
            SupersededAtUtc: DateTimeOffset.UtcNow);

        MemoryContractValidation.Validate(record);
    }

    [Fact]
    public void ValidateRecord_WhenIdBlank_Throws()
    {
        var record = new MemoryRecord(
            Id: " ",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: "content",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.5);

        Assert.Throws<ArgumentException>(() => MemoryContractValidation.Validate(record));
    }

    [Fact]
    public void ValidateRecord_WhenContentAndSummaryBlank_Throws()
    {
        var record = new MemoryRecord(
            Id: "r6",
            Kind: MemoryRecordKind.SourceDocument,
            Source: new MemorySource(MemorySourceType.LocalDocs, "filesystem"),
            Policy: new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable),
            Content: " ",
            Summary: " ",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            BaseImportance: 0.5);

        Assert.Throws<InvalidOperationException>(() => MemoryContractValidation.Validate(record));
    }
}
