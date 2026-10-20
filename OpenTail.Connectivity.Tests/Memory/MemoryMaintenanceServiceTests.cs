using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryMaintenanceServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenRunTwiceWithSameInput_ProducesStableIdempotentActions()
    {
        var service = new DefaultMemoryMaintenanceService();
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var request = BuildRequest(now);

        var first = await service.ExecuteAsync(request);
        var second = await service.ExecuteAsync(request);

        Assert.True(first.IsIdempotentSafe);
        Assert.True(second.IsIdempotentSafe);
        Assert.Equal(first.Actions.Select(a => (a.Kind, a.TargetId, a.Reason)), second.Actions.Select(a => (a.Kind, a.TargetId, a.Reason)));
    }

    [Fact]
    public async Task ExecuteAsync_WhenDataAlreadyHealthy_ReturnsNoRepairOrRetentionActions()
    {
        var service = new DefaultMemoryMaintenanceService();
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Internal,
            TrustLevel: MemoryTrustLevel.High,
            RetentionClass: MemoryRetentionClass.Durable);

        var source = new MemorySource(
            SourceType: MemorySourceType.GitHub,
            SourceSystem: "github",
            ExternalId: "pr-1",
            ProjectScope: "OpenTail.Core",
            Actor: "casey");

        var records = new List<MemoryRecord>
        {
            new(
                Id: "r-1",
                Kind: MemoryRecordKind.DecisionLog,
                Source: source,
                Policy: policy,
                Content: "Decision approved for OpenTail.Core rollout.",
                CreatedAtUtc: now.AddDays(-1),
                ObservedAtUtc: now.AddDays(-1),
                DedupeKey: "k-1")
        };

        var links = new List<MemoryLink>
        {
            new("r-1", MemoryNodeKind.Record, "r-1", MemoryNodeKind.Record, MemoryRelationType.RelatedTo)
        };

        var report = await service.ExecuteAsync(new MemoryMaintenanceRequest(records, links, AsOfUtc: now, CurrentEmbeddingVersion: "rev-1"));

        Assert.DoesNotContain(report.Actions, a => a.Kind is MemoryMaintenanceActionKind.RepairOrphan or MemoryMaintenanceActionKind.RepairLink);
        Assert.DoesNotContain(report.Actions, a => a.Kind is MemoryMaintenanceActionKind.RetentionArchive or MemoryMaintenanceActionKind.RetentionPrune);
    }

    [Fact]
    public async Task ExecuteAsync_WhenMixedDataPresent_EmitsConsolidationAndSummaryActions()
    {
        var service = new DefaultMemoryMaintenanceService();
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

        var report = await service.ExecuteAsync(BuildRequest(now));

        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.Deduplicate);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.CollapseCluster);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.RepairOrphan);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.RepairLink);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.RefreshEmbedding);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.WeeklyDecisionDigest);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.ProjectBrief);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.TeamInteractionSummary);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.UnresolvedTopicView);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.Summarize);
        Assert.Contains(report.Actions, a => a.Kind == MemoryMaintenanceActionKind.IncrementalReindex);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCrashSafeReplayEnabled_PropagatesReplayTokenInReport()
    {
        var service = new DefaultMemoryMaintenanceService();
        var now = DateTimeOffset.Parse("2026-06-01T12:00:00Z");

        var report = await service.ExecuteAsync(BuildRequest(now) with
        {
            CrashSafeReplay = true,
            ReplayToken = "replay-15",
        });

        Assert.Equal("replay-15", report.ReplayToken);
        Assert.Contains(report.Notes, n => n.Contains("replayMode=enabled", StringComparison.Ordinal));
        Assert.Contains(report.Notes, n => n.Contains("replayToken=replay-15", StringComparison.Ordinal));
    }

    private static MemoryMaintenanceRequest BuildRequest(DateTimeOffset now)
    {
        var durable = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.High, MemoryRetentionClass.Durable);
        var temp = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Temporary);
        var expiring = new MemoryPolicy(MemorySensitivity.Internal, MemoryTrustLevel.Medium, MemoryRetentionClass.Expiring);

        var srcCasey = new MemorySource(MemorySourceType.GitHub, "github", ExternalId: "pr-100", ProjectScope: "OpenTail.Core", Actor: "casey");
        var srcJordan = new MemorySource(MemorySourceType.Slack, "slack", ExternalId: "msg-200", ProjectScope: "OpenTail.Core", Actor: "jordan");

        var records = new List<MemoryRecord>
        {
            new(
                Id: "r-dup-1",
                Kind: MemoryRecordKind.DecisionLog,
                Source: srcCasey,
                Policy: durable,
                Content: "Decision approved for rollout with follow-up todo item.",
                CreatedAtUtc: now.AddDays(-2),
                ObservedAtUtc: now.AddDays(-2),
                DedupeKey: "dup-key",
                BaseImportance: 0.9,
                Embedding: new MemoryEmbeddingRef(new MemoryEmbeddingModel("test","m","old-rev",1536,"cosine"), "vec-1")),
            new(
                Id: "r-dup-2",
                Kind: MemoryRecordKind.DecisionLog,
                Source: srcCasey,
                Policy: durable,
                Content: "Decision approved for rollout with follow-up todo item.",
                CreatedAtUtc: now.AddDays(-1),
                ObservedAtUtc: now.AddDays(-1),
                DedupeKey: "dup-key",
                BaseImportance: 0.8,
                ParentRecordId: "missing-parent"),
            new(
                Id: "r-stale-1",
                Kind: MemoryRecordKind.Message,
                Source: srcJordan,
                Policy: temp,
                Content: "Thread discussion unresolved blocked follow-up.",
                CreatedAtUtc: now.AddDays(-80),
                ObservedAtUtc: now.AddDays(-80)),
            new(
                Id: "r-stale-2",
                Kind: MemoryRecordKind.Message,
                Source: srcJordan,
                Policy: temp,
                Content: "Another unresolved topic for thread summary and follow-up.",
                CreatedAtUtc: now.AddDays(-70),
                ObservedAtUtc: now.AddDays(-70)),
            new(
                Id: "r-stale-3",
                Kind: MemoryRecordKind.Message,
                Source: srcJordan,
                Policy: expiring,
                Content: "Old temporary experiment result",
                CreatedAtUtc: now.AddDays(-400),
                ObservedAtUtc: now.AddDays(-400))
        };

        var links = new List<MemoryLink>
        {
            new("r-dup-1", MemoryNodeKind.Record, "r-stale-1", MemoryNodeKind.Record, MemoryRelationType.RelatedTo),
            new("missing-source", MemoryNodeKind.Record, "r-dup-1", MemoryNodeKind.Record, MemoryRelationType.RelatedTo),
            new("r-dup-1", MemoryNodeKind.Record, "missing-target", MemoryNodeKind.Record, MemoryRelationType.RelatedTo),
        };

        return new MemoryMaintenanceRequest(
            Records: records,
            Links: links,
            AsOfUtc: now,
            CurrentEmbeddingVersion: "rev-2026-06",
            ProjectScope: "OpenTail.Core");
    }
}
