using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryOperatorControlServiceTests
{
    [Fact]
    public void Apply_PinAndUnpin_AreReversibleAndAudited()
    {
        var service = CreateService();

        var pin = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.Pin,
            Actor: "operator",
            RecordId: "rec-1"));

        var unpin = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.Unpin,
            Actor: "operator",
            RecordId: "rec-1"));

        Assert.True(pin.Success);
        Assert.True(unpin.Success);
        Assert.Contains("rec-1", pin.Snapshot.PinnedRecordIds);
        Assert.DoesNotContain("rec-1", unpin.Snapshot.PinnedRecordIds);
        Assert.Equal(2, unpin.Snapshot.AuditTrail.Count);
    }

    [Fact]
    public void Apply_BlockAndUnblockSource_AreReversible()
    {
        var service = CreateService();

        var block = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.BlockSource,
            Actor: "operator",
            SourceSystem: "slack"));

        var unblock = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.UnblockSource,
            Actor: "operator",
            SourceSystem: "slack"));

        Assert.True(block.Success);
        Assert.True(unblock.Success);
        Assert.Contains("slack", block.Snapshot.BlockedSources, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("slack", unblock.Snapshot.BlockedSources, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_MissingRecord_FailsSafelyWithoutMutation()
    {
        var service = CreateService();
        var before = service.Snapshot();

        var result = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.Forget,
            Actor: "operator",
            RecordId: "missing"));

        var after = service.Snapshot();
        Assert.False(result.Success);
        Assert.Equal(before.Records.Count, after.Records.Count);
        Assert.Equal(before.Entities.Count, after.Entities.Count);
    }

    [Fact]
    public void Apply_ArchiveForgetReembedMergeRelabelRetention_WorkAsExpected()
    {
        var service = CreateService();

        var archive = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.Archive,
            Actor: "operator",
            RecordId: "rec-1",
            Note: "manual archive"));
        Assert.True(archive.Success);
        Assert.NotNull(archive.Snapshot.Records["rec-1"].DeletedAtUtc);

        var reembed = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.ReEmbed,
            Actor: "operator",
            RecordId: "rec-1",
            EmbeddingRevision: "rev-ops"));
        Assert.True(reembed.Success);
        Assert.Equal("rev-ops", reembed.Snapshot.Records["rec-1"].Embedding!.Model.Revision);

        var relabel = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.RelabelEntity,
            Actor: "operator",
            EntityId: "ent-1",
            Label: "canonical-owner"));
        Assert.True(relabel.Success);
        Assert.Contains("canonical-owner", relabel.Snapshot.Entities["ent-1"].Tags!);

        var retention = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.UpdateRetention,
            Actor: "operator",
            RecordId: "rec-1",
            RetentionClass: MemoryRetentionClass.Expiring));
        Assert.True(retention.Success);
        Assert.Equal(MemoryRetentionClass.Expiring, retention.Snapshot.Records["rec-1"].Policy.RetentionClass);

        var markStale = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.MarkStale,
            Actor: "operator",
            RecordId: "rec-1"));
        Assert.True(markStale.Success);
        Assert.True(markStale.Snapshot.Records["rec-1"].IsSuperseded);

        var markCanonical = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.MarkCanonical,
            Actor: "operator",
            RecordId: "rec-1"));
        Assert.True(markCanonical.Success);
        Assert.False(markCanonical.Snapshot.Records["rec-1"].IsSuperseded);
        Assert.Contains("canonical", markCanonical.Snapshot.Records["rec-1"].Labels!);

        var merge = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.MergeDuplicates,
            Actor: "operator",
            RecordId: "rec-1",
            SecondaryRecordId: "rec-2"));
        Assert.True(merge.Success);
        Assert.False(merge.Snapshot.Records.ContainsKey("rec-2"));

        var forget = service.Apply(new MemoryOperatorActionRequest(
            Kind: MemoryOperatorActionKind.Forget,
            Actor: "operator",
            RecordId: "rec-1"));
        Assert.True(forget.Success);
        Assert.False(forget.Snapshot.Records.ContainsKey("rec-1"));

        Assert.True(forget.Snapshot.AuditTrail.Count >= 8);
    }

    private static InMemoryMemoryOperatorControlService CreateService()
    {
        var source = new MemorySource(
            SourceType: MemorySourceType.UserNote,
            SourceSystem: "slack",
            ExternalId: "msg-1",
            Actor: "casey");
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Internal,
            TrustLevel: MemoryTrustLevel.High,
            RetentionClass: MemoryRetentionClass.Durable);

        var records = new[]
        {
            new MemoryRecord(
                Id: "rec-1",
                Kind: MemoryRecordKind.DecisionLog,
                Source: source,
                Policy: policy,
                Content: "Decision approved",
                CreatedAtUtc: DateTimeOffset.Parse("2026-06-02T12:00:00Z"),
                ObservedAtUtc: DateTimeOffset.Parse("2026-06-02T12:00:00Z"),
                Labels: ["primary"]),
            new MemoryRecord(
                Id: "rec-2",
                Kind: MemoryRecordKind.DecisionLog,
                Source: source,
                Policy: policy,
                Content: "Decision approved duplicate",
                CreatedAtUtc: DateTimeOffset.Parse("2026-06-02T12:01:00Z"),
                ObservedAtUtc: DateTimeOffset.Parse("2026-06-02T12:01:00Z"),
                Labels: ["duplicate"]),
        };

        var entities = new[]
        {
            new MemoryEntity(
                Id: "ent-1",
                Kind: "person",
                Name: "Casey",
                Tags: ["owner"])
        };

        return new InMemoryMemoryOperatorControlService(records, entities);
    }
}
