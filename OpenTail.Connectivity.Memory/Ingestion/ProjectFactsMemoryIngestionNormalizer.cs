// Moved from opentail's OpenTail.Core.Memory (ProjectFactsMemoryIngestionNormalizer.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class ProjectFactsMemoryIngestionNormalizer : IMemoryIngestionNormalizer
{
    public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (capture.SourceType != MemoryIngestionSourceType.LocalDocs)
            throw new InvalidOperationException("Project-facts normalizer requires LocalDocs source type.");

        if (string.IsNullOrWhiteSpace(capture.ExternalId))
            throw new InvalidOperationException("Project-facts capture requires an external id.");

        var effectiveSourceSystem = string.IsNullOrWhiteSpace(capture.SourceSystem) ? "local-docs" : capture.SourceSystem;
        var content = BuildContent(capture);
        var metadata = BuildProvenanceMetadata(capture);
        var dedupeKey = string.IsNullOrWhiteSpace(capture.DedupeKey)
            ? $"project-fact:{effectiveSourceSystem}:{capture.ExternalId}"
            : capture.DedupeKey;

        var normalized = new MemoryNormalizedEnvelope(
            NormalizedId: $"project-fact:{effectiveSourceSystem}:{capture.ExternalId}",
            SourceType: capture.SourceType,
            SourceSystem: effectiveSourceSystem,
            EventKind: capture.EventKind == MemoryIngestionEventKind.Unknown ? MemoryIngestionEventKind.Document : capture.EventKind,
            ExternalId: capture.ExternalId,
            CapturedAtUtc: capture.CapturedAtUtc,
            ObservedAtUtc: capture.ObservedAtUtc,
            Content: content,
            Title: capture.Title,
            Status: capture.Status,
            Summary: BuildSummary(content),
            Workspace: capture.Workspace,
            WorkspaceId: capture.WorkspaceId,
            ProjectScope: capture.ProjectScope,
            RepositoryScope: capture.RepositoryScope,
            RepositoryOwner: capture.RepositoryOwner,
            RepositoryName: capture.RepositoryName,
            ConversationId: capture.ConversationId,
            ThreadId: capture.ThreadId,
            ThreadRootExternalId: capture.ThreadRootExternalId ?? capture.ConversationId ?? capture.ThreadId ?? capture.ExternalId,
            Actor: capture.Actor,
            SourceUrl: capture.SourceUrl,
            Permalink: capture.Permalink,
            ContentHash: capture.ContentHash,
            DedupeKey: dedupeKey,
            Labels: capture.Labels,
            LinkedExternalIds: capture.LinkedExternalIds,
            Policy: null,
            Metadata: metadata);

        return Task.FromResult(normalized);
    }

    private static string BuildContent(MemoryRawCaptureEnvelope capture)
    {
        var lines = new List<string>
        {
            $"ProjectFactId: {capture.ExternalId}"
        };

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            lines.Add($"Project: {capture.ProjectScope}");

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            lines.Add($"Repository: {capture.RepositoryScope}");

        if (!string.IsNullOrWhiteSpace(capture.Title))
            lines.Add($"Title: {capture.Title}");

        if (!string.IsNullOrWhiteSpace(capture.Status))
            lines.Add($"Status: {capture.Status}");

        if (!string.IsNullOrWhiteSpace(capture.SourceUrl))
            lines.Add($"SourcePath: {capture.SourceUrl}");

        if (capture.Labels is { Count: > 0 })
            lines.Add($"FactTags: {string.Join(", ", capture.Labels)}");

        var payload = string.IsNullOrWhiteSpace(capture.RawPayload) ? string.Empty : capture.RawPayload.Trim();
        if (!string.IsNullOrWhiteSpace(payload))
            lines.Add(payload);

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

    private static IReadOnlyDictionary<string, string> BuildProvenanceMetadata(MemoryRawCaptureEnvelope capture)
    {
        var metadata = capture.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(capture.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["provenance.sourceType"] = "LocalDocs";
        metadata["provenance.sourceSystem"] = string.IsNullOrWhiteSpace(capture.SourceSystem) ? "local-docs" : capture.SourceSystem;
        metadata["provenance.externalId"] = capture.ExternalId;
        metadata["provenance.envelopeId"] = capture.EnvelopeId;

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            metadata["provenance.projectScope"] = capture.ProjectScope!;

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            metadata["provenance.repositoryScope"] = capture.RepositoryScope!;

        if (!string.IsNullOrWhiteSpace(capture.SourceUrl))
            metadata["provenance.sourcePath"] = capture.SourceUrl!;

        return metadata;
    }
}
