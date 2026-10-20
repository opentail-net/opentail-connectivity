// Moved from opentail's OpenTail.Core.Memory (MemoryMaintenanceService.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed class DefaultMemoryMaintenanceService(IMemoryConsolidator? consolidator = null) : IMemoryMaintenanceService
{
    private readonly IMemoryConsolidator? _consolidator = consolidator;

    public async Task<MemoryMaintenanceReport> ExecuteAsync(MemoryMaintenanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var asOf = request.AsOfUtc ?? DateTimeOffset.UtcNow;
        var actions = new List<MemoryMaintenanceAction>();
        var notes = new List<string>();

        AddDeduplicationActions(request, actions);
        AddClusterCollapseActions(request, actions, asOf);
        AddOrphanRepairActions(request, actions);
        AddBrokenLinkRepairActions(request, actions);
        AddRetentionActions(request, actions, asOf);
        AddPinningAndDecayActions(request, actions, asOf);
        AddSummaryActions(request, actions, asOf);
        AddIncrementalReindexActions(request, actions, asOf);
        
        if (_consolidator is not null)
        {
            await AddSemanticConsolidationActionsAsync(request, actions, ct);
        }

        if (request.CrashSafeReplay && !string.IsNullOrWhiteSpace(request.ReplayToken))
            actions = ApplyReplaySafetyDeduping(actions, request.ReplayToken!);

        var bounded = actions
            .OrderBy(static a => a.Kind)
            .ThenBy(static a => a.TargetId, StringComparer.Ordinal)
            .Take(Math.Max(1, request.MaxActions))
            .ToList();

        var counts = bounded
            .GroupBy(static a => a.Kind)
            .ToDictionary(g => g.Key.ToString(), g => g.Count(), StringComparer.Ordinal);

        notes.Add($"input.records={request.Records.Count}");
        notes.Add($"input.links={request.Links.Count}");
        notes.Add($"output.actions={bounded.Count}");
        notes.Add("idempotence=deterministic-sort+stable-targeting");

        if (request.CrashSafeReplay)
        {
            notes.Add($"replayMode=enabled");
            notes.Add($"replayToken={request.ReplayToken ?? "none"}");
        }

        return new MemoryMaintenanceReport(
            ExecutedAtUtc: asOf,
            Actions: bounded,
            ActionCounts: counts,
            Notes: notes,
            ReplayToken: request.CrashSafeReplay ? request.ReplayToken : null,
            IsIdempotentSafe: true);
    }

    private static List<MemoryMaintenanceAction> ApplyReplaySafetyDeduping(List<MemoryMaintenanceAction> actions, string replayToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduped = new List<MemoryMaintenanceAction>(actions.Count);

        foreach (var action in actions)
        {
            var key = $"{replayToken}:{action.Kind}:{action.TargetId}";
            if (!seen.Add(key))
                continue;

            deduped.Add(action);
        }

        return deduped;
    }

    private static void AddDeduplicationActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions)
    {
        var duplicateGroups = request.Records
            .GroupBy(static r => r.DedupeKey ?? $"{r.Source.SourceSystem}:{r.Source.ExternalId}:{r.ContentHash}", StringComparer.Ordinal)
            .Where(static g => g.Count() > 1);

        foreach (var group in duplicateGroups)
        {
            var keep = group.OrderByDescending(static r => r.ObservedAtUtc).ThenBy(static r => r.Id, StringComparer.Ordinal).First();
            foreach (var dup in group.Where(r => !string.Equals(r.Id, keep.Id, StringComparison.Ordinal)))
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.Deduplicate,
                    TargetId: dup.Id,
                    Reason: $"duplicate of {keep.Id}",
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["keepRecordId"] = keep.Id,
                    }));
            }
        }
    }

    private async Task AddSemanticConsolidationActionsAsync(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, CancellationToken ct)
    {
        if (_consolidator is null) return;

        var suggestions = await _consolidator.FindDuplicatesAsync(request.Records, ct: ct);
        foreach (var suggestion in suggestions)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.SemanticMerge,
                TargetId: suggestion.DuplicateId,
                Reason: suggestion.Rationale ?? "semantic duplication detected",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["canonicalId"] = suggestion.CanonicalId,
                    ["similarity"] = suggestion.Similarity.ToString("F3", CultureInfo.InvariantCulture)
                }));
        }
    }

    private static void AddIncrementalReindexActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, DateTimeOffset asOf)
    {
        var reindexThreshold = asOf.AddDays(-14);

        foreach (var record in request.Records)
        {
            var staleEmbedding = !string.IsNullOrWhiteSpace(request.CurrentEmbeddingVersion)
                                 && !string.Equals(record.Embedding?.Model.Revision, request.CurrentEmbeddingVersion, StringComparison.Ordinal);

            if (!staleEmbedding && record.ObservedAtUtc >= reindexThreshold)
                continue;

            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.IncrementalReindex,
                TargetId: record.Id,
                Reason: staleEmbedding
                    ? "embedding revision drift requires reindex"
                    : "recently active record selected for incremental reindex"));
        }
    }

    private static void AddSummaryActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, DateTimeOffset asOf)
    {
        var weekStart = asOf.AddDays(-7);
        var weeklyDecisionCount = request.Records.Count(r => r.ObservedAtUtc >= weekStart && ContainsAny(r.Content ?? string.Empty, "decision", "approved", "rejected"));
        if (weeklyDecisionCount > 0)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.WeeklyDecisionDigest,
                TargetId: $"digest:week:{asOf:yyyyMMdd}",
                Reason: "weekly decision digest candidate",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["decisionCount"] = weeklyDecisionCount.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var projectGroups = request.Records
            .Where(r => !string.IsNullOrWhiteSpace(r.Source.ProjectScope))
            .GroupBy(r => r.Source.ProjectScope!, StringComparer.Ordinal);

        foreach (var project in projectGroups)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.ProjectBrief,
                TargetId: $"brief:project:{project.Key}",
                Reason: "project memory brief candidate",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recordCount"] = project.Count().ToString(CultureInfo.InvariantCulture),
                }));
        }

        var byActor = request.Records
            .Where(r => !string.IsNullOrWhiteSpace(r.Source.Actor))
            .GroupBy(r => r.Source.Actor!, StringComparer.Ordinal)
            .Where(static g => g.Count() >= 2);

        foreach (var actorGroup in byActor)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.TeamInteractionSummary,
                TargetId: $"team:{actorGroup.Key}",
                Reason: "team interaction summary candidate",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["interactionCount"] = actorGroup.Count().ToString(CultureInfo.InvariantCulture),
                }));
        }

        var unresolved = request.Records
            .Where(r => ContainsAny(r.Content ?? string.Empty, "todo", "follow-up", "blocked", "unresolved"))
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Source.ProjectScope) ? "(none)" : r.Source.ProjectScope!, StringComparer.Ordinal)
            .Where(static g => g.Count() >= 2);

        foreach (var cluster in unresolved)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.UnresolvedTopicView,
                TargetId: $"unresolved:{cluster.Key}",
                Reason: "unresolved topic cluster detected",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["topicCount"] = cluster.Count().ToString(CultureInfo.InvariantCulture),
                }));
        }

        var summaryTargets = request.Records
            .Where(r => (r.Content?.Length ?? 0) >= 220 || ContainsAny(r.Content ?? string.Empty, "thread", "discussion", "summary"))
            .Take(20);

        foreach (var target in summaryTargets)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.Summarize,
                TargetId: target.Id,
                Reason: "record eligible for summary projection"));
        }
    }

    private static void AddClusterCollapseActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, DateTimeOffset asOf)
    {
        var staleThreshold = asOf.AddDays(-45);
        var staleByProject = request.Records
            .Where(r => r.ObservedAtUtc <= staleThreshold)
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Source.ProjectScope) ? "(none)" : r.Source.ProjectScope!, StringComparer.Ordinal)
            .Where(static g => g.Count() >= 3);

        foreach (var cluster in staleByProject)
        {
            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.CollapseCluster,
                TargetId: $"cluster:{cluster.Key}",
                Reason: "stale cluster eligible for consolidation",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recordCount"] = cluster.Count().ToString(CultureInfo.InvariantCulture),
                }));
        }
    }

    private static void AddOrphanRepairActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions)
    {
        var byId = request.Records.ToDictionary(static r => r.Id, StringComparer.Ordinal);

        foreach (var record in request.Records)
        {
            if (string.IsNullOrWhiteSpace(record.ParentRecordId))
                continue;

            if (!byId.ContainsKey(record.ParentRecordId!))
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.RepairOrphan,
                    TargetId: record.Id,
                    Reason: "parent record missing",
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["missingParentId"] = record.ParentRecordId!,
                    }));
            }
        }
    }

    private static void AddBrokenLinkRepairActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions)
    {
        var nodeIds = request.Records.Select(static r => r.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var link in request.Links)
        {
            var sourceExists = nodeIds.Contains(link.SourceId);
            var targetExists = nodeIds.Contains(link.TargetId);
            if (sourceExists && targetExists)
                continue;

            actions.Add(new MemoryMaintenanceAction(
                Kind: MemoryMaintenanceActionKind.RepairLink,
                TargetId: $"{link.SourceId}->{link.TargetId}:{link.RelationType}",
                Reason: "link references missing record node",
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["sourceExists"] = sourceExists.ToString(),
                    ["targetExists"] = targetExists.ToString(),
                }));
        }
    }

    private static void AddRetentionActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, DateTimeOffset asOf)
    {
        var archiveThreshold = asOf.AddDays(-120);
        var pruneThreshold = asOf.AddDays(-365);

        foreach (var record in request.Records)
        {
            if (record.ObservedAtUtc <= pruneThreshold && record.Policy.RetentionClass == MemoryRetentionClass.Expiring)
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.RetentionPrune,
                    TargetId: record.Id,
                    Reason: "expiring record exceeded prune window"));
                continue;
            }

            if (record.ObservedAtUtc <= archiveThreshold && record.Policy.RetentionClass != MemoryRetentionClass.Durable)
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.RetentionArchive,
                    TargetId: record.Id,
                    Reason: "record exceeded hot retention window"));
            }
        }
    }

    private static void AddPinningAndDecayActions(MemoryMaintenanceRequest request, List<MemoryMaintenanceAction> actions, DateTimeOffset asOf)
    {
        var hotThreshold = asOf.AddDays(-10);
        foreach (var record in request.Records)
        {
            var content = record.Content ?? string.Empty;

            if (record.ObservedAtUtc >= hotThreshold && (record.BaseImportance >= 0.7 || ContainsAny(content, "decision", "approved", "incident")))
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.PinHotMemory,
                    TargetId: record.Id,
                    Reason: "high-signal recent memory"));
            }

            if (!string.Equals(record.Embedding?.Model.Revision, request.CurrentEmbeddingVersion, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(request.CurrentEmbeddingVersion))
            {
                actions.Add(new MemoryMaintenanceAction(
                    Kind: MemoryMaintenanceActionKind.RefreshEmbedding,
                    TargetId: record.Id,
                    Reason: "embedding revision drift",
                    Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["currentRevision"] = request.CurrentEmbeddingVersion!,
                        ["recordRevision"] = record.Embedding?.Model.Revision ?? "none",
                    }));
            }
        }
    }

    private static bool ContainsAny(string content, params string[] terms)
        => terms.Any(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
}
