using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryIngestionPolicyTests
{
    private static MemoryNormalizedEnvelope CreateEnvelope(
        string externalId,
        string content,
        string? contentHash,
        DateTimeOffset observedAt,
        string? dedupeKey = null)
    {
        return new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: externalId,
            CapturedAtUtc: observedAt,
            ObservedAtUtc: observedAt,
            Content: content,
            ContentHash: contentHash,
            DedupeKey: dedupeKey);
    }

    [Fact]
    public void Evaluate_WhenNoExistingDedupeKey_Accepts()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-1", "hello", "hash-1", DateTimeOffset.Parse("2026-05-14T12:00:00Z"));

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

        Assert.Equal(MemoryIngestionDecisionKind.Accept, decision.Kind);
        Assert.False(string.IsNullOrWhiteSpace(decision.DedupeKey));
    }

    [Fact]
    public void Evaluate_WhenSameDedupeAndSameHash_IsDuplicate()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-2", "hello", "hash-2", DateTimeOffset.Parse("2026-05-14T12:00:00Z"), dedupeKey: "slack:msg-2");
        var existing = CreateEnvelope("msg-2", "hello", "hash-2", DateTimeOffset.Parse("2026-05-14T12:00:00Z"), dedupeKey: "slack:msg-2");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope> { ["slack:msg-2"] = existing },
            dedupeToRecordId: new Dictionary<string, string> { ["slack:msg-2"] = "rec-2" });

        Assert.Equal(MemoryIngestionDecisionKind.Duplicate, decision.Kind);
        Assert.Equal("rec-2", decision.ExistingRecordId);
    }

    [Fact]
    public void Evaluate_WhenSameDedupeAndNewerObservedAt_IsReplayUpdate()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-3", "new content", "hash-new", DateTimeOffset.Parse("2026-05-14T12:05:00Z"), dedupeKey: "slack:msg-3");
        var existing = CreateEnvelope("msg-3", "old content", "hash-old", DateTimeOffset.Parse("2026-05-14T12:00:00Z"), dedupeKey: "slack:msg-3");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope> { ["slack:msg-3"] = existing },
            dedupeToRecordId: new Dictionary<string, string> { ["slack:msg-3"] = "rec-3" });

        Assert.Equal(MemoryIngestionDecisionKind.ReplayUpdate, decision.Kind);
    }

    [Fact]
    public void Evaluate_WhenSameDedupeAndOlderObservedAt_Rejects()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-4", "older replay", "hash-old", DateTimeOffset.Parse("2026-05-14T11:59:00Z"), dedupeKey: "slack:msg-4");
        var existing = CreateEnvelope("msg-4", "newer existing", "hash-new", DateTimeOffset.Parse("2026-05-14T12:00:00Z"), dedupeKey: "slack:msg-4");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope> { ["slack:msg-4"] = existing },
            dedupeToRecordId: new Dictionary<string, string> { ["slack:msg-4"] = "rec-4" });

        Assert.Equal(MemoryIngestionDecisionKind.Reject, decision.Kind);
    }

    [Fact]
    public void CreateFingerprint_WhenNoContentHash_UsesDeterministicHash()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-5", "deterministic content", null, DateTimeOffset.Parse("2026-05-14T12:00:00Z"));

        var fp1 = policy.CreateFingerprint(envelope);
        var fp2 = policy.CreateFingerprint(envelope);

        Assert.Equal(fp1.ContentIdentity, fp2.ContentIdentity);
        Assert.Equal("slack:msg-5", fp1.DedupeKey);
        Assert.Equal(fp1.DedupeKey, fp2.DedupeKey);
    }

    [Fact]
    public void Evaluate_WhenSameDedupeAndNullHashesButDifferentContent_IsReplayUpdate()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = CreateEnvelope("msg-6", "new content", null, DateTimeOffset.Parse("2026-05-14T12:05:00Z"), dedupeKey: "slack:msg-6");
        var existing = CreateEnvelope("msg-6", "old content", null, DateTimeOffset.Parse("2026-05-14T12:00:00Z"), dedupeKey: "slack:msg-6");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope> { ["slack:msg-6"] = existing },
            dedupeToRecordId: new Dictionary<string, string> { ["slack:msg-6"] = "rec-6" });

        Assert.Equal(MemoryIngestionDecisionKind.ReplayUpdate, decision.Kind);
    }

    [Fact]
    public void CreateFingerprint_WhenSourceSystemMissing_Throws()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: " ",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-7",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "content");

        var ex = Assert.Throws<InvalidOperationException>(() => policy.CreateFingerprint(envelope));
        Assert.Equal("Normalized envelope requires SourceSystem.", ex.Message);
    }

    [Fact]
    public void CreateFingerprint_WhenExternalIdMissing_Throws()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: " ",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "content");

        var ex = Assert.Throws<InvalidOperationException>(() => policy.CreateFingerprint(envelope));
        Assert.Equal("Normalized envelope requires ExternalId.", ex.Message);
    }

    [Fact]
    public void Evaluate_WhenAlwaysWriteDecisionSignalPresent_Accepts()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-always-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "Decision approved for production rollout and task outcome completed.");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

        Assert.Equal(MemoryIngestionDecisionKind.Accept, decision.Kind);
    }

    [Fact]
    public void Evaluate_WhenTransientNoiseOnly_Rejects()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.Diagnostic,
            SourceSystem: "diagnostics",
            EventKind: MemoryIngestionEventKind.DiagnosticEvent,
            ExternalId: "diag-noise-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "heartbeat ok retrying debug loop transient noise");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

        Assert.Equal(MemoryIngestionDecisionKind.Reject, decision.Kind);
        Assert.Contains("transient noise", decision.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WhenSensitiveContentAndAlwaysWriteSignal_ScrubsAndAccepts()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-901",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "Decision approved. token=abc123 and password=letmein must be removed.",
            Summary: "approved with bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

        Assert.Equal(MemoryIngestionDecisionKind.ScrubAndAccept, decision.Kind);
        Assert.NotNull(decision.SanitizedContent);
        Assert.DoesNotContain("abc123", decision.SanitizedContent!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("letmein", decision.SanitizedContent!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", decision.SanitizedContent!, StringComparison.Ordinal);
        Assert.NotNull(decision.SanitizedSummary);
        Assert.Contains("[REDACTED]", decision.SanitizedSummary!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WhenContentIsSafe_DoesNotScrub()
    {
        var policy = new DefaultMemoryIngestionPolicy();
        var envelope = new MemoryNormalizedEnvelope(
            NormalizedId: Guid.NewGuid().ToString("N"),
            SourceType: MemoryIngestionSourceType.LocalDocs,
            SourceSystem: "docs",
            EventKind: MemoryIngestionEventKind.Document,
            ExternalId: "doc-safe-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T12:00:00Z"),
            Content: "Architecture baseline and policy contract for stable repository facts.",
            Summary: "Durable project memory baseline.");

        var decision = policy.Evaluate(
            envelope,
            byDedupeKey: new Dictionary<string, MemoryNormalizedEnvelope>(),
            dedupeToRecordId: new Dictionary<string, string>());

        Assert.Equal(MemoryIngestionDecisionKind.Accept, decision.Kind);
        Assert.Null(decision.SanitizedContent);
        Assert.Null(decision.SanitizedSummary);
    }
}
