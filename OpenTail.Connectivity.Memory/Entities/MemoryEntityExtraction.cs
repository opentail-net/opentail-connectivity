// Moved from opentail's OpenTail.Core.Memory (MemoryEntityExtraction.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed record MemoryEntityExtractionResult(
    IReadOnlyList<MemoryEntity> Entities,
    IReadOnlyList<MemoryLink> Links,
    IReadOnlyList<string> Diagnostics);

public interface IMemoryEntityExtractor
{
    MemoryEntityExtractionResult Extract(
        MemoryNormalizedEnvelope envelope,
        string recordId,
        IReadOnlyList<MemoryChunk> chunks);
}

public sealed class DefaultMemoryEntityExtractor : IMemoryEntityExtractor
{
    private static readonly Regex MentionRegex = new(@"(?<![\w])@([A-Za-z0-9._-]{2,})", RegexOptions.Compiled);
    private static readonly Regex RepoRegex = new(@"\b([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)\b", RegexOptions.Compiled);
    private static readonly Regex ToolRegex = new(@"\b(tool|runner|pipeline|workflow|agent|connector)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public MemoryEntityExtractionResult Extract(
        MemoryNormalizedEnvelope envelope,
        string recordId,
        IReadOnlyList<MemoryChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordId);
        ArgumentNullException.ThrowIfNull(chunks);

        var entitiesById = new Dictionary<string, MemoryEntity>(StringComparer.Ordinal);
        var links = new List<MemoryLink>();
        var diagnostics = new List<string>();
        var combinedText = BuildCombinedText(envelope, chunks);

        ExtractActorEntity(envelope, entitiesById, links, recordId);
        ExtractRepositoryEntity(envelope, entitiesById, links, recordId, combinedText);
        ExtractMentionEntities(envelope, entitiesById, links, recordId, combinedText);
        ExtractToolOrWorkflowEntities(envelope, entitiesById, links, recordId, combinedText);
        ExtractDecisionAndIncidentEntities(envelope, entitiesById, links, recordId, combinedText);

        if (entitiesById.Count == 0)
            diagnostics.Add("entity-extraction:no-entities-detected");

