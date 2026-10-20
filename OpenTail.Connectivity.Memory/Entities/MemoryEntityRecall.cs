// Moved from opentail's OpenTail.Core.Memory (MemoryEntityRecall.cs); namespace changed. See docs/design/memory.md.
namespace OpenTail.Connectivity.Memory;

public sealed record MemoryEntityRecallRequest(
    string EntityIdOrName,
    int TopK = 10,
    string? Workspace = null,
    string? ProjectScope = null,
    string? RepositoryScope = null);

public sealed record MemoryEntityRecallHit(
    MemoryEntity Entity,
    IReadOnlyList<MemoryLink> Links,
    double Score,
    IReadOnlyList<string> Reasons);

public sealed record MemoryEntityRecallResponse(
    string Query,
    IReadOnlyList<MemoryEntityRecallHit> Hits,
    IReadOnlyList<string> Notes);

public interface IMemoryEntityRecallService
{
    MemoryEntityRecallResponse Recall(
        MemoryEntityRecallRequest request,
        IReadOnlyList<MemoryEntity> entities,
        IReadOnlyList<MemoryLink> links);
}

public sealed class DefaultMemoryEntityRecallService : IMemoryEntityRecallService
{
    public MemoryEntityRecallResponse Recall(
        MemoryEntityRecallRequest request,
        IReadOnlyList<MemoryEntity> entities,
        IReadOnlyList<MemoryLink> links)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntityIdOrName);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(links);

        var query = request.EntityIdOrName.Trim();

        var scored = entities
            .Select(entity =>
            {
                var score = ComputeEntityScore(query, entity);
                var related = links
                    .Where(link => string.Equals(link.TargetId, entity.Id, StringComparison.Ordinal)
                                   || string.Equals(link.SourceId, entity.Id, StringComparison.Ordinal))
                    .OrderByDescending(static l => l.Weight)
                    .ToList();

                if (related.Count > 0)
                    score += Math.Min(0.35, related.Count * 0.05);

                var reasons = new List<string>();
                if (string.Equals(entity.Id, query, StringComparison.OrdinalIgnoreCase))
                    reasons.Add("exact-id-match");

                if (string.Equals(entity.Name, query, StringComparison.OrdinalIgnoreCase))
                    reasons.Add("exact-name-match");

                if ((entity.Aliases ?? []).Any(alias => string.Equals(alias, query, StringComparison.OrdinalIgnoreCase)))
                    reasons.Add("alias-match");

                if (related.Count > 0)
                    reasons.Add($"related-links={related.Count}");

                return new MemoryEntityRecallHit(entity, related, score, reasons);
            })
            .Where(hit => hit.Score > 0)
            .OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Entity.Id, StringComparer.Ordinal)
            .Take(request.TopK)
            .ToList();

        var notes = new List<string>
        {
            "entity-centric-recall=enabled",
            $"candidateEntities={entities.Count}",
            $"resultCount={scored.Count}",
        };

        return new MemoryEntityRecallResponse(query, scored, notes);
    }

    private static double ComputeEntityScore(string query, MemoryEntity entity)
    {
        var score = 0.0;

        if (string.Equals(entity.Id, query, StringComparison.OrdinalIgnoreCase))
            score += 1.0;

        if (string.Equals(entity.Name, query, StringComparison.OrdinalIgnoreCase))
            score += 0.9;

        if (entity.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 0.35;

        if ((entity.Aliases ?? []).Any(alias => string.Equals(alias, query, StringComparison.OrdinalIgnoreCase)))
            score += 0.8;

        if ((entity.Aliases ?? []).Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
            score += 0.25;

        if (!string.IsNullOrWhiteSpace(entity.ExternalId) && entity.ExternalId.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 0.2;

        return score;
    }
}
