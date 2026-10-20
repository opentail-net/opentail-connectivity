// Moved from opentail's OpenTail.Core.Memory (UserNoteMemoryIngestionNormalizer.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class UserNoteMemoryIngestionNormalizer : IMemoryIngestionNormalizer
{
    public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (capture.SourceType != MemoryIngestionSourceType.UserNote)
            throw new InvalidOperationException("User-note normalizer requires UserNote source type.");

        if (string.IsNullOrWhiteSpace(capture.ExternalId))
            throw new InvalidOperationException("User-note capture requires an external id.");

        var effectiveSourceSystem = GetEffectiveSourceSystem(capture.SourceSystem);
        var content = BuildContent(capture);
        var metadata = BuildProvenanceMetadata(capture, effectiveSourceSystem);
        var dedupeKey = string.IsNullOrWhiteSpace(capture.DedupeKey)
            ? $"user-note:{effectiveSourceSystem}:{capture.ExternalId}"
            : capture.DedupeKey;

        var normalized = new MemoryNormalizedEnvelope(
            NormalizedId: $"user-note:{effectiveSourceSystem}:{capture.ExternalId}",
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
            ThreadRootExternalId: capture.ThreadRootExternalId ?? capture.ThreadId ?? capture.ConversationId ?? capture.ExternalId,
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
            $"UserNoteId: {capture.ExternalId}"
        };

        if (!string.IsNullOrWhiteSpace(capture.Title))
            lines.Add($"Title: {capture.Title}");

        if (!string.IsNullOrWhiteSpace(capture.Status))
            lines.Add($"Status: {capture.Status}");

        if (!string.IsNullOrWhiteSpace(capture.Actor))
            lines.Add($"Author: {capture.Actor}");

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            lines.Add($"Project: {capture.ProjectScope}");

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            lines.Add($"Repository: {capture.RepositoryScope}");

        if (capture.Labels is { Count: > 0 })
            lines.Add($"Labels: {string.Join(", ", capture.Labels)}");

        if (capture.Metadata?.TryGetValue("note.kind", out var noteKind) is true && !string.IsNullOrWhiteSpace(noteKind))
            lines.Add($"NoteKind: {noteKind}");

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

    private static IReadOnlyDictionary<string, string> BuildProvenanceMetadata(MemoryRawCaptureEnvelope capture, string effectiveSourceSystem)
    {
        var metadata = capture.Metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(capture.Metadata, StringComparer.OrdinalIgnoreCase);

        metadata["provenance.sourceType"] = "UserNote";
        metadata["provenance.sourceSystem"] = effectiveSourceSystem;
        metadata["provenance.externalId"] = capture.ExternalId;
        metadata["provenance.envelopeId"] = capture.EnvelopeId;

        if (!string.IsNullOrWhiteSpace(capture.Actor))
            metadata["provenance.actor"] = capture.Actor!;

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            metadata["provenance.projectScope"] = capture.ProjectScope!;

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            metadata["provenance.repositoryScope"] = capture.RepositoryScope!;

        if (!string.IsNullOrWhiteSpace(capture.SourceUrl))
            metadata["provenance.sourceUrl"] = capture.SourceUrl!;

        if (!string.IsNullOrWhiteSpace(capture.Permalink))
            metadata["provenance.permalink"] = capture.Permalink!;

        return metadata;
    }

    private static string GetEffectiveSourceSystem(string? sourceSystem)
        => string.IsNullOrWhiteSpace(sourceSystem) ? "user-note" : sourceSystem;
}
