// Moved from opentail's OpenTail.Core.Memory (GitHubMemoryIngestionNormalizer.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class GitHubMemoryIngestionNormalizer : IMemoryIngestionNormalizer
{
    public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (capture.SourceType != MemoryIngestionSourceType.GitHub)
            throw new InvalidOperationException("GitHub normalizer requires GitHub source type.");

        if (string.IsNullOrWhiteSpace(capture.ExternalId))
            throw new InvalidOperationException("GitHub capture requires an external id.");

        var effectiveSourceSystem = GetEffectiveSourceSystem(capture.SourceSystem);
        var normalizedContent = NormalizeContent(capture.RawPayload);
        var contentWithContext = BuildContentWithContext(capture, normalizedContent);
        var dedupeKey = ResolveDedupeKey(capture, effectiveSourceSystem);
        var metadata = BuildProvenanceMetadata(capture, effectiveSourceSystem);

        var normalized = new MemoryNormalizedEnvelope(
            NormalizedId: $"github:{effectiveSourceSystem}:{capture.ExternalId}",
            SourceType: capture.SourceType,
            SourceSystem: effectiveSourceSystem,
            EventKind: capture.EventKind == MemoryIngestionEventKind.Unknown ? MemoryIngestionEventKind.Document : capture.EventKind,
            ExternalId: capture.ExternalId,
            CapturedAtUtc: capture.CapturedAtUtc,
            ObservedAtUtc: capture.ObservedAtUtc,
            Content: contentWithContext,
            Title: capture.Title,
            Status: capture.Status,
            Summary: BuildSummary(contentWithContext),
            Workspace: capture.Workspace,
            WorkspaceId: capture.WorkspaceId,
            ProjectScope: capture.ProjectScope,
            RepositoryScope: capture.RepositoryScope,
            RepositoryOwner: capture.RepositoryOwner,
            RepositoryName: capture.RepositoryName,
            IssueNumber: capture.IssueNumber,
            PullRequestNumber: capture.PullRequestNumber,
            CommentId: capture.CommentId,
            ReviewCommentId: capture.ReviewCommentId,
            CommitSha: capture.CommitSha,
            Labels: capture.Labels,
            Assignees: capture.Assignees,
            LinkedExternalIds: capture.LinkedExternalIds,
            ConversationId: capture.ConversationId,
            ThreadId: capture.ThreadId,
            ThreadRootExternalId: ResolveThreadRootExternalId(capture),
            Actor: capture.Actor,
            SenderId: capture.SenderId,
            ChannelId: capture.ChannelId,
            ChannelName: capture.ChannelName,
            ReplyToExternalId: capture.ReplyToExternalId,
            EditedAtUtc: capture.EditedAtUtc,
            SourceUrl: capture.SourceUrl,
            Permalink: capture.Permalink,
            ContentHash: capture.ContentHash,
            DedupeKey: dedupeKey,
            Mentions: capture.Mentions,
            AttachmentUrls: capture.AttachmentUrls,
            Policy: null,
            Metadata: metadata);

        return Task.FromResult(normalized);
    }

    private static string NormalizeContent(string rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload))
            return string.Empty;

        return rawPayload.Trim();
    }

    private static string ResolveDedupeKey(MemoryRawCaptureEnvelope capture, string effectiveSourceSystem)
    {
        if (!string.IsNullOrWhiteSpace(capture.DedupeKey))
            return capture.DedupeKey!;

        var repo = TryGetRepo(capture);

        if (!string.IsNullOrWhiteSpace(repo) &&
            !string.IsNullOrWhiteSpace(capture.ReviewCommentId))
            return $"github:{repo}:review-comment:{capture.ReviewCommentId}";

        if (!string.IsNullOrWhiteSpace(repo) &&
            !string.IsNullOrWhiteSpace(capture.CommentId))
            return $"github:{repo}:comment:{capture.CommentId}";

        if (!string.IsNullOrWhiteSpace(repo) &&
            !string.IsNullOrWhiteSpace(capture.CommitSha))
            return $"github:{repo}:commit:{capture.CommitSha}";

        if (!string.IsNullOrWhiteSpace(repo) &&
            capture.PullRequestNumber.HasValue)
            return $"github:{repo}:pr:{capture.PullRequestNumber.Value}:event:{ToToken(capture.EventKind)}";

        if (!string.IsNullOrWhiteSpace(repo) &&
            capture.IssueNumber.HasValue)
            return $"github:{repo}:issue:{capture.IssueNumber.Value}:event:{ToToken(capture.EventKind)}";

        return $"github:{effectiveSourceSystem}:{capture.ExternalId}";
    }

    private static string BuildContentWithContext(MemoryRawCaptureEnvelope capture, string body)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            lines.Add($"Repository: {capture.RepositoryScope}");

        if (!string.IsNullOrWhiteSpace(capture.Actor))
            lines.Add($"Actor: {capture.Actor}");

        lines.Add($"Event: {capture.EventKind}");

        if (capture.Metadata?.TryGetValue("github.title", out var title) is true &&
            !string.IsNullOrWhiteSpace(title))
            lines.Add($"Title: {title}");

        if (capture.PullRequestNumber.HasValue)
            lines.Add($"PR: #{capture.PullRequestNumber.Value}");
        else if (capture.IssueNumber.HasValue)
            lines.Add($"Issue: #{capture.IssueNumber.Value}");

        if (!string.IsNullOrWhiteSpace(capture.CommentId))
            lines.Add($"CommentId: {capture.CommentId}");

        if (!string.IsNullOrWhiteSpace(capture.ReviewCommentId))
            lines.Add($"ReviewCommentId: {capture.ReviewCommentId}");

        if (capture.Labels is { Count: > 0 })
            lines.Add($"Labels: {string.Join(", ", capture.Labels)}");

        if (capture.Assignees is { Count: > 0 })
            lines.Add($"Assignees: {string.Join(", ", capture.Assignees)}");

        if (!string.IsNullOrWhiteSpace(body))
            lines.Add(body);

        return string.Join(Environment.NewLine, lines);
    }

    private static string? BuildSummary(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        const int maxLength = 140;
        if (content.Length <= maxLength)
            return content;

        return content[..maxLength].TrimEnd() + "…";
    }

    private static IReadOnlyDictionary<string, string> BuildProvenanceMetadata(MemoryRawCaptureEnvelope capture, string effectiveSourceSystem)
    {
        var metadata = capture.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(capture.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["provenance.sourceType"] = "GitHub";
        metadata["provenance.sourceSystem"] = effectiveSourceSystem;
        metadata["provenance.externalId"] = capture.ExternalId;
        metadata["provenance.envelopeId"] = capture.EnvelopeId;

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            metadata["provenance.repositoryScope"] = capture.RepositoryScope!;

        if (!string.IsNullOrWhiteSpace(capture.RepositoryOwner))
            metadata["provenance.repositoryOwner"] = capture.RepositoryOwner!;

        if (!string.IsNullOrWhiteSpace(capture.RepositoryName))
            metadata["provenance.repositoryName"] = capture.RepositoryName!;

        if (capture.IssueNumber.HasValue)
            metadata["provenance.issueNumber"] = capture.IssueNumber.Value.ToString(CultureInfo.InvariantCulture);

        if (capture.PullRequestNumber.HasValue)
            metadata["provenance.pullRequestNumber"] = capture.PullRequestNumber.Value.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(capture.CommentId))
            metadata["provenance.commentId"] = capture.CommentId!;

        if (!string.IsNullOrWhiteSpace(capture.ReviewCommentId))
            metadata["provenance.reviewCommentId"] = capture.ReviewCommentId!;

        if (!string.IsNullOrWhiteSpace(capture.CommitSha))
            metadata["provenance.commitSha"] = capture.CommitSha!;

        if (!string.IsNullOrWhiteSpace(capture.SourceUrl))
            metadata["provenance.sourceUrl"] = capture.SourceUrl!;

        if (!string.IsNullOrWhiteSpace(capture.Permalink))
            metadata["provenance.permalink"] = capture.Permalink!;

        return metadata;
    }

    private static string GetEffectiveSourceSystem(string? sourceSystem)
        => string.IsNullOrWhiteSpace(sourceSystem) ? "github" : sourceSystem;

    private static string? TryGetRepo(MemoryRawCaptureEnvelope capture)
    {
        if (!string.IsNullOrWhiteSpace(capture.RepositoryOwner) && !string.IsNullOrWhiteSpace(capture.RepositoryName))
            return $"{capture.RepositoryOwner}/{capture.RepositoryName}";

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            return capture.RepositoryScope;

        return null;
    }

    private static string ToToken(MemoryIngestionEventKind kind)
        => kind.ToString().ToLowerInvariant();

    private static string ResolveThreadRootExternalId(MemoryRawCaptureEnvelope capture)
    {
        if (!string.IsNullOrWhiteSpace(capture.ThreadRootExternalId))
            return capture.ThreadRootExternalId;

        var repo = TryGetRepo(capture);

        if (!string.IsNullOrWhiteSpace(repo) && capture.PullRequestNumber.HasValue)
            return $"github:{repo}:pr:{capture.PullRequestNumber.Value}";

        if (!string.IsNullOrWhiteSpace(repo) && capture.IssueNumber.HasValue)
            return $"github:{repo}:issue:{capture.IssueNumber.Value}";

        return capture.ThreadId ?? capture.ConversationId ?? capture.ExternalId;
    }
}
