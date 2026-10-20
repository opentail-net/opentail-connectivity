using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class MemoryIngestionContractsTests
{
    [Fact]
    public void MemoryRawCaptureEnvelope_CanRoundTripJson()
    {
        var envelope = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-100",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T09:59:00Z"),
            RawPayload: "{\"text\":\"hello\"}",
            PayloadFormat: "json",
            Workspace: "tenant-acme",
            WorkspaceId: "T123",
            ProjectScope: "OpenTail.Core",
            RepositoryScope: "opentail-net/OpenTail",
            ConversationId: "conv-1",
            ThreadId: "thread-1",
            Actor: "user:alex",
            SenderId: "U111",
            ChannelId: "C456",
            ChannelName: "eng",
            ReplyToExternalId: "msg-099",
            EditedAtUtc: DateTimeOffset.Parse("2026-05-14T10:00:30Z"),
            SourceUrl: "https://slack.example/messages/100",
            Permalink: "https://acme.slack.com/archives/C456/p123",
            ContentHash: "hash-1",
            DedupeKey: "slack:msg-100",
            Mentions: ["U222", "U333"],
            AttachmentUrls: ["https://files.example/a.txt"],
            Metadata: new Dictionary<string, string> { ["channel"] = "eng" });

        var json = JsonSerializer.Serialize(envelope);
        var roundTrip = JsonSerializer.Deserialize<MemoryRawCaptureEnvelope>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal("env-1", roundTrip!.EnvelopeId);
        Assert.Equal(MemoryIngestionSourceType.Slack, roundTrip.SourceType);
        Assert.Equal("thread-1", roundTrip.ThreadId);
        Assert.Equal("T123", roundTrip.WorkspaceId);
        Assert.Equal("C456", roundTrip.ChannelId);
        Assert.Equal("eng", roundTrip.ChannelName);
        Assert.Equal("U111", roundTrip.SenderId);
        Assert.Equal("msg-099", roundTrip.ReplyToExternalId);
        Assert.Equal("https://acme.slack.com/archives/C456/p123", roundTrip.Permalink);
        Assert.Equal("U222", roundTrip.Mentions![0]);
        Assert.Equal("https://files.example/a.txt", roundTrip.AttachmentUrls![0]);
        Assert.Equal("slack:msg-100", roundTrip.DedupeKey);
        Assert.Equal("eng", roundTrip.Metadata!["channel"]);
    }

    [Fact]
    public void MemoryNormalizedEnvelope_CanCarryPolicyAndDedupeMetadata()
    {
        var policy = new MemoryPolicy(
            Sensitivity: MemorySensitivity.Internal,
            TrustLevel: MemoryTrustLevel.High,
            RetentionClass: MemoryRetentionClass.Durable,
            PermissionScope: "role:engineer");

        var normalized = new MemoryNormalizedEnvelope(
            NormalizedId: "norm-1",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-200",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-14T10:01:30Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-14T10:01:00Z"),
            Content: "PR merged with migration updates.",
            Title: "Add memory migration",
            Status: "merged",
            Summary: "Merged migration PR",
            Workspace: "tenant-acme",
            ProjectScope: "OpenTail.Persistence.Sqlite",
            RepositoryScope: "opentail-net/OpenTail",
            Actor: "user:casey",
            SourceUrl: "https://github.com/opentail-net/OpenTail/pull/200",
            ContentHash: "hash-200",
            DedupeKey: "github:pr-200",
            Policy: policy,
            Metadata: new Dictionary<string, string> { ["state"] = "merged" });

        Assert.Equal("github:pr-200", normalized.DedupeKey);
        Assert.NotNull(normalized.Policy);
        Assert.Equal(MemoryTrustLevel.High, normalized.Policy!.TrustLevel);
        Assert.Equal("merged", normalized.Metadata!["state"]);
    }

    [Fact]
    public void MemoryRawCaptureEnvelope_CanCarryGitHubSpecificFields()
    {
        var envelope = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-1",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-300",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "pr content",
            RepositoryScope: "opentail-net/OpenTail",
            RepositoryOwner: "opentail-net",
            RepositoryName: "OpenTail",
            PullRequestNumber: 300,
            IssueNumber: 280,
            CommentId: "c-1",
            ReviewCommentId: "rc-1",
            CommitSha: "abc123",
            Labels: ["memory", "ingestion"],
            Assignees: ["alice", "bob"],
            LinkedExternalIds: ["issue-280", "discussion-9"]);

        Assert.Equal("opentail-net", envelope.RepositoryOwner);
        Assert.Equal("OpenTail", envelope.RepositoryName);
        Assert.Equal(300, envelope.PullRequestNumber);
        Assert.Equal(280, envelope.IssueNumber);
        Assert.Equal("c-1", envelope.CommentId);
        Assert.Equal("rc-1", envelope.ReviewCommentId);
        Assert.Equal("abc123", envelope.CommitSha);
        Assert.Equal("memory", envelope.Labels![0]);
        Assert.Equal("alice", envelope.Assignees![0]);
        Assert.Equal("issue-280", envelope.LinkedExternalIds![0]);
    }

    [Fact]
    public void MemoryIngestionResult_CanRepresentDuplicateReplayOutcome()
    {
        var result = new MemoryIngestionResult(
            Success: true,
            RecordId: "rec-1",
            ChunkGroupId: "chunks-1",
            DedupeKey: "slack:msg-100",
            IsDuplicate: true,
            IsReplay: true,
            AuditTrail:
            [
                new MemoryIngestionAuditEvent(
                    AuditId: "audit-1",
                    EnvelopeId: "env-1",
                    Stage: MemoryIngestionStage.Normalize,
                    TimestampUtc: DateTimeOffset.Parse("2026-05-14T10:02:00Z"),
                    Success: true,
                    Message: "normalized successfully")
            ]);

        Assert.True(result.Success);
        Assert.True(result.IsDuplicate);
        Assert.True(result.IsReplay);
        Assert.Single(result.AuditTrail!);
        Assert.Equal(MemoryIngestionStage.Normalize, result.AuditTrail![0].Stage);
    }
}
