using OpenTail.Connectivity.Memory;

namespace OpenTail.Connectivity.Tests.Memory;

public class GitHubMemoryIngestionNormalizerTests
{
    [Fact]
    public async Task NormalizeAsync_MapsGitHubFieldsIntoNormalizedEnvelope()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-1",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-300",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "  PR body  ",
            RepositoryScope: "opentail-net/OpenTail",
            RepositoryOwner: "opentail-net",
            RepositoryName: "OpenTail",
            PullRequestNumber: 300,
            IssueNumber: 280,
            CommentId: "c-1",
            ReviewCommentId: "rc-1",
            CommitSha: "abc123",
            Labels: ["memory"],
            Assignees: ["alice"],
            LinkedExternalIds: ["issue-280"],
            Actor: "user:octocat",
            SourceUrl: "https://github.com/opentail-net/OpenTail/pull/300",
            Permalink: "https://github.com/opentail-net/OpenTail/pull/300#issuecomment-1",
            Metadata: new Dictionary<string, string>
            {
                ["state"] = "open",
                ["github.title"] = "Improve ingestion pipeline"
            });

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("github:github:pr-300", normalized.NormalizedId);
        Assert.Contains("Repository: opentail-net/OpenTail", normalized.Content);
        Assert.Contains("Actor: user:octocat", normalized.Content);
        Assert.Contains("Event: PullRequest", normalized.Content);
        Assert.Contains("Title: Improve ingestion pipeline", normalized.Content);
        Assert.Contains("PR: #300", normalized.Content);
        Assert.Contains("CommentId: c-1", normalized.Content);
        Assert.Contains("ReviewCommentId: rc-1", normalized.Content);
        Assert.Contains("Labels: memory", normalized.Content);
        Assert.Contains("Assignees: alice", normalized.Content);
        Assert.Contains("PR body", normalized.Content);
        Assert.Equal("opentail-net", normalized.RepositoryOwner);
        Assert.Equal("OpenTail", normalized.RepositoryName);
        Assert.Equal(300, normalized.PullRequestNumber);
        Assert.Equal(280, normalized.IssueNumber);
        Assert.Equal("c-1", normalized.CommentId);
        Assert.Equal("rc-1", normalized.ReviewCommentId);
        Assert.Equal("abc123", normalized.CommitSha);
        Assert.Equal("memory", normalized.Labels![0]);
        Assert.Equal("alice", normalized.Assignees![0]);
        Assert.Equal("issue-280", normalized.LinkedExternalIds![0]);
        Assert.Equal("github:opentail-net/OpenTail:review-comment:rc-1", normalized.DedupeKey);
        Assert.Equal("github:opentail-net/OpenTail:pr:300", normalized.ThreadRootExternalId);
        Assert.Equal("GitHub", normalized.Metadata!["provenance.sourceType"]);
        Assert.Equal("opentail-net", normalized.Metadata["provenance.repositoryOwner"]);
        Assert.Equal("OpenTail", normalized.Metadata["provenance.repositoryName"]);
        Assert.Equal("300", normalized.Metadata["provenance.pullRequestNumber"]);
        Assert.Equal("open", normalized.Metadata["state"]);
    }

    [Fact]
    public async Task NormalizeAsync_WhenIssue_NoExplicitDedupe_UsesIssueDedupeKey()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-2",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Issue,
            ExternalId: "issue-280",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "issue body",
            RepositoryOwner: "opentail-net",
            RepositoryName: "OpenTail",
            IssueNumber: 280);

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal("github:opentail-net/OpenTail:issue:280:event:issue", normalized.DedupeKey);
    }

    [Fact]
    public async Task NormalizeAsync_WhenNotGitHub_Throws()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-slack-1",
            SourceType: MemoryIngestionSourceType.Slack,
            SourceSystem: "slack",
            EventKind: MemoryIngestionEventKind.Message,
            ExternalId: "msg-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "hello");

        await Assert.ThrowsAsync<InvalidOperationException>(() => normalizer.NormalizeAsync(capture));
    }

    [Fact]
    public async Task NormalizeAsync_WhenIssueComment_IncludesIssueAndCommentContextInContent()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-3",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Comment,
            ExternalId: "issue-comment-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "this needs follow-up",
            RepositoryScope: "opentail-net/OpenTail",
            IssueNumber: 281,
            CommentId: "comment-99");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Contains("Repository: opentail-net/OpenTail", normalized.Content);
        Assert.Contains("Issue: #281", normalized.Content);
        Assert.Contains("CommentId: comment-99", normalized.Content);
        Assert.Contains("this needs follow-up", normalized.Content);
        Assert.Equal("github:opentail-net/OpenTail:comment:comment-99", normalized.DedupeKey);
        Assert.Equal("github:opentail-net/OpenTail:issue:281", normalized.ThreadRootExternalId);
    }

    [Fact]
    public async Task NormalizeAsync_WhenEventUnknown_DefaultsToDocument()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-4",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.Unknown,
            ExternalId: "evt-1",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: "body");

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.Equal(MemoryIngestionEventKind.Document, normalized.EventKind);
    }

    [Fact]
    public async Task NormalizeAsync_LongContent_SummaryUsesEllipsis()
    {
        var normalizer = new GitHubMemoryIngestionNormalizer();

        var capture = new MemoryRawCaptureEnvelope(
            EnvelopeId: "env-gh-5",
            SourceType: MemoryIngestionSourceType.GitHub,
            SourceSystem: "github",
            EventKind: MemoryIngestionEventKind.PullRequest,
            ExternalId: "pr-301",
            CapturedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            ObservedAtUtc: DateTimeOffset.Parse("2026-05-16T10:00:00Z"),
            RawPayload: new string('x', 500));

        var normalized = await normalizer.NormalizeAsync(capture);

        Assert.NotNull(normalized.Summary);
        Assert.EndsWith("…", normalized.Summary);
    }
}
