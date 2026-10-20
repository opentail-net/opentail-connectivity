// Moved from opentail's OpenTail.Core.Memory (ToolExecutionMemoryIngestionNormalizer.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class ToolExecutionMemoryIngestionNormalizer : IMemoryIngestionNormalizer
{
    public Task<MemoryNormalizedEnvelope> NormalizeAsync(MemoryRawCaptureEnvelope capture, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);

        if (capture.SourceType != MemoryIngestionSourceType.ToolExecution)
            throw new InvalidOperationException("Tool-execution normalizer requires ToolExecution source type.");

        if (string.IsNullOrWhiteSpace(capture.ExternalId))
            throw new InvalidOperationException("Tool-execution capture requires an external id.");

        var effectiveSourceSystem = GetEffectiveSourceSystem(capture.SourceSystem);
        var content = BuildContent(capture);
        var metadata = BuildProvenanceMetadata(capture, effectiveSourceSystem);

        var normalized = new MemoryNormalizedEnvelope(
            NormalizedId: $"tool-execution:{effectiveSourceSystem}:{capture.ExternalId}",
            SourceType: capture.SourceType,
            SourceSystem: effectiveSourceSystem,
            EventKind: capture.EventKind == MemoryIngestionEventKind.Unknown ? MemoryIngestionEventKind.ToolEvent : capture.EventKind,
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
            ConversationId: capture.ConversationId,
            ThreadId: capture.ThreadId,
            ThreadRootExternalId: capture.ThreadRootExternalId ?? capture.ThreadId ?? capture.ConversationId ?? capture.ExternalId,
            Actor: capture.Actor,
            SourceUrl: capture.SourceUrl,
            Permalink: capture.Permalink,
            ContentHash: capture.ContentHash,
            DedupeKey: string.IsNullOrWhiteSpace(capture.DedupeKey) ? $"tool-execution:{effectiveSourceSystem}:{capture.ExternalId}" : capture.DedupeKey,
            Policy: null,
            Metadata: metadata);

        return Task.FromResult(normalized);
    }

    private static string BuildContent(MemoryRawCaptureEnvelope capture)
    {
        var lines = new List<string>
        {
            $"ToolExecutionId: {capture.ExternalId}"
        };

        if (!string.IsNullOrWhiteSpace(capture.Actor))
            lines.Add($"Invoker: {capture.Actor}");

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            lines.Add($"Project: {capture.ProjectScope}");

        if (!string.IsNullOrWhiteSpace(capture.RepositoryScope))
            lines.Add($"Repository: {capture.RepositoryScope}");

        if (!string.IsNullOrWhiteSpace(capture.Title))
            lines.Add($"Title: {capture.Title}");

        if (!string.IsNullOrWhiteSpace(capture.Status))
            lines.Add($"Status: {capture.Status}");

        if (capture.Metadata?.TryGetValue("tool.name", out var toolName) is true &&
            !string.IsNullOrWhiteSpace(toolName))
        {
            lines.Add($"ToolName: {toolName}");
        }
        else if (!string.IsNullOrWhiteSpace(capture.Labels?.FirstOrDefault()))
        {
            lines.Add($"ToolName: {capture.Labels![0]}");
        }

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

        metadata["provenance.sourceType"] = "ToolExecution";
        metadata["provenance.sourceSystem"] = effectiveSourceSystem;
        metadata["provenance.externalId"] = capture.ExternalId;
        metadata["provenance.envelopeId"] = capture.EnvelopeId;
        metadata["normalization.payloadHandling"] = "raw-preserved-policy-downstream";

        if (!string.IsNullOrWhiteSpace(capture.Actor))
            metadata["provenance.invoker"] = capture.Actor!;

        if (!string.IsNullOrWhiteSpace(capture.ProjectScope))
            metadata["provenance.projectScope"] = capture.ProjectScope!;

        if (!string.IsNullOrWhiteSpace(capture.SourceUrl))
            metadata["provenance.sourceUrl"] = capture.SourceUrl!;

        if (!string.IsNullOrWhiteSpace(capture.Permalink))
            metadata["provenance.permalink"] = capture.Permalink!;

        return metadata;
    }

    private static string GetEffectiveSourceSystem(string? sourceSystem)
        => string.IsNullOrWhiteSpace(sourceSystem) ? "tool-execution" : sourceSystem;
}