        return new MemoryEntityExtractionResult([.. entitiesById.Values], links, diagnostics);
    }

    private static string BuildCombinedText(MemoryNormalizedEnvelope envelope, IReadOnlyList<MemoryChunk> chunks)
    {
        var parts = new List<string>
        {
            envelope.Content ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(envelope.Title))
            parts.Add(envelope.Title!);

        if (!string.IsNullOrWhiteSpace(envelope.Status))
            parts.Add(envelope.Status!);

        parts.AddRange(chunks.Select(static c => c.Content));

        return string.Join("\n", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
    }

    private static void ExtractActorEntity(
        MemoryNormalizedEnvelope envelope,
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId)
    {
        if (string.IsNullOrWhiteSpace(envelope.Actor))
            return;

        var normalized = NormalizePersonIdentity(envelope.Actor!);
        var entityId = $"entity:person:{normalized}";

        UpsertEntity(entitiesById, new MemoryEntity(
            Id: entityId,
            Kind: "person",
            Name: envelope.Actor!,
            ExternalId: envelope.Actor,
            Aliases: BuildAliases(envelope.Actor!, normalized),
            Tags: ["source:actor"]));

        links.Add(new MemoryLink(
            SourceId: recordId,
            SourceKind: MemoryNodeKind.Record,
            TargetId: entityId,
            TargetKind: MemoryNodeKind.Entity,
            RelationType: MemoryRelationType.Authored,
            Weight: 0.95,
            Evidence: "actor"));
    }

    private static void ExtractRepositoryEntity(
        MemoryNormalizedEnvelope envelope,
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId,
        string text)
    {
        var repositoryScope = envelope.RepositoryScope;
        if (string.IsNullOrWhiteSpace(repositoryScope))
        {
            var repoMatch = RepoRegex.Match(text);
            if (repoMatch.Success)
                repositoryScope = repoMatch.Value;
        }

        if (string.IsNullOrWhiteSpace(repositoryScope))
            return;

        var normalized = NormalizeIdentity(repositoryScope!);
        var entityId = $"entity:repository:{normalized}";

        UpsertEntity(entitiesById, new MemoryEntity(
            Id: entityId,
            Kind: "repository",
            Name: repositoryScope!,
            ExternalId: repositoryScope,
            Aliases: [normalized],
            Tags: ["source:repository"]));

        links.Add(new MemoryLink(
            SourceId: recordId,
            SourceKind: MemoryNodeKind.Record,
            TargetId: entityId,
            TargetKind: MemoryNodeKind.Entity,
            RelationType: MemoryRelationType.DiscussedIn,
            Weight: 0.9,
            Evidence: "repository"));
    }

    private static void ExtractMentionEntities(
        MemoryNormalizedEnvelope envelope,
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId,
        string text)
    {
        var mentions = envelope.Mentions ?? [];

        foreach (var mention in mentions.Where(static m => !string.IsNullOrWhiteSpace(m)))
            AddMentionEntity(entitiesById, links, recordId, mention!, sourceTag: "mention:list");

        foreach (Match match in MentionRegex.Matches(text))
        {
            var mention = match.Value;
            if (!string.IsNullOrWhiteSpace(mention))
                AddMentionEntity(entitiesById, links, recordId, mention, sourceTag: "mention:text");
        }
    }

    private static void AddMentionEntity(
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId,
        string mention,
        string sourceTag)
    {
        var normalized = NormalizePersonIdentity(mention);
        var entityId = $"entity:person:{normalized}";

        UpsertEntity(entitiesById, new MemoryEntity(
            Id: entityId,
            Kind: "person",
            Name: mention,
            ExternalId: mention,
            Aliases: BuildAliases(mention, normalized),
            Tags: [sourceTag]));

        links.Add(new MemoryLink(
            SourceId: recordId,
            SourceKind: MemoryNodeKind.Record,
            TargetId: entityId,
            TargetKind: MemoryNodeKind.Entity,
            RelationType: MemoryRelationType.Mentioned,
            Weight: 0.7,
            Evidence: mention));
    }

    private static void ExtractToolOrWorkflowEntities(
        MemoryNormalizedEnvelope envelope,
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId,
        string text)
    {
        if (!ToolRegex.IsMatch(text) && envelope.SourceType is not MemoryIngestionSourceType.ToolExecution and not MemoryIngestionSourceType.WorkflowRun)
            return;

        var type = envelope.SourceType == MemoryIngestionSourceType.WorkflowRun ? "workflow" : "tool";
        var seed = envelope.SourceSystem;
        if (envelope.SourceType != MemoryIngestionSourceType.WorkflowRun
            && envelope.Metadata?.TryGetValue("tool.name", out var toolName) == true
            && !string.IsNullOrWhiteSpace(toolName))
        {
            seed = toolName;
        }

        if (string.IsNullOrWhiteSpace(seed))
            seed = type;

        var normalized = NormalizeIdentity(seed!);
        var entityId = $"entity:{type}:{normalized}";

        UpsertEntity(entitiesById, new MemoryEntity(
            Id: entityId,
            Kind: type,
            Name: seed!,
            ExternalId: seed,
            Aliases: [normalized],
            Tags: ["source:runtime"]));

        links.Add(new MemoryLink(
            SourceId: recordId,
            SourceKind: MemoryNodeKind.Record,
            TargetId: entityId,
            TargetKind: MemoryNodeKind.Entity,
            RelationType: envelope.SourceType == MemoryIngestionSourceType.WorkflowRun ? MemoryRelationType.RelatedTo : MemoryRelationType.DiscussedIn,
            Weight: 0.65,
            Evidence: type));
    }

    private static void ExtractDecisionAndIncidentEntities(
        MemoryNormalizedEnvelope envelope,
        Dictionary<string, MemoryEntity> entitiesById,
        List<MemoryLink> links,
        string recordId,
        string text)
    {
        var hasDecisionSignals = ContainsAny(text, "decision", "approved", "rejected");
        if (hasDecisionSignals)
        {
            var decisionName = !string.IsNullOrWhiteSpace(envelope.Title)
                ? envelope.Title!
                : $"decision-{envelope.ExternalId}";

            var decisionId = $"entity:decision:{NormalizeIdentity(decisionName)}";

            UpsertEntity(entitiesById, new MemoryEntity(
                Id: decisionId,
                Kind: "decision",
                Name: decisionName,
                ExternalId: envelope.ExternalId,
                Aliases: [NormalizeIdentity(decisionName)],
                Tags: ["semantic:decision"]));

            var decisionRelation = ContainsAny(text, "rejected")
                ? MemoryRelationType.Rejected
                : ContainsAny(text, "approved")
                    ? MemoryRelationType.Approved
                    : MemoryRelationType.RelatedTo;

            links.Add(new MemoryLink(
                SourceId: recordId,
                SourceKind: MemoryNodeKind.Record,
                TargetId: decisionId,
                TargetKind: MemoryNodeKind.Entity,
                RelationType: decisionRelation,
                Weight: 0.8,
                Evidence: "decision"));
        }

        if (ContainsAny(text, "incident", "outage", "failure", "error"))
        {
            var incidentName = !string.IsNullOrWhiteSpace(envelope.Title)
                ? envelope.Title!
                : $"incident-{envelope.ExternalId}";

            var incidentId = $"entity:incident:{NormalizeIdentity(incidentName)}";

            UpsertEntity(entitiesById, new MemoryEntity(
                Id: incidentId,
                Kind: "incident",
                Name: incidentName,
                ExternalId: envelope.ExternalId,
                Aliases: [NormalizeIdentity(incidentName)],
                Tags: ["semantic:incident"]));

            links.Add(new MemoryLink(
                SourceId: recordId,
                SourceKind: MemoryNodeKind.Record,
                TargetId: incidentId,
                TargetKind: MemoryNodeKind.Entity,
                RelationType: MemoryRelationType.RelatedTo,
                Weight: 0.75,
                Evidence: "incident"));
        }
    }

    private static void UpsertEntity(Dictionary<string, MemoryEntity> entitiesById, MemoryEntity candidate)
    {
        if (!entitiesById.TryGetValue(candidate.Id, out var existing))
        {
            entitiesById[candidate.Id] = candidate;
            return;
        }

        var aliases = (existing.Aliases ?? [])
            .Concat(candidate.Aliases ?? [])
            .Where(static a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tags = (existing.Tags ?? [])
            .Concat(candidate.Tags ?? [])
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        entitiesById[candidate.Id] = existing with
        {
            Name = existing.Name.Length >= candidate.Name.Length ? existing.Name : candidate.Name,
            ExternalId = existing.ExternalId ?? candidate.ExternalId,
            Aliases = aliases,
            Tags = tags,
        };
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9@._:/-]+", "-");
        normalized = normalized.Trim('-', ':', '/');

        return string.IsNullOrWhiteSpace(normalized)
            ? "unknown"
            : normalized;
    }

    private static string NormalizePersonIdentity(string value)
        => NormalizeIdentity(value.Trim().TrimStart('@'));

    private static IReadOnlyList<string> BuildAliases(params string[] values)
        => values
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool ContainsAny(string content, params string[] terms)
        => terms.Any(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
}
